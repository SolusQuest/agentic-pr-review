using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Contracts;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;

internal static class PricingLimits
{
    internal const string TariffFormat = "apr.r6.tariff.v1";
    internal const string ReportFormat = "apr.r6.priced-usage.v1";
    internal const string Formula = "deepseek_hit_miss_output";
    internal const string Aggregation = "campaign_components_then_sum";
    internal const string Normalization = "exact_unrounded_amount_per_input_token";
    internal const int TariffBytes = 64 * 1024;
    internal const int ReportBytes = 5 * 1024 * 1024;
    internal const int MarkdownBytes = 64 * 1024;
}

internal sealed record TariffRate(
    [property: JsonRequired] string Currency,
    [property: JsonRequired] long Units);

internal sealed record TariffRates(
    [property: JsonRequired] TariffRate CacheHitInput,
    [property: JsonRequired] TariffRate CacheMissInput,
    [property: JsonRequired] TariffRate Output);

internal sealed record TariffPeriod(
    [property: JsonRequired] string Status,
    [property: JsonRequired] string? FromInclusive,
    [property: JsonRequired] string? UntilExclusive);

internal sealed record PricingArithmetic(
    [property: JsonRequired] string Rounding,
    [property: JsonRequired] int DecimalPlaces,
    [property: JsonRequired] string Aggregation,
    [property: JsonRequired] string Normalization);

internal sealed record TariffTerms(
    [property: JsonRequired] string Formula,
    [property: JsonRequired] string ProviderId,
    [property: JsonRequired] string RequestedModel,
    [property: JsonRequired] string? ResponseModel,
    [property: JsonRequired] string PriceClass,
    [property: JsonRequired] string ReferenceAt,
    [property: JsonRequired] TariffPeriod EffectivePeriod,
    [property: JsonRequired] long TokenUnit,
    [property: JsonRequired] int RateDecimalPlaces,
    [property: JsonRequired] TariffRates Rates,
    [property: JsonRequired] PricingArithmetic Arithmetic);

internal sealed record TariffInput(
    [property: JsonRequired] string Format,
    [property: JsonRequired] string SourceUrl,
    [property: JsonRequired] string RetrievedOn,
    [property: JsonRequired] TariffTerms Terms);

// The original URL is never echoed. This public projection commits to it,
// but neither the digest nor the user-supplied terms authenticate a tariff.
internal sealed record TariffSnapshot(
    [property: JsonRequired] string Format,
    [property: JsonRequired] string SourceUrlSha256,
    [property: JsonRequired] string RetrievedOn,
    [property: JsonRequired] TariffTerms Terms);

internal sealed record ReferenceApplicability(
    [property: JsonRequired] string Period,
    [property: JsonRequired] string PriceClass);

internal sealed record PricingCoverage(
    [property: JsonRequired] int PricedInputSends,
    [property: JsonRequired] int PricedOutputSends,
    [property: JsonRequired] int UnknownUsageSends,
    [property: JsonRequired] int MissingCachePartitionSends,
    [property: JsonRequired] int ModelUnknownSends,
    [property: JsonRequired] int ModelMismatchSends);

internal sealed record NormalizedPrice(
    [property: JsonRequired] string Availability,
    [property: JsonRequired] decimal? Value,
    [property: JsonRequired] decimal? InputTokens);

internal sealed record PricedUsageView(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] string Currency,
    [property: JsonRequired] decimal? KnownInputAmount,
    [property: JsonRequired] decimal? KnownOutputAmount,
    [property: JsonRequired] decimal? KnownTotalSubtotal,
    [property: JsonRequired] bool InputComplete,
    [property: JsonRequired] bool OutputComplete,
    [property: JsonRequired] bool TotalComplete,
    [property: JsonRequired] decimal? TotalAmount,
    [property: JsonRequired] PricingCoverage Coverage,
    [property: JsonRequired] NormalizedPrice InputAmountPerInputToken,
    [property: JsonRequired] NormalizedPrice TotalAmountPerInputToken);

internal sealed record PricingReportDocument(
    [property: JsonRequired] string Format,
    [property: JsonRequired] UsageJournalDocument Journal,
    [property: JsonRequired] TariffSnapshot Tariff,
    [property: JsonRequired] string JournalSha256,
    [property: JsonRequired] string TariffSha256,
    [property: JsonRequired] ReferenceApplicability ReferenceApplicability,
    [property: JsonRequired] string ExecutionTimeApplicability,
    [property: JsonRequired] decimal? ExecutionTimeTotalAmount,
    [property: JsonRequired] string BackendSnapshotStatus,
    [property: JsonRequired] string InvoiceStatus,
    [property: JsonRequired] PricedUsageView ObservedUsage,
    [property: JsonRequired] PricedUsageView SameTokenAllMiss);
