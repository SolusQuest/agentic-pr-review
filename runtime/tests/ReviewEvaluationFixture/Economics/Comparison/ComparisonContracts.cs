using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Contracts;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Histories;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Comparison;

internal static class ComparisonLimits
{
    internal const string InputFormat = "apr.r6.comparison-side.v1";
    internal const string ReportFormat = "apr.r6.economics-comparison.v1";
    internal const int InputBytes = 16 * 1024 * 1024;
    internal const int ReportBytes = 36 * 1024 * 1024;
    internal const int Histories = 8;
    internal const int Expectations = 2048;
}

// Selection commits to supplied data. It is neither a pre-run receipt nor origin authentication.
internal sealed record ComparisonSelection(
    [property: JsonRequired] string SourceCommit,
    [property: JsonRequired] string SourceTree,
    [property: JsonRequired] bool SourceClean,
    [property: JsonRequired] string BuildId,
    [property: JsonRequired] string PlanSha256,
    [property: JsonRequired] string JournalSha256,
    [property: JsonRequired] string TariffSha256,
    [property: JsonRequired] string EvidenceSha256);

internal sealed record EffectiveReviewDefinition(
    [property: JsonRequired] string Predicate,
    [property: JsonRequired] string RequiredOrigin,
    [property: JsonRequired] int MinimumPopulation);

internal sealed record DeclaredRegressionRule(
    [property: JsonRequired] string Metric,
    [property: JsonRequired] decimal AbsoluteIncreaseThreshold,
    [property: JsonRequired] bool PromotionBlocking);

internal sealed record ComparisonDeclaration(
    [property: JsonRequired] string Axis,
    [property: JsonRequired] string ControlClaim,
    [property: JsonRequired] ComparisonSelection Left,
    [property: JsonRequired] ComparisonSelection Right,
    [property: JsonRequired] EffectiveReviewDefinition? EffectiveReview,
    [property: JsonRequired] DeclaredRegressionRule? RegressionRule);

internal sealed record AnnotationOrigin(
    [property: JsonRequired] string OutcomeSha256,
    [property: JsonRequired] string ExecutionSha256,
    [property: JsonRequired] string Origin);

internal sealed record ScenarioExpectation(
    [property: JsonRequired] string HistorySha256,
    [property: JsonRequired] int Phase,
    [property: JsonRequired] int CallOrdinal,
    [property: JsonRequired] string ObservationSha256,
    [property: JsonRequired] string Expectation);

internal sealed record ComparisonEvidence(
    [property: JsonRequired] ImmutableArray<EvaluationOutcome> Outcomes,
    [property: JsonRequired] ImmutableArray<AnnotationOrigin> Annotations,
    [property: JsonRequired] ImmutableArray<HistoryReport> Histories,
    [property: JsonRequired] ImmutableArray<ScenarioExpectation> Expectations);

internal sealed record ComparisonInput(
    [property: JsonRequired] string Format,
    [property: JsonRequired] ComparisonDeclaration Declaration,
    [property: JsonRequired] PricingReportDocument Pricing,
    [property: JsonRequired] ComparisonEvidence Evidence);

internal sealed record ComparisonDimension(string Status, ImmutableArray<string> Reasons);
internal sealed record CompletionRate(int Completed, int Scheduled, decimal Value);
internal sealed record EffectiveReviewCost(string Status, string Reason, string Currency,
    decimal? FullCampaignAmount, int Eligible, int Excluded, int Unassessed,
    int? MinimumPopulation, decimal? Value);

internal sealed record ComparisonSideSummary(
    UsageJournalTotals Campaign,
    UsageJournalReservations Reservations,
    CompletionRate CompletionRate,
    ComparisonDimension Usage,
    ComparisonDimension Pricing,
    ComparisonDimension Execution,
    ComparisonDimension QualityEligibility,
    int MatchedOutcomes,
    int QualityEligibleOutcomes,
    int ScenarioPassedOutcomes,
    int AiAnnotations,
    int HumanDeclaredAnnotations,
    int UnknownAnnotationOrigins,
    string IndependentHumanConfirmation,
    EffectiveReviewCost EffectiveReviewCost,
    int HistoryReports,
    int AcceptedHistoryRows,
    int RejectedHistoryRows,
    int UnmatchedHistorySources,
    string CampaignLifecycleAssociation);

internal sealed record PrefixSourceSummary(string SourceCommit, string SourceTree, bool SourceClean,
    int Observations, int Incomparable, int UnverifiedObservations, int ObservedInstability, int StableUnderDeclaredExpectation,
    int IntentionalFaults, int UnknownExpectedness, int UnexpectedDrift,
    string PromotionDisposition);

internal sealed record ComparisonPrefixSummary(ImmutableArray<PrefixSourceSummary> Sources,
    int UnboundExpectations, string CampaignPrefixAssociation);

internal sealed record ComparisonResult(
    ComparisonSideSummary Left,
    ComparisonSideSummary Right,
    ComparisonDimension DeclaredTaskWorkload,
    string HistoryWorkloadEquivalence,
    ComparisonDimension DescriptiveComparison,
    decimal? ObservedReferenceTotalDifference,
    string DescriptiveDirection,
    ComparisonPrefixSummary PrefixEvidence,
    ComparisonDimension ExperimentalControl,
    string RulePredeclaration,
    string FormalRegression,
    string PromotionDisposition,
    string HistoricalR5Quality,
    string R7Readiness,
    string EvidenceAuthority);

internal sealed record ComparisonReportDocument(string Format, ComparisonInput Left,
    ComparisonInput Right, ComparisonResult Result);

internal sealed class ComparisonInputException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}
