using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;

namespace AgenticPrReview.Runtime.Tests.Agent.Session;

public sealed partial class AgentSessionRoundTripTests
{
    [Theory]
    [InlineData("calls")]
    [InlineData("timeout")]
    [InlineData("uncached")]
    [InlineData("cached")]
    [InlineData("output")]
    public async Task EveryConfiguredBudgetDimensionBindsSelectedSession(string dimension)
    {
        var trusted = Trusted();
        var authority = new AgentLimitAuthority(trusted.AdapterId, AgentLimitProfile.Current,
            new(100, 200, 100), ModelCalls: 2, TimeoutSeconds: 60);
        trusted = trusted with { LimitAuthority = authority };
        var completed = await CompleteAsync(trusted, null, null, "review", "finish0", false);
        var built = AgentSessionBuilder.Build(new(completed.Run, completed.Outcome, trusted,
            completed.Run.InitialMessages.Length - 1, SyntheticContinuationCodec.Instance, null,
            AgentSessionHeadTransition.SameHead));
        Assert.True(built.Succeeded, built.FailureCode);
        var artifact = Assert.IsType<AgentSessionArtifact>(built.Artifact);
        Assert.Equal(AgentCanonical.LimitsSha256(authority), artifact.Document.LimitsSha256);
        Assert.True(Restore(artifact, trusted, AgentSessionHeadTransition.SameHead).Succeeded);
        var changed = dimension switch
        {
            "calls" => authority with { ModelCalls = 3 },
            "timeout" => authority with { TimeoutSeconds = 59 },
            "uncached" => authority with { TokenBudget = authority.TokenBudget! with { UncachedInputTokens = 99 } },
            "cached" => authority with { TokenBudget = authority.TokenBudget! with { CachedInputTokens = 199 } },
            _ => authority with { TokenBudget = authority.TokenBudget! with { OutputTokens = 99 } },
        };
        var rejected = Restore(artifact, trusted with { LimitAuthority = changed }, AgentSessionHeadTransition.SameHead);
        Assert.False(rejected.Succeeded);
        Assert.Null(rejected.RunRequest);
    }
}
