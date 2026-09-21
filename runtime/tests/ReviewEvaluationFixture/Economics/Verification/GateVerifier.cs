using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent.Core;
using static AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification.GateContracts;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;

internal static class GateVerifier
{
    internal static (GateVerdict Verdict, byte[] Projection) Verify(ReadOnlySpan<byte> bytes, GateSelection expected)
    {
        var report = Read(bytes) ?? throw new InvalidOperationException("r6_gate_report_invalid");
        Require(report.Selection == expected && !report.Cases.IsDefault && report.Cases.Length == GateInventory.Cases.Length &&
            report.Cases.Length <= MaximumCases);
        using var raw = JsonDocument.Parse(bytes.ToArray());
        Require(Safe(raw.RootElement));
        var found = new Dictionary<string, GateCase>(StringComparer.Ordinal);
        for (var index = 0; index < report.Cases.Length; index++)
        {
            var item = report.Cases[index]; var wanted = GateInventory.Cases[index];
            Require(item is not null && item.Id == wanted.Id && item.Selection == wanted.Selection && found.TryAdd(item.Id, item) &&
                item.BindingSha256 == GateCase.Binding(item.Id, item.Selection, item.Evidence));
        }
        var projection = new JsonObject
        {
            ["source_commit"] = expected.SourceCommit, ["source_tree"] = expected.SourceTree, ["source_clean"] = expected.SourceClean,
            ["replay_sha256"] = expected.ReplaySha256, ["growth_sha256"] = expected.GrowthSha256,
        };
        var projected = new JsonArray();
        foreach (var item in report.Cases)
        {
            var semantic = item.Id.StartsWith('t') ? GateTokenOracle.Verify(item, expected) :
                item.Id.StartsWith("p1-", StringComparison.Ordinal) ? GatePrefixOracle.Verify(item, expected) :
                item.Id.StartsWith("p2-", StringComparison.Ordinal) ? GateHistoryOracle.Verify(item, expected) :
                item.Id.StartsWith("c1-", StringComparison.Ordinal) ? GateComparisonOracle.Verify(item, expected, found) :
                GateEconomicsOracle.Verify(item, expected);
            projected.Add((JsonNode)new JsonObject { ["id"] = item.Id, ["selection"] = item.Selection, ["evidence"] = JsonNode.Parse(semantic.GetRawText()) });
        }
        projection["cases"] = projected;
        var output = Encoding.UTF8.GetBytes(projection.ToJsonString());
        return (new("r6_gate_verified", expected, report.Cases.Length, AgentCanonical.HashDomain("apr.r6.gate.semantic", output)), output);
    }
}
