using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;

namespace AgenticPrReview.Runtime.ActionHost.Authorization;

// Two closed authorities. The historical path is retained for the original
// SESSION producer/managed archive regression; it cannot authorize new templates.
internal sealed class ActionHostProductionWorkflowAdmission : IActionHostTrustedWorkflowAdmission
{
    internal static readonly ActionHostProductionWorkflowAdmission Instance = new();
    private ActionHostProductionWorkflowAdmission() { }

    private static bool Downstream(ActionHostLaunchContract launch) =>
        StringComparer.Ordinal.Equals(launch.WorkflowPath, ActionHostDownstreamWorkflowContract.WorkflowPath);

    public bool IsWorkflowPathAllowed(string path) => StringComparer.Ordinal.Equals(
        path, ActionHostDownstreamWorkflowContract.WorkflowPath) || StringComparer.Ordinal.Equals(
        path, ActionHostAuthorizationPolicy.PrivilegedWorkflowPath);

    public bool TryValidateWorkflow(byte[]? source, ActionHostAuthorizationPolicy policy,
        ActionHostLaunchContract launch, out ActionHostTrustedWorkflowEvidence? evidence)
    {
        evidence = null;
        if (!Downstream(launch))
        {
            return IsWorkflowPathAllowed(launch.WorkflowPath) &&
                ActionHostV1TrustedWorkflowAdmission.Instance.TryValidateWorkflow(source, policy, launch, out evidence);
        }

        if (!StringComparer.Ordinal.Equals(launch.Inputs.ConfigPath, ActionHostInputs.DefaultConfigPath))
            return false;

        // A dispatch can use either installation alternative. Only the automatic
        // source can authorize workflow_run; no semantically equivalent YAML.
        foreach (var automatic in launch.Inputs.PullRequestNumber is null ? new[] { true } : new[] { true, false })
        {
            if (ActionHostTrustedWorkflowPolicy.TryValidateExact(source, policy, launch.ActionSourceSha,
                    ActionHostDownstreamWorkflowContract.Render(launch.ActionSourceSha, automatic), out evidence, out _))
            {
                evidence = evidence! with { PreflightJobId = "review" };
                return true;
            }
        }

        return false;
    }

    public bool TryAdmitCurrentRun(ActionHostGitHubRepositoryFact repository, ActionHostEventFact eventFact,
        ActionHostGitHubWorkflowRunFact run, ActionHostLaunchContract launch)
    {
        if (!Downstream(launch))
            return IsWorkflowPathAllowed(launch.WorkflowPath) &&
                ActionHostV1TrustedWorkflowAdmission.Instance.TryAdmitCurrentRun(repository, eventFact, run, launch);

        var expectedRef = $"{repository.FullName}/{ActionHostDownstreamWorkflowContract.WorkflowPath}@refs/heads/{repository.DefaultBranch}";
        return eventFact.CandidateSourcePullRequestNumber is null && eventFact.CandidateExecutionPhase is null &&
            StringComparer.Ordinal.Equals(run.HeadBranch, repository.DefaultBranch) &&
            StringComparer.Ordinal.Equals(launch.WorkflowRef, expectedRef) &&
            (eventFact.Route == ActionHostAuthorizationRoute.WorkflowRun ||
                StringComparer.Ordinal.Equals(eventFact.DispatchStateMode ?? "auto",
                    launch.Inputs.StateMode == ActionHostStateMode.Reset ? "reset" : "auto"));
    }

    public bool TryAdmitPullRequest(ActionHostGitHubRepositoryFact repository,
        ActionHostGitHubPullRequestFact pullRequest, ActionHostLaunchContract launch, out string? effectiveReviewBaseSha)
    {
        effectiveReviewBaseSha = pullRequest.BaseSha;
        return Downstream(launch) || IsWorkflowPathAllowed(launch.WorkflowPath) &&
            ActionHostV1TrustedWorkflowAdmission.Instance.TryAdmitPullRequest(repository, pullRequest, launch,
                out effectiveReviewBaseSha);
    }
}
