using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tools.LedgerFixtureGen;

/// <summary>
/// Identity baseline shared by every ProviderSessionLedgerV1 fixture, by the runtime
/// test suite, and by future generator waves. The five cache-contract IDs live in the
/// schema's sha256Hex domain: each is the lowercase SHA-256 hex of its former
/// placeholder name, so the provenance stays reproducible
/// (AdapterId == sha256("adapter"), TemplateId == sha256("template"),
/// PolicyId == sha256("policy"), ToolDefinitionId == sha256("tools"),
/// CacheConfigId == sha256("cacheconfig")).
/// </summary>
internal static class LedgerFixtureBaseline
{
    internal const string AdapterId = "ae1eae1d76e5b7c865c4122ce366a08025842566d2d96c75cc13e6353a73db0d";
    internal const string TemplateId = "5cde0f1298f41f7d1c8b907a36992a7a513225a2615bd6e307bf1a9149b06b40";
    internal const string PolicyId = "823412d1eacb67956220e532959f0104603057c88704863ca38e7cd188fda812";
    internal const string ToolDefinitionId = "f9d35d43770d39092a663e665e82ae1d84a9e0da3d0d10c407acada6a40cd281";
    internal const string CacheConfigId = "3786c8b5fa08b53ee6c91f9a14b4324ea9018b37e738638ee7bf778ea85ec8d6";

    internal const string Repository = "owner/repo";
    internal const string ModelId = "model-2024-01-01";
    internal const string ModelAliasLiteral = "latest";

    internal const string SessionEpoch = "aaaaaaaaaaaaaaaaaaaaaa";
    internal const string LedgerEpoch = "bbbbbbbbbbbbbbbbbbbbbb";
    internal const string ResetLedgerEpoch = "dddddddddddddddddddddd";
    internal const string RecoverySessionEpoch = "eeeeeeeeeeeeeeeeeeeeee";
    internal const string RecoveryLedgerEpoch = "ffffffffffffffffffffff";

    internal const string InteractionId = "0000000000000000000000000000000000000000000000000000000000000000";
    internal const string SubjectDigest = "1111111111111111111111111111111111111111111111111111111111111111";
    internal const string ReviewedHeadSha = "0000000000000000000000000000000000000000";
    internal const string ReviewedBaseSha = "1111111111111111111111111111111111111111";
    internal const string PredecessorManifestSha256 = "0000000000000000000000000000000000000000000000000000000000000000";
    internal const string Summary = "Summary text.";

    internal static readonly ExpectedIdentities Identities = new(
        Repository, Repository, PullRequest: 1,
        WorkflowIdentity: "ci", TrustedExecutionDomain: "trusted",
        ProviderId: "provider", ModelId: ModelId,
        AdapterId, TemplateId, PolicyId, ToolDefinitionId, CacheConfigId);

    internal static readonly ExpectedIdentities ModelAliasIdentities =
        Identities with { ModelId = ModelAliasLiteral };
}
