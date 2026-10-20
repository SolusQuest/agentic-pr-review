using System.Text;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Reset;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed class AuthoredResetWorkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetWorkUsesActualCurrentToolOutputForItsAuthoredTerminal(bool missingObservation)
    {
        var root = Directory.CreateTempSubdirectory("apr-authored-reset-").FullName;
        try
        {
            File.WriteAllText(Path.Join(root, "file.txt"), "current-only-file-fact\n");
            var identity = new ReviewedIdentity("42", 1, new('a', 40), new('b', 40));
            var snapshot = new ReviewedSnapshot(identity, root, ["file.txt"]);
            var trusted = new AgentTrustedRequest("42", 1, "trusted-workflow", "fixed policy"u8.ToArray(),
                "test-build", DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Adapter);
            Assert.True(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var materialized));
            var run = new AgentRunRequest(identity, materialized!.StablePlan, "authored-reset",
                [.. materialized.ControlMessages, new("user", [new ProjectTextContent("Review current files.")])]);
            var reference = missingObservation ? "missing-read" : "current-read";
            var terminal = "{\"summary\":\"current review\",\"findings\":[{\"severity\":\"high\",\"title\":\"finding\",\"message\":\"reviewed\",\"evidence\":[{\"observation_id\":\"$current:" +
                reference + "\",\"path\":\"file.txt\",\"start_line\":1,\"end_line\":1}]}]}";
            var script = new ReplayScript([
                new([new("current-read", "read_file", "{\"path\":\"file.txt\",\"start_line\":1,\"line_count\":1}")], "authored-current-note"),
                new([new("finish", "finish_review", terminal)], "authored-terminal-note")]);
            var provider = new ResetProbeProvider(0, false, script);
            Assert.True(ActionHostProviderApiKey.TryCreate("synthetic-unused-key", out var key));
            using var runner = provider.Create(new(trusted.ProviderId, trusted.ModelId, trusted.AdapterId),
                key!, snapshot, TimeProvider.System);
            if (missingObservation)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(run, default));
                Assert.Null(provider.Outcome);
                return;
            }
            var outcome = await runner.RunAsync(run, default);
            Assert.True(outcome.CompletedSessionEligible);
            Assert.Null(outcome.Continuation);
            Assert.Equal("file.txt", Assert.Single(Assert.Single(outcome.Review!.Findings).Evidence).Path);
            Assert.Equal(2, provider.Requests.Length);
            Assert.DoesNotContain("current-only-file-fact", Encoding.UTF8.GetString(provider.Requests[0]));
            Assert.Contains("current-only-file-fact", Encoding.UTF8.GetString(provider.Requests[1]));
            Assert.DoesNotContain("authored-current-note", Encoding.UTF8.GetString(provider.Requests[0]));
            Assert.Contains("authored-current-note", Encoding.UTF8.GetString(provider.Requests[1]));
        }
        finally { Directory.Delete(root, true); }
    }
}
