using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Reset;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Quality;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Reporting;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

// V1 gate coverage tests. The declared case inventories below are written
// out independently of R5CaseVerifier so that the tests exercise the
// outside-in contract: if either side shrinks or drifts, the gate must fail.
// Reports are built as the producers' own typed records and serialized
// through the same source-generated contexts, so happy-path fixtures can
// never drift from the schema the gate admits.
public sealed class R5VerifierCoverageTests
{
    private const string CorpusCanary = "APR242_TERMINAL_CANARY";
    private const string VerifierCanary = "APR250_VERIFIER_CANARY_9F3C";

    private static readonly string[] QualityIds =
    [
        "cs-defect", "cs-safe", "ts-defect", "ts-safe", "repository-rule",
        "sticky-only", "no-required-tool", "irrelevant-tool", "wrong-evidence",
        "wrong-location", "safe-invention", "duplicate-proposal",
        "pathless-proposal",
    ];

    private static readonly string[] ReplayIds = ["replay-seed", "replay-same", "replay-ahead"];
    private static readonly string[] IncrementalIds = ["incremental-seed", "incremental-same", "incremental-ahead"];

    private static readonly string[] ResetOwnerIds =
    [
        "reset_carry_historical_target_acceptance",
        "completed_reset_reentry",
        "accepted_successor_continuation",
        "post_match_target_substitution_rejected",
        "initial_ordinary_absence_recreation_accepted",
        "initial_ordinary_absence_late_target_rejected",
        "retry_ordinary_absence_recreation_accepted",
        "retry_ordinary_absence_late_target_rejected",
    ];

    private static readonly string[] LiveSelfTestIds =
        ["dry_run_pipeline", "execute_rejects_before_transport"];

    private static string Corpus(params string[] parts) =>
        Path.Combine(new[] { AppContext.BaseDirectory, "fixtures", "agent", "r5" }.Concat(parts).ToArray());

