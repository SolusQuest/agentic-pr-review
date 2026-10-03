using AgenticPrReview.Runtime.Agent.Loop;

namespace AgenticPrReview.Runtime.Tests.Agent.Loop;

public sealed partial class AgentLoopTests
{
    [Theory]
    [InlineData("8c184a185067b867de078109295d0fa4f442d90fd5bbbec01c7ff4afaeaf30e5")]
    [InlineData("671d94914fa572ce6e6381abe1546c0a20c881115ea214ded89ef61bd21a8189")]
    public async Task PreviousSnapshotLimitsRejectBeforeAnyProviderCall(string previousLimits)
    {
        var chat = new ScriptedChatClient([]);
        var request = Request();
        request = request with
        {
            StablePlan = request.StablePlan with
            {
                LimitsSha256 = previousLimits,
            },
        };

        var outcome = await new AgentLoop(chat, new ScriptedToolExecutor())
            .RunAsync(request, CancellationToken.None);

        AssertFailure(outcome, "agent_response_invalid");
        Assert.Empty(chat.Requests);
        Assert.Equal(0, outcome.Diagnostic!.ModelCalls);
        Assert.Equal(0, outcome.Diagnostic!.ToolCalls);
    }
    [Fact]
    public async Task PreviousReadSchemaToolsetRejectsBeforeAnyProviderCall()
    {
        var chat = new ScriptedChatClient([]);
        var request = Request();
        request = request with
        {
            StablePlan = request.StablePlan with
            {
                ToolsetSha256 = "39b7291fbde316c6b0081c153fa960303d1d0e0ddeb9fc565bc0cbf821b5b502",
            },
        };
        var executor = new ScriptedToolExecutor();
        var outcome = await new AgentLoop(chat, executor).RunAsync(request, CancellationToken.None);
        AssertFailure(outcome, "agent_response_invalid");
        Assert.Empty(chat.Requests);
        Assert.Empty(executor.Order);
        Assert.Equal(0, outcome.Diagnostic!.ModelCalls);
        Assert.Equal(0, outcome.Diagnostic.ToolCalls);
    }

}
