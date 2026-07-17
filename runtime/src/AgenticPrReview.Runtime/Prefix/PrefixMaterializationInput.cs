using System.Collections.Immutable;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Prefix;

public sealed record PrefixMaterializationInput(
    MaterializationHistory History,
    ValidatedContextSource CurrentContext,
    InteractionIdentity Interaction,
    ExpectedIdentities ExpectedIdentities,
    string SessionEpoch,
    RawCacheContractEnvelopes Envelopes);

/// <summary>Deeply immutable materialization result (issue #50, D8).</summary>
public sealed class PrefixMaterialization
{
    internal PrefixMaterialization(
        ImmutableArray<byte> stableLogicalStream,
        ImmutableArray<byte> stableProviderStream,
        ImmutableArray<byte> dynamicLogicalStream,
        ImmutableArray<byte> dynamicProviderStream,
        int stableSegmentCount,
        string logicalPrefixSha256,
        string prefixSha256,
        string templateId,
        string policyId,
        string toolDefinitionId,
        string cacheConfigId,
        string adapterId)
    {
        StableLogicalStream = stableLogicalStream;
        StableProviderStream = stableProviderStream;
        DynamicLogicalStream = dynamicLogicalStream;
        DynamicProviderStream = dynamicProviderStream;
        StableSegmentCount = stableSegmentCount;
        StableLogicalStreamBytes = stableLogicalStream.Length;
        StableProviderStreamBytes = stableProviderStream.Length;
        LogicalPrefixSha256 = logicalPrefixSha256;
        PrefixSha256 = prefixSha256;
        TemplateId = templateId;
        PolicyId = policyId;
        ToolDefinitionId = toolDefinitionId;
        CacheConfigId = cacheConfigId;
        AdapterId = adapterId;
    }

    public ImmutableArray<byte> StableLogicalStream { get; }

    public ImmutableArray<byte> StableProviderStream { get; }

    public ImmutableArray<byte> DynamicLogicalStream { get; }

    public ImmutableArray<byte> DynamicProviderStream { get; }

    public int StableSegmentCount { get; }

    public long StableLogicalStreamBytes { get; }

    public long StableProviderStreamBytes { get; }

    public string LogicalPrefixSha256 { get; }

    public string PrefixSha256 { get; }

    public string TemplateId { get; }

    public string PolicyId { get; }

    public string ToolDefinitionId { get; }

    public string CacheConfigId { get; }

    public string AdapterId { get; }
}

public sealed record PrefixMaterializationOutcome(
    PrefixMaterialization? Value,
    ImmutableArray<PrefixDiagnostic> Diagnostics);
