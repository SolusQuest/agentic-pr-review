using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Contracts;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;

internal static class EconomicsLiveLimits
{
    internal const string PlanFormat = "apr.r6.economics-plan.v1";
    internal const string ReportFormat = "apr.r6.economics-run.v1";
    internal const int PlanBytes = 128 * 1024;
    internal const int InputBytes = 256 * 1024;
    internal const int ReplyBytes = 8 * 1024 * 1024;
    internal const int ReportBytes = 12 * 1024 * 1024;
    internal const int Scenarios = 16;
    internal const int Slots = 256;
    internal const int Calls = 8;
    internal const int OutputBasis = 4096;
}

internal sealed record EconomicsScenario(
    [property: JsonRequired] string Profile,
    [property: JsonRequired] int Phases,
    [property: JsonRequired] int Repeats,
    [property: JsonRequired] bool ResetAfterCapacity);

internal sealed record EconomicsPlanInput(
    [property: JsonRequired] string Format,
    [property: JsonRequired] LivePlanSource Source,
    [property: JsonRequired] string BuildSha256,
    [property: JsonRequired] LivePlanCorpus Replay,
    [property: JsonRequired] LivePlanCorpus Growth,
    [property: JsonRequired] LivePlanProvider Provider,
    [property: JsonRequired] string Thinking,
    [property: JsonRequired] string TariffPath,
    [property: JsonRequired] string TariffSha256,
    [property: JsonRequired] ImmutableArray<EconomicsScenario> Scenarios,
    [property: JsonRequired] int ChildSeconds,
    [property: JsonRequired] int SpacingMilliseconds,
    [property: JsonRequired] string StopRule,
    [property: JsonRequired] LivePlanBounds Bounds);

// Paths are deliberately absent from commitments and public output.
internal sealed record EconomicsWorkloadSelection(string ReplaySha256, string GrowthSha256,
    ImmutableArray<EconomicsScenario> Scenarios, int ChildSeconds, int SpacingMilliseconds, string StopRule);
internal sealed record EconomicsPlanSelection(string Format, LivePlanSource Source, string BuildSha256,
    EconomicsWorkloadSelection Workload, LivePlanProvider Provider, string Thinking, string TariffSha256,
    LivePlanBounds Bounds);
internal sealed record EconomicsSlot(int Index, string CaseId, string Profile, int Phase, int Chain,
    int? Previous, bool ExpectedCapacity, int? ResetChain);
internal sealed record EconomicsAllocation(long Attempts, long Calls, long InputTokens, long OutputTokens,
    long CombinedTokens, long SpendMicroUsd, long Milliseconds);
internal sealed record EconomicsLease(string Id, int Index, EconomicsAllocation Allocation);

internal enum EconomicsFault
{
    None, WrongSource, WrongBuild, WrongPredecessor, BeforeReadyCrash, AfterPrepareCrash,
    PartialReply, OversizedReply, WrongReply, RateLimit, ProviderFailure, UsageViolation,
    CancelAfterUsage, CancelAfterPrepare, RejectAccept, CorruptState, CleanupFailure,
    StartFailure, Hang, ThreeCalls, EightCalls,
}

// These types are private pipe frames. Never serialize them into a public report.
internal sealed record EconomicsChildInput(string Operation, string Root, EconomicsPlanInput Plan,
    string PlanSha256, string WorkloadSha256, string Campaign, EconomicsSlot Slot, EconomicsLease Lease,
    string Session, byte[] StateKey, AcceptedLineage? Predecessor, string Transport, EconomicsFault Fault);
internal sealed record EconomicsChildReady(string Operation, string PlanSha256, int Index, string LeaseId,
    string SourceCommit, string SourceTree, bool SourceClean, string BuildSha256,
    int ProcessId, string Startup, string Code, bool Restored);
internal sealed record EconomicsSecretFrame(string Operation, string LeaseId, string? Credential);
internal sealed record EconomicsCall(int Ordinal, bool Dispatched, string TransportOutcome, string ChatOutcome,
    string UsageStatus, UsageJournalUsage? Usage);
internal sealed record EconomicsReceipt(string Operation, string PlanSha256, string WorkloadSha256,
    int Index, string LeaseId, int ProcessId, string Startup, string Transport, string Session,
    string? PredecessorSha256, string Code, string? Stage, string? Diagnostic, bool Restored,
    string AgentStatus, EvaluationOutcome? Evaluation, PreparedStateReceipt? Prepared,
    ImmutableArray<EconomicsCall> Calls, LiveAccountingSnapshot Accounting, int ToolCalls,
    string? InitialPrefixSha256, string? CompletedSessionSha256, GrowthChatCounts Measurement);

internal sealed record EconomicsStep(int Index, string CaseId, string Code, string ReceiptCoverage,
    bool Allocated, string? AttemptSha256, string? EvaluationStatus, bool Restored,
    bool Prepared, bool Accepted, bool Readback, bool Reset, int? ProcessId, string? Startup,
    long StartedMilliseconds, long FinishedMilliseconds, long? IntervalMilliseconds,
    string? SessionSha256, string? PredecessorSha256, int? ToolCalls);
internal sealed record EconomicsReport(string Format, string ExecutionKind, EconomicsPlanSelection Plan,
    string PlanSha256, string WorkloadSha256, string Campaign, string StopReason, string Cleanup,
    EconomicsAllocation Allocations, int Scheduled, int Attempted, int ReceiptMissing,
    bool UsageComplete, bool MonetaryComplete, string C1Handoff, ImmutableArray<EconomicsStep> Steps,
    ImmutableArray<EvaluationOutcome> Outcomes, UsageJournalDocument? Journal, PricingReportDocument? Pricing);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
    RespectRequiredConstructorParameters = true, UseStringEnumConverter = true, MaxDepth = 32)]
[JsonSerializable(typeof(EconomicsPlanInput))]
[JsonSerializable(typeof(EconomicsWorkloadSelection))]
[JsonSerializable(typeof(EconomicsPlanSelection))]
[JsonSerializable(typeof(EconomicsChildInput))]
[JsonSerializable(typeof(EconomicsChildReady))]
[JsonSerializable(typeof(EconomicsSecretFrame))]
[JsonSerializable(typeof(EconomicsReceipt))]
[JsonSerializable(typeof(EconomicsReport))]
internal sealed partial class EconomicsLiveJson : JsonSerializerContext;

internal sealed class EconomicsRejected(string code) : Exception(code)
{
    internal string Code { get; } = code;
}
