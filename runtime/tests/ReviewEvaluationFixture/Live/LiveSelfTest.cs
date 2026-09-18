using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

internal sealed record LiveSelfTestReport(
    string Schema, string Code, string Cleanup,
    string SourceCommit, string SourceTree, bool SourceClean, string[] PassedCases);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(LiveSelfTestReport))]
internal sealed partial class LiveSelfTestJson : JsonSerializerContext;

// End-to-end self-test: a private minimal bundle and plan drive the real
// admission/scheduling/adapter/scorer/report path with zero provider calls.
internal static class LiveSelfTest
{
    private const string Canary = "APR251_PRIVATE_CONTENT_CANARY";

    internal static async Task<int> RunAsync()
    {
        var root = ReplayProcess.CreatePrivateRoot();
        var cleaned = false;
        var passed = new List<string>();
        try
        {
            var bundle = Path.Combine(root, "bundle");
            WriteBundle(bundle);
            var admitted = ReplayAdmission.Load(bundle, CancellationToken.None);
            var fixture = admitted.Fixture ?? throw new InvalidOperationException("r5_live_self_test_bundle " + admitted.Code);
            var planPath = Path.Combine(root, "plan.json");
            WritePlan(planPath, fixture.CorpusSha256);
            var lines = new List<string>();
            var result = await LiveRunner.RunAsync(planPath, execute: false,
                new LiveOptions { WriteLine = lines.Add }, CancellationToken.None);
            Require(result.StopReason == "complete");
            Require(result.Summary.Scheduled == 2 && result.Attempted == 2 && result.Summary.Unattempted == 0);
            Require(result.Completed == 2);
            Require(result.Summary.ActualProviderCalls == 0 && result.Summary.SimulatedAdapterCalls >= 2);
            Require(result.Summary.PlanSha256.Length == 64);
            Require(result.Summary.ExecutionKind == "loopback");
            Require(result.Outcomes.Select(o => o.AttemptSha256 ?? "").Distinct().Count() == 2);
            Require(result.Outcomes.All(o => o.Mode == "deterministic"));
            Require(result.Outcomes.All(o => o.ConfigurationSha256?.Length == 64));
            Require(lines.All(line => !line.Contains(Canary, StringComparison.Ordinal)));
            Require(EvaluationJsonCheck(lines));
            passed.Add("dry_run_pipeline");

            var factory = new CountingFactory();
            var rejected = false;
            try
            {
                await LiveRunner.RunAsync(planPath, execute: true, new LiveOptions
                {
                    SecretSource = new MissingSecret(),
                    TransportFactory = factory,
                    WriteLine = lines.Add,
                }, CancellationToken.None);
            }
            catch (LivePlanRejected rejection)
                when (rejection.Code is LiveAdmissionCode.SecretInvalid or LiveAdmissionCode.InvalidSource)
            {
                rejected = true;
            }
            Require(rejected && factory.Calls == 0);
            passed.Add("execute_rejects_before_transport");

            cleaned = ReplayProcess.Cleanup(root);
            Require(cleaned);
            Write("r5_live_self_test_passed", passed, "cleaned");
            return 0;
        }
        catch
        {
            if (!cleaned) ReplayProcess.Cleanup(root);
            Write("r5_live_self_test_failed", passed, cleaned ? "cleaned" : "cleanup_failed");
            return 1;
        }
    }

    private static bool EvaluationJsonCheck(List<string> lines)
    {
        using var document = JsonDocument.Parse(lines[0]);
        return document.RootElement.TryGetProperty("attempt_sha256", out var value) &&
            value.GetString()?.Length == 64;
    }

