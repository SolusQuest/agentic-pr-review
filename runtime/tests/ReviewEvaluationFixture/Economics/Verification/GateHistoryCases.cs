using System.Text.Json;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Histories;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;

internal static class GateHistoryCases
{
    internal static async Task RunAsync(List<GateCase> cases, GateSelection selection, string fixtures)
    {
        var replay = Path.Combine(fixtures, "replay");
        var growth = Path.Combine(fixtures, "growth");
        void Add(string id, string selected, HistoryReport report)
        {
            foreach (var row in report.Rows) GateHistoryOracle.Absent(row.ProcessId);
            cases.Add(GateCase.Create(id, selected, HistoryJson.Write(report)));
        }
        Add("p2-replay", "replay", await HistoryRunner.ReplayAsync(replay));
        Add("p2-tools", "tools", await HistoryRunner.GrowthAsync(growth, "tools"));
        Add("p2-continuation", "continuation", await HistoryRunner.GrowthAsync(growth, "continuation"));
        // Producer routing is independent of the verifier's required inventory.
        foreach (var (id, fault) in new[]
        {
            ("p2-missing-history", ReplayFault.MissingHistory), ("p2-reordered-history", ReplayFault.ReorderedHistory),
            ("p2-missing-continuation", ReplayFault.MissingContinuation), ("p2-wrong-position", ReplayFault.WrongContinuationPosition),
            ("p2-changed-continuation", ReplayFault.ChangedContinuation), ("p2-wrong-scope", ReplayFault.WrongScope),
            ("p2-wrong-head", ReplayFault.WrongHead), ("p2-stale-generation", ReplayFault.StaleGeneration),
            ("p2-policy", ReplayFault.ChangedPolicy), ("p2-model", ReplayFault.ChangedModel),
            ("p2-adapter", ReplayFault.ChangedAdapter), ("p2-toolset", ReplayFault.ChangedToolset),
        }) Add(id, fault.ToString(), await HistoryRunner.ReplayAsync(replay, fault));
        var host = await GateHostCases.RunAsync(growth, selection);
        cases.Add(GateCase.Create("p2-host-capacity-reset", "production-host-capacity-reset",
            JsonSerializer.SerializeToUtf8Bytes(host, GateJson.Default.GateHostReport)));
    }
}
