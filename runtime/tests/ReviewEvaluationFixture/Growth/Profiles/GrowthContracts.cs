using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Reporting;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles;

internal sealed record GrowthState(long Generation, int CompletedRuns, int Records, long ContinuationBytes,
    int PlaintextBytes, int EnvelopeBytes, long ScopeBytes, int MetadataBytes, int AcceptedCandidates,
    string SessionSha256, string EnvelopeSha256, string? PredecessorEnvelopeSha256, string LogicalSha256);

// Scope samples are taken after acceptance, when there is no staging candidate.
// Before is an independently restored last accepted sample; State is null on a rejected attempt.
internal sealed record GrowthRow(int Attempt, string CaseId, string AttemptSha256, string Transition,
    string Stage, string Code, string Classification, bool Accepted, GrowthState? Before, GrowthState? State,
    bool PredecessorPreserved, int? ModelCalls, int? ToolCalls, int? ProviderRequests, long? ProviderRequestBytes,
    int? LastProviderRequestBytes, GrowthChatCounts? Project);

internal sealed record GrowthProfileReport(string Profile, int AttemptLimit, string TerminalStage, string TerminalCode,
    bool LimitObserved, ImmutableArray<GrowthRow> Rows, EvaluationReportDocument Evaluation);

internal sealed record GrowthReport(string Code, string Cleanup, string? CorpusSha256, string? SeedCorpusSha256, GrowthSchedule Schedule, string SourceCommit,
    string SourceTree, bool SourceClean, ImmutableArray<GrowthProfileReport> Profiles, string? NormalizedSha256)
{
    [JsonIgnore] internal int ExitCode => Code == "verified" && Cleanup == "cleaned" ? 0 : Code == "input_invalid" ? 2 : 1;
}

[JsonSerializable(typeof(GrowthReport))]
[JsonSerializable(typeof(ImmutableArray<GrowthRow>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true, MaxDepth = 24)]
internal sealed partial class GrowthJsonContext : JsonSerializerContext;
