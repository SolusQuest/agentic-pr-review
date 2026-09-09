using System.Globalization;
using System.Text;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Reporting;

internal static class EvaluationReportMarkdown
{
    internal static ReportingResult<string> Write(EvaluationReport? report, int maximumBytes = ReportingLimits.MarkdownBytes)
    {
        if (report is null) return new(null, ReportingError.InvalidInput);
        var text = new StringBuilder("# R5 evaluation observations\n\n");
        Summary(text, report.Document.Summary);
        text.Append("\n## Cohorts\n\n");
        foreach (var cohort in report.Document.Cohorts)
        {
            Identity(text, cohort.Identity);
            Summary(text, cohort.Summary);
        }
        text.Append("\n## Case attempts\n\n| Case | Execution | Evidence | Scenario | Model | Code |\n| --- | --- | --- | --- | --- | --- |\n");
        foreach (var row in report.Document.Outcomes)
            text.Append(CultureInfo.InvariantCulture, $"| `{row.CaseId}` | {row.ExecutionStatus} | {row.EvidenceStatus} | {row.ScenarioStatus} | {row.ModelStatus} | {row.Code} |\n");
        text.Append("\nCounts describe supplied case-attempt rows, not an unseen schedule. Detached context and missing telemetry remain unknown. Deterministic results prove system mechanics, not live model accuracy. Ratios impose no quality threshold.\n");
        return Bound(text, maximumBytes);
    }

    internal static ReportingResult<string> Write(EvaluationReportComparison? comparison, int maximumBytes = ReportingLimits.MarkdownBytes)
    {
        if (comparison is null) return new(null, ReportingError.InvalidInput);
        var value = comparison.Document;
        var text = new StringBuilder("# R5 runtime comparison\n\n");
        text.Append(value.Comparable ? "Comparable inputs. Runtime source is the comparison variable.\n\n" : "Incomparable inputs; no runtime quality regression or improvement is inferred.\n\n");
        text.Append("Reasons: ").Append(value.Reasons.IsEmpty ? "None" : string.Join(", ", value.Reasons)).Append("\n\n");
        foreach (var (label, side) in new[] { ("Baseline", value.Baseline), ("Candidate", value.Candidate) })
        {
            text.Append("## ").Append(label).Append("\n\n");
            foreach (var identity in side.Cohorts) Identity(text, identity);
            Summary(text, side.Summary);
            text.Append("\n| Case | Case identity | Attempts | Eligible |\n| --- | --- | --- | --- |\n");
            foreach (var row in side.Coverage)
                text.Append(CultureInfo.InvariantCulture, $"| `{row.CaseId}` | `{row.CaseSha256}` | {row.AttemptedCases} | {row.EligibleCases} |\n");
            text.Append('\n');
        }
        return Bound(text, maximumBytes);
    }

    internal static string WriteFailure(ReportingError error, int submittedRows) =>
        string.Create(CultureInfo.InvariantCulture, $"R5 report unavailable: {(Enum.IsDefined(error) && error != ReportingError.None ? error : ReportingError.InvalidInput)}. Submitted rows: {Math.Max(0, submittedRows)}.\n");

    private static void Identity(StringBuilder text, ReportCohortIdentity identity)
    {
        text.Append("Corpus: `").Append(identity.CorpusSha256).Append("`; configuration: `")
            .Append(identity.ConfigurationSha256 ?? "unknown").Append("`; mode: `").Append(identity.Mode ?? "unknown")
            .Append("`.\n\nSource commit: `").Append(identity.SourceCommit ?? "unknown").Append("`; tree: `")
            .Append(identity.SourceTree ?? "unknown").Append("`; clean: ")
            .Append(identity.SourceClean is null ? "unknown" : identity.SourceClean.Value ? "true" : "false").Append(".\n\n");
    }

    private static void Summary(StringBuilder text, ReportSummary summary)
    {
        var e = summary.Execution;
        var o = summary.Observations;
        var a = summary.Assertions;
        var f = summary.Failures;
        text.Append(CultureInfo.InvariantCulture, $"Case attempts: {e.AttemptedCases}; known attempt context: {e.KnownAttemptCases}; detached: {e.DetachedCases}; completed: {e.Completed}; failed: {e.Failed}; invalid: {e.Invalid}.\n\n");
        text.Append(CultureInfo.InvariantCulture, $"Model cases: adjudicated {o.AdjudicatedCases}; unadjudicated {o.UnadjudicatedCases}; not evaluated {o.NotEvaluatedCases}; eligible {o.EligibleCases}.\n\n");
        text.Append(CultureInfo.InvariantCulture, $"Engineering assertions: {summary.EngineeringStatus}; blocking failures: {(summary.HasBlockingFailures ? "yes" : "no")}; evidence failures {a.EvidenceFailed}; scenario failures {a.ScenarioFailed}.\n\n");
        text.Append(CultureInfo.InvariantCulture, $"Failure attribution: provider {f.Provider}; Agent {f.Agent}; tool {f.Tool}; Host/state {f.HostState}; evaluator {f.Evaluator}; unknown {f.Unknown}; invalid annotations {f.InvalidAdjudications}.\n\n");
        text.Append(CultureInfo.InvariantCulture, $"Findings: {o.Findings}; represented tool observations: {o.RepresentedToolObservations}; expected defects across all rows: {o.ExpectedDefects}; structural matches: {o.StructuralMatches}; structural misses: {o.StructurallyMissingDefects}; duplicate observations: {o.DuplicateObservations}; prohibited observations: {o.ProhibitedObservations}.\n\n");
        text.Append(CultureInfo.InvariantCulture, $"Adjudicated observations: true {o.AdjudicatedTrue}; false {o.AdjudicatedFalse}; unique defect credit {o.AdjudicatedDefects}; pending findings {o.UnadjudicatedFindings}. Eligible expected defects: {o.EligibleExpectedDefects}; eligible uncredited defects: {o.EligibleUncreditedDefects}.\n\n");
        Ratio(text, "Finding precision", summary.Precision);
        Ratio(text, "Expected-defect recall", summary.Recall);
        text.Append("Coverage reasons: ").Append(summary.Reasons.IsEmpty ? "None" : string.Join(", ", summary.Reasons)).Append(".\n\n");
        text.Append("Token, duration, cost and usage telemetry: unknown (not supplied by Q1).\n\n");
    }

    private static void Ratio(StringBuilder text, string name, ReportRatio ratio)
    {
        text.Append(name).Append(": ").Append(ratio.Value?.ToString("0.######", CultureInfo.InvariantCulture) ?? "unavailable")
            .Append(CultureInfo.InvariantCulture, $" ({ratio.Availability}; eligible numerator/denominator {ratio.Numerator}/{ratio.Denominator}; eligible cases {ratio.EligibleCases}; excluded cases {ratio.ExcludedCases}).\n\n");
    }

    private static ReportingResult<string> Bound(StringBuilder text, int maximumBytes)
    {
        if (maximumBytes is < 1 or > ReportingLimits.MarkdownBytes) return new(null, ReportingError.InvalidOutputBudget);
        var result = text.ToString();
        return Encoding.UTF8.GetByteCount(result) > maximumBytes ? new(null, ReportingError.OutputLimit) : new(result, ReportingError.None);
    }
}
