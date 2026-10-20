using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Prefix;

// One explicit readonly historical-format vector. Actual current tool output
// supplies its suffix; the positional data grants no SDK/context authority.
internal static class AuthoredPrefixSuffix
{
    internal static ProjectChatRequest Create(AgentRunRequest run, string id, AgentToolExecution execution)
    {
        if (!AgentToolArguments.TryReadFileProvider("{\"path\":\"a.cs\",\"start_line\":1,\"line_count\":1}", out var arguments))
            throw new PrefixObservationException();
        var call = new PreparedReadFileCall(id, arguments!);
        if (!AgentToolResultAdmission.TryAdmit(call, run.ReviewedIdentity, execution, out var json, out _))
            throw new PrefixObservationException();
        var currentPosition = run.InitialMessages.Length;
        var messages = run.InitialMessages.Append(new ProjectChatMessage("assistant",
            [new ProjectToolCallContent(id, call.Name, System.Text.Encoding.UTF8.GetString(call.CanonicalArguments))]))
            .Append(new("tool", [new ProjectToolResultContent(id, json)])).ToArray();
        var prior = run.Continuation;
        var continuation = new ProjectContinuation(run.StablePlan.ProviderId, run.StablePlan.ModelId,
            run.StablePlan.AdapterId, run.SessionId,
            [.. (prior?.Items ?? []), new("current reasoning", "", DeepSeekAdapterContext.ReasoningFraming,
                null, currentPosition, 0)]);
        return new(messages, AgentToolRegistry.Definitions.ToArray(), continuation, ThinkingRequired: true);
    }
}
