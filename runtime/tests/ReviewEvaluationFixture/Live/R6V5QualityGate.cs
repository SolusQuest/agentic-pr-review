using System.Collections.Immutable;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

// A prospective, in-process quality candidate for the frozen R6 V5 sample.
// Independent privacy/safety assessment still owns the final V6-entry verdict.
internal static class R6V5QualityGate
{
    internal const string CandidatePass = "candidate_pass";
    internal const string Blocked = "blocked";
    internal const string NotEvaluable = "not_evaluable";
    internal const string CorpusSha256 =
        "0f658af24276007acbe063b1d3b81021760bb10ecf801205993f09ecc3698958";

    private static readonly ImmutableArray<string> Cases =
        ["cs-defect", "cs-safe", "ts-defect", "ts-safe", "repository-rule"];

    internal static bool ValidStatus(string? value) =>
        value is null or CandidatePass or Blocked or NotEvaluable;

    internal static string? Evaluate(LiveRunSummary summary,
        ImmutableArray<string> schedule,
        ImmutableArray<EvaluationOutcome> outcomes)
    {
        if (summary.CorpusSha256 != CorpusSha256 ||
            !schedule.SequenceEqual(Cases))
            return null;
        if (summary.ExecutionKind != "live") return NotEvaluable;
        if (summary.Scheduled != Cases.Length || summary.Attempted != Cases.Length ||
            summary.Completed != Cases.Length || summary.Failed != 0 ||
            summary.Invalid != 0 || summary.Unattempted != 0 ||
            summary.StopReason != "complete" || summary.Cleanup != "cleaned" ||
            !summary.SourceClean || summary.AccountingViolation ||
            summary.SimulatedAdapterCalls != 0 || summary.ActualProviderCalls <= 0 ||
            summary.UsageUnknownCalls != 0 ||
            !summary.AgentDiagnostics.IsDefaultOrEmpty ||
            summary.AdjudicationStatus != "adjudicated" ||
            summary.HumanConfirmedCases < 0 || summary.AiAdjudicatedCases < 0 ||
            summary.HumanConfirmedCases + summary.AiAdjudicatedCases != Cases.Length ||
            outcomes.Length != Cases.Length)
            return Blocked;

        for (var index = 0; index < Cases.Length; index++)
        {
            var row = outcomes[index];
            if (row.CaseId != Cases[index] || row.CorpusSha256 != CorpusSha256 ||
                row.Mode != "live" || row.ExecutionStatus != EvaluationStatus.Completed ||
                row.EvidenceStatus != AssertionStatus.Passed ||
                row.ScenarioStatus != AssertionStatus.Passed ||
                row.ModelStatus != ModelObservationStatus.Adjudicated ||
                row.Code != EvaluationCode.Scored ||
                row.FailureSource != EvaluationFailureSource.None ||
                row.FailureKind != EvaluationFailureKind.None ||
                row.UnadjudicatedFindings != 0 || row.AdjudicatedFalse != 0 ||
                row.ProhibitedObservations != 0 || row.DuplicateObservations != 0 ||
                row.StructurallyMissingDefects != 0)
                return Blocked;

            var safe = Cases[index] is "cs-safe" or "ts-safe";
            if (safe)
            {
                if (row.FindingCount != 0 || row.ExpectedDefects != 0 ||
                    row.StructuralMatches != 0 || row.AdjudicatedTrue != 0 ||
                    row.AdjudicatedDefects != 0)
                    return Blocked;
            }
            else if (row.ExpectedDefects != 1 || row.StructuralMatches != 1 ||
                row.AdjudicatedDefects != 1 || row.AdjudicatedTrue < 1)
            {
                return Blocked;
            }
        }
        return CandidatePass;
    }
}
