using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;

namespace AgenticPrReview.Runtime.Tests.Agent.Core;

public sealed class AgentTrustedBudgetTests
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
    public void ValidLowerBudgetMaterializesWithoutGrantingExecutionAuthority()
    {
        var trusted = new AgentTrustedRequest("repo", 1, "workflow@trusted-sha",
            "trusted policy A"u8.ToArray(), "build", "provider", "model", "adapter");
        trusted = trusted with { LimitAuthority = new(trusted.AdapterId, AgentLimitProfile.Current, new(1, 10, 1)) };
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out _));
    }
}
