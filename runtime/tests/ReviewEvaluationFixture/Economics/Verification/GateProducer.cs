using System.Text.Json;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;
using static AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification.GateContracts;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;

internal static class GateProducer
{
    internal static async Task<GateReport> RunAsync(string fixtures)
    {
        var selection = Select(fixtures);
        Require(selection.SourceClean);
        var root = ReplayProcess.CreatePrivateRoot();
        GateReport report;
        try
        {
            var cases = new List<GateCase>();
            GateTokenCases.Run(cases, selection, root);
            await GatePrefixCases.RunAsync(cases);
            await GateHistoryCases.RunAsync(cases, selection, fixtures);
            var economics = new List<GateCase>();
            await GateEconomicsCases.RunAsync(economics, fixtures, root);
            GateComparisonCases.Run(cases, selection, root, economics);
            cases.AddRange(economics);
            report = new(selection, [.. cases]);
        }
        finally { Require(ReplayProcess.Cleanup(root)); }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, GateJson.Default.GateReport);
        Require(bytes.Length <= MaximumBytes);
        using var raw = JsonDocument.Parse(bytes);
        Require(Safe(raw.RootElement));
        return report;
    }
}
