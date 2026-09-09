using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Reporting;

internal static class ReportingLimits
{
    internal const int Rows = 256;
    internal const int InputBytes = 4 * 1024 * 1024;
    internal const int JsonBytes = 1024 * 1024;
    internal const int MarkdownBytes = 256 * 1024;
    internal const int Depth = 16;
}

internal enum ReportingError { None, InvalidInput, RowLimit, InputByteLimit, DuplicateAttempt, ConflictingCase, InvalidReport, OutputLimit, InvalidOutputBudget }
internal enum RatioAvailability { Available, EmptyDenominator, Incomplete, Incomparable }
internal enum ReportReason
{
    EmptyPopulation, UnknownContext, DirtySource, MixedCorpus, MixedConfiguration, MixedMode, MixedSource,
    IncompleteExecution, RejectedEvidence, IncompleteAnnotations, CorpusChanged, ConfigurationChanged,
    ModeChanged, ExpectationsChanged, PopulationChanged,
}
internal enum ReportTelemetry { NotSupplied }

internal sealed record ReportingResult<T>(T? Value, ReportingError Error, int SubmittedRows = 0) where T : class
{
    internal bool Succeeded => Value is not null && Error == ReportingError.None;
    public override string ToString() => "evaluation_reporting_result";
}

internal sealed record ReportRatio(
    [property: JsonRequired] int Numerator,
    [property: JsonRequired] int Denominator,
    [property: JsonRequired] int EligibleCases,
    [property: JsonRequired] int ExcludedCases,
    [property: JsonRequired] RatioAvailability Availability,
    [property: JsonRequired] decimal? Value);

internal sealed record ReportExecutionCounts(
    [property: JsonRequired] int AttemptedCases,
    [property: JsonRequired] int KnownAttemptCases,
    [property: JsonRequired] int DetachedCases,
    [property: JsonRequired] int Completed,
    [property: JsonRequired] int Failed,
    [property: JsonRequired] int Invalid);

internal sealed record ReportAssertionCounts(
    [property: JsonRequired] int EvidencePassed,
    [property: JsonRequired] int EvidenceFailed,
    [property: JsonRequired] int EvidenceNotEvaluated,
    [property: JsonRequired] int ScenarioPassed,
    [property: JsonRequired] int ScenarioFailed,
    [property: JsonRequired] int ScenarioNotEvaluated);

internal sealed record ReportObservationCounts(
    [property: JsonRequired] int AdjudicatedCases,
    [property: JsonRequired] int UnadjudicatedCases,
    [property: JsonRequired] int NotEvaluatedCases,
    [property: JsonRequired] int EligibleCases,
    [property: JsonRequired] int Findings,
    [property: JsonRequired] int RepresentedToolObservations,
    [property: JsonRequired] int ExpectedDefects,
    [property: JsonRequired] int StructuralMatches,
    [property: JsonRequired] int StructurallyMissingDefects,
    [property: JsonRequired] int DuplicateObservations,
    [property: JsonRequired] int ProhibitedObservations,
    [property: JsonRequired] int AdjudicatedTrue,
    [property: JsonRequired] int AdjudicatedFalse,
    [property: JsonRequired] int AdjudicatedDefects,
    [property: JsonRequired] int UnadjudicatedFindings,
    [property: JsonRequired] int EligibleExpectedDefects,
    [property: JsonRequired] int EligibleUncreditedDefects);

internal sealed record ReportFailureCounts(
    [property: JsonRequired] int None,
    [property: JsonRequired] int Provider,
    [property: JsonRequired] int Agent,
    [property: JsonRequired] int Tool,
    [property: JsonRequired] int HostState,
    [property: JsonRequired] int Evaluator,
    [property: JsonRequired] int Unknown,
    [property: JsonRequired] int InvalidAdjudications);

internal sealed record ReportSummary(
    [property: JsonRequired] ReportExecutionCounts Execution,
    [property: JsonRequired] ReportAssertionCounts Assertions,
    [property: JsonRequired] ReportObservationCounts Observations,
    [property: JsonRequired] ReportFailureCounts Failures,
    [property: JsonRequired] AssertionStatus EngineeringStatus,
    [property: JsonRequired] bool HasBlockingFailures,
    [property: JsonRequired] ReportTelemetry Telemetry,
    [property: JsonRequired] ImmutableArray<ReportReason> Reasons,
    [property: JsonRequired] ReportRatio Precision,
    [property: JsonRequired] ReportRatio Recall);

internal sealed record ReportCohortIdentity(
    [property: JsonRequired] string CorpusSha256,
    [property: JsonRequired] string? ConfigurationSha256,
    [property: JsonRequired] string? Mode,
    [property: JsonRequired] string? SourceCommit,
    [property: JsonRequired] string? SourceTree,
    [property: JsonRequired] bool? SourceClean)
{
    internal static ReportCohortIdentity From(EvaluationOutcome row) => new(row.CorpusSha256,
        row.ConfigurationSha256, row.Mode, row.SourceCommit, row.SourceTree, row.SourceClean);
}

internal sealed record ReportCohort(
    [property: JsonRequired] ReportCohortIdentity Identity,
    [property: JsonRequired] ImmutableArray<int> RowIndexes,
    [property: JsonRequired] ReportSummary Summary);

internal sealed record EvaluationReportDocument(
    [property: JsonRequired] ImmutableArray<EvaluationOutcome> Outcomes,
    [property: JsonRequired] ReportSummary Summary,
    [property: JsonRequired] ImmutableArray<ReportCohort> Cohorts);

// Factory-owned data admission only; this grants neither execution nor Runtime subject authority.
internal sealed class EvaluationReport
{
    private EvaluationReport(EvaluationReportDocument document) => Document = document;
    internal EvaluationReportDocument Document { get; }
    internal static ReportingResult<EvaluationReport> Create(ImmutableArray<ReadOnlyMemory<byte>> rows)
    {
        var result = EvaluationReportBuilder.Build(rows);
        return new(result.Value is null ? null : new(result.Value), result.Error, result.SubmittedRows);
    }
    public override string ToString() => "evaluation_report";
}
