using System.Collections.Immutable;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Prefix;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Histories;

internal sealed record HistoryRow(int Phase, int ProcessId, string Code, bool Accepted, long? Generation,
    string? SessionSha256, string? EnvelopeSha256, bool PredecessorPreserved, bool RestoredMatch,
    bool WireMatch, PrefixComparison Comparison, HistoryCapture Capture, GrowthChatCounts? Capacity = null);

internal sealed record HistoryReport(string Mode, string Profile, string Code, string Cleanup,
    string SourceCommit, string SourceTree, bool SourceClean, ImmutableArray<HistoryRow> Rows)
{
    internal bool ContinuityVerified => Code == "verified" && Cleanup == "cleaned" && !Rows.IsEmpty &&
        Rows.Any(r => r.Accepted) && Rows.All(r => r.PredecessorPreserved) && Rows.Where(r => r.Accepted).All(Positive) &&
        Rows.Select(r => r.ProcessId).Distinct().Count() == Rows.Length &&
        (Profile == "replay" ? Rows.Length == 3 && Rows.All(r => r.Accepted) :
            Profile is "tools" or "continuation" && Rows.Length > 1 && Rows.Take(Rows.Length - 1).All(r => r.Accepted) &&
            !Rows[^1].Accepted && CapacityReached(Profile, Rows[^1]));

    private static bool CapacityReached(string profile, HistoryRow row) => row.Code == Agent.Core.AgentFailureCodes.ResponseInvalid &&
        row.Capacity is { } capacity && (profile == "tools"
            ? capacity.LastMessages <= Agent.AgentLimits.Messages && capacity.LastResponseMessages > Agent.AgentLimits.Messages
            : capacity.LastContinuationBeforeBytes <= Agent.AgentLimits.ContinuationTotalBytes &&
                capacity.LastContinuationAfterBytes > Agent.AgentLimits.ContinuationTotalBytes);

    internal static bool Positive(HistoryRow row) => row.Accepted && row.ProcessId > 0 &&
        row.RestoredMatch && row.WireMatch && row.Capture.Code == "observed" &&
        row.Comparison == new PrefixComparison("compared", true, true) &&
        row.Capture.Baseline is { } baseline && row.Capture.Calls.Length > 0 &&
        row.Capture.Calls.All(call => call is not null && baseline.Compare(call) == new PrefixComparison("compared", true, true));
}

internal static class HistoryRunner
{
    internal static async Task<HistoryReport> ReplayAsync(string bundle, ReplayFault fault = ReplayFault.None,
        CancellationToken token = default)
    {
        var collector = new Collector();
        var result = await ReplayRunner.RunAsync(bundle, new() { Fault = fault, RunProcess = collector.RunAsync }, token);
        var rows = result.Steps.Select((step, phase) =>
        {
            var observation = result.Observations.FirstOrDefault(o => o.Phase == phase);
            return collector.Row(phase, step.Code, step.Accepted, step.Generation, observation?.SessionSha256,
                observation?.EnvelopeSha256, step.PredecessorPreserved);
        }).ToImmutableArray();
        return Report("replay", result.Code, result.Cleanup, rows, collector);
    }

    internal static async Task<HistoryReport> GrowthAsync(string bundle, string profile, CancellationToken token = default)
    {
        if (profile is not ("tools" or "continuation")) return Report(profile: "invalid", "input_invalid", "not_created", [], new());
        var collector = new Collector();
        // The exact input constructed by GrowthRunner is executed and admitted unchanged.
        var result = await GrowthRunner.RunAsync(bundle, new() { Profile = profile, RunProcess = collector.RunAsync }, token);
        var rows = result.Profiles.SelectMany(p => p.Rows).Select(row => collector.Row(row.Attempt, row.Code,
            row.Accepted, row.State?.Generation, row.State?.SessionSha256, row.State?.EnvelopeSha256,
            row.PredecessorPreserved) with { Capacity = row.Project }).ToImmutableArray();
        return Report(profile, result.Code, result.Cleanup, rows, collector);
    }

    private static HistoryReport Report(string profile, string code, string cleanup, ImmutableArray<HistoryRow> rows, Collector collector)
    {
        if (rows.Any(r => r.Accepted && !HistoryReport.Positive(r)) ||
            rows.Any(r => r.ProcessId <= 0) || rows.Select(r => r.ProcessId).Distinct().Count() != rows.Length ||
            collector.Staged.Select(s => s.Startup).Distinct().Count() != collector.Staged.Count)
            code = "evidence_invalid";
        return new("deterministic", profile, code, cleanup, EvaluationSource.Commit, EvaluationSource.Tree, EvaluationSource.Clean, rows);
    }

    private sealed class Collector
    {
        internal List<HistoryStaged> Staged { get; } = [];

        internal async Task<ReplayProcessResult> RunAsync(ReplayChildInput input, TimeSpan timeout, CancellationToken token)
        {
            var expected = await HistoryBridge.ExpectedAsync(input, token);
            var process = await ReplayProcess.RunAsync(input, timeout, token);
            if (process.Reply is { } reply)
                Staged.Add(HistoryBridge.Stage(input, expected.Run, expected.Expected, reply));
            return process;
        }

        internal HistoryRow Row(int phase, string code, bool accepted, long? generation, string? session, string? envelope, bool preserved)
        {
            var staged = Staged.SingleOrDefault(s => s.Phase == phase);
            // The caller is the completed supervisor result, after admission, accept and (growth) readback.
            var admitted = staged is { Admitted: true };
            return new(phase, staged?.ProcessId ?? 0, code, accepted, generation, session, envelope, preserved,
                admitted && staged!.RestoredMatch, admitted && staged!.WireMatch,
                admitted ? staged!.Comparison : new("unavailable", null, null),
                admitted ? staged!.Capture : HistoryCapture.Unavailable);
        }
    }
}
