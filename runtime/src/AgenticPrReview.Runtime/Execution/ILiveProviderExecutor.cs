using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime;

/// <summary>
/// Provider-neutral observation seam. #55 wires only the deterministic synthetic
/// implementation; #52 can supply a real adapter without changing orchestration,
/// metadata, or publication contracts.
/// </summary>
internal interface ILiveProviderExecutor
{
    ProviderExecutionObservation Execute(ReviewInput input, string inputHash, ExpectedIdentities identities);
}

internal sealed record ProviderExecutionObservation(
    string SelectedProviderId,
    string ObservedProviderId,
    string ResolvedModelId,
    string AdapterId,
    JsonObject Capability,
    string CacheStatus,
    JsonObject NormalizedUsage,
    JsonObject RetryObservations,
    string[] ErrorCodes,
    JsonObject TelemetryCompleteness,
    string Summary,
    string[] Limitations,
    string Mode);

internal sealed class SyntheticLiveProviderExecutor : ILiveProviderExecutor
{
    public ProviderExecutionObservation Execute(ReviewInput input, string inputHash, ExpectedIdentities identities) =>
        new(
            identities.ProviderId,
            identities.ProviderId,
            identities.ModelId,
            identities.AdapterId,
            new JsonObject
            {
                ["mode"] = "standard",
                ["aggregate"] = "unknown",
                ["statelessProof"] = null
            },
            "unknown",
            new JsonObject
            {
                ["attempts"] = new JsonArray(),
                ["requests"] = new JsonArray(),
                ["aggregate"] = new JsonObject
                {
                    ["totalInputTokens"] = null,
                    ["uncachedInputTokens"] = null,
                    ["cacheWriteInputTokens"] = null,
                    ["cacheReadInputTokens"] = null,
                    ["outputTokens"] = null,
                    ["requestCount"] = 0,
                    ["attemptCount"] = 0
                }
            },
            new JsonObject
            {
                ["requests"] = new JsonArray(),
                ["aggregate"] = new JsonObject
                {
                    ["requestCount"] = 0,
                    ["attemptCount"] = 0,
                    ["succeededCount"] = 0,
                    ["failedCount"] = 0,
                    ["cancelledCount"] = 0
                }
            },
            [],
            new JsonObject
            {
                ["usage"] = "missing",
                ["cache"] = "missing",
                ["statelessProof"] = "notApplicable",
                ["aggregate"] = "missing"
            },
            "Synthetic live runtime completed without findings.",
            ["No live provider was invoked."],
            "live-provider");
}
