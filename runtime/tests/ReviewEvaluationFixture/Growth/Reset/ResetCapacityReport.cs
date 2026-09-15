using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Reset;

internal sealed record ResetCapacityObservation(int Phase, bool ExplicitReset, string ScriptSha256,
    string PolicySha256, string LimitsSha256, string ToolsetSha256, string StateDisposition,
    string? AgentCode, int ProviderAdmissions, int? ModelCalls, int PublicationWrites);

// A local lifecycle assertion report, not a Q1 scored review or hosted proof.
internal sealed record ResetCapacityReport(string Schema, string Code, string Topology,
    string SourceCommit, string SourceTree, bool SourceClean, string SeedCorpusSha256,
    int CapacityPhase, int MessageLimit, string ResetReason, bool EpochChanged,
    bool SessionChanged, bool PriorContentExcluded, bool FreshContinuationAccepted,
    ResetCapacityObservation[] Observations)
{
    internal static string Write(string seed, int capacityPhase, int messageLimit,
        ResetCapacityObservation[] observations) => JsonSerializer.Serialize(new ResetCapacityReport(
            "r5-reset-capacity-v1", "r5_reset_capacity_passed", "framework_production_host_synthetic_ports",
            EvaluationSource.Commit, EvaluationSource.Tree, EvaluationSource.Clean, seed,
            capacityPhase, messageLimit, "explicit_reset", true, true, true, true, observations),
            ResetCapacityJson.Default.ResetCapacityReport);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(ResetCapacityReport))]
internal partial class ResetCapacityJson : JsonSerializerContext;
