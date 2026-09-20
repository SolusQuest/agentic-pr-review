using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Contracts;

internal static class UsageJournalLimits
{
    internal const int Attempts = LiveLimits.ExpandedEvaluations;
    internal const int CallsPerAttempt = AgentLimits.ModelCalls;
    internal const int Calls = Attempts * CallsPerAttempt;
    internal const int JsonBytes = 4 * 1024 * 1024;
    internal const int Depth = 12;
}

internal sealed record UsageJournalProvenance(
    [property: JsonRequired] string CampaignId,
    [property: JsonRequired] string SourceCommit,
    [property: JsonRequired] string SourceTree,
    [property: JsonRequired] bool SourceClean,
    [property: JsonRequired] string BuildId,
    [property: JsonRequired] string CorpusSha256,
    [property: JsonRequired] string ProviderConfigurationSha256,
    [property: JsonRequired] string PlanSha256,
    [property: JsonRequired] string ExecutionKind);

internal sealed record UsageJournalCache(
    [property: JsonRequired] string ResponseModel,
    [property: JsonRequired] long CacheReadInputTokens,
    [property: JsonRequired] long UncachedInputTokens);

internal sealed record UsageJournalUsage(
    [property: JsonRequired] long InputTokens,
    [property: JsonRequired] long OutputTokens,
    [property: JsonRequired] long CombinedTokens,
    [property: JsonRequired] UsageJournalCache? Cache);

internal sealed record UsageJournalAttempt(
    [property: JsonRequired] string BindingSha256,
    [property: JsonRequired] string AttemptId,
    [property: JsonRequired] int ScheduleIndex,
    [property: JsonRequired] string CaseId,
    [property: JsonRequired] string Status,
    [property: JsonRequired] string AgentStatus,
    [property: JsonRequired] string? EvaluationAttemptSha256,
    [property: JsonRequired] int Calls,
    [property: JsonRequired] int Sends,
    [property: JsonRequired] int LocalRefusals);

// CallId identifies every chat invocation; it is a send identity only when
// Dispatched is true. A local refusal has no provider usage, including zero.
internal sealed record UsageJournalCall(
    [property: JsonRequired] string BindingSha256,
    [property: JsonRequired] string CallId,
    [property: JsonRequired] string AttemptId,
    [property: JsonRequired] int Ordinal,
    [property: JsonRequired] bool Dispatched,
    [property: JsonRequired] string TransportOutcome,
    [property: JsonRequired] string ChatOutcome,
    [property: JsonRequired] string UsageStatus,
    [property: JsonRequired] UsageJournalUsage? Usage);

internal sealed record UsageJournalReservations(
    [property: JsonRequired] long Calls,
    [property: JsonRequired] long InputTokens,
    [property: JsonRequired] long OutputTokens,
    [property: JsonRequired] long CombinedTokens,
    [property: JsonRequired] long SpendMicroUsd);

// Decimal is exact integer arithmetic here, not floating-point pricing. The
// maximum 2048 Int64 observations fit even when their sum exceeds Int64.
internal sealed record UsageJournalTotals(
    [property: JsonRequired] int Scheduled,
    [property: JsonRequired] int Attempted,
    [property: JsonRequired] int Completed,
    [property: JsonRequired] int Failed,
    [property: JsonRequired] int Invalid,
    [property: JsonRequired] int Unattempted,
    [property: JsonRequired] int Calls,
    [property: JsonRequired] int ActualSends,
    [property: JsonRequired] int LocalRefusals,
    [property: JsonRequired] int KnownUsageSends,
    [property: JsonRequired] int UnknownUsageSends,
    [property: JsonRequired] bool UsageComplete,
    [property: JsonRequired] decimal KnownInputTokens,
    [property: JsonRequired] decimal KnownOutputTokens,
    [property: JsonRequired] decimal KnownCombinedTokens,
    [property: JsonRequired] int MeasuredCacheSends,
    [property: JsonRequired] decimal? CacheReadInputTokens,
    [property: JsonRequired] decimal? UncachedInputTokens);

internal sealed record UsageJournalDocument(
    [property: JsonRequired] UsageJournalProvenance Provenance,
    [property: JsonRequired] string BindingSha256,
    [property: JsonRequired] string StopReason,
    [property: JsonRequired] string CacheWriteBillingStatus,
    [property: JsonRequired] UsageJournalReservations Reservations,
    [property: JsonRequired] ImmutableArray<UsageJournalAttempt> Attempts,
    [property: JsonRequired] ImmutableArray<UsageJournalCall> Calls,
    [property: JsonRequired] UsageJournalTotals Totals);

// Trusted caller input, never reconstructed from the candidate journal. This
// binds admission to a selected plan, not to proof of historical paid traffic.
internal sealed class UsageJournalExpectation
{
    internal UsageJournalExpectation(UsageJournalProvenance provenance,
        ImmutableArray<string> schedule, LivePlanBounds bounds)
    {
        if (provenance is null || !EvaluationLimits.Id(provenance.CampaignId) ||
            !EvaluationLimits.Hash(provenance.SourceCommit, 40) ||
            !EvaluationLimits.Hash(provenance.SourceTree, 40) ||
            !EvaluationLimits.Id(provenance.BuildId) ||
            !EvaluationLimits.Hash(provenance.CorpusSha256) ||
            !EvaluationLimits.Hash(provenance.ProviderConfigurationSha256) ||
            !EvaluationLimits.Hash(provenance.PlanSha256) ||
            provenance.ExecutionKind is not ("live" or "loopback") ||
            schedule.IsDefaultOrEmpty || schedule.Length > UsageJournalLimits.Attempts ||
            schedule.Any(id => !EvaluationLimits.Id(id)) || bounds is null ||
            bounds.PerCall is null || bounds.MaxModelCalls is < 1 or > UsageJournalLimits.Calls ||
            bounds.PerCall.MaxInputTokens < 1 || bounds.PerCall.MaxOutputTokens < 1 ||
            bounds.PerCall.MaxChargeMicroUsd < 1 ||
            !EvaluationLimits.Id(provenance.CampaignId + "-256-c8"))
            throw new ArgumentException("usage_journal_expectation_invalid");
        Provenance = provenance;
        Schedule = schedule;
        Bounds = bounds;
        BindingSha256 = AgentCanonical.HashDomain("apr.r6.usage-journal.provenance",
            JsonSerializer.SerializeToUtf8Bytes(provenance, UsageJournalJsonContext.Default.UsageJournalProvenance));
    }

    internal UsageJournalProvenance Provenance { get; }
    internal ImmutableArray<string> Schedule { get; }
    internal LivePlanBounds Bounds { get; }
    internal string BindingSha256 { get; }
    internal string AttemptId(int index) => Provenance.CampaignId + "-" + (index + 1);
    internal string CallId(int index, int ordinal) => AttemptId(index) + "-c" + ordinal;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, MaxDepth = UsageJournalLimits.Depth)]
[JsonSerializable(typeof(UsageJournalDocument))]
[JsonSerializable(typeof(UsageJournalProvenance))]
internal sealed partial class UsageJournalJsonContext : JsonSerializerContext;
