using AgenticPrReview.Runtime.Host.State.Locator;

namespace AgenticPrReview.Runtime.Host.State.Lineage;

internal sealed class AuthorizedLineageReset
{
    private readonly AuthorizedLocatorAccess authority;

    private AuthorizedLineageReset(
        AuthorizedLocatorAccess authority,
        string baseScopeDigest,
        string repositoryId,
        long pullRequestNumber,
        string trustedWorkflowIdentity,
        string trustedWorkflowRoute,
        string producingRunIdentity,
        long producingRunAttempt,
        string requestIdentity,
        string priorHeadIdentity)
    {
        this.authority = authority;
        BaseScopeDigest = baseScopeDigest;
        RepositoryId = repositoryId;
        PullRequestNumber = pullRequestNumber;
        TrustedWorkflowIdentity = trustedWorkflowIdentity;
        TrustedWorkflowRoute = trustedWorkflowRoute;
        ProducingRunIdentity = producingRunIdentity;
        ProducingRunAttempt = producingRunAttempt;
        RequestIdentity = requestIdentity;
        PriorHeadIdentity = priorHeadIdentity;
    }

    internal string BaseScopeDigest { get; }
    internal string RepositoryId { get; }
    internal long PullRequestNumber { get; }
    internal string TrustedWorkflowIdentity { get; }
    internal string TrustedWorkflowRoute { get; }
    internal string ProducingRunIdentity { get; }
    internal long ProducingRunAttempt { get; }
    internal string RequestIdentity { get; }
    internal string PriorHeadIdentity { get; }

    internal bool Allows(
        AuthorizedLocatorAccess? access,
        LineageBaseScope scope,
        string baseScopeDigest,
        string producingRunIdentity,
        long producingRunAttempt,
        string selectedHeadIdentity) =>
        AllowsScope(
            access,
            scope,
            baseScopeDigest,
            producingRunIdentity,
            producingRunAttempt) &&
        StringComparer.Ordinal.Equals(
            PriorHeadIdentity,
            selectedHeadIdentity);

    internal bool AllowsCompleted(
        AuthorizedLocatorAccess? access,
        LineageBaseScope scope,
        string baseScopeDigest,
        string producingRunIdentity,
        long producingRunAttempt,
        LineageHeadCandidate selected) =>
        AllowsScope(
            access,
            scope,
            baseScopeDigest,
            producingRunIdentity,
            producingRunAttempt) &&
        selected.Head.Transition == LineageTransitionKind.Reset &&
        StringComparer.Ordinal.Equals(
            selected.Head.PreviousHeadIdentity,
            PriorHeadIdentity) &&
        StringComparer.Ordinal.Equals(
            selected.Head.TransitionEvidenceIdentity,
            RequestIdentity) &&
        StringComparer.Ordinal.Equals(
            selected.Header.ProducingRunIdentity,
            ProducingRunIdentity) &&
        selected.Header.ProducingRunAttempt == ProducingRunAttempt;

    private bool AllowsScope(
        AuthorizedLocatorAccess? access,
        LineageBaseScope scope,
        string baseScopeDigest,
        string producingRunIdentity,
        long producingRunAttempt) =>
        ReferenceEquals(authority, access) &&
        authority.Allows(access, scope.RepositoryId) &&
        LineageValidation.IsSha256(BaseScopeDigest) &&
        StringComparer.Ordinal.Equals(BaseScopeDigest, baseScopeDigest) &&
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
        LineageValidation.IsSha256(PriorHeadIdentity);

    public override string ToString() => nameof(AuthorizedLineageReset);
}
