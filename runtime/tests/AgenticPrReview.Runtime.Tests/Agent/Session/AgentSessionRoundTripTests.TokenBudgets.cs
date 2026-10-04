using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;

namespace AgenticPrReview.Runtime.Tests.Agent.Session;

public sealed partial class AgentSessionRoundTripTests
{
    [Fact]
    public async Task TrustedLowerBudgetBindsSessionAndCannotAcceptOverrunSuccessor()
    {
        var budget = new ReviewTokenBudget(1, 10, 1);
        var trusted = Trusted();
        trusted = trusted with { LimitAuthority = new(trusted.AdapterId, AgentLimitProfile.Current, budget) };
        var completed = await CompleteAsync(trusted, null, null, "review", "finish0", false);
        var built = AgentSessionBuilder.Build(new(completed.Run, completed.Outcome, trusted,
            completed.Run.InitialMessages.Length - 1, SyntheticContinuationCodec.Instance, null,
            AgentSessionHeadTransition.SameHead));
        Assert.True(built.Succeeded, built.FailureCode);
        var artifact = Assert.IsType<AgentSessionArtifact>(built.Artifact);
        var original = artifact.Plaintext.ToArray();
        var restored = Restore(artifact, trusted, AgentSessionHeadTransition.SameHead);
        Assert.True(restored.Succeeded, restored.Code);
        Assert.False(Restore(artifact, trusted with
        {
            LimitAuthority = trusted.LimitAuthority! with { TokenBudget = budget with { OutputTokens = 2 } },
        }, AgentSessionHeadTransition.SameHead).Succeeded);
        Assert.False(Restore(artifact, trusted with { LimitAuthority = null }, AgentSessionHeadTransition.SameHead).Succeeded);

        var run = restored.RunRequest!;
        var client = new OneResponseChatClient(_ => new(new("assistant",
            [new ProjectToolCallContent("finish1", "finish_review", FinishJson)]), new(2, 1), 1));
        var incomplete = await new AgentLoop(client, new NeverToolExecutor(), limitAuthority: trusted.LimitAuthority)
            .RunAsync(run, default);
        Assert.False(incomplete.Succeeded);
        Assert.False(incomplete.CompletedSessionEligible);
        Assert.Equal(AgentFailureCodes.TokenLimit, incomplete.Diagnostic!.Code);
        var successor = AgentSessionBuilder.Build(new(run, incomplete, trusted,
            run.InitialMessages.Length - 1, SyntheticContinuationCodec.Instance, new AgentSessionPredecessor(artifact.Plaintext, artifact.SessionSha256, new string('e', 64), 0, artifact.Document.ProducerBaseSha, artifact.Document.ProducerHeadSha, null),
            AgentSessionHeadTransition.SameHead));
        Assert.False(successor.Succeeded);
        Assert.Null(successor.Artifact);
        Assert.Equal(original, artifact.Plaintext);
    }

    [Theory]
    [InlineData(0, 10, 1)]
    [InlineData(2_000_001, 10, 1)]
    [InlineData(1, 38_000_001, 1)]
    [InlineData(1, 10, 524_289)]
    public void InvalidTrustedBudgetCannotMaterialize(long uncached, long cached, long output)
    {
        var trusted = Trusted();
        trusted = trusted with { LimitAuthority = new(trusted.AdapterId, AgentLimitProfile.Current,
            new(uncached, cached, output)) };
        Assert.False(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out _));
    }
}
