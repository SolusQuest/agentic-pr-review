using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Comparison;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Reset;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Quality;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Reporting;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "r6-gate") return await GateCommand.InvokeAsync(args);
            if (args.SequenceEqual(["economics-child"])) return await EconomicsChild.MainAsync();
            if (args.Length > 0 && args[0] is "economics-live" or "economics-plan") return await EconomicsCommand.InvokeAsync(args);
            if (args.Length > 0 && args[0] == "economics-compare")
                return ComparisonCommand.Invoke(args);
            if (args.Length > 0 && args[0] == "economics-price")
                return PricingCommand.Invoke(args);
            if (args.SequenceEqual(["live-local", "--dry-run", "--fixture", "self-test"]))
                return await LiveSelfTest.RunAsync();
            if (args is ["live-local", "--dry-run", "--plan", { } dryPlan])
                return await LiveRunner.InvokeAsync(dryPlan, execute: false);
            if (args is ["live-local", "--execute", "--plan", { } livePlan])
                return await LiveRunner.InvokeAsync(livePlan, execute: true);
            if (args is ["live-local", "--dry-run", "--plan", { } reviewDryPlan, "--adjudicate"])
                return await LiveRunner.InvokeAsync(reviewDryPlan, execute: false, adjudicate: true);
            if (args is ["live-local", "--execute", "--plan", { } reviewPlan, "--adjudicate"])
                return await LiveRunner.InvokeAsync(reviewPlan, execute: true, adjudicate: true);
            if (args.Length > 0 && args[0] == "live-local")
            {
                Console.Error.WriteLine("r5_evaluation_input_invalid");
                return 2;
            }
            if (args.Length > 0 && args[0] == "verify-cases")
                return R5CaseVerifier.Invoke(args);
            if (args.Length == 5 && args[0] == "r5-plan" && args[1] == "--corpus" && args[3] == "--out")
            {
                var (code, verdict) = R5CaseVerifier.MakeLivePlan(args[2], args[4]);
                Console.WriteLine(verdict.ToJsonString());
                return code;
            }
            if (args is ["r5-plan", "--corpus", { } candidateCorpus, "--out", { } candidateOut,
                "--profile", "output8192"])
            {
                var (code, verdict) = R5CaseVerifier.MakeLivePlan(candidateCorpus, candidateOut,
                    DeepSeekRequestProfile.Output8192);
                Console.WriteLine(verdict.ToJsonString());
                return code;
            }
            if (args is ["r5-plan", "--corpus", { } output65536Corpus, "--out", { } output65536Out,
                "--profile", "output65536"])
            {
                var (code, verdict) = R5CaseVerifier.MakeLivePlan(output65536Corpus, output65536Out,
                    DeepSeekRequestProfile.Output65536);
                Console.WriteLine(verdict.ToJsonString());
                return code;
            }
            if (args.SequenceEqual(["reset", "--fixture", "self-test"]))
                return await Growth.Reset.ResetOwnerProbe.RunAsync();
            if (args.SequenceEqual(["replay-child"])) return await ReplayChild.MainAsync();
            if (args.Length == 3 && args[0] == "replay" && args[1] == "--bundle")
            {
                var admitted = ReplayAdmission.Load(args[2]);
                if (admitted.Fixture is { } fixture && GrowthProfiles.IsCandidate(fixture))
                {
                    var growth = await GrowthRunner.RunAsync(args[2]);
                    var bytes = GrowthJson.Write(growth);
                    if (GrowthJson.Read(bytes) is null) throw new InvalidOperationException("growth_report_invalid");
                    Console.WriteLine(Encoding.UTF8.GetString(bytes));
                    return growth.ExitCode;
                }
                var replay = await ReplayRunner.RunAsync(args[2]);
                Console.WriteLine(Encoding.UTF8.GetString(ReplayWire.Write(replay)));
                return replay.ExitCode;
            }
            if (args.Length == 3 && args[0] == "quality" && args[1] == "--corpus")
            {
                var quality = await QualityRunner.RunAsync(args[2]);
                foreach (var execution in quality.Executions)
                    Console.WriteLine(Encoding.UTF8.GetString(EvaluationJson.Write(execution.Outcome)));
                Console.WriteLine(Encoding.UTF8.GetString(QualityRunner.Write(quality.Summary)));
                return quality.ExitCode;
            }
            if (!args.SequenceEqual(["evaluate", "--fixture", "self-test"]))
            {
                Console.Error.WriteLine("r5_evaluation_input_invalid");
                return 2;
            }
            var result = await EvaluationSelfTest.RunAsync();
            foreach (var outcome in result.Outcomes)
                Console.WriteLine(Encoding.UTF8.GetString(EvaluationJson.Write(outcome)));
            if (!result.Passed) Console.Error.WriteLine("r5_evaluation_self_test_failed");
            return result.Passed ? 0 : 1;
        }
        catch
        {
            Console.Error.WriteLine("r5_evaluation_infrastructure_failed");
            return 1;
        }
    }
}

