using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;

namespace AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;

internal sealed class StickyCommentPublisher
{
    private readonly IBoundedGitHubPublisherTransportFactory _factory;

    internal StickyCommentPublisher(
        IBoundedGitHubPublisherTransportFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    internal async Task<StickyPublicationResult> PublishAsync(
        ActionHostGitHubToken token,
        AuthorizedStickyPublicationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
            return Failure(StickyPublicationOutcome.CancelledBeforeSend,
                StickyPublicationReason.Cancelled);
        IBoundedGitHubPublisherTransport transport;
        try { transport = _factory.Create(token, request.Authorization); }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException)
        {
            return Failure(
                StickyPublicationOutcome.AuthorizationOrValidationFailure,
                StickyPublicationReason.AdmissionInvalid);
        }
        using (transport)
        {
            var discovery = await DiscoverCoreAsync(transport, request,
                cancellationToken);
            if (discovery.Kind == StickyDiscoveryKind.Cancelled)
                return Failure(StickyPublicationOutcome.CancelledBeforeSend,
                    StickyPublicationReason.Cancelled);
            if (discovery.Kind == StickyDiscoveryKind.InvalidOrIncomplete)
                return Failure(
                    StickyPublicationOutcome.AuthorizationOrValidationFailure,
                    discovery.Reason);
            if (!StickyCommentSerializer.TrySerialize(
                    request.Rendered.Comment, out var body))
                return Failure(StickyPublicationOutcome.KnownNotWritten,
                    StickyPublicationReason.RequestInvalid);
            if (cancellationToken.IsCancellationRequested)
                return Failure(StickyPublicationOutcome.CancelledBeforeSend,
                    StickyPublicationReason.Cancelled);

            var operation = discovery.Kind == StickyDiscoveryKind.Absent
                ? StickyPublicationOperation.Create
                : StickyPublicationOperation.Update;
            var mutation = operation == StickyPublicationOperation.Create
                ? await transport.CreateIssueCommentAsync(body!,
                    cancellationToken)
                : await transport.UpdateIssueCommentAsync(
                    discovery.CommentId!.Value, body!, cancellationToken);
            if (mutation.Failure ==
                BoundedGitHubPublisherFailure.AuthorizationOrValidationFailure)
                return Failure(
                    StickyPublicationOutcome.AuthorizationOrValidationFailure,
                    StickyPublicationReason.AuthorizationDenied);
            if (mutation.Failure ==
                BoundedGitHubPublisherFailure.CancelledBeforeSend)
                return Failure(StickyPublicationOutcome.CancelledBeforeSend,
                    StickyPublicationReason.Cancelled);
            if (mutation.Failure ==
                BoundedGitHubPublisherFailure.Unavailable)
                return Failure(StickyPublicationOutcome.KnownNotWritten,
                    StickyPublicationReason.RequestInvalid);

            var expectedId = operation == StickyPublicationOperation.Update
                ? discovery.CommentId : mutation.Value?.Id;
            if (mutation.Value is not null &&
                IsExact(mutation.Value, request, expectedId))
            {
                var verified = await VerifyAsync(transport, request,
                    expectedId!.Value);
                if (verified is not null)
                    return StickyPublicationResult.Written(
                        Receipt(request, operation, verified));
            }

            var reconciled = await ReconcileAsync(transport, request,
                expectedId);
            return reconciled is null
                ? Failure(StickyPublicationOutcome.OutcomeUnknown,
                    StickyPublicationReason.ReconciliationIncomplete)
                : StickyPublicationResult.Written(
                    Receipt(request, operation, reconciled));
        }
    }

