using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;

namespace AgenticPrReview.Runtime.ActionHost.Authorization;

internal interface IActionHostTrustedWorkflowAdmission
{
    bool TryValidateWorkflow(
        byte[]? source,
        ActionHostAuthorizationPolicy policy,
        ActionHostLaunchContract launch,
        out ActionHostTrustedWorkflowEvidence? evidence);

    bool TryAdmitPullRequest(
        ActionHostGitHubRepositoryFact repository,
        ActionHostGitHubPullRequestFact pullRequest,
        ActionHostLaunchContract launch,
        out string? effectiveReviewBaseSha);
}

internal sealed class ActionHostV1TrustedWorkflowAdmission :
    IActionHostTrustedWorkflowAdmission
{
    internal static readonly ActionHostV1TrustedWorkflowAdmission Instance =
        new();

    private ActionHostV1TrustedWorkflowAdmission() { }

    public bool TryValidateWorkflow(
        byte[]? source,
        ActionHostAuthorizationPolicy policy,
        ActionHostLaunchContract launch,
        out ActionHostTrustedWorkflowEvidence? evidence) =>
        ActionHostTrustedWorkflowPolicy.TryValidate(
            source,
            policy,
            launch.ActionSourceSha,
            launch.PayloadSha256,
            out evidence);

    public bool TryAdmitPullRequest(
        ActionHostGitHubRepositoryFact repository,
        ActionHostGitHubPullRequestFact pullRequest,
        ActionHostLaunchContract launch,
        out string? effectiveReviewBaseSha)
    {
        effectiveReviewBaseSha = pullRequest.BaseSha;
        return true;
    }
}
