using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;

namespace AgenticPrReview.Runtime.Host.Publishing.GitHub.Inline;

internal sealed class PostAcceptanceInlinePublisherHook :
    IActionHostPostAcceptanceInlineHook
{
    private readonly InlineCommentPublisher _publisher;

    internal PostAcceptanceInlinePublisherHook(
        IInlineGitHubPublisherTransportFactory factory) =>
        _publisher = new(factory);

    public async Task<ActionHostInlineHookResult> PublishAsync(
        ActionHostCoordinator.PostAcceptanceInlineRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.TryConsume(out var operation) ||
            !AuthorizedInlinePublicationRequest.TryCreate(
                operation, out var authorized) ||
            authorized is null)
        {
            return ActionHostInlineHookResult.Incomplete;
        }

        var result = await _publisher.PublishAsync(
            authorized, cancellationToken).ConfigureAwait(false);
        return result.IsComplete
            ? ActionHostInlineHookResult.Complete
            : ActionHostInlineHookResult.Incomplete;
    }
}

internal sealed class InlineCommentPublisher
{
    private readonly IInlineGitHubPublisherTransportFactory _factory;

    internal InlineCommentPublisher(
        IInlineGitHubPublisherTransportFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    internal async Task<InlinePublicationResult> PublishAsync(
        AuthorizedInlinePublicationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = new ResultBuilder(request.CandidateMap.Candidates.Length);
        if (cancellationToken.IsCancellationRequested)
        {
            result.FailAll(InlineFailureKind.Cancelled);
            return result.Build(BoundedGitHubPublisherOutcome
                .CancelledBeforeSend);
        }

        if (!InlineCommentSerializer.TryRender(request, out var rendered) ||
            rendered is null)
        {
            result.FailAll(InlineFailureKind.InvalidCandidate);
            return result.Build(BoundedGitHubPublisherOutcome
                .AuthorizationOrValidationFailure);
        }

        IInlineGitHubPublisherTransport transport;
        try
        {
            transport = _factory.Create(request);
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            result.FailAll(InlineFailureKind.AuthorityRejected);
            return result.Build(BoundedGitHubPublisherOutcome
                .AuthorizationOrValidationFailure);
        }

        using (transport)
        {
            var initial = await DiscoverAsync(transport,
                cancellationToken).ConfigureAwait(false);
            if (!initial.Complete)
            {
                result.FailAll(initial.Cancelled
                    ? InlineFailureKind.Cancelled
                    : InlineFailureKind.ListingIncomplete);
                return result.Build(initial.Cancelled
                    ? BoundedGitHubPublisherOutcome.CancelledBeforeSend
                    : BoundedGitHubPublisherOutcome
                        .AuthorizationOrValidationFailure);
            }

            var pending = rendered.Where(comment =>
                !initial.ByKey.ContainsKey(comment.Candidate.InlineKey))
                .ToList();
            result.ExistingDuplicate = rendered.Count - pending.Count;
            if (pending.Count == 0)
            {
                return result.Build(
                    BoundedGitHubPublisherOutcome.WrittenAndReadBack);
            }

            var initialHead = await RevalidateHeadAsync(
                request, cancellationToken).ConfigureAwait(false);
            if (!IsExactHead(request, initialHead))
            {
                var cancelled = IsCancellation(initialHead.Status);
                result.Fail(pending.Count, cancelled
                    ? InlineFailureKind.Cancelled
                    : InlineFailureKind.HeadNotExact);
                return result.Build(cancelled
                    ? BoundedGitHubPublisherOutcome.CancelledBeforeSend
                    : BoundedGitHubPublisherOutcome
                        .AuthorizationOrValidationFailure);
            }

            if (cancellationToken.IsCancellationRequested ||
                !transport.IsWithinOverallDeadline)
            {
                result.Fail(pending.Count, InlineFailureKind.Cancelled);
                return result.Build(BoundedGitHubPublisherOutcome
                    .CancelledBeforeSend);
            }

            if (!InlineCommentSerializer.TryBatch(request, pending,
                    out var batch) || batch is null)
            {
                result.Fail(pending.Count,
                    InlineFailureKind.InvalidCandidate);
                return result.Build(BoundedGitHubPublisherOutcome
                    .AuthorizationOrValidationFailure);
            }

            result.BatchAttempts = 1;
            var batchResult = await transport.CreateBatchReviewAsync(
                batch, cancellationToken).ConfigureAwait(false);
            if (batchResult.Outcome == BoundedGitHubHttpOutcome.KnownNotSent &&
                batchResult.Reason ==
                    BoundedGitHubPublisherReason.BatchValidationRejected)
            {
                return await FallbackAsync(request, transport, pending,
                    result, cancellationToken).ConfigureAwait(false);
            }

            var reconciliation = await DiscoverAsync(transport,
                CancellationToken.None).ConfigureAwait(false);
            if (!reconciliation.Complete)
            {
                result.Fail(pending.Count,
                    InlineFailureKind.ReadbackIncomplete);
                return result.Build(BoundedGitHubPublisherOutcome
                    .OutcomeUnknown);
            }

            var reconciled = Reconcile(request, pending,
                reconciliation.ByKey, result,
                InlineSuccessKind.ReconciledPublished);
            if (reconciled == pending.Count)
            {
                return result.Build(
                    BoundedGitHubPublisherOutcome.WrittenAndReadBack);
            }

            var unresolved = pending.Count - reconciled;
            if (batchResult.Outcome ==
                BoundedGitHubHttpOutcome.CancelledBeforeSend)
            {
                result.Fail(unresolved, InlineFailureKind.Cancelled);
                return result.Build(BoundedGitHubPublisherOutcome
                    .CancelledBeforeSend);
            }

            result.Fail(unresolved,
                batchResult.Outcome is BoundedGitHubHttpOutcome.Success or
                    BoundedGitHubHttpOutcome.OutcomeUnknown
                    ? InlineFailureKind.BatchOutcomeUnknown
                    : InlineFailureKind.BatchKnownFailure);
            return result.Build(batchResult.Outcome switch
            {
                BoundedGitHubHttpOutcome.Success or
                    BoundedGitHubHttpOutcome.OutcomeUnknown =>
                    BoundedGitHubPublisherOutcome.OutcomeUnknown,
                BoundedGitHubHttpOutcome.KnownNotSent =>
                    BoundedGitHubPublisherOutcome.KnownNotWritten,
                _ => BoundedGitHubPublisherOutcome
                    .AuthorizationOrValidationFailure,
            });
        }
    }

    private static async Task<InlinePublicationResult> FallbackAsync(
        AuthorizedInlinePublicationRequest request,
        IInlineGitHubPublisherTransport transport,
        List<RenderedInlineComment> pending,
        ResultBuilder result,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            result.Fail(pending.Count, InlineFailureKind.Cancelled);
            return result.Build(BoundedGitHubPublisherOutcome.OutcomeUnknown);
        }

        var relisted = await DiscoverAsync(transport, cancellationToken)
            .ConfigureAwait(false);
        if (!relisted.Complete)
        {
            result.Fail(pending.Count,
                InlineFailureKind.ReadbackIncomplete);
            return result.Build(BoundedGitHubPublisherOutcome
                .OutcomeUnknown);
        }

        var stillAbsent = new List<RenderedInlineComment>(pending.Count);
        foreach (var comment in pending)
        {
            if (relisted.ByKey.TryGetValue(comment.Candidate.InlineKey,
                    out var observed))
            {
                if (!IsExact(request, comment, observed))
                {
                    result.Fail(1, InlineFailureKind.ReadbackIncomplete);
                    continue;
                }

                result.ConcurrentDuplicate++;
            }
            else
            {
                stillAbsent.Add(comment);
            }
        }

        if (result.HasFailures)
        {
            result.Fail(stillAbsent.Count,
                InlineFailureKind.ReadbackIncomplete);
            return result.Build(BoundedGitHubPublisherOutcome
                .OutcomeUnknown);
        }

        if (stillAbsent.Count == 0)
        {
            return result.Build(
                BoundedGitHubPublisherOutcome.WrittenAndReadBack);
        }

        if (stillAbsent.Count != pending.Count)
        {
            result.Fail(stillAbsent.Count,
                InlineFailureKind.ReadbackIncomplete);
            return result.Build(BoundedGitHubPublisherOutcome.OutcomeUnknown);
        }

        var fallbackHead = await RevalidateHeadAsync(
            request, cancellationToken).ConfigureAwait(false);
        if (!IsExactHead(request, fallbackHead))
        {
            var cancelled = IsCancellation(fallbackHead.Status);
            result.Fail(stillAbsent.Count, cancelled
                ? InlineFailureKind.Cancelled
                : InlineFailureKind.HeadNotExact);
            return result.Build(cancelled
                ? BoundedGitHubPublisherOutcome.OutcomeUnknown
                : BoundedGitHubPublisherOutcome
                    .AuthorizationOrValidationFailure);
        }

        for (var index = 0; index < stillAbsent.Count; index++)
        {
            var comment = stillAbsent[index];
            if (cancellationToken.IsCancellationRequested ||
                !transport.IsWithinOverallDeadline)
            {
                result.Fail(stillAbsent.Count - index,
                    InlineFailureKind.Cancelled);
                return result.Build(BoundedGitHubPublisherOutcome
                    .OutcomeUnknown);
            }

            result.IndividualAttempts++;
            var created = await transport.CreateReviewCommentAsync(
                comment.IndividualRequest, cancellationToken)
                .ConfigureAwait(false);
            if (created.Value is null)
            {
                var unknown = created.Outcome is
                        BoundedGitHubHttpOutcome.OutcomeUnknown or
                        BoundedGitHubHttpOutcome.CancelledBeforeSend ||
                    created.Reason == BoundedGitHubPublisherReason.Deadline ||
                    created.ValidationEvidence?.ReviewIdentityReturned == true;
                if (unknown)
                {
                    result.Fail(stillAbsent.Count - index,
                        InlineFailureKind.IndividualOutcomeUnknown);
                    return result.Build(BoundedGitHubPublisherOutcome
                        .OutcomeUnknown);
                }

                result.Fail(1, InlineFailureKind.IndividualKnownFailure);
                continue;
            }

            if (!IsExact(request, comment, created.Value))
            {
                result.Fail(stillAbsent.Count - index,
                    InlineFailureKind.IndividualOutcomeUnknown);
                return result.Build(BoundedGitHubPublisherOutcome
                    .OutcomeUnknown);
            }

            var readback = await transport.GetReviewCommentAsync(
                created.Value.Id, CancellationToken.None).ConfigureAwait(false);
            if (readback.Value is null ||
                !IsExact(request, comment, readback.Value) ||
                readback.Value.Id != created.Value.Id)
            {
                result.Fail(stillAbsent.Count - index,
                    InlineFailureKind.IndividualReadbackIncomplete);
                return result.Build(BoundedGitHubPublisherOutcome
                    .OutcomeUnknown);
            }

            result.IndividualPublished++;
        }

        return result.Build(result.HasFailures
            ? BoundedGitHubPublisherOutcome.AuthorizationOrValidationFailure
            : BoundedGitHubPublisherOutcome.WrittenAndReadBack);
    }

