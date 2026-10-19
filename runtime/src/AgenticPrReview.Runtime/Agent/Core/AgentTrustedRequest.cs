using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Agent.Core;

internal enum ReviewedHeadTransition
{
    SameHead,
    VerifiedAhead,
    Unknown,
    Diverged,
    Unrelated,
}

internal sealed record AgentTrustedRequest(
    string RepositoryId,
    long ReviewTarget,
    string WorkflowIdentity,
    byte[] TrustedPolicyBytes,
    string BuildId,
    string ProviderId,
    string ModelId,
    string AdapterId,
    AgentLimitAuthority? LimitAuthority = null);

internal sealed record AgentMaterializedStableRequest(
    StableAgentPlan StablePlan,
    ProjectChatMessage[] ControlMessages);

