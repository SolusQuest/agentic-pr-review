using System.Collections.Immutable;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Quality;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

// Generic completed-run projection only; installed SDK capacity and durable
// continuation require the separate actual archive proofs.
public sealed class CurrentWideQualityProjectionTests
{
    [Theory]
    [InlineData(256, 16)]
    [InlineData(257, 16)]
    [InlineData(272, 16)]
    [InlineData(63, 1)]
    public async Task CurrentFreshProjectionAdmitsActualWideCompletedRun(int tools, int batchSize)
    {
        var bundle = Path.Combine(AppContext.BaseDirectory, "fixtures", "agent", "r5", "replay");
        var fixture = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(bundle).Fixture);
        var input = fixture.Runs[0];
        var trusted = input.CreateTrustedRequest("r5-current-wide");
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var stable));
        var request = new AgentRunRequest(input.Input.ReviewedIdentity.Runtime, stable!.StablePlan,
            Guid.NewGuid().ToString("N"),
            [.. stable.ControlMessages, new("user", [new ProjectTextContent(input.InitialContext)])]);
        var root = Path.Combine(Path.GetTempPath(), "apr-current-wide-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var snapshot = input.CreateSnapshot(root);
            var chat = new WideChat(input.Script, tools, batchSize);
            var outcome = await new AgentLoop(chat,
                new SnapshotToolExecutor(snapshot, input.CreateFileAccess(snapshot)))
                .RunAsync(request, CancellationToken.None);
            Assert.True(outcome.Succeeded, outcome.Diagnostic?.Code);
            Assert.Equal(tools, outcome.Events.OfType<AgentToolResultEvent>().Count());
            Assert.Equal(tools + 1, outcome.Events.OfType<AgentToolCallEvent>().Count());
            Assert.Single(outcome.Events.OfType<AgentTerminalEvent>());
            Assert.Equal((tools + batchSize - 1) / batchSize + 1, chat.RequestBytes.Count);
            Assert.All(chat.RequestBytes, bytes => Assert.InRange(bytes, 1, AgentLimits.RequestBytes));
            var actual = new FreshQualityRun(request, outcome, trusted, request.InitialMessages.Length - 1);
            var descriptor = new EvaluationRunInput(input.Input.Id, "deterministic", EvaluationSource.Commit,
                EvaluationSource.Tree, EvaluationSource.Clean, input.ProviderConfigurationSha256);
            var subject = EvaluationSubject.AdmitFresh(actual, descriptor);
            Assert.NotNull(subject);
            var scored = EvaluationScorer.Evaluate(input.Expected, subject);
            Assert.Equal(EvaluationStatus.Completed, scored.ExecutionStatus);
            Assert.False(Directory.Exists(Path.Combine(root, "state")));
            // Detached diagnostics cannot replace the real completed execution.
            Assert.Null(EvaluationSubject.AdmitFresh(actual with
                { Outcome = outcome with { Events = outcome.Events.Remove(outcome.Events.OfType<AgentToolResultEvent>().Last()) } }, descriptor));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        Assert.False(Directory.Exists(root));
    }

    private sealed class WideChat(ReplayScript script, int tools, int batchSize) : IProjectChatClient
    {
        private int consumed;
        internal List<int> RequestBytes { get; } = [];

        public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            RequestBytes.Add(AgentRequestWriter.Write(request).Length);
            ImmutableArray<ReplayToolCall> calls;
            if (consumed < tools)
            {
                var count = Math.Min(batchSize, tools - consumed);
                calls = [.. Enumerable.Range(consumed, count).Select(index => index < 2
                    ? script.Turns[0].ToolCalls[index]
                    : new ReplayToolCall("wide-" + index, "list_changed_files", "{}"))];
                consumed += count;
            }
            else calls = script.Turns[^1].ToolCalls;
            return Task.FromResult(new ProjectChatResponse(new("assistant", calls.Select(call =>
                (ProjectChatContent)new ProjectToolCallContent(call.Id, call.Name, call.ArgumentsJson)).ToArray()), new(1, 1), 1));
        }
    }
}
