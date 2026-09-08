using System.Reflection;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

internal sealed class TrustedProofV2WorkflowAdmission :
    IActionHostTrustedWorkflowAdmission
{
    internal static TrustedProofV2WorkflowAdmission Instance { get; } = new();

    internal const string ProofKind =
        "apr-r4-e2p-trusted-proof-payload-v2";
    private const string ResourceName =
        "AgenticPrReview.Runtime.ActionHostTrustedProofPayload.TrustedProofWorkflowTemplateV2";
    private static readonly Lazy<string> Template = new(LoadTemplate);

    public bool TryValidateWorkflow(
        byte[]? source,
        ActionHostAuthorizationPolicy policy,
        ActionHostLaunchContract launch,
        out ActionHostTrustedWorkflowEvidence? evidence)
    {
        evidence = null;
        if (!StringComparer.Ordinal.Equals(launch.WorkflowSha, launch.ActionSourceSha) ||
            !StringComparer.Ordinal.Equals(
                launch.WorkflowSha,
                TrustedProofPayloadBuildIdentity.SourceCommit))
        {
            return false;
        }

        if (!ActionHostTrustedWorkflowPolicy.TryValidateExact(
            source,
            policy,
            launch.ActionSourceSha,
            Render(launch.ActionSourceSha),
            out evidence,
            out _) ||
            evidence is null)
        {
            return false;
        }

        evidence = evidence with
        {
            SameHeadContinuationPolicy = ActionHostSameHeadContinuationPolicy
                .ContinueAcrossWorkflowRuns,
            PayloadContinuityMode = ActionHostPayloadContinuityMode.ExactSource,
            PayloadSourceCommit = TrustedProofPayloadBuildIdentity.SourceCommit,
            PayloadSourceTree = TrustedProofPayloadBuildIdentity.SourceTree,
        };
        return true;
    }

    public bool TryAdmitCurrentRun(
        ActionHostGitHubRepositoryFact repository,
        ActionHostEventFact eventFact,
        ActionHostGitHubWorkflowRunFact run,
        ActionHostLaunchContract launch)
    {
        var candidate = eventFact.CandidateExecutionPhase is not null;
        var expectedBranch = candidate
            ? run.HeadBranch
            : repository.DefaultBranch;
        var expectedWorkflowRef =
            $"{repository.FullName}/{launch.WorkflowPath}@" +
            $"refs/heads/{expectedBranch}";
        return candidate
            ? eventFact.Route == ActionHostAuthorizationRoute.WorkflowDispatch &&
                eventFact.CandidateSourcePullRequestNumber is > 0 &&
                eventFact.CandidateSourcePullRequestNumber !=
                    eventFact.DispatchPullRequestNumber &&
                !StringComparer.Ordinal.Equals(
                    run.HeadBranch,
                    repository.DefaultBranch) &&
                StringComparer.Ordinal.Equals(
                    launch.WorkflowRef,
                    expectedWorkflowRef)
            : eventFact.CandidateSourcePullRequestNumber is null &&
                StringComparer.Ordinal.Equals(
                    run.HeadBranch,
                    repository.DefaultBranch) &&
                StringComparer.Ordinal.Equals(
                    launch.WorkflowRef,
                    expectedWorkflowRef);
    }

    public bool TryAdmitPullRequest(
        ActionHostGitHubRepositoryFact repository,
        ActionHostGitHubPullRequestFact pullRequest,
        ActionHostLaunchContract launch,
        out string? effectiveReviewBaseSha)
    {
        effectiveReviewBaseSha = null;
        if (!StringComparer.Ordinal.Equals(repository.DefaultBranch, "main") ||
            !StringComparer.Ordinal.Equals(pullRequest.BaseRef, "main"))
        {
            return false;
        }

        effectiveReviewBaseSha = launch.WorkflowSha;
        return true;
    }

    internal static string Render(string actionSourceSha)
    {
        if (!IsLowerHex(actionSourceSha, 40) ||
            !IsLowerHex(TrustedProofPayloadBuildIdentity.SourceCommit, 40) ||
            !IsLowerHex(TrustedProofPayloadBuildIdentity.SourceTree, 40))
        {
            throw new ArgumentException("Workflow identities are invalid.");
        }

        return Template.Value;
    }

    private static string LoadTemplate()
    {
        using var stream = typeof(TrustedProofV2WorkflowAdmission).Assembly
            .GetManifestResourceStream(ResourceName) ??
            throw new InvalidOperationException("The v2 workflow template is missing.");
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false);
        var v2 = reader.ReadToEnd();
        if (!v2.EndsWith('\n') || v2.Contains('\r'))
        {
            throw new InvalidOperationException("The v2 workflow template is invalid.");
        }

        if (v2.Contains("__PAYLOAD_SOURCE_SHA__", StringComparison.Ordinal) ||
            v2.Contains(ActionHostTrustedWorkflowContract.ActionSourcePlaceholder,
                StringComparison.Ordinal) ||
            v2.Contains(ActionHostTrustedWorkflowContract.PayloadShaPlaceholder,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The v2 workflow topology is invalid.");
        }

        return v2;
    }

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