// ---------------- R5-V1 coverage verification ----------------
// Outside-in declared inventory for the R5 deterministic gate. These sets are
// the gate's own authority: they are intentionally not derived from the
// corpora or the reports under test, so a producer that shrinks its coverage
// cannot pass silently.
internal static class R5CaseVerifier
{
    private const string VerdictSchema = "r5-v1-verdict-v1";
    private const string TempRootMarker = ".r5-v1-temp-root";

    private static readonly string[] QualityCases =
    [
        "cs-defect", "cs-safe", "ts-defect", "ts-safe", "repository-rule",
        "sticky-only", "no-required-tool", "irrelevant-tool", "wrong-evidence",
        "wrong-location", "safe-invention", "duplicate-proposal",
        "pathless-proposal",
    ];

    private static readonly string[] LiveCoverageCases =
        ["cs-defect", "cs-safe", "ts-defect", "ts-safe", "repository-rule"];

    private static readonly string[] IncrementalCases =
        ["incremental-seed", "incremental-same", "incremental-ahead"];

    private static readonly string[] ReplayCases =
        ["replay-seed", "replay-same", "replay-ahead"];

    // Per-profile terminal oracle from the measured #249 envelope: exact row
    // population plus the intended terminal (stage, code, classification).
    private static readonly (string Profile, int Rows, string Stage, string Code, string Classification)[]
        GrowthOracle =
        [
            ("short", 21, "build", "session_construction_limit", "append_limit"),
            ("tools", 6, "agent", "agent_response_invalid", "message_limit"),
            ("continuation", 7, "agent", "agent_response_invalid", "continuation_limit"),
            ("updates", 13, "agent", "agent_response_invalid", "message_limit"),
        ];

    private static readonly string[] ResetOwnerCases =
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

    private static readonly string[] LiveSelfTestCases =
        ["dry_run_pipeline", "execute_rejects_before_transport"];

    internal static int Invoke(string[] args)
    {
        var (code, verdict) = Run(args);
        Console.WriteLine(verdict.ToJsonString());
        return code;
    }

