using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles;

// A bounded regression of the current implementation, never capacity evidence.
internal static class CurrentGrowthOracle
{
    internal static bool Valid(GrowthReport report) =>
        report.Code == "observation_incomplete" && report.Cleanup == "cleaned" &&
        report.Schedule == new GrowthSchedule(2, ReplayFault.None, 1) &&
        report.Profiles.Select(p => p.Profile).SequenceEqual(GrowthProfiles.Names) &&
        report.Profiles.All(p => p.AttemptLimit == 2 && !p.LimitObserved &&
            p.TerminalStage == "schedule" && p.TerminalCode == "attempt_limit" &&
            p.Rows.Length == 2 && p.Rows.All(r => r.Accepted && r.PredecessorPreserved &&
                r.Stage == "accept" && r.Code == RestrictedStateCodes.Accepted &&
                r.Classification == "accepted"));
}