    private static void WriteBundle(string root)
    {
        Directory.CreateDirectory(root);
        var members = new Dictionary<string, (string Role, byte[] Bytes)>(StringComparer.Ordinal)
        {
            ["source.txt"] = ("repository", Encoding.UTF8.GetBytes(
                "public static int Add(int a, int b) => a + b;\n")),
            ["diff.json"] = ("diff", Encoding.UTF8.GetBytes(
                "{\"changes\":[{\"path\":\"src/Counter.cs\",\"previous_path\":null,\"status\":\"modified\"," +
                "\"source_truncated\":false,\"hunks\":[{\"old_start\":1,\"old_count\":1,\"new_start\":1," +
                "\"new_count\":1,\"lines\":[{\"kind\":\"deletion\",\"old_line\":1,\"new_line\":null," +
                "\"text\":\"public static int Add(int a, int b) => a;\"},{\"kind\":\"addition\"," +
                "\"old_line\":null,\"new_line\":1,\"text\":\"public static int Add(int a, int b) => a + b;\"}]}]}]}")),
            ["policy.txt"] = ("policy", Encoding.UTF8.GetBytes(
                "Review only the changed source. Report grounded defects.\n")),
            ["context.txt"] = ("context", Encoding.UTF8.GetBytes(
                "Review the change in src/Counter.cs. " + Canary + "\n")),
            ["provider.json"] = ("script", Encoding.UTF8.GetBytes(
                "{\"turns\":[{" +
                "\"tool_calls\":[{\"id\":\"read0\",\"name\":\"read_file\",\"arguments_json\":" +
                "\"{\\\"path\\\":\\\"src/Counter.cs\\\",\\\"start_line\\\":1,\\\"line_count\\\":1}\"}]," +
                "\"reasoning_content\":\"Authored synthetic tool request.\"},{" +
                "\"tool_calls\":[{\"id\":\"finish0\",\"name\":\"finish_review\",\"arguments_json\":" +
                "\"{\\\"summary\\\":\\\"Synthetic safe control.\\\",\\\"findings\\\":[]}\"}]," +
                "\"reasoning_content\":\"Authored synthetic completion.\"}]}")),
            ["expected.json"] = ("assertions", Encoding.UTF8.GetBytes(
                "{\"expected_code\":\"Scored\",\"defects\":[],\"required_observations\":[]," +
                "\"prohibited_findings\":[{\"path\":\"src/Counter.cs\",\"start_line\":1,\"end_line\":1}]}")),
        };
        var files = new StringBuilder();
        foreach (var (path, (_, bytes)) in members.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            File.WriteAllBytes(Path.Combine(root, path), bytes);
            files.Append("{\"path\":\"").Append(path).Append("\",\"role\":\"").Append(members[path].Role)
                .Append("\",\"length\":").Append(bytes.Length).Append(",\"sha256\":\"")
                .Append(Convert.ToHexStringLower(SHA256.HashData(bytes))).Append("\"},");
        }
        var manifest = "{\"format\":\"apr.r5.synthetic-replay.v1\",\"source_kind\":\"authored-synthetic\"," +
            "\"configuration\":{\"workflow_identity\":\"r5@live-self-test\"," +
            "\"provider_id\":\"synthetic-provider\",\"model_id\":\"synthetic-model\"," +
            "\"adapter_id\":\"synthetic-adapter\"},\"files\":[" +
            files.ToString().TrimEnd(',') + "],\"runs\":[{" +
            "\"id\":\"self-test-run\",\"case_id\":\"self-test-safe\",\"transition\":\"initial\"," +
            "\"previous_run_id\":null,\"reviewed_identity\":{\"repository_id\":\"synthetic/live-self-test\"," +
            "\"review_target\":251,\"base_sha\":\"1111111111111111111111111111111111111111\"," +
            "\"head_sha\":\"2222222222222222222222222222222222222222\"},\"repository\":[" +
            "{\"path\":\"src/Counter.cs\",\"file\":\"source.txt\"}],\"diff\":\"diff.json\"," +
            "\"policy\":\"policy.txt\",\"context\":\"context.txt\",\"script\":\"provider.json\"," +
            "\"assertions\":\"expected.json\"}]}";
        File.WriteAllText(Path.Combine(root, "manifest.json"), manifest);
    }

    private static void WritePlan(string path, string corpusSha256)
    {
        var plan = "{\"format\":\"" + LiveLimits.PlanFormat + "\",\"source\":{\"commit\":\"" +
            EvaluationSource.Commit + "\",\"tree\":\"" + EvaluationSource.Tree + "\",\"clean\":" +
            (EvaluationSource.Clean ? "true" : "false") + "},\"corpus\":{\"path\":\"" +
            JsonEscape(Path.GetDirectoryName(path)! + "/bundle") + "\",\"sha256\":\"" + corpusSha256 +
            "\"},\"provider\":{\"provider_id\":\"deepseek\",\"model_id\":\"deepseek-v4-flash\"," +
            "\"adapter_id\":\"" + DeepSeekAdapterContext.Adapter +
            "\",\"configuration_sha256\":\"" + LivePlanAdmission.ProviderConfigurationSha256() +
            "\"},\"schedule\":[{\"case_id\":\"self-test-safe\",\"repeats\":2}],\"bounds\":{" +
            "\"max_evaluations\":4,\"max_model_calls\":8,\"max_input_tokens\":524288," +
            "\"max_output_tokens\":65536,\"max_combined_tokens\":589824,\"max_seconds\":120," +
            "\"spend_ceiling_micro_usd\":100000,\"per_call\":{\"max_input_tokens\":65536," +
            "\"max_output_tokens\":4096,\"max_charge_micro_usd\":1000}}}";
        File.WriteAllText(path, plan);
    }

    private static string JsonEscape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    internal static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("r5_live_self_test_invariant");
    }

    private static void Write(string code, List<string> cases, string cleanup) => Console.WriteLine(
        JsonSerializer.Serialize(
            new LiveSelfTestReport("r5-live-self-test-v1", code, cleanup, EvaluationSource.Commit,
                EvaluationSource.Tree, EvaluationSource.Clean, cases.ToArray()),
            LiveSelfTestJson.Default.LiveSelfTestReport));

    private sealed class MissingSecret : ILiveSecretSource
    {
        public string? TakeProviderCredential() => null;
    }

    private sealed class CountingFactory : ILiveTransportFactory
    {
        internal int Calls { get; private set; }
        public IDeepSeekTransport Create(DeepSeekCredential credential)
        {
            Calls++;
            return LiveDeepSeekTransportFactory.Instance.Create(credential);
        }
    }
}
