using System.Collections.Immutable;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Quality;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

public sealed class R6QualitySandboxTests
{
    private static string Corpus => Path.Combine(AppContext.BaseDirectory, "fixtures", "agent", "r6", "quality-sandbox");

    [Theory]
    [InlineData("cs-safe", "{\"path\":null}", AgentFailureCodes.ToolArgumentsInvalid)]
    [InlineData("repository-rule", "{\"path\":\"src/NotTracked.cs\"}", AgentFailureCodes.ToolPathNotTracked)]
    public async Task FrozenSnapshotRejectsBadToolBatchBeforeAnyExecution(
        string caseId, string rejectedArguments, string expectedCode)
    {
        var fixture = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Corpus).Fixture);
        var run = fixture.Runs.Single(value => value.Input.CaseId == caseId);
        var first = run.Script.Turns[0] with
        {
            ToolCalls =
            [
                new ReplayToolCall("first", AgentToolRegistry.ReadFileName,
                    "{\"path\":\"src/Upload.cs\"}"),
                new ReplayToolCall("second", AgentToolRegistry.ReadFileName, rejectedArguments),
            ],
        };
        var script = new ReplayScript(run.Script.Turns.SetItem(0, first));
        var derived = run.Derive(run.Input, script, fixture.CorpusSha256);
        var result = await QualityRunner.ExecuteAsync(derived, Truth(caseId));
        Assert.False(result.AgentOutcome.Succeeded);
        Assert.Equal(expectedCode, result.AgentOutcome.Diagnostic?.Code);
        Assert.Equal(0, result.AgentOutcome.Diagnostic?.ToolCalls);
        Assert.Empty(result.AgentOutcome.Events.OfType<AgentToolResultEvent>());
    }

    private static QualityCaseSpec Truth(string id)
    {
        var source = QualityCoverage.Cases.Single(value => value.Id == id);
        var facts = id switch
        {
            "cs-defect" => new QualityFact[]
            {
                new("src/Caller.cs", 5, "    public static string Label(string key) => Lookup.Find(key).Trim();"),
                new("src/Lookup.cs", 5, "    public static string? Find(string key) => key == \"missing\" ? null : key.Trim();"),
            },
            "cs-safe" => new QualityFact[]
            {
                new("src/SafeCaller.cs", 5, "    public static string Label(string key) => Lookup.Find(key)?.Trim() ?? string.Empty;"),
                new("src/Lookup.cs", 5, "    public static string? Find(string key) => key == \"missing\" ? null : key.Trim();"),
            },
            "ts-defect" => new QualityFact[]
            {
                new("src/client.ts", 2, "export const timeout = config.timeoutMs || 3000;"),
                new("src/config.ts", 1, "// A zero timeout disables the local-job deadline."),
                new("src/config.ts", 2, "export const config = { timeoutMs: 0 };"),
            },
            "ts-safe" => new QualityFact[]
            {
                new("src/safe-client.ts", 2, "export const timeout = config.timeoutMs ?? 3000;"),
                new("src/config.ts", 1, "// A zero timeout disables the local-job deadline."),
                new("src/config.ts", 2, "export const config = { timeoutMs: 0 };"),
            },
            "repository-rule" => new QualityFact[]
            {
                new("src/Upload.cs", 5, "    public static void Failed(string accessToken) => Log(accessToken);"),
                new("src/Upload.cs", 6, "    private static void Log(string value) => System.Console.Error.WriteLine(value);"),
                new("rules/review.md", 3, "Never log an upload access token, including on a failure path."),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(id)),
        };
        return source with
        {
            Line = id is "cs-defect" or "cs-safe" or "repository-rule" ? 5 : 2,
            Facts = [.. facts],
        };
    }

    [Fact]
    public async Task FrozenPrSnapshotAdmitsAllFiveIndependentSourceTruths()
    {
        var admission = ReplayAdmission.Load(Corpus);
        var fixture = Assert.IsType<AdmittedReplayFixture>(admission.Fixture);
        Assert.Equal(ReplayAdmissionCode.Admitted, admission.Code);
        Assert.Equal(new[] { "cs-defect", "cs-safe", "ts-defect", "ts-safe", "repository-rule" },
            fixture.Runs.Select(run => run.Input.CaseId));
        foreach (var run in fixture.Runs)
        {
            var identity = run.Input.ReviewedIdentity;
            Assert.Equal("1383960589", identity.RepositoryId);
            Assert.Equal(1, identity.ReviewTarget);
            Assert.Equal("9eec5432d1e11e7823804f4de61f97dfb07822e6", identity.BaseSha);
            Assert.Equal("ac9d7a41f971e4b1071313c979b41b20637bfef4", identity.HeadSha);
            Assert.Equal(10, run.Input.Repository.Length);
            var truth = Truth(run.Input.CaseId);
            var audit = await QualityAudit.ObserveAsync(run, truth);
            Assert.True(audit.FactsMatch);
            Assert.Equal(64, audit.TargetObservationId.Length);
            foreach (var fact in truth.Facts)
            {
                Assert.False(run.InitialContext.Contains(fact.Text.Trim(), StringComparison.Ordinal));
                Assert.Contains(run.Expected.Input.RequiredObservations, required =>
                    required.Tool == AgentToolRegistry.ReadFileName && required.ObservationId is null &&
                    required.Coverage is { } span && span.Path == fact.Path &&
                    span.StartLine <= fact.Line && span.EndLine >= fact.Line);
            }
            var expected = run.Expected.Input;
            if (truth.Safe)
            {
                Assert.Empty(expected.Defects);
                var prohibited = Assert.Single(expected.ProhibitedFindings);
                Assert.Equal(truth.Target, prohibited.Path);
                Assert.Equal(truth.Line, prohibited.StartLine);
            }
            else
            {
                Assert.Empty(expected.ProhibitedFindings);
                var defect = Assert.Single(expected.Defects);
                Assert.Null(defect.ObservationId);
                Assert.Equal(truth.Target, defect.Path);
                Assert.Equal(truth.Line, defect.StartLine);
                Assert.Equal(run.Input.CaseId == "ts-defect" ? "medium" : "high", defect.Severity);
            }
        }
    }

    [Fact]
    public async Task DiffOnlyCitationCannotReplaceRequiredReturnedSourceReads()
    {
        var fixture = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Corpus).Fixture);
        var run = fixture.Runs.Single(value => value.Input.CaseId == "cs-defect");
        var truth = Truth("cs-defect");
        var complete = await QualityRunner.ExecuteAsync(run, truth);
        Assert.Equal(EvaluationCode.Scored, complete.Outcome.Code);
        var subject = Assert.IsType<EvaluationSubject>(complete.Subject);
        var diff = subject.GroundedObservations.Single(value => value.Tool == AgentToolRegistry.ReadDiffName);
        var terminal = run.Script.Turns[^1].ToolCalls.Single();
        var arguments = JsonNode.Parse(terminal.ArgumentsJson)!.AsObject();
        arguments["findings"]![0]!["evidence"]![0]!["observation_id"] = diff.Observation.ObservationId;
        var diffOnly = new ReplayScript([
            run.Script.Turns[0], run.Script.Turns[1],
            new ReplayScriptTurn([terminal with { ArgumentsJson = arguments.ToJsonString() }], ""),
        ]);
        var replay = run.Derive(run.Input, diffOnly, fixture.CorpusSha256);
        var attempt = await QualityRunner.ExecuteAsync(replay, truth);
        Assert.NotEqual(EvaluationCode.Scored, attempt.Outcome.Code);
        Assert.NotEqual(AssertionStatus.Passed, attempt.Outcome.EvidenceStatus);
    }
}
