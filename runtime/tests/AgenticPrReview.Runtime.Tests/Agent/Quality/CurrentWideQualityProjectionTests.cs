using System.Collections.Immutable;
using AuthoredReviewWork = AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Reset.AuthoredReviewWork;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Quality;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

// Readonly wide projection from finite actual APR tools. Native batching and
// model limits are historical; SDK capacity requires the actual archive proof.
public sealed class CurrentWideQualityProjectionTests
{
    [Theory]
    [InlineData(256, 16)]
    [InlineData(257, 16)]
    [InlineData(272, 16)]
    [InlineData(63, 1)]
    public async Task AuthoredWideProjectionRetainsEveryActualToolObservation(int tools, int batchSize)
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
            Assert.InRange(batchSize, 1, 16); // historical vector parameter, not an SDK profile
            var authored = Enumerable.Range(0, tools).Select(index => index < 2
                ? input.Script.Turns[0].ToolCalls[index]
                : new ReplayToolCall("wide-" + index, "list_changed_files", "{}")).ToArray();
            var steps = authored.Select(call => AuthoredReviewWork.PrepareCall(call.Id, call.Name, call.ArgumentsJson)).ToArray();
            var terminal = input.Script.Turns[^1].ToolCalls.Single();
            var requestBytes = new List<int>();
            var outcome = await AuthoredReviewWork.ExecuteAsync(request,
                new SnapshotToolExecutor(snapshot, input.CreateFileAccess(snapshot)), steps,
                _ => System.Text.Encoding.UTF8.GetBytes(terminal.ArgumentsJson),
                (value, _, phase) => { if (phase == "before") requestBytes.Add(AgentRequestWriter.Write(value).Length); },
                null, CancellationToken.None,
                terminalCallId: terminal.Id, recordAuthoredAccounting: false);
            Assert.True(outcome.Succeeded, outcome.Diagnostic?.Code);
            var results = outcome.Events.OfType<AgentToolResultEvent>().ToArray();
            var calls = outcome.Events.OfType<AgentToolCallEvent>().Where(call => call.Name != "finish_review").ToArray();
            Assert.Equal(tools, results.Length);
            Assert.Equal(calls.Select(call => call.CallId), results.Select(result => result.CallId));
            Assert.Equal(Enumerable.Range(0, tools).Select(index => index < 2
                ? input.Script.Turns[0].ToolCalls[index].Id : "wide-" + index), results.Select(result => result.CallId));
            foreach (var result in results)
            {
                using var parsed = JsonDocument.Parse(result.CanonicalResult.ToArray());
                Assert.Equal("ok", parsed.RootElement.GetProperty("status").GetString());
                if (result.Name == "read_file")
                    Assert.Equal(result.CallId == "seed_read" ? ReplayCoverage.Fact : ReplayCoverage.Current,
                        parsed.RootElement.GetProperty("lines")[0].GetProperty("text").GetString());
                else
                {
                    Assert.Equal("list_changed_files", result.Name);
                    Assert.Equal(0, parsed.RootElement.GetProperty("changes").GetArrayLength());
                }
            }
            Assert.Equal("Seed complete.", outcome.Review!.Summary);
            Assert.Empty(outcome.Review.Findings);
            Assert.Equal(tools + 1, outcome.Events.OfType<AgentToolCallEvent>().Count());
            Assert.Single(outcome.Events.OfType<AgentTerminalEvent>());
            Assert.Equal(tools + 1, requestBytes.Count);
            Assert.All(requestBytes, bytes => Assert.InRange(bytes, 1, AgentLimits.RequestBytes));
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

}
