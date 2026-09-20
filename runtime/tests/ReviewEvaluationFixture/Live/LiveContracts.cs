using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Contracts;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

// An admitted plan is the complete authorization input for one live-local
// invocation. Token and spend ceilings are maintainer-authorized reservation
// bounds; the harness enforces them mechanically and records violations as
// inconclusive evidence rather than proving provider billing.
internal static class LiveLimits
{
    internal const string PlanFormat = "apr.r5.live-plan.v1";
    internal const int PlanBytes = 64 * 1024;
    internal const int ScheduleEntries = 64;
    internal const int ExpandedEvaluations = 256;
    internal const int Repeats = 16;
}

internal sealed record LivePlanSource(
    [property: JsonRequired] string Commit,
    [property: JsonRequired] string Tree,
    [property: JsonRequired] bool Clean);

internal sealed record LivePlanCorpus(
    [property: JsonRequired] string Path,
    [property: JsonRequired] string Sha256);

internal sealed record LivePlanProvider(
    [property: JsonRequired] string ProviderId,
    [property: JsonRequired] string ModelId,
    [property: JsonRequired] string AdapterId,
    [property: JsonRequired] string ConfigurationSha256);

internal sealed record LivePlanScheduleEntry(
    [property: JsonRequired] string CaseId,
    [property: JsonRequired] int Repeats);

internal sealed record LivePlanPerCall(
    [property: JsonRequired] long MaxInputTokens,
    [property: JsonRequired] long MaxOutputTokens,
    [property: JsonRequired] long MaxChargeMicroUsd);

internal sealed record LivePlanBounds(
    [property: JsonRequired] long MaxEvaluations,
    [property: JsonRequired] long MaxModelCalls,
    [property: JsonRequired] long MaxInputTokens,
    [property: JsonRequired] long MaxOutputTokens,
    [property: JsonRequired] long MaxCombinedTokens,
    [property: JsonRequired] long MaxSeconds,
    [property: JsonRequired] long SpendCeilingMicroUsd,
    [property: JsonRequired] LivePlanPerCall PerCall);

internal sealed record LivePlanInput(
    [property: JsonRequired] string Format,
    [property: JsonRequired] LivePlanSource Source,
    [property: JsonRequired] LivePlanCorpus Corpus,
    [property: JsonRequired] LivePlanProvider Provider,
    [property: JsonRequired] ImmutableArray<LivePlanScheduleEntry> Schedule,
    [property: JsonRequired] LivePlanBounds Bounds);

// Normalized admitted projection; its digest is the only public plan identity.
internal sealed record LivePlanDigestInput(
    [property: JsonRequired] string Format,
    [property: JsonRequired] LivePlanSource Source,
    [property: JsonRequired] string CorpusSha256,
    [property: JsonRequired] LivePlanProvider Provider,
    [property: JsonRequired] ImmutableArray<string> Schedule,
    [property: JsonRequired] LivePlanBounds Bounds);

internal sealed record LiveTransportOutcomeCounts(
    int RequestRejected,
    int ResponseTooLarge,
    int Http4xx,
    int Http429,
    int Http5xx,
    int ConnectTimeout,
    int ProviderTimeout,
    int TransportFailure,
    int BudgetRefused,
    int ViolationRefused,
    int Cancelled,
    int BackendExceptions,
    int NormalizationExceptions);

internal sealed record LiveRunSummary(
    string Format,
    string ExecutionKind,
    string PlanSha256,
    string CorpusSha256,
    string SourceCommit,
    string SourceTree,
    bool SourceClean,
    int Scheduled,
    int Attempted,
    int Completed,
    int Failed,
    int Invalid,
    int Unattempted,
    int SimulatedAdapterCalls,
    int ActualProviderCalls,
    long KnownInputTokens,
    long KnownOutputTokens,
    long KnownCombinedTokens,
    long ReservedInputTokens,
    long ReservedOutputTokens,
    long ReservedCombinedTokens,
    int UsageUnknownCalls,
    bool AccountingViolation,
    LiveTransportOutcomeCounts TransportOutcomeCounts,
    long ReservedSpendMicroUsd,
    long SpendCeilingMicroUsd,
    string StopReason,
    string Cleanup,
    ImmutableArray<LiveAgentDiagnostic> AgentDiagnostics,
    string AdjudicationStatus = "not_requested",
    int HumanConfirmedCases = 0,
    int AiAdjudicatedCases = 0,
    LiveCacheUsageSummary? CacheUsage = null,
    UsageJournalDocument? UsageJournal = null);

// Known subtotals describe only measured observations, not billed campaign
// totals. The fixed identity domain bounds the output independently of traffic.
internal sealed record LiveCacheUsageSummary(
    string ProviderId,
    string Status,
    int MeasuredCalls,
    int KnownUsageWithoutCacheCalls,
    long? CacheReadInputTokens,
    long? UncachedInputTokens,
    string CacheWriteBillingStatus,
    string RequestedModel,
    ImmutableArray<string> ResponseModels,
    string BackendSnapshotStatus);

internal enum LiveAdmissionCode
{
    Admitted,
    InvalidPlan,
    InvalidSource,
    InvalidCorpus,
    UnsupportedConfiguration,
    Unpriceable,
    SecretInvalid,
    IoFailure,
}

internal sealed class LivePlanRejected(LiveAdmissionCode code) : Exception(code.ToString())
{
    internal LiveAdmissionCode Code { get; } = code;
}

internal sealed class LivePlan(
    LivePlanCorpus corpus,
    LivePlanProvider provider,
    LivePlanBounds bounds,
    ImmutableArray<string> schedule,
    string digest)
{
    internal LivePlanCorpus Corpus { get; } = corpus;
    internal LivePlanProvider Provider { get; } = provider;
    internal LivePlanBounds Bounds { get; } = bounds;
    internal ImmutableArray<string> Schedule { get; } = schedule;
    internal string Digest { get; } = digest;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 12,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(LivePlanInput))]
[JsonSerializable(typeof(LivePlanDigestInput))]
[JsonSerializable(typeof(LiveRunSummary))]
internal sealed partial class LiveJsonContext : JsonSerializerContext;
