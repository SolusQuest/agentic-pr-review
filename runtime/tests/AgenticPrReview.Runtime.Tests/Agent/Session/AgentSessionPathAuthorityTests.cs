using System.Text;
using System.Text.Json;
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
    [InlineData("list_files", "{\"after\":\"src/a.cs\"}", false)]
    [InlineData("list_changed_files", "{\"after\":\"src/a.cs\"}", false)]
    public async Task RestoredHistoricalPathRecoversWithoutGrantingCurrentAuthority(
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
            remainsTracked ? ["src/a.cs", "current.txt"] : ["current.txt"]);
        var files = new CurrentFileOnlyAccess();
        var turn = 0;
        var outcome = await new AgentLoop(
            new OneResponseChatClient(request =>
            {
                ProjectToolCallContent[] calls;
                switch (turn++)
                {
                    case 0:
                        calls = [new("discover-current", "list_files", "{}"), new("reuse-path", tool, arguments)];
                        break;
                    case 1:
                        Assert.Equal(0, files.Reads);
                        Assert.Equal(2, request.Messages.SelectMany(message => message.Contents)
                            .OfType<ProjectToolErrorContent>().Count());
                        calls = [new("lookup-corrected", "list_files", "{}")];
                        break;
                    case 2:
                        calls = [new("read-corrected", "read_file", "{\"path\":\"current.txt\"}")];
                        break;
                    default:
                        var result = request.Messages.SelectMany(message => message.Contents)
                            .OfType<ProjectToolResultContent>().Last();
                        using (var json = JsonDocument.Parse(result.Result))
                        {
                            var observation = json.RootElement.GetProperty("observation_id").GetString();
                            calls = [new("finish-corrected", "finish_review",
                                "{\"summary\":\"found\",\"findings\":[{\"severity\":\"high\",\"title\":\"bug\",\"message\":\"fix\",\"evidence\":[{\"observation_id\":\"" +
                                observation + "\",\"path\":\"current.txt\",\"start_line\":1,\"end_line\":1}]}]}")];
                        }
                        break;
                }
                return new(new("assistant", calls), new(1, 1), 1);
            }),
            new SnapshotToolExecutor(snapshot, files))
            .RunAsync(restored.RunRequest, CancellationToken.None);
        Assert.True(outcome.Succeeded, outcome.Diagnostic?.Code);
        Assert.Equal(1, files.Reads);
        Assert.Equal(2, outcome.Events.OfType<AgentRecoveryToolCallEvent>().Count());
        Assert.Equal(2, outcome.Events.OfType<AgentToolResultEvent>().Count());
        var input = new AgentSessionBuildInput(restored.RunRequest!, outcome, trusted,
            restored.RunRequest!.InitialMessages.Length - 1, SyntheticContinuationCodec.Instance,
            new(artifact.Plaintext, artifact.SessionSha256, new string('e', 64), 0,
                completed.Run.ReviewedIdentity.BaseSha, completed.Run.ReviewedIdentity.HeadSha, null),
            AgentSessionHeadTransition.VerifiedAhead);
        var successor = AgentSessionBuilder.Build(input);
        Assert.True(successor.Succeeded, successor.FailureCode);
        var next = Assert.IsType<AgentSessionArtifact>(successor.Artifact);
        var again = AgentSessionRestorer.Restore(new(AgentSessionLocatorFamily.Current,
            AgentSessionRestoreIntent.Automatic, false, next.Plaintext,
            new(1, next.SessionSha256, new string('e', 64), current.BaseSha, current.HeadSha,
                next.Document.PredecessorStateSha256), trusted, next.Document.SessionId, current,
            User("Continue current review."), AgentSessionHeadTransition.SameHead, SyntheticContinuationCodec.Instance));
        Assert.True(again.Succeeded, again.Code);
        var resumedOutcome = await new AgentLoop(new OneResponseChatClient(_ =>
            new(new("assistant", [new ProjectToolCallContent("finish-next", "finish_review", FinishJson)]), new(1, 1), 1)),
            new NeverToolExecutor()).RunAsync(again.RunRequest!, default);
        Assert.True(resumedOutcome.Succeeded, resumedOutcome.Diagnostic?.Code);
        var rebuilt = AgentSessionBuilder.Build(new(again.RunRequest!, resumedOutcome, trusted,
            again.RunRequest!.InitialMessages.Length - 1, SyntheticContinuationCodec.Instance,
            new(next.Plaintext, next.SessionSha256, new string('e', 64), 1,
                current.BaseSha, current.HeadSha, next.Document.PredecessorStateSha256), AgentSessionHeadTransition.SameHead));
        Assert.True(rebuilt.Succeeded, rebuilt.FailureCode);
        var last = next.Document.CompletedRuns[1];
        var errorIndex = Enumerable.Range(0, last.Records.Length).Single(index =>
            last.Records[index] is AgentSessionToolErrorRecord { CallId: "reuse-path" });
        var error = (AgentSessionToolErrorRecord)last.Records[errorIndex];
        foreach (var invalid in new[] { "PRIVATE_CANARY", AgentRecoveryFeedback.BatchNotExecuted,
                     tool == "read_diff" ? AgentRecoveryFeedback.TrackedPathMissing : AgentRecoveryFeedback.ChangedPathMissing })
        {
            var changed = last with { Records = last.Records.SetItem(errorIndex, error with { ResultJson = invalid }) };
            Assert.False(AgentSessionValidation.TryValidateRecords(next.Document with
            {
                CompletedRuns = next.Document.CompletedRuns.SetItem(1, changed),
            }, SyntheticContinuationCodec.Instance, out _));
        }
    }

    private sealed class CurrentFileOnlyAccess : IReviewedFileAccess
    {
        private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("line\n");
        internal int Reads { get; private set; }
        public ReviewedFileMetadata InspectMetadata(ReviewedSnapshot snapshot, string path)
        {
            Assert.Equal("current.txt", path);
            return new(ReviewedFileAccessStatus.Success, Bytes.Length);
        }
        public ReviewedFileProbe Probe(ReviewedSnapshot snapshot, string path)
        {
            Assert.Equal("current.txt", path);
            return new(ReviewedFileAccessStatus.Success, Bytes.Length, new(1, 1));
        }
        public ValueTask<ReviewedFileRead> ReadAsync(ReviewedSnapshot snapshot, string path,
            ReviewedFileProbe expected, CancellationToken cancellationToken)
        {
            Assert.Equal("current.txt", path);
            Reads++;
            return ValueTask.FromResult(new ReviewedFileRead(ReviewedFileAccessStatus.Success, Bytes));
        }
    }
}