    private static Task<ExactHeadRevalidationResult> RevalidateHeadAsync(
        AuthorizedInlinePublicationRequest request,
        CancellationToken cancellationToken) =>
        request.RevalidateHeadAsync(cancellationToken);

    private static bool IsExactHead(
        AuthorizedInlinePublicationRequest request,
        ExactHeadRevalidationResult exact) =>
        exact.Status == ExactHeadRevalidationStatus.Exact &&
            exact.MayMutate &&
            StringComparer.Ordinal.Equals(exact.FrozenHeadSha,
                request.Authorization.PullRequest.HeadSha) &&
            StringComparer.Ordinal.Equals(exact.ObservedHeadSha,
                request.Authorization.PullRequest.HeadSha);

    private static bool IsCancellation(ExactHeadRevalidationStatus status) =>
        status is ExactHeadRevalidationStatus.Cancelled or
            ExactHeadRevalidationStatus.DeadlineExceeded;

    private static int Reconcile(
        AuthorizedInlinePublicationRequest request,
        IReadOnlyList<RenderedInlineComment> expected,
        IReadOnlyDictionary<string, BoundedGitHubReviewComment> observed,
        ResultBuilder result,
        InlineSuccessKind success)
    {
        var count = 0;
        foreach (var comment in expected)
        {
            if (observed.TryGetValue(comment.Candidate.InlineKey,
                    out var found) && IsExact(request, comment, found))
            {
                count++;
                if (success == InlineSuccessKind.ReconciledPublished)
                {
                    result.ReconciledPublished++;
                }
            }
        }

        return count;
    }

