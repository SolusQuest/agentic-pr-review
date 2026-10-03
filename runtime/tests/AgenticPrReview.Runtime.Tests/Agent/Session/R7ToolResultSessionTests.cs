using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.Tests.Agent.Session;

public sealed partial class AgentSessionRoundTripTests
{
    [Fact]
    public async Task R7LargeEscapedResultsBuildRestoreAndProjectAsExactHistory()
    {
        var root = Directory.CreateTempSubdirectory("apr-r7-c5-session-");
        try
        {
            var line = string.Concat(Enumerable.Repeat("雪\"\\\u0001", 10)) + new string('x', 60);
            var raw = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(line + "\n", 1000)));
            Assert.True(raw.Length > 64 * 1024);
            await File.WriteAllBytesAsync(Path.Combine(root.FullName, "large.txt"), raw);
            var executor = new SnapshotToolExecutor(new ReviewedSnapshot(Identity(), root.FullName, ["large.txt"]), new VerifiedReviewedFileAccess());
            Assert.True(AgentToolArguments.TryReadFile("{\"path\":\"large.txt\"}", out var readArguments));
            var preview = await executor.ExecuteAsync(new PreparedReadFileCall("preview", readArguments!), CancellationToken.None);
            Assert.True(preview.Succeeded, preview.FailureCode);
            Assert.InRange(preview.CanonicalResult!.Length, 32 * 1024 + 1, 64 * 1024);
            using var previewJson = JsonDocument.Parse(preview.CanonicalResult);
            Assert.Equal("result_bytes", previewJson.RootElement.GetProperty("truncation_reason").GetString());
            var terminal = Encoding.UTF8.GetString(AgentToolArguments.WriteFinishReview("grounded C5 result", [
                new AgentFinding("low", "grounded", "Returned line only.", [
                    new AgentEvidence(preview.Observation!.ObservationId, "large.txt", 1, 1),
                ]),
            ]));
            var trusted = new AgentSessionTrustedRequest("repo", 1, "workflow@trusted-sha", "trusted policy A"u8.ToArray(), "build",
                DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Adapter);
            var run = new AgentRunRequest(Identity(), Materialize(trusted, null), "session0", [.. Controls(trusted), User("review")]);
            var firstPosition = run.InitialMessages.Length;
            var responses = new Queue<ProjectChatResponse>([
                R7DeepSeekResponse(firstPosition, Enumerable.Range(0, 8).Select(index => new ProjectToolCallContent(
                    "read" + index, "read_file", "{\"path\":\"large.txt\"}")).ToArray()),
                R7DeepSeekResponse(firstPosition + 9, [new ProjectToolCallContent("finish", "finish_review", terminal)]),
            ]);
            var outcome = await new AgentLoop(new QueueChatClient(responses), executor).RunAsync(run, CancellationToken.None);
            Assert.True(outcome.CompletedSessionEligible, outcome.Diagnostic?.Code);
            var original = outcome.Events.OfType<AgentToolResultEvent>().Select(result => Encoding.UTF8.GetString(result.CanonicalResult.AsSpan())).ToArray();
            Assert.Equal(8, original.Length);
            Assert.True(original.Sum(Encoding.UTF8.GetByteCount) > 256 * 1024);
            Assert.All(original, value => Assert.InRange(Encoding.UTF8.GetByteCount(value), 32 * 1024 + 1, 64 * 1024));
            var built = AgentSessionBuilder.Build(new AgentSessionBuildInput(run, outcome, trusted,
                run.InitialMessages.Length - 1, DeepSeekReasoningContinuationCodec.Instance, null, AgentSessionHeadTransition.SameHead));
            Assert.True(built.Succeeded, built.FailureCode);
            var artifact = built.Artifact!;
            Assert.True(AgentSessionCodec.TryParse(artifact.Plaintext, out var parsed, out var parseFailure), parseFailure);
            Assert.Equal(artifact.SessionSha256, parsed!.SessionSha256);
            var restored = AgentSessionRestorer.Restore(new AgentSessionRestoreInput(
                AgentSessionLocatorFamily.Current, AgentSessionRestoreIntent.Automatic, false, artifact.Plaintext,
                new AgentSessionAcceptedState(0, artifact.SessionSha256, new string('e', 64), run.ReviewedIdentity.BaseSha, run.ReviewedIdentity.HeadSha, null),
                trusted, run.SessionId, run.ReviewedIdentity, User("continue"), AgentSessionHeadTransition.SameHead,
                DeepSeekReasoningContinuationCodec.Instance));
            Assert.True(restored.Succeeded, restored.Code);
            var logical = new ProjectChatRequest(restored.RunRequest!.InitialMessages, AgentToolRegistry.Definitions.ToArray(), restored.RunRequest.Continuation, true);
            var native = MinimalChatClient.Materialize(logical);
            var replayed = native.Messages.Where(message => message.Role == "tool").Select(message => Assert.Single(message.Contents).Text!).Where(text => text != "{}").ToArray();
            Assert.Equal(original, replayed);
            var projection = DeepSeekRequestWriter.Write(native);
            Assert.Equal(DeepSeekRequestWriteOutcome.Success, projection.Outcome);
            Assert.True(projection.HasBody);
            Assert.InRange(projection.Body.Length, 256 * 1024 + 1, 8 * 1024 * 1024);
            Assert.True(DeepSeekContextAdmission.TryEstimate(projection.Body.AsSpan(), out var bound));
            Assert.True(DeepSeekContextAdmission.Allows(bound, DeepSeekRequestProfile.Current));
            using var providerJson = JsonDocument.Parse(projection.Body.ToArray());
            var wireResults = providerJson.RootElement.GetProperty("messages").EnumerateArray()
                .Where(message => message.GetProperty("role").GetString() == "tool")
                .Select(message => message.GetProperty("content").GetString()!).Where(text => text != "{}").ToArray();
            Assert.Equal(original, wireResults);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("limits_sha256", "671d94914fa572ce6e6381abe1546c0a20c881115ea214ded89ef61bd21a8189")]
    [InlineData("toolset_sha256", "39b7291fbde316c6b0081c153fa960303d1d0e0ddeb9fc565bc0cbf821b5b502")]
    public async Task R7PreviousInspectionAuthorityCannotRestoreAsSelectedCurrent(string field, string previous)
    {
        var trusted = Trusted();
        var built = await BuildGenerationAsync(trusted, null, "g0", "finish0", false);
        var current = field == "limits_sha256" ? AgentCanonical.LimitsSha256() : AgentCanonical.ToolsetSha256(AgentToolRegistry.Definitions);
        var mutated = RawMutation(built.Artifact, "\"" + field + "\":\"" + current + "\"", "\"" + field + "\":\"" + previous + "\"");
        var restored = Restore(mutated, built.EnvelopeSha256, trusted, AgentSessionHeadTransition.SameHead);
        Assert.False(restored.Succeeded);
        Assert.Equal(AgentSessionCodes.ScopeMismatch, restored.Code);
        Assert.Null(restored.RunRequest);
        Assert.Null(restored.Artifact);
    }

    private static ProjectChatResponse R7DeepSeekResponse(int position, ProjectToolCallContent[] calls)
    {
        const string reasoning = "bounded synthetic reasoning";
        return new ProjectChatResponse(new ProjectChatMessage("assistant", [
            new ProjectReasoningContent(reasoning, string.Empty, DeepSeekReasoningContinuationCodec.FramingName, null, position, 0),
            .. calls,
        ]), new ProjectChatUsage(1, 1), 1, new ProjectContinuation(
            DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Adapter, "session0", [
                new ProjectContinuationItem(reasoning, string.Empty, DeepSeekReasoningContinuationCodec.FramingName, null, position, 0),
            ]));
    }
}
