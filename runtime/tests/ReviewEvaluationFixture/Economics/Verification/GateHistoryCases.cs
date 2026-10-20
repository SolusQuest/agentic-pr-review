using System.Text.Json;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Histories;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;

internal static class GateHistoryCases
{
    internal static async Task RunAsync(List<GateCase> cases, GateSelection selection, string fixtures)
    {
        var growth = Path.Combine(fixtures, "growth");
        var host = await GateHostCases.RunAsync(growth, selection);
        cases.Add(GateCase.Create("p2-host-explicit-reset", "production-host-explicit-reset",
            JsonSerializer.SerializeToUtf8Bytes(host, GateJson.Default.GateHostReport)));
    }
}
