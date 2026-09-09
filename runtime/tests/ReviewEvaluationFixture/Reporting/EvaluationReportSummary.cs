using System.Collections.Immutable;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Reporting;

internal static class EvaluationReportSummary
{
    internal static bool Eligible(EvaluationOutcome row) => row.ExecutionStatus == EvaluationStatus.Completed &&
        row.EvidenceStatus == AssertionStatus.Passed && row.ModelStatus == ModelObservationStatus.Adjudicated &&
        row.Code != EvaluationCode.AdjudicationInvalid;

    internal static ReportSummary Summarize(ImmutableArray<EvaluationOutcome> rows, bool unknownPopulation)
    {
        var eligible = rows.Where(Eligible).ToImmutableArray();
        var reasons = Reasons(rows).ToBuilder();
        if (unknownPopulation && !reasons.Contains(ReportReason.UnknownContext)) reasons.Add(ReportReason.UnknownContext);
        var orderedReasons = reasons.Order().ToImmutableArray();
        var incomparable = orderedReasons.Any(r => r is ReportReason.MixedCorpus or ReportReason.MixedConfiguration or
            ReportReason.MixedMode or ReportReason.MixedSource);
        var incomplete = eligible.Length != rows.Length || unknownPopulation;
        ReportRatio Ratio(int numerator, int denominator) => new(numerator, denominator, eligible.Length,
            rows.Length - eligible.Length,
            incomparable ? RatioAvailability.Incomparable : incomplete ? RatioAvailability.Incomplete :
                denominator == 0 ? RatioAvailability.EmptyDenominator : RatioAvailability.Available,
            incomparable || incomplete || denominator == 0 ? null : (decimal)numerator / denominator);

        int Count(Func<EvaluationOutcome, bool> condition) => rows.Count(condition);
        int Source(EvaluationFailureSource source) => Count(r => r.FailureSource == source);
        var evidenceFailed = Count(r => r.EvidenceStatus == AssertionStatus.Failed);
        var scenarioFailed = Count(r => r.ScenarioStatus == AssertionStatus.Failed);
        var evidenceUnknown = Count(r => r.EvidenceStatus == AssertionStatus.NotEvaluated);
        var scenarioUnknown = Count(r => r.ScenarioStatus == AssertionStatus.NotEvaluated);
        var expectedEligible = eligible.Sum(r => r.ExpectedDefects);
        var credited = eligible.Sum(r => r.AdjudicatedDefects);
        return new(
            new(rows.Length, Count(r => r.AttemptSha256 is not null), Count(r => r.AttemptSha256 is null),
                Count(r => r.ExecutionStatus == EvaluationStatus.Completed), Count(r => r.ExecutionStatus == EvaluationStatus.Failed),
                Count(r => r.ExecutionStatus == EvaluationStatus.Invalid)),
            new(Count(r => r.EvidenceStatus == AssertionStatus.Passed), evidenceFailed, evidenceUnknown,
                Count(r => r.ScenarioStatus == AssertionStatus.Passed), scenarioFailed, scenarioUnknown),
            new(Count(r => r.ModelStatus == ModelObservationStatus.Adjudicated),
                Count(r => r.ModelStatus == ModelObservationStatus.Unadjudicated),
                Count(r => r.ModelStatus == ModelObservationStatus.NotEvaluated), eligible.Length,
                rows.Sum(r => r.FindingCount), rows.Sum(r => r.ToolObservationCount), rows.Sum(r => r.ExpectedDefects),
                rows.Sum(r => r.StructuralMatches), rows.Sum(r => r.StructurallyMissingDefects),
                rows.Sum(r => r.DuplicateObservations), rows.Sum(r => r.ProhibitedObservations),
                rows.Sum(r => r.AdjudicatedTrue), rows.Sum(r => r.AdjudicatedFalse), rows.Sum(r => r.AdjudicatedDefects),
                rows.Sum(r => r.UnadjudicatedFindings), expectedEligible, expectedEligible - credited),
            new(Source(EvaluationFailureSource.None), Source(EvaluationFailureSource.Provider), Source(EvaluationFailureSource.Agent),
                Source(EvaluationFailureSource.Tool), Source(EvaluationFailureSource.HostState), Source(EvaluationFailureSource.Evaluator),
                Source(EvaluationFailureSource.Unknown), Count(r => r.Code == EvaluationCode.AdjudicationInvalid)),
            evidenceFailed + scenarioFailed > 0 ? AssertionStatus.Failed : rows.Length == 0 || evidenceUnknown + scenarioUnknown > 0
                ? AssertionStatus.NotEvaluated : AssertionStatus.Passed,
            rows.Any(r => r.ExecutionStatus != EvaluationStatus.Completed || r.EvidenceStatus == AssertionStatus.Failed ||
                r.ScenarioStatus == AssertionStatus.Failed || r.Code == EvaluationCode.AdjudicationInvalid),
            ReportTelemetry.NotSupplied, orderedReasons,
            Ratio(eligible.Sum(r => r.AdjudicatedTrue), eligible.Sum(r => r.AdjudicatedTrue + r.AdjudicatedFalse)),
            Ratio(credited, expectedEligible));
    }

    internal static ImmutableArray<ReportReason> Reasons(ImmutableArray<EvaluationOutcome> rows)
    {
        var reasons = ImmutableArray.CreateBuilder<ReportReason>();
        if (rows.IsEmpty) reasons.Add(ReportReason.EmptyPopulation);
        if (rows.Any(r => r.ConfigurationSha256 is null)) reasons.Add(ReportReason.UnknownContext);
        if (rows.Any(r => r.SourceClean == false)) reasons.Add(ReportReason.DirtySource);
        if (rows.Select(r => r.CorpusSha256).Distinct().Count() > 1) reasons.Add(ReportReason.MixedCorpus);
        var known = rows.Where(r => r.ConfigurationSha256 is not null).ToArray();
        if (known.Select(r => r.ConfigurationSha256).Distinct().Count() > 1) reasons.Add(ReportReason.MixedConfiguration);
        if (known.Select(r => r.Mode).Distinct().Count() > 1) reasons.Add(ReportReason.MixedMode);
        if (known.Select(r => (r.SourceCommit, r.SourceTree, r.SourceClean)).Distinct().Count() > 1) reasons.Add(ReportReason.MixedSource);
        if (rows.Any(r => r.ExecutionStatus != EvaluationStatus.Completed)) reasons.Add(ReportReason.IncompleteExecution);
        if (rows.Any(r => r.EvidenceStatus == AssertionStatus.Failed)) reasons.Add(ReportReason.RejectedEvidence);
        if (rows.Any(r => r.ModelStatus != ModelObservationStatus.Adjudicated)) reasons.Add(ReportReason.IncompleteAnnotations);
        return reasons.ToImmutable();
    }
}
