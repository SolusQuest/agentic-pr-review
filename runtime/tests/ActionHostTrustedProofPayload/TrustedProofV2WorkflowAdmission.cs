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

        return ActionHostTrustedWorkflowPolicy.TryValidateExact(
            source,
            policy,
            launch.ActionSourceSha,
            Render(launch.ActionSourceSha, launch.PayloadSha256),
            out evidence,
            out _);
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

    internal static string Render(string actionSourceSha, string payloadSha256)
    {
        if (!IsLowerHex(actionSourceSha, 40) ||
            !IsLowerHex(payloadSha256, 64) ||
            !IsLowerHex(TrustedProofPayloadBuildIdentity.SourceCommit, 40))
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
