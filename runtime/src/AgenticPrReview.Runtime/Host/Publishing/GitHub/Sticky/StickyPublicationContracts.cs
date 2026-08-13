using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
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
        R4ValidatedPublicationReview? validatedReview,
        out AuthorizedStickyPublicationRequest? request)
    {
        request = null;
        try
        {
            if (authorization is null || validatedReview is null)
                return false;
            var scope = validatedReview.Scope;
            if (!R4PublicationIdentityV1.IsValidScope(scope) ||
                scope.RepositoryId > long.MaxValue ||
                (long)scope.RepositoryId !=
                    authorization.PullRequest.RepositoryId ||
                scope.WorkflowSourceRepositoryId > long.MaxValue ||
                (long)scope.WorkflowSourceRepositoryId !=
                    authorization.PullRequest.RepositoryId ||
                scope.PullRequestNumber > long.MaxValue ||
                (long)scope.PullRequestNumber !=
                    authorization.PullRequest.Number ||
                !StringComparer.Ordinal.Equals(scope.WorkflowPath,
                    authorization.WorkflowPath)) return false;
            var rendered = R4StickyRenderer.Render(validatedReview);
            if (!R4PublicationBudget.Fits(rendered.Comment,
                    R4PublicationBudget.MaximumScalars,
                    R4PublicationBudget.MaximumUtf8Bytes) ||
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
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException)
        {
            return false;
        }
    }
}

internal sealed class AuthorizedStickyReadbackRequest
{
    private AuthorizedStickyReadbackRequest(
        ActionHostAuthorizer.AuthorizedInvocation authorization,
        R4PublicationScopeV1 scope,
        R4PublicationIdentityV1 expectedIdentity,
        string? exactComment, long? expectedCommentId) =>
        (Authorization, Scope, ExpectedIdentity, ExactComment,
            ExpectedCommentId) =
        (authorization, scope, expectedIdentity, exactComment,
            expectedCommentId);

    internal ActionHostAuthorizer.AuthorizedInvocation Authorization { get; }
    internal R4PublicationScopeV1 Scope { get; }
    internal R4PublicationIdentityV1 ExpectedIdentity { get; }
    internal string? ExactComment { get; }
    internal long? ExpectedCommentId { get; }

    internal static bool TryCreate(
        ActionHostAuthorizer.AuthorizedInvocation? authorization,
        R4PublicationScopeV1? scope,
        R4RenderedStickyComment? persisted,
        out AuthorizedStickyReadbackRequest? request)
    {
        request = null;
        try
        {
            if (authorization is null || scope is null || persisted is null ||
                !IsBound(authorization, scope) ||
                !R4PublicationBudget.Fits(persisted.Comment,
                    R4PublicationBudget.MaximumScalars,
                    R4PublicationBudget.MaximumUtf8Bytes) ||
                !StringComparer.Ordinal.Equals(
                    R4PublicationIdentityV1.ComputeScopeSha256(scope),
                    persisted.Identity.ScopeSha256) ||
                !StringComparer.Ordinal.Equals(persisted.Identity.HeadSha,
                    authorization.PullRequest.HeadSha)) return false;
            var inspected = R4StickyMarker.Inspect(persisted.Comment);
            if (inspected.Kind != R4StickyInspectionKind.ValidR4 ||
                !StringComparer.Ordinal.Equals(inspected.Body,
                    persisted.Body) ||
                !Equals(inspected.Identity, persisted.Identity)) return false;
            request = new(authorization, scope, persisted.Identity,
                persisted.Comment, null);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException)
        {
            return false;
        }
    }