    private static string CorpusSha(params string[] parts) =>
        Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Corpus(parts)).Fixture).CorpusSha256;

    private static string WriteTemp(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), "r5v1-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, contents);
        return path;
    }

    private static string Hex(char c) => new(c, 64);

    private static IReadOnlyDictionary<string, EvaluationCode> Expected(params string[] parts) =>
        Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Corpus(parts)).Fixture)
            .Runs.ToDictionary(run => run.Input.Id, run => run.ExpectedCode);

    // An admission-valid outcome for each expected code class. The negative
    // shapes mirror the corpus scripts: evidence failures, scenario-code
    // violations and executor-level failures carry their own status/count
    // combinations, otherwise ReadOutcome admission rejects the row.
    private static EvaluationOutcome OutcomeFor(string caseId, string corpusSha, EvaluationCode code,
        string? configuration = null, string? sourceCommit = null, string? attemptSha = null)
    {
        var clean = EvaluationSource.Clean;
        var commit = sourceCommit ?? EvaluationSource.Commit;
        var tree = EvaluationSource.Tree;
        var cfg = configuration ?? Hex('7');
        var attempt = attemptSha ?? Hex('4');
        const ModelObservationStatus A = ModelObservationStatus.Adjudicated;
        const ModelObservationStatus N = ModelObservationStatus.NotEvaluated;
        const AssertionStatus P = AssertionStatus.Passed;
        const AssertionStatus F = AssertionStatus.Failed;
        const AssertionStatus NE = AssertionStatus.NotEvaluated;
        const EvaluationStatus C = EvaluationStatus.Completed;
        const EvaluationStatus X = EvaluationStatus.Failed;
        const EvaluationFailureSource No = EvaluationFailureSource.None;
        const EvaluationFailureKind Nk = EvaluationFailureKind.None;
        return code switch
        {
            EvaluationCode.ExecutionFailed or EvaluationCode.SubjectInvalid =>
                new(caseId, corpusSha, Hex('9'), cfg, attempt, null, commit, tree, clean,
                    "deterministic", X, NE, NE, N, code, EvaluationFailureSource.Agent,
                    EvaluationFailureKind.MalformedOutput, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            EvaluationCode.RequiredToolMissing or EvaluationCode.WrongSnapshot =>
                new(caseId, corpusSha, Hex('9'), cfg, attempt, Hex('5'), commit, tree, clean,
                    "deterministic", C, F, NE, N, code, No, Nk, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            EvaluationCode.RequiredObservationMissing =>
                new(caseId, corpusSha, Hex('9'), cfg, attempt, Hex('5'), commit, tree, clean,
                    "deterministic", C, F, NE, N, code, No, Nk, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            EvaluationCode.ExpectedFindingMissing =>
                new(caseId, corpusSha, Hex('9'), cfg, attempt, Hex('5'), commit, tree, clean,
                    "deterministic", C, P, F, A, code, No, Nk, 0, 0, 1, 0, 1, 0, 0, 0, 0, 0, 0),
            EvaluationCode.ProhibitedFinding =>
                new(caseId, corpusSha, Hex('9'), cfg, attempt, Hex('5'), commit, tree, clean,
                    "deterministic", C, P, F, A, code, No, Nk, 1, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0),
            EvaluationCode.DuplicateObservation =>
                new(caseId, corpusSha, Hex('9'), cfg, attempt, Hex('5'), commit, tree, clean,
                    "deterministic", C, P, F, A, code, No, Nk, 2, 0, 1, 1, 0, 1, 0, 2, 0, 0, 0),
            _ => new(caseId, corpusSha, Hex('9'), cfg, attempt, Hex('5'), commit, tree, clean,
                "deterministic", C, P, P, A, EvaluationCode.Scored, No, Nk, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0),
        };
    }

    private static EvaluationOutcome OutcomeRow(string caseId, string corpusSha, EvaluationCode code = EvaluationCode.Scored,
        string? configuration = null, string? sourceCommit = null, string? attemptSha = null) => new(
        caseId, corpusSha, Hex('9'), configuration ?? Hex('7'), attemptSha ?? Hex('4'), Hex('5'),
        sourceCommit ?? EvaluationSource.Commit, EvaluationSource.Tree, EvaluationSource.Clean,
        "deterministic", EvaluationStatus.Completed, AssertionStatus.Passed, AssertionStatus.Passed,
        ModelObservationStatus.Adjudicated, code, EvaluationFailureSource.None,
        EvaluationFailureKind.None, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static string OutcomeLine(EvaluationOutcome outcome) =>
        JsonSerializer.Serialize(outcome, EvaluationJsonContext.Default.EvaluationOutcome);

    private static EvaluationReportDocument EvalDoc(params EvaluationOutcome[] outcomes)
    {
        var rows = outcomes.Select(o => (ReadOnlyMemory<byte>)System.Text.Encoding.UTF8.GetBytes(OutcomeLine(o))).ToImmutableArray();
        return Assert.IsType<EvaluationReport>(EvaluationReport.Create(rows).Value).Document;
    }

    private static QualityCaseResult QualityCase(string id, EvaluationCode? expected = null,
        EvaluationCode? actual = null, bool verified = true) => new(
        id, "generated", null, expected ?? Expected("quality", "bundle")[id],
        actual ?? expected ?? Expected("quality", "bundle")[id], null, true, true, verified);

    private static string QualityReport(IEnumerable<QualityCaseResult>? cases = null,
        IEnumerable<EvaluationOutcome>? outcomes = null, string code = "verified",
        int? expected = null, int? executed = null, int? verified = null)
    {
        var corpusSha = CorpusSha("quality", "bundle");
        var expectedMap = Expected("quality", "bundle");
        var caseList = (cases ?? QualityIds.Select(id => QualityCase(id, expectedMap[id]))).ToArray();
        var outcomeList = outcomes?.ToArray() ??
            QualityIds.Select(id => OutcomeFor(id, corpusSha, expectedMap[id])).ToArray();
        var summary = new QualitySummary("deterministic", code, corpusSha,
            expected ?? caseList.Length, executed ?? caseList.Length, verified ?? caseList.Length,
            [..caseList]);
        var lines = outcomeList.Select(OutcomeLine)
            .Append(JsonSerializer.Serialize(summary, QualityJsonContext.Default.QualitySummary));
        return string.Join('\n', lines);
    }

    private static ReplayStep Step(string id, string corpusDir = "replay", bool accepted = true,
        bool predecessor = true) => new(
        id, Hex('9'), Hex('7'), Hex('8'), "initial", "completed", accepted, 1, Hex('3'), Hex('2'),
        1, 1, Expected(corpusDir)[id].ToString(), "Passed", "Passed", predecessor);

    private static string ReplayReport(IEnumerable<string> ids, string corpusDir = "replay",
        string code = "verified", string cleanup = "cleaned", string? normalized = null,
        string? sourceCommit = null, IEnumerable<ReplayStep>? steps = null)
    {
        var stepArray = (steps ?? ids.Select(id => Step(id, corpusDir))).ToImmutableArray();
        return JsonSerializer.Serialize(new ReplayReport("deterministic", code, cleanup, CorpusSha(corpusDir),
            sourceCommit ?? EvaluationSource.Commit, EvaluationSource.Tree, EvaluationSource.Clean,
            "completed", stepArray,
            [], normalized ?? ReplayProjection.Steps(stepArray)), ReplayExecutionJson.Default.ReplayReport);
    }

    private static readonly Lazy<Task<GrowthReport>> RealGrowth = new(async () =>
        await GrowthRunner.RunAsync(Corpus("growth")));

    private static async Task<string> GrowthJson(GrowthReport report) =>
        await Task.FromResult(Encoding.UTF8.GetString(
            AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles.GrowthJson.Write(report)));

    private static string ResetOwnerReport(IEnumerable<string>? names = null,
        string code = "r5_reset_owner_passed", string topology = "production_host_synthetic_ports",
        string? sourceCommit = null) =>
        JsonSerializer.Serialize(new ResetOwnerProbeReport("r5-reset-owner-v1", code, topology,
            sourceCommit ?? EvaluationSource.Commit, EvaluationSource.Tree, EvaluationSource.Clean,
            [..(names ?? ResetOwnerIds)]), ResetOwnerProbeJson.Default.ResetOwnerProbeReport);

    private static string LiveSelfTestReport(IEnumerable<string>? names = null,
        string code = "r5_live_self_test_passed", string cleanup = "cleaned") =>
        JsonSerializer.Serialize(new LiveSelfTestReport("r5-live-self-test-v1", code, cleanup,
            EvaluationSource.Commit, EvaluationSource.Tree, EvaluationSource.Clean,
            [..(names ?? LiveSelfTestIds)]), LiveSelfTestJson.Default.LiveSelfTestReport);

    private static string LivePlanSummaryLine(LiveRunSummary summary) =>
        JsonSerializer.Serialize(summary, LiveJsonContext.Default.LiveRunSummary);

    private static LiveRunSummary PlanSummary(int scheduled = 13, int attempted = 13, int unattempted = 0,
        int providerCalls = 0, string stop = "complete", string kind = "loopback", int invalid = 0,
        int? completed = null, int? failed = null, string? sourceCommit = null) =>
        new("r5-live-local-v1", kind, Hex('f'),
            CorpusSha("quality", "bundle"), sourceCommit ?? EvaluationSource.Commit,
            EvaluationSource.Tree, EvaluationSource.Clean, scheduled, attempted,
            completed ?? attempted - 2, failed ?? 2, invalid, unattempted, attempted * 4, providerCalls,
            0, 0, 0, 0, 0, 0, 0, false,
            new LiveTransportOutcomeCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            0, 104000, stop, "none", [new(8, "unknown", null, null), new(12, "unknown", null, null)]);

    private static string LivePlanOutput(LiveRunSummary? summary = null,
        IEnumerable<EvaluationOutcome>? outcomes = null)
    {
        var corpusSha = CorpusSha("quality", "bundle");
        var expected = Expected("quality", "bundle");
        var rows = (outcomes ?? QualityIds.Select((id, i) =>
            OutcomeFor(id, corpusSha, expected[id],
                attemptSha: new string('4', 62) + i.ToString("x2")))).ToArray();
        var doc = EvalDoc(rows);
        return string.Join('\n', rows.Select(OutcomeLine)
            .Append(JsonSerializer.Serialize(doc, EvaluationReportJsonContext.Default.EvaluationReportDocument))
            .Append(LivePlanSummaryLine(summary ?? PlanSummary())));
    }

    private static (int Code, JsonObject Verdict) Verify(string scenario, string report,
        string? corpus = null, string forbid = CorpusCanary)
    {
        var args = new List<string> { "verify-cases", "--scenario", scenario };
        if (corpus is not null) args.AddRange(["--corpus", corpus]);
        if (forbid.Length > 0) args.AddRange(["--forbid", forbid]);
        args.AddRange(["--report", WriteTemp(report)]);
        return R5CaseVerifier.Run(args.ToArray());
    }

    [Fact]
    public void QualityReportWithDeclaredCasesIsVerified()
    {
        var (code, verdict) = Verify("quality",
            QualityReport(),
            corpus: Corpus("quality", "bundle"));
        Assert.True(code == 0, verdict.ToJsonString());
        Assert.Equal("verified", verdict["code"]?.GetValue<string>());
        var parity = verdict["parity"]!;
        Assert.Equal(13, parity["cases"]!.AsArray().Count);
        Assert.Equal(EvaluationSource.Commit, parity["source_commit"]?.GetValue<string>());
        Assert.NotNull(parity["configuration_sha256"]?.GetValue<string>());
    }

    [Fact]
    public void QualityReportMissingCaseIsRejected()
    {
        var corpusSha = CorpusSha("quality", "bundle");
        var cases = QualityIds.Skip(1).Select(id => QualityCase(id));
        var expectedMap = Expected("quality", "bundle");
        var outcomes = QualityIds.Skip(1).Select(id => OutcomeFor(id, corpusSha, expectedMap[id]));
        var (code, verdict) = Verify("quality",
            QualityReport(cases, outcomes, expected: 13, executed: 12, verified: 12),
            corpus: Corpus("quality", "bundle"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_case_mismatch", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void QualityReportWithZeroCasesIsRejected()
    {
        var (code, verdict) = Verify("quality",
            QualityReport(Enumerable.Empty<QualityCaseResult>(), Enumerable.Empty<EvaluationOutcome>(),
                expected: 0, executed: 0, verified: 0),
            corpus: Corpus("quality", "bundle"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_empty", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void QualityReportWithDuplicateCaseIsRejected()
    {
        var rows = QualityIds.Skip(1).Select(id => QualityCase(id)).Append(QualityCase("cs-safe"));
        var (code, verdict) = Verify("quality", QualityReport(rows),
            corpus: Corpus("quality", "bundle"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_case_mismatch", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void QualityReportWithUnverifiedRowIsRejected()
    {
        var rows = QualityIds.Select((id, i) => QualityCase(id, verified: i != 3));
        var (code, verdict) = Verify("quality", QualityReport(rows),
            corpus: Corpus("quality", "bundle"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_code", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void QualityCaseMissingActualCodeMemberIsRejected()
    {
        // Scored is enum value zero; without complete-projection admission a
        // missing actual_code member would silently deserialize back to it.
        var report = (JsonObject)JsonNode.Parse(
            QualityReport(QualityIds.Select(id => QualityCase(id))).Split('\n')[^1])!;
        var cases = (JsonArray)report["cases"]!;
        var first = (JsonObject)cases[0]!;
        first.Remove("actual_code");
        var corpusSha = CorpusSha("quality", "bundle");
        var expectedMap = Expected("quality", "bundle");
        var outcomes = QualityIds.Select(id => OutcomeFor(id, corpusSha, expectedMap[id]));
        var mutated = string.Join('\n',
            outcomes.Select(OutcomeLine).Append(report.ToJsonString()));
        var (code, verdict) = Verify("quality", mutated, corpus: Corpus("quality", "bundle"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_report_invalid", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void QualityOutcomeDivergingFromSummaryCodeIsRejected()
    {
        var cases = QualityIds.Select((id, i) =>
            QualityCase(id, actual: i == 0 ? EvaluationCode.ExecutionFailed : null));
        var (code, verdict) = Verify("quality", QualityReport(cases),
            corpus: Corpus("quality", "bundle"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_code", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void QualityOutcomeWithWrongExpectedCodeIsRejected()
    {
        var corpusSha = CorpusSha("quality", "bundle");
        var expectedMap = Expected("quality", "bundle");
        var outcomes = QualityIds.Select((id, i) => OutcomeFor(id, corpusSha,
            i == 0 ? EvaluationCode.ProhibitedFinding : expectedMap[id]));
        var (code, verdict) = Verify("quality",
            QualityReport(outcomes: outcomes), corpus: Corpus("quality", "bundle"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_code", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void QualityOutcomeWithDivergentConfigurationIsRejected()
    {
        var corpusSha = CorpusSha("quality", "bundle");
        var outcomes = QualityIds.Select((id, i) =>
            OutcomeFor(id, corpusSha, Expected("quality", "bundle")[id], configuration: i == 0 ? Hex('a') : null));
        var (code, verdict) = Verify("quality",
            QualityReport(QualityIds.Select(id => QualityCase(id)), outcomes),
            corpus: Corpus("quality", "bundle"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_configuration", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void QualityOutcomeWithForeignSourceIdentityIsRejected()
    {
        var corpusSha = CorpusSha("quality", "bundle");
        var outcomes = QualityIds.Select((id, i) =>
            OutcomeFor(id, corpusSha, Expected("quality", "bundle")[id], sourceCommit: i == 0 ? new string('b', 40) : null));
        var (code, verdict) = Verify("quality",
            QualityReport(QualityIds.Select(id => QualityCase(id)), outcomes),
            corpus: Corpus("quality", "bundle"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_configuration", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void ReportContainingForbiddenCanaryIsRejected()
    {
        var report = QualityReport(QualityIds.Select(id => QualityCase(id)));
        var injected = report.Replace("\"category\":\"generated\"",
            "\"category\":\"" + VerifierCanary + "\"", StringComparison.Ordinal);
        var (code, verdict) = Verify("quality", injected,
            corpus: Corpus("quality", "bundle"), forbid: VerifierCanary);
        Assert.Equal(1, code);
        Assert.Equal("rejected_canary", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void ReportWhoseCorpusDoesNotMatchDeclaredCorpusIsRejected()
    {
        var report = QualityReport(QualityIds.Select(id => QualityCase(id)));
        var (code, verdict) = Verify("quality", report, corpus: Corpus("incremental"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_corpus_mismatch", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void MalformedReportIsRejected()
    {
        var (code, verdict) = R5CaseVerifier.Run(["verify-cases", "--scenario", "quality",
            "--corpus", Corpus("quality", "bundle"), "--report", WriteTemp("{not json")]);
        Assert.Equal(1, code);
        Assert.Equal("rejected_report_invalid", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void ReportWithMissingRequiredMemberIsRejected()
    {
        var report = (JsonObject)JsonNode.Parse(ReplayReport(ReplayIds))!;
        report.Remove("normalized_sha256");
        var (code, verdict) = Verify("replay", report.ToJsonString(), corpus: Corpus("replay"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_report_invalid", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void ReplayReportIsVerifiedAndMismatchIsRejected()
    {
        var ok = Verify("replay", ReplayReport(ReplayIds), corpus: Corpus("replay"));
        Assert.Equal(0, ok.Code);

        var bad = Verify("replay", ReplayReport(ReplayIds.Take(2)), corpus: Corpus("replay"));
        Assert.Equal(1, bad.Code);
        Assert.Equal("rejected_case_mismatch", bad.Verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void ReplayReportWithStaleNormalizedDigestIsRejected()
    {
        var (code, verdict) = Verify("replay", ReplayReport(ReplayIds, normalized: Hex('1')),
            corpus: Corpus("replay"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_report_invalid", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void ReplayReportWithForeignSourceIdentityIsRejected()
    {
        var (code, verdict) = Verify("replay", ReplayReport(ReplayIds, sourceCommit: new string('b', 40)),
            corpus: Corpus("replay"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_source", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void IncrementalReportIsVerified()
    {
        var (code, verdict) = Verify("incremental", ReplayReport(IncrementalIds, "incremental"),
            corpus: Corpus("incremental"));
        Assert.Equal(0, code);
        Assert.Equal("verified", verdict["code"]?.GetValue<string>());
    }

    [Fact]
    public void ReplayReportWithFailedCleanupIsRejected()
    {
        var (code, verdict) = Verify("replay", ReplayReport(ReplayIds, cleanup: "cleanup_failed"),
            corpus: Corpus("replay"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_cleanup", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public async Task GrowthReportIsVerifiedAgainstTerminalOracle()
    {
        var (code, verdict) = Verify("growth", await GrowthJson(await RealGrowth.Value),
            corpus: Corpus("growth"));
        Assert.Equal(0, code);
        Assert.Equal(4, verdict["parity"]!["profiles"]!.AsArray().Count);
        Assert.Equal(EvaluationSource.Commit, verdict["parity"]!["source_commit"]?.GetValue<string>());
    }

    [Fact]
    public async Task GrowthReportWithReorderedProfilesIsRejected()
    {
        var report = await RealGrowth.Value;
        var reordered = report.Profiles.Reverse().ToImmutableArray();
        var mutated = report with
        {
            Profiles = reordered,
            NormalizedSha256 = AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles.GrowthJson.Normalize(reordered),
        };
        var (code, verdict) = Verify("growth", await GrowthJson(mutated), corpus: Corpus("growth"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_case_mismatch", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public async Task GrowthReportWithMutatedTerminalClassificationIsRejected()
    {
        var report = await RealGrowth.Value;
        var profiles = report.Profiles.ToArray();
        var rows = profiles[0].Rows.ToArray();
        var last = rows[^1] with { Classification = "message_limit" };
        rows[^1] = last;
        // Recomputing the normalized digest keeps producer admission intact on
        // every other dimension; the oracle divergence is the only change.
        profiles[0] = profiles[0] with { Rows = rows.ToImmutableArray() };
        var mutated = report with
        {
            Profiles = profiles.ToImmutableArray(),
            NormalizedSha256 = AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles.GrowthJson.Normalize(profiles.ToImmutableArray()),
        };
        var (code, verdict) = Verify("growth", await GrowthJson(mutated), corpus: Corpus("growth"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_report_invalid", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public async Task GrowthReportWithForeignSourceIdentityIsRejected()
    {
        var report = await RealGrowth.Value;
        // The embedded outcome rows still carry the real source, so the closed
        // producer reader rejects the report before any oracle assertion runs.
        var mutated = report with { SourceCommit = new string('b', 40) };
        var (code, verdict) = Verify("growth", await GrowthJson(mutated), corpus: Corpus("growth"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_report_invalid", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public async Task GrowthReportWithRejectedNonTerminalRowIsRejected()
    {
        var report = await RealGrowth.Value;
        var profiles = report.Profiles.ToArray();
        var rows = profiles[0].Rows.ToArray();
        rows[0] = rows[0] with { Accepted = false, State = null };
        profiles[0] = profiles[0] with { Rows = rows.ToImmutableArray() };
        var mutated = report with
        {
            Profiles = profiles.ToImmutableArray(),
            NormalizedSha256 = AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles.GrowthJson.Normalize(profiles.ToImmutableArray()),
        };
        var (code, verdict) = Verify("growth", await GrowthJson(mutated), corpus: Corpus("growth"));
        Assert.Equal(1, code);
        Assert.StartsWith("rejected_", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public async Task GrowthReportWithFailedCleanupIsRejected()
    {
        var report = await RealGrowth.Value;
        var mutated = report with { Code = "cleanup_failed", Cleanup = "cleanup_failed" };
        var (code, verdict) = Verify("growth", await GrowthJson(mutated), corpus: Corpus("growth"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_code", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void ResetOwnerReportIsVerifiedAndShrunkReportRejected()
    {
        var ok = Verify("reset-owner", ResetOwnerReport());
        Assert.Equal(0, ok.Code);

        var bad = Verify("reset-owner", ResetOwnerReport(ResetOwnerIds.Skip(1)));
        Assert.Equal(1, bad.Code);
    }

    [Fact]
    public void ResetOwnerReportWithWrongTopologyOrSourceIsRejected()
    {
        var (code, verdict) = Verify("reset-owner", ResetOwnerReport(topology: "synthetic_ports"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_topology", verdict["reason"]?.GetValue<string>());

        var (code2, verdict2) = Verify("reset-owner", ResetOwnerReport(sourceCommit: new string('b', 40)));
        Assert.Equal(1, code2);
        Assert.Equal("rejected_source", verdict2["reason"]?.GetValue<string>());
    }

    [Fact]
    public void LiveSelfTestReportIsVerified()
    {
        var (code, verdict) = Verify("live-self-test", LiveSelfTestReport());
        Assert.Equal(0, code);
        Assert.Equal("verified", verdict["code"]?.GetValue<string>());
    }

    [Fact]
    public void LivePlanSummaryIsVerified()
    {
        var (code, verdict) = Verify("live-plan", LivePlanOutput(), corpus: Corpus("quality", "bundle"));
        Assert.Equal(0, code);
        var parity = verdict["parity"]!;
        Assert.Equal("loopback", parity["execution_kind"]?.GetValue<string>());
        Assert.Equal(EvaluationSource.Commit, parity["source_commit"]?.GetValue<string>());
        Assert.Equal(13, parity["cases"]!.AsArray().Count);
        Assert.NotNull(parity["configuration_sha256"]?.GetValue<string>());
    }

    [Fact]
    public async Task Output65536LiveScenarioUsesTheStrictPlanVerifier()
    {
        var plan = WriteTemp("{}");
        try
        {
            Assert.Equal(0, R5CaseVerifier.MakeLivePlan(Corpus("quality", "bundle"), plan,
                DeepSeekRequestProfile.Output65536).Item1);
            var lines = new List<string>();
            var run = await LiveRunner.RunAsync(plan, false,
                new LiveOptions { WriteLine = lines.Add }, CancellationToken.None);
            Assert.Equal(DeepSeekAdapterContext.Output65536Adapter,
                run.Journal.Document.Plan.Provider.AdapterId);
            Assert.Equal(0, Verify("live-output65536", string.Join('\n', lines),
                corpus: Corpus("quality", "bundle")).Code);
            lines[^1] = LivePlanSummaryLine(run.Summary with
            { Completed = run.Summary.Completed + 1 });
            var (code, verdict) = Verify("live-output65536", string.Join('\n', lines),
                corpus: Corpus("quality", "bundle"));
            Assert.Equal(1, code);
            Assert.Equal("rejected_report_invalid", verdict["reason"]?.GetValue<string>());
        }
        finally { File.Delete(plan); }
    }

    [Fact]
    public async Task LivePlanDiagnosticsMustCoverEveryFailedAttemptExactlyOnce()
    {
        var plan = WriteTemp("{}");
        try
        {
            Assert.Equal(0, R5CaseVerifier.MakeLivePlan(Corpus("quality", "bundle"), plan).Item1);
            var lines = new List<string>();
            var run = await LiveRunner.RunAsync(plan, false, new LiveOptions { WriteLine = lines.Add }, CancellationToken.None);
            Assert.Equal(new[] { "wrong-evidence", "pathless-proposal" },
                run.Outcomes.Where(o => o.ExecutionStatus == EvaluationStatus.Failed).Select(o => o.CaseId));
            Assert.Equal(new[] { 8, 12 }, run.Summary.AgentDiagnostics.Select(d => d.ScheduleIndex));
            Assert.Equal(0, Verify("live-plan", string.Join('\n', lines), corpus: Corpus("quality", "bundle")).Code);
            lines[^1] = LivePlanSummaryLine(run.Summary with
            { Completed = 13, Failed = 0, AgentDiagnostics = [] });
            Assert.Equal(1, Verify("live-plan", string.Join('\n', lines), corpus: Corpus("quality", "bundle")).Code);
            foreach (var diagnostics in new ImmutableArray<LiveAgentDiagnostic>[]
            {
                [], [run.Summary.AgentDiagnostics[0]], [run.Summary.AgentDiagnostics[1]],
                [run.Summary.AgentDiagnostics[0], run.Summary.AgentDiagnostics[0]],
                [run.Summary.AgentDiagnostics[0], run.Summary.AgentDiagnostics[1] with { ScheduleIndex = 0 }],
                [run.Summary.AgentDiagnostics[0], run.Summary.AgentDiagnostics[1] with { ScheduleIndex = 13 }],
            })
            {
                lines[^1] = LivePlanSummaryLine(run.Summary with { AgentDiagnostics = diagnostics });
                var (code, verdict) = Verify("live-plan", string.Join('\n', lines), corpus: Corpus("quality", "bundle"));
                Assert.Equal(1, code);
                Assert.Equal("rejected_report_invalid", verdict["reason"]?.GetValue<string>());
            }
            // Coverage is a set: a different serialization order is not missing evidence.
            lines[^1] = LivePlanSummaryLine(run.Summary with
            { AgentDiagnostics = run.Summary.AgentDiagnostics.Reverse().ToImmutableArray() });
            Assert.Equal(0, Verify("live-plan", string.Join('\n', lines), corpus: Corpus("quality", "bundle")).Code);
        }
        finally { File.Delete(plan); }
    }

    [Fact]
    public void LivePlanDiagnosticAttributionIsAdmittedOnlyFromClosedDomains()
    {
        var attributed = PlanSummary() with
        {
            AgentDiagnostics =
            [
                new(8, AgentFailureCodes.ToolArgumentsInvalid, 1, 0,
                    AgentToolRegistry.ReadFileName, LiveToolRejectionProjector.InvalidContract),
                new(12, "unknown", null, null),
            ],
        };
        Assert.Equal(0, Verify("live-plan", LivePlanOutput(attributed),
            corpus: Corpus("quality", "bundle")).Code);

        foreach (var rejected in new LiveAgentDiagnostic[]
        {
            attributed.AgentDiagnostics[0] with { Tool = VerifierCanary },
            attributed.AgentDiagnostics[0] with { Category = VerifierCanary },
            attributed.AgentDiagnostics[0] with { Category = LiveToolRejectionProjector.PathNotTracked },
            attributed.AgentDiagnostics[0] with { Tool = null },
            attributed.AgentDiagnostics[0] with { Code = AgentFailureCodes.ToolPathNotTracked },
        })
        {
            var report = attributed with { AgentDiagnostics = [rejected, attributed.AgentDiagnostics[1]] };
            var (code, verdict) = Verify("live-plan", LivePlanOutput(report),
                corpus: Corpus("quality", "bundle"));
            Assert.Equal(1, code);
            Assert.Equal("rejected_report_invalid", verdict["reason"]?.GetValue<string>());
        }
    }

    [Fact]
    public void LivePlanUnknownFailureStillRequiresAnExplicitDiagnostic()
    {
        var corpusSha = CorpusSha("quality", "bundle");
        var expected = Expected("quality", "bundle");
        var rows = QualityIds.Select((id, i) => OutcomeFor(id, corpusSha, expected[id],
            attemptSha: new string('4', 62) + i.ToString("x2"))).ToArray();
        rows[8] = rows[8] with { FailureSource = EvaluationFailureSource.Unknown, FailureKind = EvaluationFailureKind.Unknown };
        Assert.Equal(0, Verify("live-plan", LivePlanOutput(outcomes: rows), corpus: Corpus("quality", "bundle")).Code);
        var missing = PlanSummary() with { AgentDiagnostics = [new(12, "unknown", null, null)] };
        var (code, verdict) = Verify("live-plan", LivePlanOutput(missing, rows), corpus: Corpus("quality", "bundle"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_report_invalid", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public async Task LiveCoverageGateExecutesItsDeclaredFiveCasesAndRejectsMissingRowsAndDiagnostics()
    {
        var plan = WriteTemp("{}");
        try
        {
            Assert.Equal(0, R5CaseVerifier.MakeLivePlan(Corpus("live-coverage"), plan).Item1);
            var lines = new List<string>();
            var result = await LiveRunner.RunAsync(plan, false, new LiveOptions { WriteLine = lines.Add }, CancellationToken.None);
            Assert.Equal(5, result.Completed);
            Assert.All(result.Outcomes, row => Assert.Equal(EvaluationCode.Scored, row.Code));
            Assert.Empty(result.Summary.AgentDiagnostics);
            Assert.Equal(0, Verify("live-coverage", string.Join('\n', lines), corpus: Corpus("live-coverage")).Item1);
            Assert.NotEqual(0, Verify("live-coverage", string.Join('\n', lines.Skip(1)), corpus: Corpus("live-coverage")).Item1);
            var missing = JsonNode.Parse(lines[^1])!.AsObject();
            missing.Remove("agent_diagnostics");
            lines[^1] = missing.ToJsonString();
            Assert.NotEqual(0, Verify("live-coverage", string.Join('\n', lines), corpus: Corpus("live-coverage")).Item1);
            lines[^1] = LivePlanSummaryLine(result.Summary with { AgentDiagnostics = [new(0, "unknown", null, null)] });
            Assert.NotEqual(0, Verify("live-coverage", string.Join('\n', lines), corpus: Corpus("live-coverage")).Item1);
        }
        finally { File.Delete(plan); }
    }

    [Fact]
    public void LivePlanSummaryWithProviderCallsOrIncompleteScheduleIsRejected()
    {
        var (code, _) = Verify("live-plan", LivePlanOutput(PlanSummary(providerCalls: 1)),
            corpus: Corpus("quality", "bundle"));
        Assert.Equal(1, code);

        var (code2, _) = Verify("live-plan",
            LivePlanOutput(PlanSummary(attempted: 12, unattempted: 1)),
            corpus: Corpus("quality", "bundle"));
        Assert.Equal(1, code2);

        var (code3, verdict3) = Verify("live-plan",
            LivePlanOutput(PlanSummary(stop: "bound_stop")),
            corpus: Corpus("quality", "bundle"));
        Assert.Equal(1, code3);
        Assert.Equal("rejected_code", verdict3["reason"]?.GetValue<string>());
    }

    [Fact]
    public void LivePlanSummaryMissingActualProviderCallsIsRejected()
    {
        // Zero is the expected value; the member must still be present.
        var summary = (JsonObject)JsonNode.Parse(LivePlanSummaryLine(PlanSummary()))!;
        summary.Remove("actual_provider_calls");
        var mutated = string.Join('\n',
            LivePlanOutput().Split('\n').Take(QualityIds.Length + 1).Append(summary.ToJsonString()));
        var (code, verdict) = Verify("live-plan", mutated, corpus: Corpus("quality", "bundle"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_report_invalid", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void LivePlanOutcomeWithWrongExpectedCodeIsRejected()
    {
        // Accounting still balances; the per-case oracle catches the drift.
        var corpusSha = CorpusSha("quality", "bundle");
        var expectedMap = Expected("quality", "bundle");
        var outcomes = QualityIds.Select((id, i) => OutcomeFor(id, corpusSha,
            i == 0 ? EvaluationCode.ProhibitedFinding : expectedMap[id],
            attemptSha: new string('4', 62) + i.ToString("x2")));
        var (code, verdict) = Verify("live-plan", LivePlanOutput(outcomes: outcomes),
            corpus: Corpus("quality", "bundle"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_code", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void LivePlanBareSummaryIsRejected()
    {
        // A summary alone cannot prove the executed case population.
        var (code, verdict) = Verify("live-plan", LivePlanSummaryLine(PlanSummary()),
            corpus: Corpus("quality", "bundle"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_case_mismatch", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void LivePlanSummaryWithBrokenAccountingIsRejected()
    {
        var (code, verdict) = Verify("live-plan",
            LivePlanOutput(PlanSummary(attempted: 12, unattempted: 1, completed: 13)),
            corpus: Corpus("quality", "bundle"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_report_invalid", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void ParityVerificationAcceptsIdenticalReceiptsAndRejectsDivergence()
    {
        var verdict = JsonObject.Parse(
            """{"schema":"r5-v1-verdict-v1","code":"verified","scenario":"replay","parity":{"normalized_sha256":"aa","corpus_sha256":"bb"}}""")!;
        var same = WriteTemp(verdict.ToJsonString());
        var identical = WriteTemp(verdict.ToJsonString());
        var mutated = (JsonObject)verdict.DeepClone();
        mutated["parity"]!["normalized_sha256"] = "cc";
        var different = WriteTemp(mutated.ToJsonString());

        Assert.Equal(0, R5CaseVerifier.Run(["verify-cases", "--parity", same, identical]).Code);
        var (code, verdict2) = R5CaseVerifier.Run(["verify-cases", "--parity", same, different]);
        Assert.Equal(1, code);
        Assert.Equal("rejected_parity", verdict2["reason"]?.GetValue<string>());
    }

    [Fact]
    public void TrxAdmissionRequiresEveryDeclaredClassNonZeroAndAllPassed()
    {
        const string xml = """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testName="N.S.R5VerifierCoverageTests.A" outcome="Passed" />
                <UnitTestResult testName="N.S.R5SessionGrowthTests.B" outcome="Passed" />
              </Results>
            </TestRun>
            """;
        var ok = R5CaseVerifier.Run(["verify-cases", "--trx", WriteTemp(xml),
            "--require", "R5VerifierCoverageTests,R5SessionGrowthTests"]);
        Assert.Equal(0, ok.Code);

        var missing = R5CaseVerifier.Run(["verify-cases", "--trx", WriteTemp(xml),
            "--require", "R5VerifierCoverageTests,R5CapacityResetTests"]);
        Assert.Equal(1, missing.Code);
        Assert.Equal("rejected_case_mismatch", missing.Verdict["reason"]?.GetValue<string>());

        var failed = xml.Replace(
            "<UnitTestResult testName=\"N.S.R5SessionGrowthTests.B\" outcome=\"Passed\" />",
            "<UnitTestResult testName=\"N.S.R5SessionGrowthTests.B\" outcome=\"Failed\" />");
        var failedRun = R5CaseVerifier.Run(["verify-cases", "--trx", WriteTemp(failed),
            "--require", "R5VerifierCoverageTests,R5SessionGrowthTests"]);
        Assert.Equal(1, failedRun.Code);
        Assert.Equal("rejected_code", failedRun.Verdict["reason"]?.GetValue<string>());

        var empty = R5CaseVerifier.Run(["verify-cases", "--trx",
            WriteTemp("""<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results /></TestRun>"""),
            "--require", "R5VerifierCoverageTests"]);
        Assert.Equal(1, empty.Code);
        Assert.Equal("rejected_empty", empty.Verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void CleanupHelperDeletesOnlyMarkedPrivateRoots()
    {
        var root = Path.Combine(Path.GetTempPath(), "r5v1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".r5-v1-temp-root"), "");
        File.WriteAllText(Path.Combine(root, "report.json"), "{}");
        var (code, _) = R5CaseVerifier.Run(["verify-cases", "--cleanup", root]);
        Assert.Equal(0, code);
        Assert.False(Directory.Exists(root));

        var unmarked = Path.Combine(Path.GetTempPath(), "r5v1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unmarked);
        try
        {
            var (bad, verdict) = R5CaseVerifier.Run(["verify-cases", "--cleanup", unmarked]);
            Assert.Equal(1, bad);
            Assert.Equal("rejected_cleanup_root", verdict["reason"]?.GetValue<string>());
            Assert.True(Directory.Exists(unmarked));
        }
        finally
        {
            Directory.Delete(unmarked, true);
        }

        var repoPath = AppContext.BaseDirectory;
        var (repo, _) = R5CaseVerifier.Run(["verify-cases", "--cleanup", repoPath]);
        Assert.Equal(1, repo);
        Assert.True(Directory.Exists(repoPath));
    }

    [Fact]
    public void CleanupHelperRejectsSymlinkedAncestor()
    {
        if (OperatingSystem.IsWindows()) return;
        var real = Path.Combine(Path.GetTempPath(), "r5v1-real-" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(Path.GetTempPath(), "r5v1-link-" + Guid.NewGuid().ToString("N"));
        var marked = Path.Combine(real, "marked");
        var nested = Path.Combine(link, "marked");
        Directory.CreateDirectory(marked);
        try
        {
            File.WriteAllText(Path.Combine(marked, ".r5-v1-temp-root"), "");
            File.WriteAllText(Path.Combine(marked, "report.json"), "{}");
            Directory.CreateSymbolicLink(link, real);
            var (code, verdict) = R5CaseVerifier.Run(["verify-cases", "--cleanup", nested]);
            Assert.Equal(1, code);
            Assert.Equal("rejected_cleanup_root", verdict["reason"]?.GetValue<string>());
            Assert.True(Directory.Exists(marked));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(real)) Directory.Delete(real, true);
        }
    }

    [Fact]
    public void CleanupHelperFailsClosedWhenRootCannotBeRemoved()
    {
        var root = Path.Combine(Path.GetTempPath(), "r5v1-" + Guid.NewGuid().ToString("N"));
        var child = Path.Combine(root, "held");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(root, ".r5-v1-temp-root"), "");
        File.WriteAllText(Path.Combine(child, "report.json"), "{}");
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            else
                File.SetAttributes(Path.Combine(child, "report.json"), FileAttributes.ReadOnly);
            var (code, verdict) = R5CaseVerifier.Run(["verify-cases", "--cleanup", root]);
            Assert.Equal(1, code);
            Assert.Equal("rejected_cleanup_root", verdict["reason"]?.GetValue<string>());
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            else
                File.SetAttributes(Path.Combine(child, "report.json"), FileAttributes.Normal);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GeneratedLivePlanAdmitsAndBindsDeclaredInventory()
    {
        var outPath = Path.Combine(Path.GetTempPath(), "r5v1-plan-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var (code, verdict) = R5CaseVerifier.MakeLivePlan(Corpus("quality", "bundle"), outPath);
            Assert.Equal(0, code);
            Assert.Equal("plan_written", verdict["code"]?.GetValue<string>());
            var plan = LivePlanAdmission.Load(outPath, execute: false, CancellationToken.None);
            Assert.Equal(13, plan.Schedule.Length);
            Assert.Equal(QualityIds, plan.Schedule.ToArray());
            Assert.True(plan.Bounds.PerCall.MaxInputTokens > 0);
            Assert.Equal(verdict["plan_sha256"]?.GetValue<string>(), plan.Digest);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }
}
