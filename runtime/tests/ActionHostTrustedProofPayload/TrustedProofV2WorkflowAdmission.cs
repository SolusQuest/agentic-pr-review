using System.Reflection;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

internal sealed class TrustedProofV2WorkflowAdmission :
    IActionHostTrustedWorkflowAdmission
{
    internal const string ProofKind =
        "apr-r4-e2p-trusted-proof-payload-v2";
    internal const string PayloadSourcePlaceholder = "__PAYLOAD_SOURCE_SHA__";
    private const string ResourceName =
        "AgenticPrReview.Runtime.ActionHostTrustedProofPayload.TrustedProofWorkflowTemplateV2";
    private static readonly Lazy<string> Template = new(LoadTemplate);

    public bool TryValidateWorkflow(
        byte[]? source,
        ActionHostAuthorizationPolicy policy,
        ActionHostLaunchContract launch,
        out ActionHostTrustedWorkflowEvidence? evidence) =>
        ActionHostTrustedWorkflowPolicy.TryValidateExact(
            source,
            policy,
            launch.ActionSourceSha,
            Render(launch.ActionSourceSha, launch.PayloadSha256),
            out evidence,
            out _);

    public bool IsPullRequestAdmitted(
        ActionHostGitHubRepositoryFact repository,
        ActionHostGitHubPullRequestFact pullRequest,
        ActionHostLaunchContract launch) =>
        StringComparer.Ordinal.Equals(repository.DefaultBranch, "main") &&
        StringComparer.Ordinal.Equals(pullRequest.BaseRef, "main") &&
        StringComparer.Ordinal.Equals(pullRequest.BaseSha, launch.WorkflowSha);

    internal static string Render(string actionSourceSha, string payloadSha256)
    {
        if (!IsLowerHex(actionSourceSha, 40) ||
            !IsLowerHex(payloadSha256, 64) ||
            !IsLowerHex(TrustedProofPayloadBuildIdentity.SourceCommit, 40))
        {
            throw new ArgumentException("Workflow identities are invalid.");
        }

        return Template.Value
            .Replace(ActionHostTrustedWorkflowContract.ActionSourcePlaceholder,
                actionSourceSha, StringComparison.Ordinal)
            .Replace(PayloadSourcePlaceholder,
                TrustedProofPayloadBuildIdentity.SourceCommit,
                StringComparison.Ordinal)
            .Replace(ActionHostTrustedWorkflowContract.PayloadShaPlaceholder,
                payloadSha256, StringComparison.Ordinal);
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

        if (v2.Contains(PayloadSourcePlaceholder, StringComparison.Ordinal) is false ||
            Count(v2, PayloadSourcePlaceholder) != 5)
        {
            throw new InvalidOperationException("The v2 workflow topology is invalid.");
        }

        return v2;
    }

    private static int Count(string value, string needle)
    {
        var count = 0;
        for (var index = 0;
             (index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0;
             index += needle.Length)
        {
            count++;
        }

        return count;
    }

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
