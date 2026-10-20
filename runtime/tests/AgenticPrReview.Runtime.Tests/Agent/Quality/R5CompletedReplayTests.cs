using System.Security.Cryptography;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Serialization;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Quality;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Histories;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Prefix;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Quality;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class R5CompletedReplayTests
{
    private static string Bundle => Path.Combine(AppContext.BaseDirectory, "fixtures", "agent", "r5", "replay");

    [Theory]
    [InlineData(256, 16)]
    [InlineData(257, 16)]
    [InlineData(272, 16)]
    [InlineData(63, 1)]
    public async Task CurrentReplyAdmitsRealWideReplayAndRetainsIndependentBounds(int tools, int batchSize)
    {
        // The closed S5 disposition binds each of the thirteen former private
        // reply mutations to its actual current reader, authority or execution
        // oracle. Native SESSION reply fields grant no current state authority.
        await new CurrentWideQualityProjectionTests()
            .AuthoredWideProjectionRetainsEveryActualToolObservation(tools, batchSize);
    }

    [Theory]
    [InlineData(512, "unknown_failed")]
    [InlineData(513, "result_invalid")]
    public async Task AuthoredNativeFailureBoundsCannotMasqueradeAsCompletedEvaluation(int reportedTools, string expected)
    {
        var fixture = await EvaluationSelfTest.CreateAsync(findingCount: 0, includeTool: false);
        // Historical diagnostic envelope inputs, not current runtime limit proof.
        var outcome = AgentRunOutcome.Failure(AgentFailureCodes.ToolLimit, 33, reportedTools, []);
        Assert.False(outcome.Succeeded);
        Assert.False(outcome.CompletedSessionEligible);
        Assert.Null(EvaluationSubject.AdmitFresh(fixture.Input with { Outcome = outcome }, fixture.Run));
        var classified = EvaluationFailure.FromAgentOutcome(outcome);
        if (expected == "unknown_failed") Assert.Equal(EvaluationFailureSource.Unknown, classified.Source);
        else
        {
            Assert.Equal("result_invalid", expected);
            Assert.Same(EvaluationFailure.Invalid, classified);
        }
        var report = EvaluationScorer.Failure(fixture.Case, classified, fixture.Attempt);
        Assert.NotEqual(EvaluationStatus.Completed, report.ExecutionStatus);
        Assert.Equal(ModelObservationStatus.NotEvaluated, report.ModelStatus);
        Assert.Equal(AssertionStatus.NotEvaluated, report.ScenarioStatus);
    }

    [Theory]
    [InlineData(17, 17)]
    [InlineData(64, 1)]
    public async Task CurrentReplayStillRejects17MemberResponsesAnd65TurnScripts(int tools, int batchSize)
    {
        using var bundle = new MutableBundle();
        WriteCountReplay(bundle, tools, batchSize);
        var admission = ReplayAdmission.Load(bundle.Root);
        Assert.Equal(ReplayAdmissionCode.InvalidContent, admission.Code);
        string[] Snapshot() => Directory.GetFiles(bundle.Root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(file => Path.GetRelativePath(bundle.Root, file) + ":" +
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)))).ToArray();
        var before = Snapshot();
        var result = await QualityRunner.RunAsync(admission);
        Assert.Equal("input_invalid", result.Summary.Code);
        Assert.Empty(result.Executions);
        Assert.Empty(result.Summary.Cases);
        Assert.Equal(0, result.Summary.ExecutedCases);
        Assert.Equal(before, Snapshot());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"run_id\":\"1\",\"run_id\":\"2\"}")]
    [InlineData("{\"unknown\":true}")]
    [InlineData("[]")]
    public void ChildProtocolRejectsMalformedOrIncompleteDocuments(string json) =>
        Assert.False(ActionHostJsonCodec.TryReadLaunch(Encoding.UTF8.GetBytes(json), out _, out _));

    [Fact]
    public async Task PrivateProtocolBoundsUtf8AndStreams()
    {
        Assert.False(ActionHostJsonCodec.TryReadLaunch([0xff], out _, out _));
        Assert.False(ActionHostJsonCodec.TryReadLaunch(
            new byte[ActionHostContractBounds.MaximumLaunchDocumentBytes + 1], out _, out _));
        await Assert.ThrowsAsync<IOException>(() => ReplayWire.ReadBoundedAsync(new MemoryStream(new byte[100]), 99, CancellationToken.None));
    }

    private static void WriteCountReplay(MutableBundle bundle, int tools, int batchSize)
    {
        bundle.EditJson("script-0.json", script =>
        {
            var original = script["turns"]!.AsArray();
            var first = original[0]!;
            var calls = first["tool_calls"]!.AsArray();
            var turns = new JsonArray();
            for (var offset = 0; offset < tools; offset += batchSize)
            {
                var turn = first.DeepClone();
                turn["tool_calls"] = new JsonArray(Enumerable.Range(offset, Math.Min(batchSize, tools - offset)).Select(index =>
                {
                    return index < 2 ? calls[index]!.DeepClone() : new JsonObject
                    {
                        ["id"] = "count-" + index, ["name"] = "list_changed_files", ["arguments_json"] = "{}",
                    };
                }).ToArray());
                turns.Add(turn);
            }
            turns.Add(original[^1]!.DeepClone());
            script["turns"] = turns;
        });
        var unused = new[] { "context-1.txt", "context-2.txt", "script-1.json", "script-2.json" };
        bundle.EditJson("manifest.json", manifest =>
        {
            manifest["runs"] = new JsonArray(manifest["runs"]![0]!.DeepClone());
            manifest["files"] = new JsonArray(manifest["files"]!.AsArray()
                .Where(file => !unused.Contains(file!["path"]!.GetValue<string>())).Select(file => file!.DeepClone()).ToArray());
        });
        foreach (var file in unused) File.Delete(Path.Combine(bundle.Root, file));
    }

    private sealed class MutableBundle : IDisposable
    {
        private readonly string owner = ReplayProcess.CreatePrivateRoot();
        internal MutableBundle()
        {
            Root = Path.Combine(owner, "bundle"); Directory.CreateDirectory(Root);
            foreach (var file in Directory.GetFiles(Bundle)) File.Copy(file, Path.Combine(Root, Path.GetFileName(file)));
        }
        internal string Root { get; }
        internal void Edit(string file, Func<string, string> edit)
        { File.WriteAllText(Path.Combine(Root, file), edit(File.ReadAllText(Path.Combine(Root, file))), new UTF8Encoding(false)); Rehash(); }
        internal void EditJson(string file, Action<JsonNode> edit)
        { var json = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, file)))!; edit(json); File.WriteAllText(Path.Combine(Root, file), json.ToJsonString()); Rehash(); }
        private void Rehash()
        {
            var path = Path.Combine(Root, "manifest.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            foreach (var file in json["files"]!.AsArray())
            {
                var bytes = File.ReadAllBytes(Path.Combine(Root, file!["path"]!.GetValue<string>()));
                file["length"] = bytes.Length; file["sha256"] = AgentCanonical.HashRaw(bytes);
            }
            File.WriteAllText(path, json.ToJsonString());
        }
        public void Dispose() { Assert.True(ReplayProcess.Cleanup(owner)); }
    }
}
