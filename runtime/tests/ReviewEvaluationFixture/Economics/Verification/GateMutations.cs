using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;
using static AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification.GateContracts;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;

internal static class GateMutations
{
    internal static byte[] Create(byte[] bytes, string mutation)
    {
        var json = JsonNode.Parse(bytes)!;
        var cases = json["cases"]!.AsArray();
        JsonNode Case(string id) => cases.Single(item => item!["id"]!.GetValue<string>() == id)!;
        void Rebind(JsonNode item)
        {
            using var document = JsonDocument.Parse(item["evidence"]!.ToJsonString());
            item["binding_sha256"] = GateCase.Binding(item["id"]!.GetValue<string>(), item["selection"]!.GetValue<string>(), document.RootElement);
        }
        void Edit(string id, Action<JsonNode> change)
        {
            var item = Case(id);
            change(item["evidence"]!);
            Rebind(item);
        }
        void Domain(JsonNode capture, string field, string value)
        {
            capture["baseline"]!["domain"]![field] = value;
            foreach (var call in capture["calls"]!.AsArray()) call!["domain"]![field] = value;
        }
        var c2 = Case("c2-replay");
        switch (mutation)
        {
            case "missing-case": cases.RemoveAt(0); break;
            case "duplicate-case": cases[1] = cases[0]!.DeepClone(); break;
            case "renamed-case": cases[0]!["id"] = "not-required"; Rebind(cases[0]!); break;
            case "reordered-case":
                var first = cases[0]!.DeepClone(); cases[0] = cases[1]!.DeepClone(); cases[1] = first; break;
            case "substituted-case": cases[1]!["evidence"] = cases[0]!["evidence"]!.DeepClone(); break;
            case "wrong-source": json["selection"]!["source_commit"] = Hash('f', 40); break;
            case "wrong-tree": json["selection"]!["source_tree"] = Hash('f', 40); break;
            case "wrong-clean": json["selection"]!["source_clean"] = false; break;
            case "wrong-build": json["selection"]!["build_sha256"] = Hash('f'); break;
            case "wrong-mode": json["selection"]!["mode"] = json["selection"]!["mode"]!.GetValue<string>() == "framework" ? "aot" : "framework"; break;
            case "wrong-corpus": json["selection"]!["replay_sha256"] = Hash('f'); break;
            case "invalid-binding": cases[0]!["binding_sha256"] = Hash('f'); break;
            case "wrong-artifact":
                var price = Case("t3-usd"); price["evidence"] = Case("t3-cny")["evidence"]!.DeepClone(); Rebind(price); break;
            case "aggregate-inputs":
            {
                var selected = Read(bytes)!.Selection;
                var wrong = PricingReport.Create(GateTokenCases.Journal(selected,
                    [GateTokenCases.Measured(0, 0, 0), GateTokenCases.Measured(2, 0, 0)]),
                    GateTokenCases.Tariff(GateTokenCases.Input(miss: 5, unit: 1000, places: 2)));
                var item = Case("t3-aggregate"); item["evidence"] = JsonNode.Parse(PricingJson.Write(wrong)); Rebind(item);
                break;
            }
            case "comparison-corpus":
            {
                var selected = Read(bytes)!.Selection;
                Require(selected.ReplaySha256 != selected.GrowthSha256);
                var comparisonRoot = ReplayProcess.CreatePrivateRoot();
                try
                {
                    var wrong = new List<GateCase>();
                    GateComparisonCases.Run(wrong, selected with { ReplaySha256 = selected.GrowthSha256 }, comparisonRoot);
                    var item = Case("c1-descriptive");
                    item["evidence"] = JsonNode.Parse(wrong.Single(value => value.Id == "c1-descriptive").Evidence.GetRawText());
                    Rebind(item);
                }
                finally { Require(ReplayProcess.Cleanup(comparisonRoot)); }
                break;
            }
            case "prefix-domain":
                Edit("p1-dynamic-suffix", value =>
                {
                    foreach (var observation in value["observations"]!.AsArray())
                        observation!["domain"]!["stable_plan_sha256"] = "UNLISTED_PRIVATE_PAYLOAD_279";
                }); break;
            case "prefix-generation":
                Edit("p1-dynamic-suffix", value =>
                {
                    foreach (var observation in value["observations"]!.AsArray()) observation!["domain"]!["generation"] = -1;
                }); break;
            case "history-session":
                Edit("p2-replay", value => Domain(value["rows"]![1]!["capture"]!, "session_sha256", Hash('e'))); break;
            case "history-plan":
                Edit("p2-replay", value => Domain(value["rows"]![1]!["capture"]!, "stable_plan_sha256",
                    value["rows"]![0]!["capture"]!["baseline"]!["domain"]!["stable_plan_sha256"]!.GetValue<string>())); break;
            case "host-capacity-epoch" or "host-capacity-session" or "host-restore-epoch" or "host-restore-session":
                Edit("p2-host-capacity-reset", value => value["rows"]![mutation.Contains("capacity", StringComparison.Ordinal) ? 5 : 6]!
                    [mutation.EndsWith("epoch", StringComparison.Ordinal) ? "epoch_sha256" : "session_id_sha256"] = Hash('e')); break;
            case "host-code":
                Edit("p2-host-capacity-reset", value => value["rows"]![0]!["agent_code"] = "UNLISTED_PRIVATE_PAYLOAD_279"); break;
            case "host-disposition":
                Edit("p2-host-capacity-reset", value => value["rows"]![5]!["disposition"] = "UNLISTED_PRIVATE_PAYLOAD_279"); break;
            case "price-tamper":
                var tampered = Case("t3-usd"); tampered["evidence"]!["observed_usage"]!["total_amount"] = 999; Rebind(tampered); break;
            case "chain-tamper": c2["evidence"]!["report"]!["steps"]![1]!["predecessor_sha256"] = Hash('f'); Rebind(c2); break;
            case "live": c2["evidence"]!["report"]!["execution_kind"] = "live"; Rebind(c2); break;
            case "cleanup": c2["evidence"]!["report"]!["cleanup"] = "cleanup_failed"; Rebind(c2); break;
            case "unreaped": c2["evidence"]!["workers"]![0]!["process_id"] = 0; Rebind(c2); break;
            case "unknown-field": json["unexpected"] = true; break;
            case "duplicate-field": return Encoding.UTF8.GetBytes("{\"selection\":" + json["selection"]!.ToJsonString() + "," + json.ToJsonString()[1..]);
            case "raw-canary": return Encoding.UTF8.GetBytes(Canary + "\n" + json.ToJsonString());
            case "escaped-canary":
                json["selection"]!["mode"] = Canary;
                return Encoding.UTF8.GetBytes(json.ToJsonString().Replace(Canary, "\\u0041PR279_PRIVATE_GATE_CANARY", StringComparison.Ordinal));
            case "extra-record": return Encoding.UTF8.GetBytes(json.ToJsonString() + "\n{}");
            case "stderr-canary": Console.Error.WriteLine(Canary); break;
            case "retained-root":
                var root = ReplayProcess.CreatePrivateRoot(); File.WriteAllText(Path.Combine(root, "synthetic-probe"), "owned-negative"); break;
            default: throw new InvalidOperationException("r6_gate_mutation_invalid");
        }
        return Encoding.UTF8.GetBytes(json.ToJsonString());
    }
}
