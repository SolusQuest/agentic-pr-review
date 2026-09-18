using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Quality;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.SequenceEqual(["live-local", "--dry-run", "--fixture", "self-test"]))
                return await LiveSelfTest.RunAsync();
            if (args is ["live-local", "--dry-run", "--plan", { } dryPlan])
                return await LiveRunner.InvokeAsync(dryPlan, execute: false);
            if (args is ["live-local", "--execute", "--plan", { } livePlan])
                return await LiveRunner.InvokeAsync(livePlan, execute: true);
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
        var line = LastJsonLine(bytes);
        JsonObject? report = null;
        try { report = line is null ? null : JsonNode.Parse(line) as JsonObject; }
        catch { }
        if (report is null)
            return Reject(scenario, "rejected_report_invalid");
        // Independent binding: the declared corpus directory is re-admitted and
        // its digest must equal the identity the report claims to have run.
        if (corpus is not null)
        {
            string? corpusSha = null;
            try
            {
                var admitted = ReplayAdmission.Load(corpus);
                corpusSha = admitted.Fixture?.CorpusSha256;
            }
            catch { }
            var field = scenario == "growth" ? "seed_corpus_sha256" : "corpus_sha256";
            if (corpusSha is null || report[field]?.GetValue<string>() != corpusSha)
                return Reject(scenario, "rejected_corpus_mismatch");
        }
        var (parity, reason) = Extract(scenario, report);
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

    private static string? LastJsonLine(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes).Trim();
        var index = text.LastIndexOf('\n');
        return index < 0 ? text : text[(index + 1)..].Trim();
    }

    private static (JsonObject? Parity, string? Reason) Extract(string scenario, JsonObject report)
    {
        switch (scenario)
        {
            case "quality": return ExtractQuality(report);
            case "replay": return ExtractReplay(report, ReplayCases);
            case "incremental": return ExtractReplay(report, IncrementalCases);
            case "growth": return ExtractGrowth(report);
            case "reset-owner": return ExtractResetOwner(report);
            case "live-self-test": return ExtractLiveSelfTest(report);
            case "live-plan": return ExtractLivePlan(report);
            default: return (null, "input_invalid");
        }
    }

    private static bool Codes(JsonObject report, params string[] accepted)
    {
        var code = report["code"]?.GetValue<string>();
        return code is not null && accepted.Contains(code);
    }

    private static string[]? OrderedCases(JsonNode? node, string member)
    {
        if (node is not JsonArray rows) return null;
        var ids = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            if (row?[member]?.GetValue<string>() is not { } id) return null;
            ids.Add(id);
        }
        return ids.ToArray();
    }

    private static bool ExactSet(string[]? executed, string[] declared) =>
        executed is not null && executed.Length == declared.Length &&
        executed.SequenceEqual(declared) && executed.Distinct().Count() == executed.Length;

    private static (JsonObject?, string?) ExtractQuality(JsonObject report)
    {
        if (!Codes(report, "verified")) return (null, "rejected_code");
        var cases = report["cases"];
        var ids = OrderedCases(cases, "case_id");
        if (!ExactSet(ids, QualityCases))
            return (null, ids is null || ids.Length == 0 ? "rejected_empty" : "rejected_case_mismatch");
        if (report["expected_cases"]?.GetValue<int>() != QualityCases.Length ||
            report["executed_cases"]?.GetValue<int>() != QualityCases.Length ||
            report["verified_cases"]?.GetValue<int>() != QualityCases.Length)
            return (null, "rejected_case_mismatch");
        var rows = new JsonArray();
        foreach (var row in cases!.AsArray())
        {
            if (row!["verified"]?.GetValue<bool>() != true) return (null, "rejected_code");
            rows.Add((JsonNode)new JsonObject
            {
                ["case_id"] = row!["case_id"]!.GetValue<string>(),
                ["expected_code"] = row["expected_code"]?.GetValue<string>(),
                ["actual_code"] = row["actual_code"]?.GetValue<string>(),
                ["verified"] = true,
            });
        }
        return (new JsonObject
        {
            ["corpus_sha256"] = report["corpus_sha256"]?.GetValue<string>(),
            ["expected_cases"] = QualityCases.Length,
            ["executed_cases"] = QualityCases.Length,
            ["verified_cases"] = QualityCases.Length,
            ["cases"] = rows,
        }, null);
    }

    private static (JsonObject?, string?) ExtractReplay(JsonObject report, string[] declared)
    {
        if (!Codes(report, "verified")) return (null, "rejected_code");
        if (report["cleanup"]?.GetValue<string>() != "cleaned") return (null, "rejected_cleanup");
        var steps = report["steps"];
        var ids = OrderedCases(steps, "case_id");
        if (!ExactSet(ids, declared))
            return (null, ids is null || ids.Length == 0 ? "rejected_empty" : "rejected_case_mismatch");
        var rows = new JsonArray();
        foreach (var step in steps!.AsArray())
        {
            if (step!["accepted"]?.GetValue<bool>() != true) return (null, "rejected_code");
            rows.Add((JsonNode)new JsonObject
            {
                ["case_id"] = step["case_id"]!.GetValue<string>(),
                ["transition"] = step["transition"]?.GetValue<string>(),
                ["code"] = step["code"]?.GetValue<string>(),
                ["accepted"] = true,
                ["predecessor_preserved"] = step["predecessor_preserved"]?.GetValue<bool>(),
                ["quality_code"] = step["quality_code"]?.GetValue<string>(),
                ["evidence_status"] = step["evidence_status"]?.GetValue<string>(),
            });
        }
        return (new JsonObject
        {
            ["corpus_sha256"] = report["corpus_sha256"]?.GetValue<string>(),
            ["normalized_sha256"] = report["normalized_sha256"]?.GetValue<string>(),
            ["steps"] = rows,
        }, null);
    }

    private static (JsonObject?, string?) ExtractGrowth(JsonObject report)
    {
        if (!Codes(report, "verified")) return (null, "rejected_code");
        if (report["cleanup"]?.GetValue<string>() != "cleaned") return (null, "rejected_cleanup");
        if (report["profiles"] is not JsonArray profiles || profiles.Count != GrowthOracle.Length)
            return (null, "rejected_case_mismatch");
        var parity = new JsonArray();
        for (var i = 0; i < GrowthOracle.Length; i++)
        {
            var oracle = GrowthOracle[i];
            var profile = profiles[i]!;
            if (profile["profile"]?.GetValue<string>() != oracle.Profile)
                return (null, "rejected_case_mismatch");
            if (profile["limit_observed"]?.GetValue<bool>() != true) return (null, "rejected_terminal");
            var rows = profile["rows"] as JsonArray;
            if (rows is null || rows.Count != oracle.Rows) return (null, "rejected_case_mismatch");
            var terminal = rows[^1]!;
            if (profile["terminal_stage"]?.GetValue<string>() != oracle.Stage ||
                profile["terminal_code"]?.GetValue<string>() != oracle.Code ||
                terminal["classification"]?.GetValue<string>() != oracle.Classification ||
                terminal["code"]?.GetValue<string>() != oracle.Code ||
                terminal["accepted"]?.GetValue<bool>() != false)
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
        return (new JsonObject
        {
            ["seed_corpus_sha256"] = report["seed_corpus_sha256"]?.GetValue<string>(),
            ["corpus_sha256"] = report["corpus_sha256"]?.GetValue<string>(),
            ["normalized_sha256"] = report["normalized_sha256"]?.GetValue<string>(),
            ["profiles"] = parity,
        }, null);
    }

    private static (JsonObject?, string?) ExtractResetOwner(JsonObject report)
    {
        if (!Codes(report, "r5_reset_owner_passed")) return (null, "rejected_code");
        if (report["passedCases"] is not JsonArray passed) return (null, "rejected_empty");
        var ids = passed.Select(n => n!.GetValue<string>()).ToArray();
        if (!ExactSet(ids, ResetOwnerCases)) return (null, "rejected_case_mismatch");
        return (new JsonObject
        {
            ["topology"] = report["topology"]?.GetValue<string>(),
            ["passed_cases"] = new JsonArray(ids.Select(id => (JsonNode)id).ToArray()),
        }, null);
    }

    private static (JsonObject?, string?) ExtractLiveSelfTest(JsonObject report)
    {
        if (!Codes(report, "r5_live_self_test_passed")) return (null, "rejected_code");
        if (report["cleanup"]?.GetValue<string>() != "cleaned") return (null, "rejected_cleanup");
        if (report["passedCases"] is not JsonArray passed) return (null, "rejected_empty");
        var ids = passed.Select(n => n!.GetValue<string>()).ToArray();
        if (!ExactSet(ids, LiveSelfTestCases)) return (null, "rejected_case_mismatch");
        return (new JsonObject
        {
            ["source_commit"] = report["sourceCommit"]?.GetValue<string>(),
            ["source_tree"] = report["sourceTree"]?.GetValue<string>(),
            ["passed_cases"] = new JsonArray(ids.Select(id => (JsonNode)id).ToArray()),
        }, null);
    }

    private static (JsonObject?, string?) ExtractLivePlan(JsonObject report)
    {
        if (report["stop_reason"]?.GetValue<string>() != "complete")
            return (null, "rejected_code");
        if (report["execution_kind"]?.GetValue<string>() != "loopback" ||
            report["actual_provider_calls"]?.GetValue<int>() != 0 ||
            report["scheduled"]?.GetValue<int>() != QualityCases.Length ||
            report["attempted"]?.GetValue<int>() != QualityCases.Length ||
            report["unattempted"]?.GetValue<int>() != 0 ||
            report["invalid"]?.GetValue<int>() != 0)
            return (null, "rejected_case_mismatch");
        return (new JsonObject
        {
            ["plan_sha256"] = report["plan_sha256"]?.GetValue<string>(),
            ["corpus_sha256"] = report["corpus_sha256"]?.GetValue<string>(),
            ["execution_kind"] = "loopback",
            ["scheduled"] = QualityCases.Length,
            ["attempted"] = QualityCases.Length,
            ["completed"] = report["completed"]?.GetValue<int>(),
            ["stop_reason"] = "complete",
        }, null);
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
            if (File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
                return Reject("cleanup", "rejected_cleanup_root");
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
    internal static (int, JsonObject) MakeLivePlan(string corpus, string outPath)
    {
        try
        {
            var admitted = ReplayAdmission.Load(corpus);
            if (admitted.Fixture is not { } fixture) return Reject("r5-plan", "rejected_corpus");
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
                    ["adapter_id"] = DeepSeekAdapterContext.Adapter,
                    ["configuration_sha256"] = LivePlanAdmission.ProviderConfigurationSha256(),
                },
                ["schedule"] = new JsonArray(QualityCases
                    .Select(id => (JsonNode)new JsonObject { ["case_id"] = id, ["repeats"] = 1 }).ToArray()),
                ["bounds"] = new JsonObject
                {
                    ["max_evaluations"] = QualityCases.Length,
                    ["max_model_calls"] = QualityCases.Length * AgentLimits.ModelCalls,
                    ["max_input_tokens"] = QualityCases.Length * AgentLimits.ModelCalls * 8192L,
                    ["max_output_tokens"] = QualityCases.Length * AgentLimits.ModelCalls * 512L,
                    ["max_combined_tokens"] = QualityCases.Length * AgentLimits.ModelCalls * 8704L,
                    ["max_seconds"] = 600,
                    ["spend_ceiling_micro_usd"] = QualityCases.Length * AgentLimits.ModelCalls * 1000L,
                    ["per_call"] = new JsonObject
                    {
                        ["max_input_tokens"] = 8192,
                        ["max_output_tokens"] = 512,
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
