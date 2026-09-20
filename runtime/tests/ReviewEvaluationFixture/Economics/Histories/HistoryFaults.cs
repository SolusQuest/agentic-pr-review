using System.Text;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Histories;

internal static class HistoryFaults
{
    internal static RestrictedStateSessionAdmissionContext Context(ReplayFault fault, RestrictedStateSessionAdmissionContext context)
    {
        var trusted = context.SessionContext.TrustedRequest;
        trusted = fault switch
        {
            ReplayFault.ChangedPolicy => trusted with { TrustedPolicyBytes = Encoding.UTF8.GetBytes("changed synthetic policy") },
            ReplayFault.ChangedModel => trusted with { ModelId = "changed-model" },
            ReplayFault.ChangedAdapter => trusted with { AdapterId = new string('a', 64) },
            _ => trusted,
        };
        return context with
        {
            SessionContext = context.SessionContext with { TrustedRequest = trusted },
        };
    }

    // Test a mismatched commitment through the existing admission owner; never configure a second registry.
    internal static bool ToolsetRejected(AgentSessionArtifact artifact, AgentSessionStateAdmissionContext context)
    {
        if (!AgentSessionCodec.TryWrite(artifact.Document with { ToolsetSha256 = new string('a', 64) }, out var changed, out _) || changed is null)
            throw new InvalidOperationException("history_toolset_mutation_invalid");
        return !AgentSessionStateBoundary.Admit(changed.Plaintext, context).Succeeded;
    }
}
