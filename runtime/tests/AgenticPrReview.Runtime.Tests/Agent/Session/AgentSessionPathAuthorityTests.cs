using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Tests.Agent.Session;

public sealed partial class AgentSessionRoundTripTests
{
    [Theory]
    [InlineData("read_file", "{\"path\":\"src/a.cs\"}", false)]
    [InlineData("search_text", "{\"query\":\"line\",\"path\":\"src/a.cs\"}", false)]
    [InlineData("read_diff", "{\"path\":\"src/a.cs\"}", true)]
    public async Task RestoredHistoricalPathDoesNotAuthorizeCurrentToolBatch(
        string tool, string arguments, bool remainsTracked)
    {
        var trusted = Trusted();
        var completed = await CompleteOneReadAsync(trusted);
        var built = AgentSessionBuilder.Build(new AgentSessionBuildInput(
            completed.Run,
            completed.Outcome,
            trusted,
            completed.Run.InitialMessages.Length - 1,
            SyntheticContinuationCodec.Instance,
            Predecessor: null,
            AgentSessionHeadTransition.SameHead));
        Assert.True(built.Succeeded, built.FailureCode);
        var artifact = Assert.IsType<AgentSessionArtifact>(built.Artifact);
        var current = Identity() with { HeadSha = new string('2', 40) };
        var restored = AgentSessionRestorer.Restore(new AgentSessionRestoreInput(
            AgentSessionLocatorFamily.Current,
            AgentSessionRestoreIntent.Automatic,
            ExplicitReset: false,
            artifact.Plaintext,
            new AgentSessionAcceptedState(
                0, artifact.SessionSha256, new string('e', 64),
                completed.Run.ReviewedIdentity.BaseSha,
                completed.Run.ReviewedIdentity.HeadSha,
                PredecessorStateSha256: null),
            trusted,
            completed.Run.SessionId,
            current,
            User("Review the current snapshot."),
            AgentSessionHeadTransition.VerifiedAhead,
            SyntheticContinuationCodec.Instance));
        Assert.True(restored.Succeeded, restored.Code);
        Assert.Contains(
            restored.RunRequest!.InitialMessages.SelectMany(message => message.Contents),
            content => content is ProjectToolCallContent
            {
                Name: "read_file", CallId: "read0",
            });
        var snapshot = new ReviewedSnapshot(
            current, Directory.GetCurrentDirectory(),
            remainsTracked ? ["src/a.cs"] : []);
        var outcome = await new AgentLoop(
            new OneResponseChatClient(_ => new ProjectChatResponse(
                new ProjectChatMessage("assistant", [
                    new ProjectToolCallContent("discover-current", "list_files", "{}"),
                    new ProjectToolCallContent("reuse-path", tool, arguments),
                ]),
                new ProjectChatUsage(1, 1),
                1)),
            new SnapshotToolExecutor(snapshot, new VerifiedReviewedFileAccess()))
            .RunAsync(restored.RunRequest, CancellationToken.None);
        Assert.False(outcome.Succeeded);
        Assert.Equal(AgentFailureCodes.ToolPathNotTracked, outcome.Diagnostic!.Code);
        Assert.Equal(1, outcome.Diagnostic.ModelCalls);
        Assert.Equal(0, outcome.Diagnostic.ToolCalls);
        Assert.Empty(outcome.Events.OfType<AgentToolCallEvent>());
        Assert.Empty(outcome.Events.OfType<AgentToolResultEvent>());
    }
}
