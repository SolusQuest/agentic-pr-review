using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Tests.Agent.Loop;

public sealed partial class AgentLoopTests
{
    private const string PhysicalSlotFinishJson = "{\"summary\":\"complete\",\"findings\":[]}";

    [Fact]
    public async Task EveryCurrentReasoningSlotMaterializesAtItsPhysicalPosition()
    {
        var trusted = CompletedInputTrusted();
        for (var slotPosition = 0;
            slotPosition < AgentLimits.PartsPerMessage;
            slotPosition++)
        {
            Assert.True(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var materializedInput));
            var plan = materializedInput!.StablePlan;
            var callId = string.Concat("finish", slotPosition);
            var run = new AgentRunRequest(
                Identity,
                plan,
                "session0",
                [.. materializedInput.ControlMessages, new("user", [new ProjectTextContent("review")])]);
            ProjectChatResponse? actualResponse = null;
            var loop = new AgentLoop(
                new PhysicalSlotResponseFactory(request =>
                {
                    var messagePosition = request.Messages.Length;
                    var terminalPosition =
                        slotPosition == AgentLimits.PartsPerMessage - 1
                            ? AgentLimits.PartsPerMessage - 2
                            : AgentLimits.PartsPerMessage - 1;
                    var item = new ProjectContinuationItem(
                        string.Concat("readable-", slotPosition),
                        string.Concat("opaque-", slotPosition),
                        "structured",
                        callId,
                        messagePosition,
                        slotPosition);
                    var contents = Enumerable.Range(
                            0,
                            AgentLimits.PartsPerMessage)
                        .Select(position =>
                            position == slotPosition
                                ? (ProjectChatContent)
                                    new ProjectReasoningContent(
                                        item.Readable,
                                        item.Opaque,
                                        item.Framing,
                                        item.AssociatedCallId,
                                        item.MessagePosition,
                                        item.ContentPosition)
                                : position == terminalPosition
                                    ? new ProjectToolCallContent(
                                        callId,
                                        AgentToolRegistry.FinishReviewName,
                                        PhysicalSlotFinishJson)
                                    : new ProjectTextContent(
                                        string.Concat("text-", position)))
                        .ToArray();
                    actualResponse = new ProjectChatResponse(
                        new ProjectChatMessage("assistant", contents),
                        new ProjectChatUsage(1, 1),
                        CapturedResponseBodyBytes: 1,
                        new ProjectContinuation(
                            "provider",
                            "model",
                            "adapter",
                            "session0",
                            [item]));
                    return actualResponse;
                }),
                new ScriptedToolExecutor());
            var outcome = await loop.RunAsync(
                run,
                CancellationToken.None);
            Assert.True(outcome.CompletedSessionEligible);
            // The generic input represents reasoning in its typed sideband.
            // Use only this actual current response, without also supplying the
            // output-only physical reasoning content a second time.
            var currentMessage = actualResponse!.Message with
            {
                Contents = actualResponse.Message.Contents
                    .Where(content => content is not ProjectReasoningContent).ToArray(),
            };
            var materialized = MinimalChatClient.Materialize(
                new ProjectChatRequest(
                    [.. run.InitialMessages, currentMessage],
                    AgentToolRegistry.Definitions.ToArray(),
                    actualResponse.Continuation,
                    ThinkingRequired: true));
            var assistant = Assert.Single(
                materialized.Messages,
                message => message.Role == "assistant");
            Assert.Equal(
                "reasoning",
                assistant.Contents[slotPosition].Kind);
            Assert.Equal(
                Enumerable.Range(0, AgentLimits.PartsPerMessage),
                assistant.Contents.Select(content => content.Position));
        }
    }

    private sealed class PhysicalSlotResponseFactory(Func<ProjectChatRequest, ProjectChatResponse> response)
        : IProjectChatClient
    {
        public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request,
            CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
