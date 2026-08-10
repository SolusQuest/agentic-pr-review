using AgenticPrReview.Runtime.Host.State.Locator;

namespace AgenticPrReview.Runtime.Host.State.Lineage;

internal sealed class AuthorizedLineageReset
{
    private readonly AuthorizedLocatorAccess authority;

    private AuthorizedLineageReset(
        AuthorizedLocatorAccess authority,
        string repositoryId,
        long pullRequestNumber,
        string trustedWorkflowIdentity,
        string trustedWorkflowRoute,
        string producingRunIdentity,
        long producingRunAttempt,
        string requestIdentity,
        string? priorHeadIdentity)
    {
        this.authority = authority;
        RepositoryId = repositoryId;
        PullRequestNumber = pullRequestNumber;
        TrustedWorkflowIdentity = trustedWorkflowIdentity;
        TrustedWorkflowRoute = trustedWorkflowRoute;
        ProducingRunIdentity = producingRunIdentity;
        ProducingRunAttempt = producingRunAttempt;
        RequestIdentity = requestIdentity;
        PriorHeadIdentity = priorHeadIdentity;
    }

    internal string RepositoryId { get; }
    internal long PullRequestNumber { get; }
    internal string TrustedWorkflowIdentity { get; }
    internal string TrustedWorkflowRoute { get; }
    internal string ProducingRunIdentity { get; }
    internal long ProducingRunAttempt { get; }
    internal string RequestIdentity { get; }
    internal string? PriorHeadIdentity { get; }

    internal bool Allows(
        AuthorizedLocatorAccess? access,
        LineageBaseScope scope,
        string producingRunIdentity,
        long producingRunAttempt,
        string selectedHeadIdentity) =>
        ReferenceEquals(authority, access) &&
        authority.Allows(access, scope.RepositoryId) &&
        StringComparer.Ordinal.Equals(RepositoryId, scope.RepositoryId) &&
        PullRequestNumber == scope.PullRequestNumber &&
        StringComparer.Ordinal.Equals(
            TrustedWorkflowIdentity,
            scope.TrustedWorkflowIdentity) &&
        StringComparer.Ordinal.Equals(
            TrustedWorkflowRoute,
            "workflow_dispatch") &&
        StringComparer.Ordinal.Equals(
            ProducingRunIdentity,
            producingRunIdentity) &&
        ProducingRunAttempt == producingRunAttempt &&
        LineageValidation.IsSha256(RequestIdentity) &&
        (PriorHeadIdentity is null ||
            StringComparer.Ordinal.Equals(
                PriorHeadIdentity,
                selectedHeadIdentity));

    public override string ToString() => nameof(AuthorizedLineageReset);
}
