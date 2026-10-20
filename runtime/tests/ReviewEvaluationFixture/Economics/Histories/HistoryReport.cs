using System.Collections.Immutable;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Prefix;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Histories;

internal sealed record HistoryRow(int Phase, int ProcessId, string Code, bool Accepted, long? Generation,
    string? SessionSha256, string? EnvelopeSha256, bool PredecessorPreserved, bool RestoredMatch,
    bool LogicalRequestMatch, PrefixComparison Comparison, HistoryCapture Capture, GrowthChatCounts? Capacity = null);

internal sealed record HistoryReport(string Mode, string Profile, string Code, string Cleanup,
    string SourceCommit, string SourceTree, bool SourceClean, ImmutableArray<HistoryRow> Rows)
{
    internal bool ContinuityVerified => Code == "verified" && Cleanup == "cleaned" && !Rows.IsEmpty &&
        Rows.Any(r => r.Accepted) && Rows.All(r => r.PredecessorPreserved) && Rows.Where(r => r.Accepted).All(Positive) &&
        Rows.Select(r => r.ProcessId).Distinct().Count() == Rows.Length &&
        (Profile == "replay" ? Rows.Length == 3 && Rows.All(r => r.Accepted) :
            Profile is "tools" or "continuation" && Rows.Length > 1 && Rows.Take(Rows.Length - 1).All(r => r.Accepted) &&
            !Rows[^1].Accepted && CapacityReached(Profile, Rows[^1]));

    internal bool CurrentContinuityVerified => Cleanup == "cleaned" &&
        (Profile == "replay" ? Code == "verified" && Rows.Length == 3 :
            Profile is "tools" or "continuation" && Code == "observation_incomplete" && Rows.Length == 2) &&
        Rows.All(r => r.PredecessorPreserved && Positive(r)) &&
        Rows.Select(r => r.ProcessId).Distinct().Count() == Rows.Length;

    private static bool CapacityReached(string profile, HistoryRow row) => row.Code == Agent.Core.AgentFailureCodes.ResponseInvalid &&
        row.Capacity is { } capacity && (profile == "tools"
            ? capacity.LastMessages <= MeasurementLimits.Historical.Messages && capacity.LastResponseMessages > MeasurementLimits.Historical.Messages
            : capacity.LastContinuationBeforeBytes <= MeasurementLimits.Historical.ContinuationTotalBytes &&
                capacity.LastContinuationAfterBytes > MeasurementLimits.Historical.ContinuationTotalBytes);

    internal static bool Positive(HistoryRow row) => row.Accepted && row.ProcessId > 0 &&
        row.RestoredMatch && row.LogicalRequestMatch && row.Capture.Code == "observed" &&
        row.Comparison == new PrefixComparison("compared", true) &&
        row.Capture.Baseline is { } baseline && row.Capture.Calls.Length > 0 &&
        row.Capture.Calls.All(call => call is not null && baseline.Compare(call) == new PrefixComparison("compared", true));
}