    private static bool IsExact(
        AuthorizedInlinePublicationRequest request,
        RenderedInlineComment expected,
        BoundedGitHubReviewComment observed) =>
        StringComparer.Ordinal.Equals(observed.Body, expected.Body) &&
        StringComparer.Ordinal.Equals(observed.Path,
            expected.Candidate.Path) &&
        observed.Line == expected.Candidate.Line &&
        StringComparer.Ordinal.Equals(observed.Side, "RIGHT") &&
        StringComparer.Ordinal.Equals(observed.CommitId,
            request.Authorization.PullRequest.HeadSha);

    private static async Task<Discovery> DiscoverAsync(
        IInlineGitHubPublisherTransport transport,
        CancellationToken cancellationToken)
    {
        var seenIds = new HashSet<long>();
        var byKey = new Dictionary<string, BoundedGitHubReviewComment>(
            StringComparer.Ordinal);
        var total = 0;
        int? expectedLast = null;
        for (var page = 1;
            page <= BoundedGitHubPublisherPolicy.MaximumPages;
            page++)
        {
            var response = await transport.ListReviewCommentsAsync(
                page, cancellationToken).ConfigureAwait(false);
            if (response.Value is null)
            {
                return new(false,
                    response.Outcome ==
                        BoundedGitHubHttpOutcome.CancelledBeforeSend,
                    byKey);
            }

            if (!transport.IsWithinOverallDeadline ||
                cancellationToken.IsCancellationRequested)
            {
                return new(false,
                    cancellationToken.IsCancellationRequested, byKey);
            }

            if (response.Value.LastPage is int last)
            {
                if (expectedLast is int prior && prior != last ||
                    response.Value.NextPage is int next && next > last)
                {
                    return new(false, false, byKey);
                }

                expectedLast = last;
            }

            total = checked(total + response.Value.Comments.Count);
            if (total > BoundedGitHubPublisherPolicy.MaximumRecords)
            {
                return new(false, false, byKey);
            }

            foreach (var comment in response.Value.Comments)
            {
                if (!seenIds.Add(comment.Id))
                {
                    return new(false, false, byKey);
                }

                var marker = InlineCommentMarker.Inspect(comment.Body);
                if (marker.Kind == InlineMarkerInspectionKind.Invalid ||
                    marker.Kind == InlineMarkerInspectionKind.Valid &&
                    !byKey.TryAdd(marker.InlineKey!, comment))
                {
                    return new(false, false, byKey);
                }
            }

            if (!transport.IsWithinOverallDeadline ||
                cancellationToken.IsCancellationRequested)
            {
                return new(false,
                    cancellationToken.IsCancellationRequested, byKey);
            }

            if (response.Value.NextPage is null)
            {
                return new(expectedLast is null || expectedLast == page,
                    false, byKey);
            }

            if (response.Value.NextPage != page + 1 ||
                expectedLast is int known && page + 1 > known)
            {
                return new(false, false, byKey);
            }
        }

        return new(false, false, byKey);
    }

