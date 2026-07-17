using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests.Ledger;

/// <summary>
/// Identity baseline shared by the ledger tests, the committed protocol fixtures
/// (protocol/fixtures/v1/provider-session-ledger/), and the LedgerFixtureGen tool.
/// The five cache-contract IDs live in the schema's sha256Hex domain: each is the
/// lowercase SHA-256 hex of its former placeholder name (AdapterId == sha256("adapter"),
/// TemplateId == sha256("template"), PolicyId == sha256("policy"),
/// ToolDefinitionId == sha256("tools"), CacheConfigId == sha256("cacheconfig")).
/// The digests are committed oracles: CacheContractDigest is the SHA-256 of the RFC 8785
/// canonical identity object over these identities; ModelAliasCacheContractDigest is the
/// same with modelId "latest". The restore-fixture test cross-checks both against the
/// committed fixtures.
/// </summary>
internal static class LedgerTestBaseline
{
    internal const string AdapterId = "ae1eae1d76e5b7c865c4122ce366a08025842566d2d96c75cc13e6353a73db0d";
    internal const string TemplateId = "5cde0f1298f41f7d1c8b907a36992a7a513225a2615bd6e307bf1a9149b06b40";
    internal const string PolicyId = "823412d1eacb67956220e532959f0104603057c88704863ca38e7cd188fda812";
    internal const string ToolDefinitionId = "f9d35d43770d39092a663e665e82ae1d84a9e0da3d0d10c407acada6a40cd281";
    internal const string CacheConfigId = "3786c8b5fa08b53ee6c91f9a14b4324ea9018b37e738638ee7bf778ea85ec8d6";

    internal const string ModelId = "model-2024-01-01";
    internal const string CacheContractDigest = "bb4b2c18e601bdf52bf25b087488b19c8fe49c16cf5ec5e9f9ce0018d7bc72c8";
    internal const string ModelAliasCacheContractDigest = "03be3438950133022e9352a4035fe778a95a6d9551451092388893b36cfbc3d1";

    internal static readonly ExpectedIdentities Identities = new(
        Repository: "owner/repo",
        HeadRepository: "owner/repo",
        PullRequest: 1,
        WorkflowIdentity: "ci",
        TrustedExecutionDomain: "trusted",
        ProviderId: "provider",
        ModelId: ModelId,
        AdapterId: AdapterId,
        TemplateId: TemplateId,
        PolicyId: PolicyId,
        ToolDefinitionId: ToolDefinitionId,
        CacheConfigId: CacheConfigId);
}
