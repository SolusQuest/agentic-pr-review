using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;

namespace AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;

internal sealed class StickyCommentPublisher
{
    private readonly IStickyGitHubPublisherTransportFactory _factory;

    internal StickyCommentPublisher(
        IStickyGitHubPublisherTransportFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    internal async Task<StickyPublicationResult> PublishAsync(
        ActionHostGitHubToken token,
        AuthorizedStickyPublicationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
            return Failure(BoundedGitHubPublisherOutcome.CancelledBeforeSend,
                StickyPublicationReason.Cancelled);
        IStickyGitHubPublisherTransport transport;
        try { transport = _factory.Create(token, request); }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException)
        {
            return Failure(
                BoundedGitHubPublisherOutcome.AuthorizationOrValidationFailure,
                StickyPublicationReason.AdmissionInvalid);
        }
        using (transport)
        {
            var discovery = await DiscoverCoreAsync(transport, request,
                cancellationToken);
            if (discovery.Kind == StickyDiscoveryKind.Cancelled)
                return Failure(
                    BoundedGitHubPublisherOutcome.CancelledBeforeSend,
                    StickyPublicationReason.Cancelled);
            if (discovery.Kind == StickyDiscoveryKind.InvalidOrIncomplete)
                return Failure(
                    BoundedGitHubPublisherOutcome
                        .AuthorizationOrValidationFailure,
                    discovery.Reason);
            if (discovery.Kind == StickyDiscoveryKind.Deadline)
                return Failure(BoundedGitHubPublisherOutcome.KnownNotWritten,
                    StickyPublicationReason.Deadline);
            if (cancellationToken.IsCancellationRequested)
                return Failure(
                    BoundedGitHubPublisherOutcome.CancelledBeforeSend,
                    StickyPublicationReason.Cancelled);

            var operation = discovery.Kind == StickyDiscoveryKind.Absent
                ? StickyPublicationOperation.Create
                : StickyPublicationOperation.Update;
            var mutation = await transport.MutateStickyCommentAsync(
                cancellationToken);
            if (mutation.Outcome == BoundedGitHubHttpOutcome
                .AuthorizationOrValidationFailure)
                return Failure(
                    BoundedGitHubPublisherOutcome
                        .AuthorizationOrValidationFailure,
                    StickyPublicationReason.AuthorizationDenied);
            if (mutation.Outcome ==
                BoundedGitHubHttpOutcome.CancelledBeforeSend)
                return Failure(
                    BoundedGitHubPublisherOutcome.CancelledBeforeSend,
                    StickyPublicationReason.Cancelled);
            if (mutation.Outcome == BoundedGitHubHttpOutcome.KnownNotSent)
                return Failure(BoundedGitHubPublisherOutcome.KnownNotWritten,
                    mutation.Reason == BoundedGitHubPublisherReason.Deadline
                        ? StickyPublicationReason.Deadline
                        : StickyPublicationReason.RequestInvalid);

            var expectedId = operation == StickyPublicationOperation.Update
                ? discovery.CommentId : mutation.Value?.Id;
            if (mutation.Value is not null &&
                IsExact(mutation.Value, request, expectedId))
            {
                var verified = await VerifyAsync(transport, request,
                    expectedId!.Value);
                if (verified is not null &&
                    transport.IsWithinOverallDeadline)
                    return Written(request, operation, verified);
            }

            var reconciled = await ReconcileAsync(transport, request,
                expectedId);
            return reconciled is null
                ? Failure(BoundedGitHubPublisherOutcome.OutcomeUnknown,
                    transport.IsWithinOverallDeadline
                        ? StickyPublicationReason.ReconciliationIncomplete
                        : StickyPublicationReason.Deadline)
                : transport.IsWithinOverallDeadline
                    ? Written(request, operation, reconciled)
                    : Failure(BoundedGitHubPublisherOutcome.OutcomeUnknown,
                        StickyPublicationReason.Deadline);
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
            using var transport = _factory.Create(token, request);
            var result = await DiscoverCoreAsync(transport, request,
                cancellationToken);
            if (result.Kind != StickyDiscoveryKind.ExactTarget) return result;
            var verified = await VerifyAsync(transport, request,
                result.CommentId!.Value);
            return verified is null || !transport.IsWithinOverallDeadline
                ? transport.IsWithinOverallDeadline
                    ? Discovery(StickyDiscoveryKind.InvalidOrIncomplete, null,
                        null, null,
                        StickyPublicationReason.DiscoveryIncomplete)
                    : Discovery(StickyDiscoveryKind.Deadline, null, null, null,
                        StickyPublicationReason.Deadline)
                : Discovery(StickyDiscoveryKind.ExactTarget,
                    result.CommentId, result.CommentUrl,
                    Receipt(request, StickyPublicationOperation.Observed,
                        verified),
                    StickyPublicationReason.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException)
        {
            return Discovery(StickyDiscoveryKind.InvalidOrIncomplete, null,
                null, null, StickyPublicationReason.AdmissionInvalid);
        }
    }

    private static async Task<StickyDiscoveryResult> DiscoverCoreAsync(
        IBoundedGitHubPublisherTransport transport,
        AuthorizedStickyPublicationRequest request,
        CancellationToken cancellationToken)
    {
        if (!transport.IsWithinOverallDeadline)
            return Discovery(StickyDiscoveryKind.Deadline, null, null, null,
                StickyPublicationReason.Deadline);
        var seen = new HashSet<long>();
        BoundedGitHubIssueComment? target = null;
        var total = 0;
        int? expectedLastPage = null;
        for (var page = 1; page <= BoundedGitHubPublisherPolicy.MaximumPages;
            page++)
        {
            var result = await transport.ListIssueCommentsAsync(page,
                cancellationToken);
            if (result.Value is null)
                return Discovery(result.Outcome ==
                        BoundedGitHubHttpOutcome.CancelledBeforeSend
                            ? StickyDiscoveryKind.Cancelled
                            : result.Reason ==
                                BoundedGitHubPublisherReason.Deadline
                                ? StickyDiscoveryKind.Deadline
                            : StickyDiscoveryKind.InvalidOrIncomplete,
                    null, null, null,
                    result.Outcome ==
                        BoundedGitHubHttpOutcome.CancelledBeforeSend
                            ? StickyPublicationReason.Cancelled
                            : result.Reason ==
                                BoundedGitHubPublisherReason.Deadline
                                ? StickyPublicationReason.Deadline
                            : StickyPublicationReason.DiscoveryIncomplete);
            if (!transport.IsWithinOverallDeadline)
                return Discovery(StickyDiscoveryKind.Deadline, null, null,
                    null, StickyPublicationReason.Deadline);
            if (result.Value.LastPage is int lastPage)
            {
                if (expectedLastPage is int expected &&
                        expected != lastPage ||
                    result.Value.NextPage is int next && next > lastPage)
                    return Invalid(
                        StickyPublicationReason.DiscoveryIncomplete);
                expectedLastPage = lastPage;
            }
            total = checked(total + result.Value.Comments.Count);
            if (total > BoundedGitHubPublisherPolicy.MaximumRecords)
                return Invalid(StickyPublicationReason.DiscoveryIncomplete);
            foreach (var comment in result.Value.Comments)
            {
                if (!seen.Add(comment.Id))
                    return Invalid(StickyPublicationReason.TargetConflict);
                if (!TryInspect(comment.Body, out var inspection) ||
                    inspection.Kind == R4StickyInspectionKind.InvalidR4)
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
                if (!transport.IsWithinOverallDeadline)
                    return Discovery(StickyDiscoveryKind.Deadline, null, null,
                        null, StickyPublicationReason.Deadline);
            }
            if (result.Value.NextPage is null)
            {
                if (expectedLastPage is int expected && expected != page)
                    return Invalid(
                        StickyPublicationReason.DiscoveryIncomplete);
                if (target is null)
                    return Discovery(StickyDiscoveryKind.Absent, null, null,
                        null, StickyPublicationReason.None);
                return Discovery(IsExact(target, request, target.Id)
                        ? StickyDiscoveryKind.ExactTarget
                        : StickyDiscoveryKind.StaleTarget,
                    target.Id, target.HtmlUrl, null,
                    StickyPublicationReason.None);
            }
            if (result.Value.NextPage != page + 1 ||
                expectedLastPage is int last && page + 1 > last ||
                page == BoundedGitHubPublisherPolicy.MaximumPages ||
                total == BoundedGitHubPublisherPolicy.MaximumRecords)
                return Invalid(StickyPublicationReason.DiscoveryIncomplete);
        }
        return Invalid(StickyPublicationReason.DiscoveryIncomplete);
    }

    private static async Task<ReadbackEvidence?> VerifyAsync(
        IBoundedGitHubPublisherTransport transport,
        AuthorizedStickyPublicationRequest request,
        long id)
    {
        var read = await transport.GetIssueCommentAsync(id,
            CancellationToken.None);
        if (read.Value is null || !transport.IsWithinOverallDeadline ||
            !IsExact(read.Value, request, id)) return null;
        var discovery = await DiscoverCoreAsync(transport, request,
            CancellationToken.None);
        return transport.IsWithinOverallDeadline &&
            discovery.Kind == StickyDiscoveryKind.ExactTarget &&
            discovery.CommentId == id ? new(read.Value) : null;
    }

    private static async Task<ReadbackEvidence?> ReconcileAsync(
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
        var read = await transport.GetIssueCommentAsync(id,
            CancellationToken.None);
        return read.Value is not null && transport.IsWithinOverallDeadline &&
            IsExact(read.Value, request, id)
            ? new(read.Value)
            : null;
    }

    private static bool IsExact(BoundedGitHubIssueComment comment,
        AuthorizedStickyPublicationRequest request, long? expectedId)
    {
        if (expectedId is not null && comment.Id != expectedId ||
            !StringComparer.Ordinal.Equals(comment.Body,
                request.Rendered.Comment) ||
            !TryInspect(comment.Body, out var inspected)) return false;
        return inspected.Kind == R4StickyInspectionKind.ValidR4 &&
            Equals(inspected.Identity, request.Rendered.Identity) &&
            StringComparer.Ordinal.Equals(inspected.Body,
                request.Rendered.Body);
    }

    private static bool TryInspect(string body,
        out R4StickyInspection inspection)
    {
        try
        {
            inspection = R4StickyMarker.Inspect(body);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException)
        {
            inspection = new(R4StickyInspectionKind.InvalidR4, null, null,
                R4StickyInvalidReason.BodyDigestMismatch);
            return false;
        }
    }

    private static StickyPublicationReceipt Receipt(
        AuthorizedStickyPublicationRequest request,
        StickyPublicationOperation operation,
        ReadbackEvidence evidence) =>
        StickyPublicationReceipt.FromReadback(request, operation, evidence);

    private static StickyPublicationResult Written(
        AuthorizedStickyPublicationRequest request,
        StickyPublicationOperation operation,
        ReadbackEvidence evidence) => StickyPublicationResult.FromReadback(
            Receipt(request, operation, evidence), evidence);

    private static StickyPublicationResult Failure(
        BoundedGitHubPublisherOutcome outcome,
        StickyPublicationReason reason)
    {
        var valid = outcome switch
        {
            BoundedGitHubPublisherOutcome.CancelledBeforeSend =>
                reason == StickyPublicationReason.Cancelled,
            BoundedGitHubPublisherOutcome.KnownNotWritten =>
                reason is StickyPublicationReason.RequestInvalid or
                    StickyPublicationReason.Deadline,
            BoundedGitHubPublisherOutcome.OutcomeUnknown =>
                reason is StickyPublicationReason.ReconciliationIncomplete or
                    StickyPublicationReason.Deadline,
            BoundedGitHubPublisherOutcome.AuthorizationOrValidationFailure =>
                reason is StickyPublicationReason.AdmissionInvalid or
                    StickyPublicationReason.DiscoveryIncomplete or
                    StickyPublicationReason.TargetConflict or
                    StickyPublicationReason.AuthorizationDenied,
            _ => false,
        };
        if (!valid) throw new InvalidOperationException(
            "Invalid sticky publication outcome and reason pairing.");
        return StickyPublicationResult.FromFailure(
            new FailureEvidence(outcome, reason));
    }

    private static StickyDiscoveryResult Invalid(
        StickyPublicationReason reason) => Discovery(
            StickyDiscoveryKind.InvalidOrIncomplete, null, null, null, reason);

    private static StickyDiscoveryResult Discovery(StickyDiscoveryKind kind,
        long? commentId, string? commentUrl,
        StickyPublicationReceipt? receipt,
        StickyPublicationReason reason) => StickyDiscoveryResult.FromEvidence(
            new DiscoveryEvidence(kind, commentId, commentUrl, receipt,
                reason));

    private static T RequireIssuer<T>(
        IStickyPublicationIssuerEvidence evidence)
        where T : class, IStickyPublicationIssuerEvidence
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.GetType() != typeof(T))
            throw new InvalidOperationException(
                "The sticky publication issuer evidence is invalid.");
        return (T)evidence;
    }

    private sealed record ReadbackEvidence(BoundedGitHubIssueComment Comment) :
        IStickyPublicationIssuerEvidence;
    private sealed record FailureEvidence(BoundedGitHubPublisherOutcome Outcome,
        StickyPublicationReason Reason) : IStickyPublicationIssuerEvidence;
    private sealed record DiscoveryEvidence(StickyDiscoveryKind Kind,
        long? CommentId, string? CommentUrl,
        StickyPublicationReceipt? Receipt, StickyPublicationReason Reason) :
        IStickyPublicationIssuerEvidence;

    internal sealed class StickyPublicationReceipt
    {
        private StickyPublicationReceipt(StickyPublicationOperation operation,
            long repositoryId, long pullRequestNumber, long commentId,
            string commentUrl, string scopeSha256, string bodySha256,
            string headSha) =>
            (Operation, RepositoryId, PullRequestNumber, CommentId, CommentUrl,
                ScopeSha256, BodySha256, HeadSha) =
            (operation, repositoryId, pullRequestNumber, commentId, commentUrl,
                scopeSha256, bodySha256, headSha);

        internal StickyPublicationOperation Operation { get; }
        internal long RepositoryId { get; }
        internal long PullRequestNumber { get; }
        internal long CommentId { get; }
        internal string CommentUrl { get; }
        internal string ScopeSha256 { get; }
        internal string BodySha256 { get; }
        internal string HeadSha { get; }

        internal static StickyPublicationReceipt FromReadback(
            AuthorizedStickyPublicationRequest request,
            StickyPublicationOperation operation,
            IStickyPublicationIssuerEvidence issuer)
        {
            var evidence = RequireIssuer<ReadbackEvidence>(issuer);
            return new(operation,
                request.Authorization.PullRequest.RepositoryId,
                request.Authorization.PullRequest.Number,
                evidence.Comment.Id, evidence.Comment.HtmlUrl,
                request.Rendered.Identity.ScopeSha256,
                request.Rendered.Identity.BodySha256,
                request.Rendered.Identity.HeadSha);
        }

        internal static bool TryRehydrate(
            StickyPublicationOperation operation,
            long repositoryId,
            long pullRequestNumber,
            long commentId,
            string? commentUrl,
            string? scopeSha256,
            string? bodySha256,
            string? headSha,
            out StickyPublicationReceipt? receipt)
        {
            receipt = null;
            if (!Enum.IsDefined(operation) || repositoryId <= 0 ||
                pullRequestNumber <= 0 || commentId <= 0 ||
                !IsLowerHex(scopeSha256, 64) ||
                !IsLowerHex(bodySha256, 64) ||
                !IsLowerHex(headSha, 40) ||
                !IsCanonicalCommentUrl(commentUrl, pullRequestNumber,
                    commentId)) return false;
            receipt = new(operation, repositoryId, pullRequestNumber,
                commentId, commentUrl!, scopeSha256!, bodySha256!, headSha!);
            return true;
        }

        private static bool IsCanonicalCommentUrl(string? value,
            long pullRequestNumber, long commentId)
        {
            if (value is null ||
                !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !StringComparer.OrdinalIgnoreCase.Equals(uri.Host,
                    "github.com") ||
                !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.Query))
                return false;
            var segments = uri.AbsolutePath.Split('/',
                StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 4 || segments[2] != "pull" ||
                segments[3] != pullRequestNumber.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) ||
                !IsRepositorySegment(segments[0]) ||
                !IsRepositorySegment(segments[1])) return false;
            var canonical = $"https://github.com/{segments[0]}/" +
                $"{segments[1]}/pull/{pullRequestNumber}" +
                $"#issuecomment-{commentId}";
            return StringComparer.Ordinal.Equals(value, canonical);
        }

        private static bool IsRepositorySegment(string value) =>
            value.Length is > 0 and <= 100 &&
            value.All(static character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.');

        private static bool IsLowerHex(string? value, int length) =>
            value is not null && value.Length == length &&
            value.All(static character => character is >= '0' and <= '9' or
                >= 'a' and <= 'f');

        public override string ToString() => "[PRIVATE]";
    }

    internal sealed class StickyPublicationResult
    {
        private StickyPublicationResult(BoundedGitHubPublisherOutcome outcome,
            StickyPublicationReason reason,
            StickyPublicationReceipt? receipt) =>
            (Outcome, Reason, Receipt) = (outcome, reason, receipt);

        internal BoundedGitHubPublisherOutcome Outcome { get; }
        internal StickyPublicationReason Reason { get; }
        internal StickyPublicationReceipt? Receipt { get; }

        internal static StickyPublicationResult FromReadback(
            StickyPublicationReceipt receipt,
            IStickyPublicationIssuerEvidence issuer)
        {
            _ = RequireIssuer<ReadbackEvidence>(issuer);
            return new(BoundedGitHubPublisherOutcome.WrittenAndReadBack,
                StickyPublicationReason.None, receipt);
        }

        internal static StickyPublicationResult FromFailure(
            IStickyPublicationIssuerEvidence issuer)
        {
            var evidence = RequireIssuer<FailureEvidence>(issuer);
            return new(evidence.Outcome, evidence.Reason, null);
        }
    }

    internal sealed class StickyDiscoveryResult
    {
        private StickyDiscoveryResult(StickyDiscoveryKind kind,
            long? commentId, string? commentUrl,
            StickyPublicationReceipt? receipt,
            StickyPublicationReason reason) =>
            (Kind, CommentId, CommentUrl, Receipt, Reason) =
            (kind, commentId, commentUrl, receipt, reason);

        internal StickyDiscoveryKind Kind { get; }
        internal long? CommentId { get; }
        internal string? CommentUrl { get; }
        internal StickyPublicationReceipt? Receipt { get; }
        internal StickyPublicationReason Reason { get; }

        internal static StickyDiscoveryResult FromEvidence(
            IStickyPublicationIssuerEvidence issuer)
        {
            var evidence = RequireIssuer<DiscoveryEvidence>(issuer);
            return new(evidence.Kind, evidence.CommentId, evidence.CommentUrl,
                evidence.Receipt, evidence.Reason);
        }
    }
}
