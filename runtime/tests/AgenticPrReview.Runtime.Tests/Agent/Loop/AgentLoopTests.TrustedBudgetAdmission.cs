using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;

namespace AgenticPrReview.Runtime.Tests.Agent.Loop;

public sealed partial class AgentLoopTests
{
    [Theory]
    [InlineData(0, 10, 1)]
    [InlineData(2_000_001, 10, 1)]
    [InlineData(1, 38_000_001, 1)]
    [InlineData(1, 10, 524_289)]
    public void InvalidTrustedBudgetCannotMaterialize(long uncached, long cached, long output)
    {
        var trusted = new AgentTrustedRequest("repo", 1, "workflow@trusted-sha",
            "trusted policy A"u8.ToArray(), "build", "provider", "model", "adapter");
        trusted = trusted with { LimitAuthority = new(trusted.AdapterId, AgentLimitProfile.Current,
            new(uncached, cached, output)) };
        Assert.False(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out _));
    }
    [Fact]
    public async Task TrustedLowerBudgetRejectsAnActualRunOverrun()
    {
        var budget = new ReviewTokenBudget(1, 10, 1);
        var trusted = CompletedInputTrusted();
        trusted = trusted with { LimitAuthority = new(trusted.AdapterId, AgentLimitProfile.Current, budget) };
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var materialized));
        var run = Request() with { StablePlan = materialized!.StablePlan };
        var client = new ScriptedChatClient([Response(TerminalCall("finish1", "done"), 2, 1)]);
        var incomplete = await new AgentLoop(client, new ScriptedToolExecutor(), limitAuthority: trusted.LimitAuthority)
            .RunAsync(run, default);
        Assert.False(incomplete.Succeeded);
        Assert.False(incomplete.CompletedSessionEligible);
        Assert.Equal(AgentFailureCodes.TokenLimit, incomplete.Diagnostic!.Code);
    }

}