    internal static bool TryCreateRecovery(
        ActionHostAuthorizer.AuthorizedInvocation? authorization,
        R4PublicationScopeV1? scope,
        R4RenderedStickyComment? persisted,
        out AuthorizedStickyReadbackRequest? request)
    {
        request = null;
        try
        {
            if (authorization is null || scope is null || persisted is null ||
                !IsBound(authorization, scope) ||
                !R4PublicationBudget.Fits(
                    persisted.Comment,
                    R4PublicationBudget.MaximumScalars,
                    R4PublicationBudget.MaximumUtf8Bytes) ||
                !StringComparer.Ordinal.Equals(
                    R4PublicationIdentityV1.ComputeScopeSha256(scope),
                    persisted.Identity.ScopeSha256))
            {
                return false;
            }

            var inspected = R4StickyMarker.Inspect(persisted.Comment);
            if (inspected.Kind != R4StickyInspectionKind.ValidR4 ||
                !StringComparer.Ordinal.Equals(
                    inspected.Body,
                    persisted.Body) ||
                !Equals(inspected.Identity, persisted.Identity))
            {
                return false;
            }

            request = new(
                authorization,
                scope,
                persisted.Identity,
                persisted.Comment,
                expectedCommentId: null);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException)
        {
            return false;
        }
    }

    internal static bool TryCreate(
        ActionHostAuthorizer.AuthorizedInvocation? authorization,
        R4PublicationScopeV1? scope,
        StickyCommentPublisher.StickyPublicationReceipt? receipt,
        out AuthorizedStickyReadbackRequest? request)
    {
        request = null;
        try
        {
            if (authorization is null || scope is null || receipt is null ||
                !IsBound(authorization, scope) ||
                receipt.RepositoryId !=
                    authorization.PullRequest.RepositoryId ||
                receipt.PullRequestNumber !=
                    authorization.PullRequest.Number ||
                !StringComparer.Ordinal.Equals(receipt.HeadSha,
                    authorization.PullRequest.HeadSha) ||
                !StringComparer.Ordinal.Equals(receipt.ScopeSha256,
                    R4PublicationIdentityV1.ComputeScopeSha256(scope)))
                return false;
            request = new(authorization, scope, new(receipt.ScopeSha256,
                receipt.BodySha256, receipt.HeadSha), null,
                receipt.CommentId);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException)
        {
            return false;
        }
    }

    private static bool IsBound(
        ActionHostAuthorizer.AuthorizedInvocation authorization,
        R4PublicationScopeV1 scope) =>
        R4PublicationIdentityV1.IsValidScope(scope) &&
        scope.RepositoryId <= long.MaxValue &&
        (long)scope.RepositoryId == authorization.PullRequest.RepositoryId &&
        scope.WorkflowSourceRepositoryId <= long.MaxValue &&
        (long)scope.WorkflowSourceRepositoryId ==
            authorization.PullRequest.RepositoryId &&
        scope.PullRequestNumber <= long.MaxValue &&
        (long)scope.PullRequestNumber == authorization.PullRequest.Number &&
        StringComparer.Ordinal.Equals(scope.WorkflowPath,
            authorization.WorkflowPath);
}

internal interface IStickyGitHubPublisherTransportFactory
{
    IStickyGitHubPublisherTransport Create(ActionHostGitHubToken token,
        AuthorizedStickyPublicationRequest request);
    IStickyGitHubReadbackTransport CreateReadback(ActionHostGitHubToken token,
        AuthorizedStickyReadbackRequest request);
}

internal interface IStickyGitHubReadbackTransport :
    IBoundedGitHubPublisherTransport;

internal interface IStickyGitHubPublisherTransport :
    IBoundedGitHubPublisherTransport
{
    Task<BoundedGitHubHttpResult<BoundedGitHubIssueComment>>
        MutateStickyCommentAsync(CancellationToken cancellationToken);
}

internal interface IStickyPublicationIssuerEvidence { }

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
    Deadline,
}

internal enum StickyPublicationOperation { Create = 1, Update, Observed }

internal enum StickyDiscoveryKind
{
    Absent = 1,
    StaleTarget,
    ExactTarget,
    Cancelled,
    Deadline,
    InvalidOrIncomplete,
}