    internal async Task<StickyDiscoveryResult> DiscoverAsync(
        ActionHostGitHubToken token,
        AuthorizedStickyPublicationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            using var transport = _factory.Create(token, request.Authorization);
            var result = await DiscoverCoreAsync(transport, request,
                cancellationToken);
            if (result.Kind != StickyDiscoveryKind.ExactTarget) return result;
            var verified = await VerifyAsync(transport, request,
                result.CommentId!.Value);
            return verified is null
                ? new(StickyDiscoveryKind.InvalidOrIncomplete, null, null, null,
                    StickyPublicationReason.DiscoveryIncomplete)
                : result with
                {
                    Receipt = Receipt(request,
                        StickyPublicationOperation.Observed, verified),
                };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException)
        {
            return new(StickyDiscoveryKind.InvalidOrIncomplete, null, null,
                null, StickyPublicationReason.AdmissionInvalid);
        }
    }

    private static async Task<StickyDiscoveryResult> DiscoverCoreAsync(
        IBoundedGitHubPublisherTransport transport,
        AuthorizedStickyPublicationRequest request,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<long>();
        BoundedGitHubIssueComment? target = null;
        var total = 0;
        for (var page = 1; page <= BoundedGitHubPublisherPolicy.MaximumPages;
            page++)
        {
            var result = await transport.ListIssueCommentsAsync(page,
                cancellationToken);
            if (result.Value is null)
                return new(result.Failure ==
                        BoundedGitHubPublisherFailure.CancelledBeforeSend
                            ? StickyDiscoveryKind.Cancelled
                            : StickyDiscoveryKind.InvalidOrIncomplete,
                    null, null, null,
                    result.Failure ==
                        BoundedGitHubPublisherFailure.CancelledBeforeSend
                            ? StickyPublicationReason.Cancelled
                            : StickyPublicationReason.DiscoveryIncomplete);
            total = checked(total + result.Value.Comments.Count);
            if (total > BoundedGitHubPublisherPolicy.MaximumRecords)
                return Invalid(StickyPublicationReason.DiscoveryIncomplete);
            foreach (var comment in result.Value.Comments)
            {
                if (!seen.Add(comment.Id))
                    return Invalid(StickyPublicationReason.TargetConflict);
                var inspection = R4StickyMarker.Inspect(comment.Body);
                if (inspection.Kind == R4StickyInspectionKind.InvalidR4)
                    return Invalid(StickyPublicationReason.TargetConflict);
                if (inspection.Kind == R4StickyInspectionKind.ValidR4 &&
                    StringComparer.Ordinal.Equals(
                        inspection.Identity!.ScopeSha256,
                        request.Rendered.Identity.ScopeSha256))
                {
                    if (target is not null)
                        return Invalid(StickyPublicationReason.TargetConflict);
                    target = comment;
                }
            }
            if (result.Value.NextPage is null)
            {
                if (target is null)
                    return new(StickyDiscoveryKind.Absent, null, null, null,
                        StickyPublicationReason.None);
                return new(IsExact(target, request, target.Id)
                        ? StickyDiscoveryKind.ExactTarget
                        : StickyDiscoveryKind.StaleTarget,
                    target.Id, target.HtmlUrl, null,
                    StickyPublicationReason.None);
            }
            if (result.Value.NextPage != page + 1 ||
                page == BoundedGitHubPublisherPolicy.MaximumPages ||
                total == BoundedGitHubPublisherPolicy.MaximumRecords)
                return Invalid(StickyPublicationReason.DiscoveryIncomplete);
        }
        return Invalid(StickyPublicationReason.DiscoveryIncomplete);
    }

    private static async Task<BoundedGitHubIssueComment?> VerifyAsync(
        IBoundedGitHubPublisherTransport transport,
        AuthorizedStickyPublicationRequest request,
        long id)
    {
        var read = await transport.GetIssueCommentAsync(id,
            CancellationToken.None);
        if (read.Value is null || !IsExact(read.Value, request, id)) return null;
        var discovery = await DiscoverCoreAsync(transport, request,
            CancellationToken.None);
        return discovery.Kind == StickyDiscoveryKind.ExactTarget &&
            discovery.CommentId == id ? read.Value : null;
    }

    private static async Task<BoundedGitHubIssueComment?> ReconcileAsync(
        IBoundedGitHubPublisherTransport transport,
        AuthorizedStickyPublicationRequest request,
        long? expectedId)
    {
        var discovery = await DiscoverCoreAsync(transport, request,
            CancellationToken.None);
        if (discovery is not
                { Kind: StickyDiscoveryKind.ExactTarget, CommentId: long id } ||
            expectedId is not null && id != expectedId)
            return null;
        var read = await transport.GetIssueCommentAsync(
            id, CancellationToken.None);
        return read.Value is not null &&
            IsExact(read.Value, request, id)
                ? read.Value
                : null;
    }

    private static bool IsExact(BoundedGitHubIssueComment comment,
        AuthorizedStickyPublicationRequest request, long? expectedId)
    {
        if (expectedId is not null && comment.Id != expectedId ||
            !StringComparer.Ordinal.Equals(comment.Body,
                request.Rendered.Comment)) return false;
        var inspected = R4StickyMarker.Inspect(comment.Body);
        return inspected.Kind == R4StickyInspectionKind.ValidR4 &&
            Equals(inspected.Identity, request.Rendered.Identity) &&
            StringComparer.Ordinal.Equals(inspected.Body,
                request.Rendered.Body);
    }

    private static StickyPublicationReceipt Receipt(
        AuthorizedStickyPublicationRequest request,
        StickyPublicationOperation operation,
        BoundedGitHubIssueComment comment) => new(operation,
            request.Authorization.PullRequest.RepositoryId,
            request.Authorization.PullRequest.Number,
            comment.Id, comment.HtmlUrl,
            request.Rendered.Identity.ScopeSha256,
            request.Rendered.Identity.BodySha256,
            request.Rendered.Identity.HeadSha);

    private static StickyDiscoveryResult Invalid(
        StickyPublicationReason reason) => new(
            StickyDiscoveryKind.InvalidOrIncomplete, null, null, null, reason);

    private static StickyPublicationResult Failure(
        StickyPublicationOutcome outcome, StickyPublicationReason reason) =>
        StickyPublicationResult.Failed(outcome, reason);
}
