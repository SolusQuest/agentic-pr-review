using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.AgentLoopAotFixture;

// Generic protocol bounds only. Current durable transactions and SDK context
// import are qualified by the Host and installed Solus proofs.
internal static class StateNegativeProofRunner
{
    internal static bool Handles(string value) => value is "state-oversize" or "continuation-limit";
    internal static Task<bool> RunAsync(ProofCommand command) => command.Case switch
    {
        "state-oversize" => Task.FromResult(EnvelopeOversize()),
        "continuation-limit" => ContinuationLimitAsync(),
        _ => Task.FromResult(false),
    };

    private static bool EnvelopeOversize()
    {
        var bytes = new byte[AgentLimits.StateEnvelopeBytes + 1];
        return !RestrictedStateEnvelope.TryParse(bytes, out _);
    }

    private static async Task<bool> ContinuationLimitAsync()
    {
        var run = Run();
        var outcome = await new AgentLoop(
            new OneResponseChatClient(request =>
            {
                var position = request.Messages.Length;
                var item = new ProjectContinuationItem(
                    new string('r', AgentLimits.ContinuationItemBytes / 2),
                    new string('o', AgentLimits.ContinuationItemBytes / 2),
                    new string('f', 2 * 1024),
                    "finish-continuation",
                    position,
                    0);
                return new ProjectChatResponse(
                    new ProjectChatMessage(
                        "assistant",
                        [
                            new ProjectReasoningContent(
                                item.Readable,
                                item.Opaque,
                                item.Framing,
                                item.AssociatedCallId,
                                item.MessagePosition,
                                item.ContentPosition),
                            new ProjectToolCallContent(
                                "finish-continuation",
                                AgentToolRegistry.FinishReviewName,
                                FinishJson),
                        ]),
                    new ProjectChatUsage(1, 1),
                    CapturedResponseBodyBytes: 1,
                    new ProjectContinuation(
                        ProofScenario.ProviderId,
                        ProofScenario.ModelId,
                        ProofScenario.AdapterId,
                        ProofScenario.SessionId,
                        [item]));
            }),
            new NeverToolExecutor()).RunAsync(
                run,
                CancellationToken.None);
        return !outcome.CompletedSessionEligible &&
            outcome.Diagnostic is
            {
                Code: AgentFailureCodes.ResponseInvalid,
                ModelCalls: 1,
                ToolCalls: 0,
            };
    }

    private static AgentRunRequest Run()
    {
        var trusted = ProofScenario.Trusted();
        if (!AgentStableRequestMaterializer.TryMaterialize(
                trusted,
                priorSessionSha256: null,
                out var materialized))
        {
            throw new InvalidOperationException();
        }

        return new AgentRunRequest(
            ProofScenario.BootstrapIdentity(),
            materialized!.StablePlan,
            ProofScenario.SessionId,
            [
                .. materialized.ControlMessages,
                ProofScenario.User("Synthetic capacity context."),
            ]);
    }

    private const string FinishJson =
        "{\"summary\":\"Synthetic terminal.\",\"findings\":[]}";

    private sealed class OneResponseChatClient(
        Func<ProjectChatRequest, ProjectChatResponse> response)
        : IProjectChatClient
    {
        public Task<ProjectChatResponse> GetResponseAsync(
            ProjectChatRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class NeverToolExecutor : IAgentToolExecutor
    {
        public string? Preflight(PreparedAgentToolCall call) => null;

        public ValueTask<AgentToolExecution> ExecuteAsync(
            PreparedAgentToolCall call,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException();
    }

}
