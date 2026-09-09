using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Reporting;

internal sealed record ReportCaseCoverage(
    [property: JsonRequired] string CorpusSha256,
    [property: JsonRequired] string CaseId,
    [property: JsonRequired] string CaseSha256,
    [property: JsonRequired] int ExpectedDefects,
    [property: JsonRequired] int AttemptedCases,
    [property: JsonRequired] int EligibleCases);

internal sealed record ReportComparisonSide(
    [property: JsonRequired] ImmutableArray<ReportCohortIdentity> Cohorts,
    [property: JsonRequired] ImmutableArray<ReportCaseCoverage> Coverage,
    [property: JsonRequired] ReportSummary Summary);

internal sealed record ReportComparisonDocument(
    [property: JsonRequired] bool Comparable,
    [property: JsonRequired] ImmutableArray<ReportReason> Reasons,
    [property: JsonRequired] ReportComparisonSide Baseline,
    [property: JsonRequired] ReportComparisonSide Candidate);

internal sealed class EvaluationReportComparison
{
    private EvaluationReportComparison(ReportComparisonDocument document) => Document = document;
    internal ReportComparisonDocument Document { get; }

    internal static ReportingResult<EvaluationReportComparison> Create(EvaluationReport? baseline, EvaluationReport? candidate)
    {
        if (baseline is null || candidate is null) return new(null, ReportingError.InvalidInput);
        var left = Side(baseline.Document);
        var right = Side(candidate.Document);
        var reasons = new HashSet<ReportReason>(left.Summary.Reasons.Concat(right.Summary.Reasons));
        static string[] Values(EvaluationReport report, Func<EvaluationOutcome, string?> selector) =>
            report.Document.Outcomes.Select(selector).Where(s => s is not null).Cast<string>()
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!Values(baseline, r => r.CorpusSha256).SequenceEqual(Values(candidate, r => r.CorpusSha256)))
            reasons.Add(ReportReason.CorpusChanged);
        if (!Values(baseline, r => r.ConfigurationSha256).SequenceEqual(Values(candidate, r => r.ConfigurationSha256)))
            reasons.Add(ReportReason.ConfigurationChanged);
        if (!Values(baseline, r => r.Mode).SequenceEqual(Values(candidate, r => r.Mode)))
            reasons.Add(ReportReason.ModeChanged);

        // Case/repeat populations must agree; findings and exact execution hashes are comparison outputs.
        var leftPopulation = left.Coverage.Select(c => (c.CorpusSha256, c.CaseId, c.AttemptedCases));
        var rightPopulation = right.Coverage.Select(c => (c.CorpusSha256, c.CaseId, c.AttemptedCases));
        if (!leftPopulation.SequenceEqual(rightPopulation)) reasons.Add(ReportReason.PopulationChanged);
        var leftExpectations = left.Coverage.Select(c => (c.CorpusSha256, c.CaseId, c.CaseSha256, c.ExpectedDefects));
        var rightExpectations = right.Coverage.Select(c => (c.CorpusSha256, c.CaseId, c.CaseSha256, c.ExpectedDefects));
        if (!leftExpectations.SequenceEqual(rightExpectations)) reasons.Add(ReportReason.ExpectationsChanged);
        return new(new(new(reasons.Count == 0, reasons.Order().ToImmutableArray(), left, right)), ReportingError.None);
    }

    private static ReportComparisonSide Side(EvaluationReportDocument report) => new(
        report.Cohorts.Select(c => c.Identity).ToImmutableArray(),
        report.Outcomes.GroupBy(r => (r.CorpusSha256, r.CaseId, r.CaseSha256, r.ExpectedDefects))
            .OrderBy(g => g.Key.CorpusSha256, StringComparer.Ordinal).ThenBy(g => g.Key.CaseId, StringComparer.Ordinal)
            .Select(g => new ReportCaseCoverage(g.Key.CorpusSha256, g.Key.CaseId, g.Key.CaseSha256,
                g.Key.ExpectedDefects, g.Count(), g.Count(EvaluationReportSummary.Eligible))).ToImmutableArray(),
        report.Summary);

    public override string ToString() => "evaluation_report_comparison";
}
