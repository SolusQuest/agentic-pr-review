using System.Text.Json.Nodes;
using System.Collections.Immutable;
using AgenticPrReview.Runtime.Ledger;
using AgenticPrReview.Runtime.Prefix;

namespace AgenticPrReview.Runtime;

/// <summary>
/// Provider-neutral observation seam. The executor receives only a materialized
/// provider request plan; it never receives ReviewInput or persisted ledger state.
/// </summary>
internal interface ILiveProviderExecutor
{
    Task<ProviderExecutionObservation> ExecuteAsync(
        ProviderRequestPlan plan,
        ExpectedIdentities identities,
        CancellationToken cancellationToken = default);
}

internal interface ILiveProviderExecutorFactory
{
    ILiveProviderExecutor Create(string providerMode);
}

internal sealed class DefaultLiveProviderExecutorFactory : ILiveProviderExecutorFactory
{
    public ILiveProviderExecutor Create(string providerMode) => providerMode switch
    {
        "synthetic" => new SyntheticLiveProviderExecutor(),
        "live" => DeepSeekLiveProviderExecutor.FromEnvironment(),
        _ => throw new ProviderFailureException("APR_PROVIDER_CONFIG", 20),
    };
}

internal sealed record ProviderRequestMessage(string Role, string Text);

internal sealed record ProviderRequestPlan(
    ImmutableArray<ProviderRequestMessage> Messages,
    int MaxFindings,
    string LogicalPrefixSha256,
    string PrefixSha256,
    string AdapterId,
    string? RequestContractSha256)
{
    public static ProviderRequestPlan From(
        PrefixMaterialization materialization,
        ExpectedIdentities identities,
        int maxFindings,
        string? requestContractSha256)
    {
        var stable = ProviderRequestPlanDecoder.Decode(materialization.StableProviderStream);
        var dynamic = ProviderRequestPlanDecoder.Decode(materialization.DynamicProviderStream);
        if (stable.Length < 3 || dynamic.Length != 1 ||
            stable.Take(3).Any(message => message.Role != "system") ||
            dynamic[0].Role != "user")
        {
            throw new ProviderFailureException("APR_PROVIDER_CONFIG", 20);
        }

        return new ProviderRequestPlan(
            stable.AddRange(dynamic),
            Math.Clamp(maxFindings, 1, 50),
            materialization.LogicalPrefixSha256,
            materialization.PrefixSha256,
            identities.AdapterId,
            requestContractSha256);
    }
}

internal static class ProviderRequestPlanDecoder
{
    public static ImmutableArray<ProviderRequestMessage> Decode(ImmutableArray<byte> stream)
    {
        var result = ImmutableArray.CreateBuilder<ProviderRequestMessage>();
        var offset = 0;
        while (offset < stream.Length)
        {
            if (stream.Length - offset < 4)
                throw new ProviderFailureException("APR_PROVIDER_CONFIG", 20);
            var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(stream.AsSpan(offset, 4));
            offset += 4;
            if (length > stream.Length - offset)
                throw new ProviderFailureException("APR_PROVIDER_CONFIG", 20);
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(
                    stream.AsSpan(offset, checked((int)length)).ToArray());
                var root = document.RootElement;
                var role = root.GetProperty("role").GetString();
                var content = root.GetProperty("content");
                if (role is not ("system" or "user" or "assistant") ||
                    content.ValueKind != System.Text.Json.JsonValueKind.Array ||
                    content.GetArrayLength() != 1)
                    throw new ProviderFailureException("APR_PROVIDER_CONFIG", 20);
                var block = content[0];
                if (block.GetProperty("type").GetString() != "text" ||
                    block.GetProperty("text").ValueKind != System.Text.Json.JsonValueKind.String)
                    throw new ProviderFailureException("APR_PROVIDER_CONFIG", 20);
                result.Add(new ProviderRequestMessage(role, block.GetProperty("text").GetString()!));
            }
            catch (ProviderFailureException)
            {
                throw;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
            {
                throw new ProviderFailureException("APR_PROVIDER_CONFIG", 20);
            }
            offset += checked((int)length);
        }
        return result.ToImmutable();
    }
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
    ImmutableArray<LedgerFinding> Findings,
    string[] Limitations,
    string Mode);

internal sealed class SyntheticLiveProviderExecutor : ILiveProviderExecutor
{
    public Task<ProviderExecutionObservation> ExecuteAsync(
        ProviderRequestPlan plan,
        ExpectedIdentities identities,
        CancellationToken cancellationToken = default) => Task.FromResult(new ProviderExecutionObservation(
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
            ImmutableArray<LedgerFinding>.Empty,
            ["No live provider was invoked."],
            "live-provider"));
}
