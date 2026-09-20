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
    [property: JsonRequired] LivePlanDigestInput Plan,
    [property: JsonRequired] string BindingSha256,
    [property: JsonRequired] string StopReason,
    [property: JsonRequired] string CacheWriteBillingStatus,
    [property: JsonRequired] UsageJournalReservations Reservations,
    [property: JsonRequired] ImmutableArray<UsageJournalAttempt> Attempts,
    [property: JsonRequired] ImmutableArray<UsageJournalCall> Calls,
    [property: JsonRequired] UsageJournalTotals Totals);

// Validated provenance and selection. An independently supplied instance adds
// a selected-plan match to structural admission of a journal's embedded claims;
// reconstructing it from candidate bytes cannot establish independent origin.
internal sealed class UsageJournalExpectation
{
    internal UsageJournalExpectation(UsageJournalProvenance provenance, LivePlanDigestInput plan)
    {
        if (provenance is null || !EvaluationLimits.Id(provenance.CampaignId) ||
            !EvaluationLimits.Id(provenance.BuildId) ||
            !LivePlanAdmission.ValidProjection(plan) ||
            provenance.SourceCommit != plan.Source.Commit || provenance.SourceTree != plan.Source.Tree ||
            provenance.SourceClean != plan.Source.Clean || provenance.CorpusSha256 != plan.CorpusSha256 ||
            provenance.ProviderConfigurationSha256 != plan.Provider.ConfigurationSha256 ||
            provenance.PlanSha256 != LivePlanAdmission.Digest(plan) ||
            provenance.ExecutionKind is not ("live" or "loopback") ||
            provenance.ExecutionKind == "live" && !provenance.SourceClean ||
            !EvaluationLimits.Id(provenance.CampaignId + "-256-c8"))
            throw new ArgumentException("usage_journal_expectation_invalid");
        Provenance = provenance;
        Plan = plan;
        BindingSha256 = AgentCanonical.HashDomain("apr.r6.usage-journal.provenance",
            JsonSerializer.SerializeToUtf8Bytes(provenance, UsageJournalJsonContext.Default.UsageJournalProvenance));
    }

    internal UsageJournalProvenance Provenance { get; }
    internal LivePlanDigestInput Plan { get; }
    internal ImmutableArray<string> Schedule => Plan.Schedule;
    internal LivePlanBounds Bounds => Plan.Bounds;
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
