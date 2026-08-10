using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;

namespace AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;

internal sealed class AuthorizedStickyPublicationRequest
{
    private AuthorizedStickyPublicationRequest(
        ActionHostAuthorizer.AuthorizedInvocation authorization,
        R4PublicationScopeV1 scope,
        R4RenderedStickyComment rendered) =>
        (Authorization, Scope, Rendered) = (authorization, scope, rendered);

    internal ActionHostAuthorizer.AuthorizedInvocation Authorization { get; }
    internal R4PublicationScopeV1 Scope { get; }
    internal R4RenderedStickyComment Rendered { get; }

    internal static bool TryCreate(
        ActionHostAuthorizer.AuthorizedInvocation? authorization,
        R4PublicationScopeV1? scope,
        R4RenderedStickyComment? rendered,
        out AuthorizedStickyPublicationRequest? request)
    {
        request = null;
        if (authorization is null || scope is null || rendered is null ||
            !R4PublicationIdentityV1.IsValidScope(scope) ||
            scope.RepositoryId > long.MaxValue ||
            (long)scope.RepositoryId != authorization.PullRequest.RepositoryId ||
            scope.WorkflowSourceRepositoryId > long.MaxValue ||
            (long)scope.WorkflowSourceRepositoryId !=
                authorization.PullRequest.RepositoryId ||
            scope.PullRequestNumber > long.MaxValue ||
            (long)scope.PullRequestNumber != authorization.PullRequest.Number ||
            !StringComparer.Ordinal.Equals(scope.WorkflowPath,
                authorization.WorkflowPath) ||
            !StringComparer.Ordinal.Equals(
                R4PublicationIdentityV1.ComputeScopeSha256(scope),
                rendered.Identity.ScopeSha256) ||
            !StringComparer.Ordinal.Equals(rendered.Identity.HeadSha,
                authorization.PullRequest.HeadSha)) return false;
        var inspected = R4StickyMarker.Inspect(rendered.Comment);
        if (inspected.Kind != R4StickyInspectionKind.ValidR4 ||
            !StringComparer.Ordinal.Equals(inspected.Body, rendered.Body) ||
            !Equals(inspected.Identity, rendered.Identity)) return false;
        request = new(authorization, scope, rendered);
        return true;
    }
}

internal enum StickyPublicationOutcome
{
    CancelledBeforeSend = 1,
    KnownNotWritten,
    WrittenAndReadBack,
    OutcomeUnknown,
    AuthorizationOrValidationFailure,
}

internal enum StickyPublicationReason
{
    None = 0,
    Cancelled,
    AdmissionInvalid,
    DiscoveryIncomplete,
    TargetConflict,
    RequestInvalid,
    AuthorizationDenied,
    ReconciliationIncomplete,
}

internal enum StickyPublicationOperation { Create = 1, Update, Observed }

internal sealed record StickyPublicationReceipt(
    StickyPublicationOperation Operation,
    long RepositoryId,
    long PullRequestNumber,
    long CommentId,
    string CommentUrl,
    string ScopeSha256,
    string BodySha256,
    string HeadSha)
{
    public override string ToString() => "[PRIVATE]";
}

internal sealed class StickyPublicationResult
{
    private StickyPublicationResult(StickyPublicationOutcome outcome,
        StickyPublicationReason reason, StickyPublicationReceipt? receipt) =>
        (Outcome, Reason, Receipt) = (outcome, reason, receipt);

    internal StickyPublicationOutcome Outcome { get; }
    internal StickyPublicationReason Reason { get; }
    internal StickyPublicationReceipt? Receipt { get; }

    internal static StickyPublicationResult Written(
        StickyPublicationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return new(StickyPublicationOutcome.WrittenAndReadBack,
            StickyPublicationReason.None, receipt);
    }
    internal static StickyPublicationResult Failed(
        StickyPublicationOutcome outcome, StickyPublicationReason reason)
    {
        if (outcome == StickyPublicationOutcome.WrittenAndReadBack ||
            reason == StickyPublicationReason.None)
            throw new ArgumentOutOfRangeException(nameof(outcome));
        return new(outcome, reason, null);
    }
}

internal enum StickyDiscoveryKind
{
    Absent = 1,
    StaleTarget,
    ExactTarget,
    Cancelled,
    InvalidOrIncomplete,
}

internal sealed record StickyDiscoveryResult(
    StickyDiscoveryKind Kind,
    long? CommentId,
    string? CommentUrl,
    StickyPublicationReceipt? Receipt,
    StickyPublicationReason Reason);
