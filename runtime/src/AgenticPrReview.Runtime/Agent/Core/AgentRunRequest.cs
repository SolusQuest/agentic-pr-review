using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Agent.Loop;

internal sealed record AgentRunRequest(
    ReviewedIdentity ReviewedIdentity,
    StableAgentPlan StablePlan,
    string SessionId,
    ProjectChatMessage[] InitialMessages,
    ProjectContinuation? Continuation = null,
    TimeSpan? RemainingHostTime = null);
