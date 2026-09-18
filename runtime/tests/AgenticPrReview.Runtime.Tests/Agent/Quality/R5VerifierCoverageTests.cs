using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.ReviewEvaluationFixture;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

// V1 gate coverage tests. The declared case inventories below are written
// out independently of R5CaseVerifier so that the tests exercise the
// outside-in contract: if either side shrinks or drifts, the gate must fail.
public sealed class R5VerifierCoverageTests
{
    private const string QualityDir = "quality";
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

    private static string Corpus(params string[] parts) =>
        Path.Combine(new[] { AppContext.BaseDirectory, "fixtures", "agent", "r5" }.Concat(parts).ToArray());

    private static string WriteTemp(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), "r5v1-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, contents);
        return path;
    }

    private static JsonObject QualityReport(IEnumerable<JsonObject?> rows, int? expected = null,
        int? executed = null, int? verified = null, string code = "verified")
    {
        var cases = new JsonArray();
        foreach (var row in rows) cases.Add(row);
        return new JsonObject
        {
            ["mode"] = "deterministic",
            ["code"] = code,
            ["corpus_sha256"] = CorpusSha(QualityDir, "bundle"),
            ["expected_cases"] = expected ?? QualityIds.Length,
            ["executed_cases"] = executed ?? QualityIds.Length,
            ["verified_cases"] = verified ?? QualityIds.Length,
            ["cases"] = cases,
        };
    }

    private static JsonObject QualityRow(string id, string expected = "Scored", string? actual = null,
        bool verified = true) => new()
    {
        ["case_id"] = id,
        ["category"] = "generated",
        ["expected_code"] = expected,
        ["actual_code"] = actual ?? expected,
        ["verified"] = verified,
    };

    private static JsonObject ReplayReport(IEnumerable<string> ids, string corpusDir = "replay",
        string code = "verified", string cleanup = "cleaned") => new()
    {
        ["mode"] = "deterministic",
        ["code"] = code,
        ["cleanup"] = cleanup,
        ["corpus_sha256"] = CorpusSha(corpusDir),
        ["source_commit"] = new string('a', 40),
        ["source_tree"] = new string('b', 40),
        ["source_clean"] = true,
        ["operation"] = "completed",
        ["steps"] = new JsonArray(ids.Select(id => (JsonNode)new JsonObject
        {
            ["case_id"] = id,
            ["transition"] = "initial",
            ["code"] = "accepted",
            ["accepted"] = true,
            ["predecessor_preserved"] = true,
            ["quality_code"] = "scored",
            ["evidence_status"] = "complete",
        }).ToArray()),
        ["observations"] = new JsonArray(),
        ["normalized_sha256"] = new string('c', 64),
    };

    private static JsonObject GrowthReport(int? shortRows = 21, string? terminalStage = "build",
        string? terminalCode = "session_construction_limit", string? terminalClassification = "append_limit",
        string code = "verified", string cleanup = "cleaned", bool limitObserved = true)
    {
        var profiles = new JsonArray();
        var specs = new (string Profile, int Rows, string Stage, string Code, string Classification)[]
        {
            ("short", shortRows ?? 21, terminalStage ?? "build", terminalCode ?? "session_construction_limit",
                terminalClassification ?? "append_limit"),
            ("tools", 6, "agent", "agent_response_invalid", "message_limit"),
            ("continuation", 7, "agent", "agent_response_invalid", "continuation_limit"),
            ("updates", 13, "agent", "agent_response_invalid", "message_limit"),
        };
        foreach (var spec in specs)
        {
            var rows = new JsonArray();
            for (var i = 0; i < spec.Rows; i++)
            {
                var terminal = i == spec.Rows - 1;
                rows.Add((JsonNode)new JsonObject
                {
                    ["attempt"] = i + 1,
                    ["case_id"] = $"growth-{spec.Profile}-{i + 1}",
                    ["transition"] = i == 0 ? "initial" : "continuation",
                    ["stage"] = terminal ? spec.Stage : "agent",
                    ["code"] = terminal ? spec.Code : "accepted",
                    ["classification"] = terminal ? spec.Classification : "accepted",
                    ["accepted"] = !terminal,
                });
            }
            profiles.Add((JsonNode)new JsonObject
            {
                ["profile"] = spec.Profile,
                ["attempt_limit"] = spec.Rows,
                ["terminal_stage"] = spec.Stage,
                ["terminal_code"] = spec.Code,
                ["limit_observed"] = limitObserved,
                ["rows"] = rows,
            });
        }
        return new JsonObject
        {
            ["code"] = code,
            ["cleanup"] = cleanup,
            ["corpus_sha256"] = new string('d', 64),
            ["seed_corpus_sha256"] = CorpusSha("growth"),
            ["source_commit"] = new string('a', 40),
            ["source_tree"] = new string('b', 40),
            ["source_clean"] = true,
            ["profiles"] = profiles,
            ["normalized_sha256"] = new string('e', 64),
        };
    }

    private static JsonObject ResetOwnerReport(IEnumerable<string>? names = null, string code = "r5_reset_owner_passed") =>
        new()
        {
            ["schema"] = "r5-reset-owner-v1",
            ["code"] = code,
            ["topology"] = "production_host_synthetic_ports",
            ["sourceCommit"] = new string('a', 40),
            ["sourceTree"] = new string('b', 40),
            ["sourceClean"] = true,
            ["passedCases"] = new JsonArray((names ?? ResetOwnerIds).Select(id => (JsonNode)id).ToArray()),
        };

    private static JsonObject LivePlanSummary(int scheduled = 13, int attempted = 13, int unattempted = 0,
        int providerCalls = 0, string stop = "complete", string kind = "loopback", int invalid = 0) => new()
    {
        ["format"] = "r5-live-local-v1",
        ["execution_kind"] = kind,
        ["plan_sha256"] = new string('f', 64),
        ["corpus_sha256"] = CorpusSha(QualityDir, "bundle"),
        ["source_commit"] = new string('a', 40),
        ["source_tree"] = new string('b', 40),
        ["source_clean"] = true,
        ["scheduled"] = scheduled,
        ["attempted"] = attempted,
        ["completed"] = attempted,
        ["failed"] = 0,
        ["invalid"] = invalid,
        ["unattempted"] = unattempted,
        ["actual_provider_calls"] = providerCalls,
        ["stop_reason"] = stop,
    };

    private static string CorpusSha(params string[] parts) =>
        Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Corpus(parts)).Fixture).CorpusSha256;

    private static (int Code, JsonObject Verdict) Verify(string scenario, JsonObject report,
        string? corpus = null, string forbid = CorpusCanary)
    {
        var args = new List<string> { "verify-cases", "--scenario", scenario };
        if (corpus is not null) args.AddRange(["--corpus", corpus]);
        if (forbid.Length > 0) args.AddRange(["--forbid", forbid]);
        args.AddRange(["--report", WriteTemp(report.ToJsonString())]);
        return R5CaseVerifier.Run(args.ToArray());
    }

    [Fact]
    public void QualityReportWithDeclaredCasesIsVerified()
    {
        var (code, verdict) = Verify("quality", QualityReport(QualityIds.Select(id => QualityRow(id))),
            corpus: Corpus(QualityDir, "bundle"));
        Assert.Equal(0, code);
        Assert.Equal("verified", verdict["code"]?.GetValue<string>());
        Assert.Equal(13, verdict["parity"]!["cases"]!.AsArray().Count);
    }

    [Fact]
    public void QualityReportMissingCaseIsRejected()
    {
        var rows = QualityIds.Skip(1).Select(id => QualityRow(id));
        var (code, verdict) = Verify("quality",
            QualityReport(rows, expected: 13, executed: 12, verified: 12),
            corpus: Corpus(QualityDir, "bundle"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_case_mismatch", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void QualityReportWithZeroCasesIsRejected()
    {
        var (code, verdict) = Verify("quality",
            QualityReport(Enumerable.Empty<JsonObject?>(), expected: 0, executed: 0, verified: 0),
            corpus: Corpus(QualityDir, "bundle"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_empty", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void QualityReportWithDuplicateCaseIsRejected()
    {
        var rows = QualityIds.Skip(1).Select(id => QualityRow(id)).Append(QualityRow("cs-safe")).ToArray();
        var (code, verdict) = Verify("quality", QualityReport(rows), corpus: Corpus(QualityDir, "bundle"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_case_mismatch", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void QualityReportWithUnverifiedRowIsRejected()
    {
        var rows = QualityIds.Select(id => QualityRow(id)).ToArray();
        rows[3]!["verified"] = false;
        var (code, verdict) = Verify("quality", QualityReport(rows), corpus: Corpus(QualityDir, "bundle"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_code", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void ReportContainingForbiddenCanaryIsRejected()
    {
        var report = QualityReport(QualityIds.Select(id => QualityRow(id)));
        report["cases"]!.AsArray().First()!["agent_diagnostic"] = VerifierCanary;
        var (code, verdict) = Verify("quality", report, corpus: Corpus(QualityDir, "bundle"),
            forbid: VerifierCanary);
        Assert.Equal(1, code);
        Assert.Equal("rejected_canary", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void ReportWhoseCorpusDoesNotMatchDeclaredCorpusIsRejected()
    {
        var report = QualityReport(QualityIds.Select(id => QualityRow(id)));
        var (code, verdict) = Verify("quality", report, corpus: Corpus("incremental"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_corpus_mismatch", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void MalformedReportIsRejected()
    {
        var path = WriteTemp("{not json");
        var (code, verdict) = R5CaseVerifier.Run(["verify-cases", "--scenario", "quality",
            "--corpus", Corpus(QualityDir, "bundle"), "--report", path]);
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
    public void IncrementalReportIsVerified()
    {
        var (code, verdict) = Verify("incremental", ReplayReport(IncrementalIds, "incremental"), corpus: Corpus("incremental"));
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
    public void GrowthReportIsVerifiedAgainstTerminalOracle()
    {
        var (code, verdict) = Verify("growth", GrowthReport(), corpus: Corpus("growth"));
        Assert.Equal(0, code);
        Assert.Equal(4, verdict["parity"]!["profiles"]!.AsArray().Count);
    }

    [Fact]
    public void GrowthReportWithWrongTerminalClassificationIsRejected()
    {
        var (code, verdict) = Verify("growth", GrowthReport(terminalClassification: "message_limit"),
            corpus: Corpus("growth"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_terminal", verdict["reason"]?.GetValue<string>());
    }

    [Fact]
    public void GrowthReportWithMissingRowsIsRejected()
    {
        var (code, verdict) = Verify("growth", GrowthReport(shortRows: 20), corpus: Corpus("growth"));
        Assert.Equal(1, code);
        Assert.Equal("rejected_case_mismatch", verdict["reason"]?.GetValue<string>());
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
    public void LivePlanSummaryIsVerified()
    {
        var (code, verdict) = Verify("live-plan", LivePlanSummary(), corpus: Corpus(QualityDir, "bundle"));
        Assert.Equal(0, code);
        Assert.Equal("loopback", verdict["parity"]!["execution_kind"]?.GetValue<string>());
    }

    [Fact]
    public void LivePlanSummaryWithProviderCallsOrIncompleteScheduleIsRejected()
    {
        var (code, _) = Verify("live-plan", LivePlanSummary(providerCalls: 1),
            corpus: Corpus(QualityDir, "bundle"));
        Assert.Equal(1, code);

        var (code2, _) = Verify("live-plan", LivePlanSummary(attempted: 12, unattempted: 1),
            corpus: Corpus(QualityDir, "bundle"));
        Assert.Equal(1, code2);

        var (code3, verdict3) = Verify("live-plan", LivePlanSummary(stop: "bound_stop"),
            corpus: Corpus(QualityDir, "bundle"));
        Assert.Equal(1, code3);
        Assert.Equal("rejected_code", verdict3["reason"]?.GetValue<string>());
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
            var (code, verdict) = R5CaseVerifier.MakeLivePlan(Corpus(QualityDir, "bundle"), outPath);
            Assert.Equal(0, code);
            Assert.Equal("plan_written", verdict["code"]?.GetValue<string>());
            var plan = LivePlanAdmission.Load(outPath, execute: false, CancellationToken.None);
            Assert.Equal(13, plan.Schedule.Length);
            Assert.Equal(QualityIds, plan.Schedule.ToArray());
            Assert.False(plan.Bounds.PerCall.MaxInputTokens <= 0);
            Assert.Equal(verdict["plan_sha256"]?.GetValue<string>(), plan.Digest);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

}