    private static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;

    private sealed record Discovery(bool Complete, bool Cancelled,
        IReadOnlyDictionary<string, BoundedGitHubReviewComment> ByKey);

    private enum InlineSuccessKind { ReconciledPublished = 1 }

    private enum InlineFailureKind
    {
        AuthorityRejected = 1,
        InvalidCandidate,
        ListingIncomplete,
        HeadNotExact,
        BatchKnownFailure,
        BatchOutcomeUnknown,
        ReadbackIncomplete,
        FallbackExcluded,
        IndividualKnownFailure,
        IndividualOutcomeUnknown,
        IndividualReadbackIncomplete,
        Cancelled,
    }

    private sealed class ResultBuilder(int candidateCount)
    {
        internal int ExistingDuplicate { get; set; }
        internal int ConcurrentDuplicate { get; set; }
        internal int ReconciledPublished { get; set; }
        internal int IndividualPublished { get; set; }
        internal int BatchAttempts { get; set; }
        internal int IndividualAttempts { get; set; }
        internal bool HasFailures => FailureTotal > 0;

        private int AuthorityRejected { get; set; }
        private int InvalidCandidate { get; set; }
        private int ListingIncomplete { get; set; }
        private int HeadNotExact { get; set; }
        private int BatchKnownFailure { get; set; }
        private int BatchOutcomeUnknown { get; set; }
        private int ReadbackIncomplete { get; set; }
        private int FallbackExcluded { get; set; }
        private int IndividualKnownFailure { get; set; }
        private int IndividualOutcomeUnknown { get; set; }
        private int IndividualReadbackIncomplete { get; set; }
        private int Cancelled { get; set; }

