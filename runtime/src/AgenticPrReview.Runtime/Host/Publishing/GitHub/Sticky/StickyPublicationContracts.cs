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
        R4PublicationScopeV1? scope,
        R4RenderedStickyComment? rendered,
        out AuthorizedStickyPublicationRequest? request)
    {
        request = null;
        try
        {
            if (authorization is null || scope is null || rendered is null ||
                !R4PublicationIdentityV1.IsValidScope(scope) ||
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
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException)
        {
            return false;
        }
    }
}

internal interface IStickyGitHubPublisherTransportFactory
{
    IStickyGitHubPublisherTransport Create(ActionHostGitHubToken token,
        AuthorizedStickyPublicationRequest request);
}

internal interface IStickyGitHubPublisherTransport :
    IBoundedGitHubPublisherTransport
{
    Task<BoundedGitHubPublisherResult<BoundedGitHubIssueComment>>
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
}

internal enum StickyPublicationOperation { Create = 1, Update, Observed }

internal enum StickyDiscoveryKind
{
    Absent = 1,
    StaleTarget,
    ExactTarget,
    Cancelled,
    InvalidOrIncomplete,
}
