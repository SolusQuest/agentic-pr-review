using AgenticPrReview.Runtime.Agent.Loop;

namespace AgenticPrReview.Runtime.Tests.Agent.Loop;

public sealed partial class AgentLoopTests
{
    [Fact]
    public async Task PreviousSnapshotLimitsRejectBeforeAnyProviderCall()
    {
        var chat = new ScriptedChatClient([]);
        var request = Request();
        request = request with
        {
            StablePlan = request.StablePlan with
            {
                LimitsSha256 = "8c184a185067b867de078109295d0fa4f442d90fd5bbbec01c7ff4afaeaf30e5",
            },
        };

        var outcome = await new AgentLoop(chat, new ScriptedToolExecutor())
            .RunAsync(request, CancellationToken.None);

        AssertFailure(outcome, "agent_response_invalid");
        Assert.Empty(chat.Requests);
        Assert.Equal(0, outcome.Diagnostic!.ModelCalls);
        Assert.Equal(0, outcome.Diagnostic!.ToolCalls);
    }
}