    internal static (int Code, JsonObject Verdict) Run(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[1] == "--cleanup")
                return CleanupRoot(args[2]);
            if (args.Length >= 4 && args[1] == "--trx")
            {
                var i = Array.FindIndex(args, a => a == "--require");
                var require = i > 0 && i + 1 < args.Length ? args[i + 1].Split(',') : [];
                return VerifyTrx(args[2], require);
            }
            if (args.Length == 4 && args[1] == "--parity")
                return VerifyParity(args[2], args[3]);
            if (args.Length >= 4 && args[1] == "--scenario")
            {
                string? corpus = null, report = null;
                string[] forbids = [];
                for (var i = 3; i + 1 < args.Length; i += 2)
                {
                    if (args[i] == "--corpus") corpus = args[i + 1];
                    else if (args[i] == "--forbid") forbids = args[i + 1].Split(',');
                    else if (args[i] == "--report") report = args[i + 1];
                    else return Reject("verify-cases", "input_invalid");
                }
                if (report is null) return Reject("verify-cases", "input_invalid");
                return VerifyScenario(args[2], corpus, report, forbids);
            }
            return Reject("verify-cases", "input_invalid");
        }
        catch
        {
            return Reject("verify-cases", "infrastructure_failed");
        }
    }

    private static (int, JsonObject) VerifyScenario(string scenario, string? corpus, string reportPath, string[] forbids)
    {
        if (CorpusRequired(scenario) != (corpus is not null))
            return Reject(scenario, "input_invalid");
        byte[] bytes;
        try { bytes = File.ReadAllBytes(reportPath); }
        catch { return Reject(scenario, "rejected_report_invalid"); }
        foreach (var forbid in forbids)
        {
            if (forbid.Length > 0 && bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(forbid)) >= 0)
                return Reject(scenario, "rejected_canary");
        }
        var lines = Encoding.UTF8.GetString(bytes)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return Reject(scenario, "rejected_report_invalid");
        // Independent binding: the declared corpus directory is re-admitted and
        // its digest must equal the identity the report claims to have run.
        string? corpusSha = null;
        // The same admission supplies the per-case expected outcome codes.
        IReadOnlyDictionary<string, EvaluationCode>? expected = null;
        if (corpus is not null)
        {
            try
            {
                var fixture = ReplayAdmission.Load(corpus).Fixture;
                corpusSha = fixture?.CorpusSha256;
                if (fixture is not null)
                    expected = fixture.Runs.ToDictionary(run => run.Input.Id, run => run.ExpectedCode);
            }
            catch { }
            if (corpusSha is null) return Reject(scenario, "rejected_corpus_mismatch");
        }
        var (parity, reason) = Extract(scenario, lines, corpusSha, expected);
        if (parity is null) return Reject(scenario, reason ?? "rejected_case_mismatch");
        return (0, new JsonObject
        {
            ["schema"] = VerdictSchema,
            ["code"] = "verified",
            ["scenario"] = scenario,
            ["parity"] = parity,
        });
    }

    private static bool CorpusRequired(string scenario) => scenario is not "reset-owner" and not "live-self-test";

    private static bool Hash(string? value) =>
        value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool GitSha(string? value) =>
        value is not null && value.Length == 40 && value.All(Uri.IsHexDigit);

    // The report's source identity must equal the executing artifact's own
    // compiled EvaluationSource — a report produced by a different artifact
    // (or a drifted/dirty tree) is not gate evidence for this build.
    private static bool SourceBinds(string? commit, string? tree, bool? clean) =>
        GitSha(commit) && GitSha(tree) && clean is not null &&
        commit == EvaluationSource.Commit && tree == EvaluationSource.Tree &&
        clean == EvaluationSource.Clean;

    private static T? Read<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        where T : class
    {
        try { return JsonSerializer.Deserialize(json, info); }
        catch { return null; }
    }

    // Contexts that do not enable RespectRequiredConstructorParameters treat
    // constructor parameters as optional: a missing member silently becomes
    // its default, which can be indistinguishable from a legitimate zero
    // value (e.g. actual_provider_calls). Admission therefore also requires
    // the raw object to equal its complete producer projection — the same
    // rebuild/DeepEquals pattern the report readers use.
    private static T? ReadComplete<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        where T : class
    {
        try
        {
            var value = JsonSerializer.Deserialize(json, info);
            if (value is null) return null;
            var raw = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
            var projected = JsonSerializer.SerializeToElement(value, info);
            return JsonElement.DeepEquals(raw.RootElement, projected) ? value : null;
        }
        catch { return null; }
    }

    private static JsonObject SourceParity() => new()
    {
        ["source_commit"] = EvaluationSource.Commit,
        ["source_tree"] = EvaluationSource.Tree,
        ["source_clean"] = EvaluationSource.Clean,
    };

    private static (JsonObject? Parity, string? Reason) Extract(
        string scenario, string[] lines, string? corpusSha,
        IReadOnlyDictionary<string, EvaluationCode>? expected)
    {
        switch (scenario)
        {
            case "quality": return ExtractQuality(lines, corpusSha, expected);
            case "replay": return ExtractReplay(lines[^1], ReplayCases, corpusSha, expected);
            case "incremental": return ExtractReplay(lines[^1], IncrementalCases, corpusSha, expected);
            case "growth": return ExtractGrowth(lines[^1], corpusSha);
            case "reset-owner": return ExtractResetOwner(lines[^1]);
            case "live-self-test": return ExtractLiveSelfTest(lines[^1]);
            case "live-plan": return ExtractLivePlan(lines, corpusSha, expected, QualityCases);
            case "live-candidate": return ExtractLivePlan(lines, corpusSha, expected, QualityCases);
            case "live-coverage": return ExtractLivePlan(lines, corpusSha, expected, LiveCoverageCases);
            case "quality-sandbox": return ExtractLivePlan(lines, corpusSha, expected, LiveCoverageCases);
            default: return (null, "input_invalid");
        }
    }

    private static (JsonObject?, string?) ExtractQuality(string[] lines, string? corpusSha,
        IReadOnlyDictionary<string, EvaluationCode>? expected)
    {
        var summary = ReadComplete(lines[^1], QualityJsonContext.Default.QualitySummary);
        if (summary is null) return (null, "rejected_report_invalid");
        if (summary.Code != "verified") return (null, "rejected_code");
        if (summary.Cases.IsDefault) return (null, "rejected_report_invalid");
        if (summary.Cases.Length == 0 || lines.Length == 1) return (null, "rejected_empty");
        if (summary.Mode != "deterministic" || summary.CorpusSha256 != corpusSha)
            return (null, "rejected_corpus_mismatch");
        if (summary.ExpectedCases != QualityCases.Length ||
            summary.ExecutedCases != QualityCases.Length ||
            summary.VerifiedCases != QualityCases.Length ||
            summary.Cases.Length != QualityCases.Length)
            return (null, "rejected_case_mismatch");
        // The preceding Q1 outcome rows carry the per-case source and
        // configuration identities; they are admitted through the closed
        // outcome reader, must cover the declared set exactly and share one
        // configuration, one source identity and deterministic mode.
        if (lines.Length - 1 != QualityCases.Length) return (null, "rejected_case_mismatch");
        string? configuration = null;
        for (var i = 0; i < QualityCases.Length; i++)
        {
            var outcome = EvaluationJson.ReadOutcome(Encoding.UTF8.GetBytes(lines[i]));
            if (outcome is null) return (null, "rejected_report_invalid");
            if (outcome.CaseId != QualityCases[i] || outcome.CorpusSha256 != corpusSha ||
                !Hash(outcome.CaseSha256))
                return (null, "rejected_case_mismatch");
            if (!Hash(outcome.ConfigurationSha256)) return (null, "rejected_configuration");
            configuration ??= outcome.ConfigurationSha256;
            if (outcome.ConfigurationSha256 != configuration ||
                !SourceBinds(outcome.SourceCommit, outcome.SourceTree, outcome.SourceClean) ||
                outcome.Mode != "deterministic")
                return (null, "rejected_configuration");
            var row = summary.Cases[i];
            if (row.CaseId != QualityCases[i]) return (null, "rejected_case_mismatch");
            if (outcome.Code != row.ActualCode) return (null, "rejected_code");
            if (expected is not null && (!expected.TryGetValue(row.CaseId, out var want) ||
                outcome.Code != want || row.ExpectedCode != want))
                return (null, "rejected_code");
            if (!row.Verified) return (null, "rejected_code");
        }
        var cases = new JsonArray();
        foreach (var row in summary.Cases)
        {
            cases.Add((JsonNode)new JsonObject
            {
                ["case_id"] = row.CaseId,
                ["expected_code"] = row.ExpectedCode.ToString(),
                ["actual_code"] = row.ActualCode.ToString(),
                ["verified"] = true,
            });
        }
        var parity = SourceParity();
        parity["corpus_sha256"] = corpusSha;
        parity["configuration_sha256"] = configuration;
        parity["mode"] = "deterministic";
        parity["expected_cases"] = QualityCases.Length;
        parity["executed_cases"] = QualityCases.Length;
        parity["verified_cases"] = QualityCases.Length;
        parity["cases"] = cases;
        return (parity, null);
    }

    private static (JsonObject?, string?) ExtractReplay(string line, string[] declared, string? corpusSha,
        IReadOnlyDictionary<string, EvaluationCode>? expected)
    {
        var report = Read(line, ReplayExecutionJson.Default.ReplayReport);
        if (report is null) return (null, "rejected_report_invalid");
        if (report.Code != "verified") return (null, "rejected_code");
        if (report.Cleanup != "cleaned") return (null, "rejected_cleanup");
        if (report.CorpusSha256 != corpusSha) return (null, "rejected_corpus_mismatch");
        if (!Hash(report.NormalizedSha256)) return (null, "rejected_report_invalid");
        if (!SourceBinds(report.SourceCommit, report.SourceTree, report.SourceClean))
            return (null, "rejected_source");
        if (report.Steps.IsDefault) return (null, "rejected_report_invalid");
        if (report.Steps.Length != declared.Length)
            return (null, report.Steps.Length == 0 ? "rejected_empty" : "rejected_case_mismatch");
        // The semantic receipt must be the producer-defined digest of these
        // steps, not merely a well-formed hash.
        if (report.NormalizedSha256 != ReplayProjection.Steps(report.Steps))
            return (null, "rejected_report_invalid");
        var steps = new JsonArray();
        for (var i = 0; i < declared.Length; i++)
        {
            var step = report.Steps[i];
            if (step.CaseId != declared[i] || !step.Accepted || !step.PredecessorPreserved)
                return (null, "rejected_case_mismatch");
            if (expected is not null && (!expected.TryGetValue(step.CaseId, out var want) ||
                step.QualityCode != want.ToString()))
                return (null, "rejected_code");
            steps.Add((JsonNode)new JsonObject
            {
                ["case_id"] = step.CaseId,
                ["transition"] = step.Transition,
                ["code"] = step.Code,
                ["accepted"] = true,
                ["predecessor_preserved"] = step.PredecessorPreserved,
                ["quality_code"] = step.QualityCode,
                ["evidence_status"] = step.EvidenceStatus,
            });
        }
        var parity = SourceParity();
        parity["corpus_sha256"] = corpusSha;
        parity["normalized_sha256"] = report.NormalizedSha256;
        parity["steps"] = steps;
        return (parity, null);
    }

    private static (JsonObject?, string?) ExtractGrowth(string line, string? corpusSha)
    {
        // The closed producer reader re-derives the corpus and normalized
        // digests and relinks every row, state sample and evaluation outcome;
        // V1 then applies its independent declarations on the admitted value.
        var report = GrowthJson.Read(Encoding.UTF8.GetBytes(line));
        if (report is null) return (null, "rejected_report_invalid");
        if (report.Code != "verified") return (null, "rejected_code");
        if (report.Cleanup != "cleaned") return (null, "rejected_cleanup");
        if (report.SeedCorpusSha256 != corpusSha || !Hash(report.CorpusSha256) ||
            !Hash(report.NormalizedSha256))
            return (null, "rejected_corpus_mismatch");
        if (!SourceBinds(report.SourceCommit, report.SourceTree, report.SourceClean))
            return (null, "rejected_source");
        if (report.Profiles.Length != GrowthOracle.Length) return (null, "rejected_case_mismatch");
        var parity = new JsonArray();
        for (var i = 0; i < GrowthOracle.Length; i++)
        {
            var oracle = GrowthOracle[i];
            var profile = report.Profiles[i];
            if (profile.Profile != oracle.Profile) return (null, "rejected_case_mismatch");
            if (!profile.LimitObserved) return (null, "rejected_terminal");
            var rows = profile.Rows;
            if (rows.Length != oracle.Rows) return (null, "rejected_case_mismatch");
            for (var r = 0; r < rows.Length - 1; r++)
                if (!rows[r].Accepted) return (null, "rejected_terminal");
            var terminal = rows[^1];
            if (profile.TerminalStage != oracle.Stage || profile.TerminalCode != oracle.Code ||
                terminal.Classification != oracle.Classification || terminal.Code != oracle.Code ||
                terminal.Accepted)
                return (null, "rejected_terminal");
            parity.Add((JsonNode)new JsonObject
            {
                ["profile"] = oracle.Profile,
                ["rows"] = oracle.Rows,
                ["terminal_stage"] = oracle.Stage,
                ["terminal_code"] = oracle.Code,
                ["terminal_classification"] = oracle.Classification,
            });
        }
        var result = SourceParity();
        result["seed_corpus_sha256"] = corpusSha;
        result["corpus_sha256"] = report.CorpusSha256;
        result["normalized_sha256"] = report.NormalizedSha256;
        result["profiles"] = parity;
        return (result, null);
    }

    private static (JsonObject?, string?) ExtractResetOwner(string line)
    {
        var report = ReadComplete(line, ResetOwnerProbeJson.Default.ResetOwnerProbeReport);
        if (report is null) return (null, "rejected_report_invalid");
        if (report.Schema != "r5-reset-owner-v1" || report.PassedCases is null)
            return (null, "rejected_report_invalid");
        if (report.Code != "r5_reset_owner_passed") return (null, "rejected_code");
        if (report.Topology != "production_host_synthetic_ports")
            return (null, "rejected_topology");
        if (!SourceBinds(report.SourceCommit, report.SourceTree, report.SourceClean))
            return (null, "rejected_source");
        if (report.PassedCases.Length == 0) return (null, "rejected_empty");
        if (report.PassedCases.Distinct().Count() != report.PassedCases.Length ||
            !report.PassedCases.SequenceEqual(ResetOwnerCases))
            return (null, "rejected_case_mismatch");
        var parity = SourceParity();
        parity["topology"] = report.Topology;
        parity["passed_cases"] = new JsonArray(report.PassedCases.Select(id => (JsonNode)id).ToArray());
        return (parity, null);
    }

    private static (JsonObject?, string?) ExtractLiveSelfTest(string line)
    {
        var report = ReadComplete(line, LiveSelfTestJson.Default.LiveSelfTestReport);
        if (report is null) return (null, "rejected_report_invalid");
        if (report.Schema != "r5-live-self-test-v1" || report.PassedCases is null)
            return (null, "rejected_report_invalid");
        if (report.Code != "r5_live_self_test_passed") return (null, "rejected_code");
        if (report.Cleanup != "cleaned") return (null, "rejected_cleanup");
        if (!SourceBinds(report.SourceCommit, report.SourceTree, report.SourceClean))
            return (null, "rejected_source");
        if (!report.PassedCases.SequenceEqual(LiveSelfTestCases))
            return (null, report.PassedCases.Length == 0 ? "rejected_empty" : "rejected_case_mismatch");
        var parity = SourceParity();
        parity["passed_cases"] = new JsonArray(report.PassedCases.Select(id => (JsonNode)id).ToArray());
        return (parity, null);
    }

    private static (JsonObject?, string?) ExtractLivePlan(string[] lines, string? corpusSha,
        IReadOnlyDictionary<string, EvaluationCode>? expected, string[] declared)
    {
        // The complete live output shape is Q1 outcome rows, one Q4 evaluation
        // report and the run summary. A bare summary is incomplete evidence:
        // the exact executed case population is proven by the Q1 rows and
        // correlated with the admitted Q4 document.
        if (lines.Length != declared.Length + 2)
            return (null, "rejected_case_mismatch");
        var outcomes = new EvaluationOutcome[declared.Length];
        var attempts = new HashSet<string>(StringComparer.Ordinal);
        string? configuration = null;
        for (var i = 0; i < declared.Length; i++)
        {
            var outcome = EvaluationJson.ReadOutcome(Encoding.UTF8.GetBytes(lines[i]));
            if (outcome is null) return (null, "rejected_report_invalid");
            if (outcome.CaseId != declared[i] || outcome.CorpusSha256 != corpusSha ||
                !Hash(outcome.CaseSha256))
                return (null, "rejected_case_mismatch");
            if (expected is not null && (!expected.TryGetValue(outcome.CaseId, out var want) ||
                outcome.Code != want))
                return (null, "rejected_code");
            if (!Hash(outcome.ConfigurationSha256) || outcome.AttemptSha256 is null ||
                !attempts.Add(outcome.AttemptSha256))
                return (null, "rejected_case_mismatch");
            configuration ??= outcome.ConfigurationSha256;
            if (outcome.ConfigurationSha256 != configuration ||
                !SourceBinds(outcome.SourceCommit, outcome.SourceTree, outcome.SourceClean) ||
                outcome.Mode != "deterministic")
                return (null, "rejected_configuration");
            outcomes[i] = outcome;
        }
        var evaluation = EvaluationReportJson.Read(Encoding.UTF8.GetBytes(lines[declared.Length]));
        if (!evaluation.Succeeded || evaluation.Value!.Document.Outcomes.Length != declared.Length)
            return (null, "rejected_report_invalid");
        var byCase = outcomes.ToDictionary(o => o.CaseId, StringComparer.Ordinal);
        foreach (var row in evaluation.Value!.Document.Outcomes)
        {
            if (!byCase.TryGetValue(row.CaseId, out var q1) ||
                row.Code != q1.Code || row.AttemptSha256 != q1.AttemptSha256)
                return (null, "rejected_case_mismatch");
        }
        var report = ReadComplete(lines[^1], LiveJsonContext.Default.LiveRunSummary);
        if (report is null) return (null, "rejected_report_invalid");
        if (report.Format != "r5-live-local-v1") return (null, "rejected_report_invalid");
        if (report.StopReason != "complete") return (null, "rejected_code");
        if (report.Scheduled != report.Attempted + report.Unattempted ||
            report.Attempted != report.Completed + report.Failed + report.Invalid)
            return (null, "rejected_report_invalid");
        if (report.ExecutionKind != "loopback" || report.ActualProviderCalls != 0 ||
            report.Scheduled != declared.Length || report.Attempted != declared.Length ||
            report.Unattempted != 0 || report.Invalid != 0)
            return (null, "rejected_case_mismatch");
        if (!Hash(report.PlanSha256) || report.CorpusSha256 != corpusSha)
            return (null, "rejected_corpus_mismatch");
        if (!SourceBinds(report.SourceCommit, report.SourceTree, report.SourceClean))
            return (null, "rejected_source");
        // Derive coverage from admitted outcomes, never from the diagnostic rows
        // themselves: even an empty array must not hide failed attempts.
        if (report.Failed != outcomes.Count(o => o.ExecutionStatus == EvaluationStatus.Failed) ||
            report.Completed != outcomes.Count(o => o.ExecutionStatus == EvaluationStatus.Completed) ||
            report.AgentDiagnostics.IsDefault || report.AgentDiagnostics.Length != report.Failed ||
            report.AgentDiagnostics.Any(d => d is null ||
            d.ScheduleIndex < 0 || d.ScheduleIndex >= report.Attempted ||
            outcomes[d.ScheduleIndex].ExecutionStatus != EvaluationStatus.Failed ||
            !d.IsCanonical()) ||
            report.AgentDiagnostics.Select(d => d.ScheduleIndex).Distinct().Count() != report.AgentDiagnostics.Length)
            return (null, "rejected_report_invalid");
        var parity = SourceParity();
        parity["plan_sha256"] = report.PlanSha256;
        parity["corpus_sha256"] = corpusSha;
        parity["configuration_sha256"] = configuration;
        parity["mode"] = "deterministic";
        parity["execution_kind"] = "loopback";
        parity["scheduled"] = declared.Length;
        parity["attempted"] = declared.Length;
        parity["completed"] = report.Completed;
        parity["stop_reason"] = "complete";
        parity["agent_diagnostics"] = JsonNode.Parse(JsonSerializer.Serialize(report, LiveJsonContext.Default.LiveRunSummary))!["agent_diagnostics"]!.DeepClone();
        parity["cases"] = new JsonArray(declared.Select(id => (JsonNode)id).ToArray());
        return (parity, null);
    }

    // Constrained private-root deletion: only a gate-created marked temp root
    // inside the system temp area is eligible. Arbitrary paths, repository
    // directories, symlinks and unmarked directories are refused.
    private static (int, JsonObject) CleanupRoot(string dir)
    {
        var full = Path.GetFullPath(dir);
        var temp = Path.GetFullPath(Path.GetTempPath());
        if (!full.StartsWith(temp, StringComparison.Ordinal) ||
            full.Length == temp.TrimEnd(Path.DirectorySeparatorChar).Length)
            return Reject("cleanup", "rejected_cleanup_root");
        try
        {
            // Every component from the temp root down must be a real directory;
            // a symlinked ancestor would make the lexical containment check
            // meaningless.
            for (var node = new DirectoryInfo(full); node is not null &&
                node.FullName.Length >= temp.TrimEnd(Path.DirectorySeparatorChar).Length;
                node = node.Parent)
            {
                if (File.GetAttributes(node.FullName).HasFlag(FileAttributes.ReparsePoint))
                    return Reject("cleanup", "rejected_cleanup_root");
            }
        }
        catch { return Reject("cleanup", "rejected_cleanup_root"); }
        if (!File.Exists(Path.Combine(full, TempRootMarker)))
            return Reject("cleanup", "rejected_cleanup_root");
        try { Directory.Delete(full, recursive: true); }
        catch { return Reject("cleanup", "rejected_cleanup_root"); }
        if (Directory.Exists(full)) return Reject("cleanup", "rejected_cleanup_root");
        return (0, new JsonObject { ["schema"] = VerdictSchema, ["code"] = "verified", ["scenario"] = "cleanup" });
    }

    private static (int, JsonObject) VerifyTrx(string path, string[] required)
    {
        if (required.Length == 0) return Reject("trx", "input_invalid");
        System.Xml.Linq.XDocument doc;
        try { doc = System.Xml.Linq.XDocument.Load(path); }
        catch { return Reject("trx", "rejected_report_invalid"); }
        var ns = doc.Root?.Name.Namespace ?? System.Xml.Linq.XNamespace.None;
        var results = doc.Descendants(ns + "UnitTestResult").ToArray();
        if (results.Length == 0) return Reject("trx", "rejected_empty");
        foreach (var result in results)
        {
            if (result.Attribute("outcome")?.Value != "Passed") return Reject("trx", "rejected_code");
        }
        var counts = new JsonObject();
        foreach (var cls in required)
        {
            var count = results.Count(r =>
                (r.Attribute("testName")?.Value ?? "").Contains(cls, StringComparison.Ordinal));
            if (count == 0) return Reject("trx", "rejected_case_mismatch");
            counts[cls] = count;
        }
        return (0, new JsonObject
        {
            ["schema"] = VerdictSchema,
            ["code"] = "verified",
            ["scenario"] = "trx",
            ["parity"] = new JsonObject { ["total"] = results.Length, ["classes"] = counts },
        });
    }

    private static (int, JsonObject) VerifyParity(string aPath, string bPath)
    {
        JsonObject? a = null, b = null;
        try
        {
            a = JsonNode.Parse(File.ReadAllText(aPath)) as JsonObject;
            b = JsonNode.Parse(File.ReadAllText(bPath)) as JsonObject;
        }
        catch { }
        if (a is null || b is null) return Reject("parity", "rejected_report_invalid");
        if (a["code"]?.GetValue<string>() != "verified" || b["code"]?.GetValue<string>() != "verified" ||
            a["scenario"]?.GetValue<string>() != b["scenario"]?.GetValue<string>())
            return Reject("parity", "rejected_parity");
        if (!JsonNode.DeepEquals(a["parity"], b["parity"])) return Reject("parity", "rejected_parity");
        return (0, new JsonObject
        {
            ["schema"] = VerdictSchema,
            ["code"] = "verified",
            ["scenario"] = "parity",
            ["parity"] = new JsonObject { ["of"] = a["scenario"]?.GetValue<string>() },
        });
    }

    // Ephemeral V2 dry-run plan. The generated plan is dry-run input only: it
    // carries no credential, is written to a caller-owned private path, and V1
    // never dispatches --execute. Live authorization remains a separate V3
    // act.
    internal static (int, JsonObject) MakeLivePlan(string corpus, string outPath,
        DeepSeekRequestProfile profile = DeepSeekRequestProfile.Current)
    {
        try
        {
            var admitted = ReplayAdmission.Load(corpus);
            if (admitted.Fixture is not { } fixture) return Reject("r5-plan", "rejected_corpus");
            var declared = fixture.Runs.Select(r => r.Input.CaseId).SequenceEqual(LiveCoverageCases)
                ? LiveCoverageCases : QualityCases;
            if (!fixture.Runs.Select(r => r.Input.CaseId).SequenceEqual(declared))
                return Reject("r5-plan", "rejected_case_mismatch");
            var plan = new JsonObject
            {
                ["format"] = LiveLimits.PlanFormat,
                ["source"] = new JsonObject
                {
                    ["commit"] = EvaluationSource.Commit,
                    ["tree"] = EvaluationSource.Tree,
                    ["clean"] = EvaluationSource.Clean,
                },
                ["corpus"] = new JsonObject
                {
                    ["path"] = Path.GetFullPath(corpus),
                    ["sha256"] = fixture.CorpusSha256,
                },
                ["provider"] = new JsonObject
                {
                    ["provider_id"] = DeepSeekAdapterContext.Provider,
                    ["model_id"] = DeepSeekAdapterContext.Model,
                    ["adapter_id"] = DeepSeekAdapterContext.AdapterFor(profile),
                    ["configuration_sha256"] = LivePlanAdmission.ProviderConfigurationSha256(profile),
                },
                ["schedule"] = new JsonArray(declared
                    .Select(id => (JsonNode)new JsonObject { ["case_id"] = id, ["repeats"] = 1 }).ToArray()),
                ["bounds"] = new JsonObject
                {
                    ["max_evaluations"] = declared.Length,
                    ["max_model_calls"] = declared.Length * AgentLimits.ModelCalls,
                    ["max_input_tokens"] = declared.Length * AgentLimits.ModelCalls * 8192L,
                    ["max_output_tokens"] = declared.Length * AgentLimits.ModelCalls *
                        (profile == DeepSeekRequestProfile.Current ? 512L : DeepSeekRequestWriter.MaxTokensFor(profile)),
                    ["max_combined_tokens"] = declared.Length * AgentLimits.ModelCalls *
                        (profile == DeepSeekRequestProfile.Current ? 8704L : 8192L + DeepSeekRequestWriter.MaxTokensFor(profile)),
                    ["max_seconds"] = 600,
                    ["spend_ceiling_micro_usd"] = declared.Length * AgentLimits.ModelCalls * 1000L,
                    ["per_call"] = new JsonObject
                    {
                        ["max_input_tokens"] = 8192,
                        ["max_output_tokens"] = profile == DeepSeekRequestProfile.Current ? 512 :
                            DeepSeekRequestWriter.MaxTokensFor(profile),
                        ["max_charge_micro_usd"] = 1000,
                    },
                },
            };
            File.WriteAllText(outPath, plan.ToJsonString());
            var admittedPlan = LivePlanAdmission.Load(outPath, execute: false, CancellationToken.None);
            return (0, new JsonObject
            {
                ["schema"] = "r5-v1-plan-v1",
                ["code"] = "plan_written",
                ["plan_sha256"] = admittedPlan.Digest,
            });
        }
        catch { return Reject("r5-plan", "infrastructure_failed"); }
    }

    private static (int, JsonObject) Reject(string scenario, string reason) =>
        (reason == "input_invalid" ? 2 : 1, new JsonObject
        {
            ["schema"] = VerdictSchema,
            ["code"] = "rejected",
            ["scenario"] = scenario,
            ["reason"] = reason,
        });
}