        private int FailureTotal => AuthorityRejected + InvalidCandidate +
            ListingIncomplete + HeadNotExact + BatchKnownFailure +
            BatchOutcomeUnknown + ReadbackIncomplete + FallbackExcluded +
            IndividualKnownFailure + IndividualOutcomeUnknown +
            IndividualReadbackIncomplete + Cancelled;

        internal void FailAll(InlineFailureKind kind) =>
            Fail(candidateCount, kind);

        internal void Fail(int count, InlineFailureKind kind)
        {
            switch (kind)
            {
                case InlineFailureKind.AuthorityRejected:
                    AuthorityRejected += count;
                    break;
                case InlineFailureKind.InvalidCandidate:
                    InvalidCandidate += count;
                    break;
                case InlineFailureKind.ListingIncomplete:
                    ListingIncomplete += count;
                    break;
                case InlineFailureKind.HeadNotExact:
                    HeadNotExact += count;
                    break;
                case InlineFailureKind.BatchKnownFailure:
                    BatchKnownFailure += count;
                    break;
                case InlineFailureKind.BatchOutcomeUnknown:
                    BatchOutcomeUnknown += count;
                    break;
                case InlineFailureKind.ReadbackIncomplete:
                    ReadbackIncomplete += count;
                    break;
                case InlineFailureKind.FallbackExcluded:
                    FallbackExcluded += count;
                    break;
                case InlineFailureKind.IndividualKnownFailure:
                    IndividualKnownFailure += count;
                    break;
                case InlineFailureKind.IndividualOutcomeUnknown:
                    IndividualOutcomeUnknown += count;
                    break;
                case InlineFailureKind.IndividualReadbackIncomplete:
                    IndividualReadbackIncomplete += count;
                    break;
                case InlineFailureKind.Cancelled:
                    Cancelled += count;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        internal InlinePublicationResult Build(
            BoundedGitHubPublisherOutcome outcome) => new(
                outcome,
                candidateCount,
                BatchAttempts,
                IndividualAttempts,
                new InlinePublicationReasonCounts(
                    ExistingDuplicate,
                    ConcurrentDuplicate,
                    0,
                    ReconciledPublished,
                    IndividualPublished,
                    AuthorityRejected,
                    InvalidCandidate,
                    ListingIncomplete,
                    HeadNotExact,
                    BatchKnownFailure,
                    BatchOutcomeUnknown,
                    ReadbackIncomplete,
                    FallbackExcluded,
                    IndividualKnownFailure,
                    IndividualOutcomeUnknown,
                    IndividualReadbackIncomplete,
                    Cancelled));
    }
}
