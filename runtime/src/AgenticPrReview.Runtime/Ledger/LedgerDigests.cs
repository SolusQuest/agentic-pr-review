using System.Text;

namespace AgenticPrReview.Runtime.Ledger;

/// <summary>
/// Ledger-computed digests. subjectDigest is host-supplied pass-through
/// per the M4 Batch #1 shared contract; only cacheContractDigest is derived
/// inside the runtime.
///
/// Issue #49 section 6 freezes cacheContractDigest as:
///   Sha256Hex(LedgerCanonicalizer.SerializeCacheContractIdentity(identities))
/// i.e. SHA-256 directly over the canonical bytes of the cache-contract
/// identity envelope. No domain tag, no NUL separator.
/// </summary>
internal static class LedgerDigests
{
    public static string ComputeCacheContractDigest(ExpectedIdentities identities)
    {
        var envelope = LedgerCanonicalizer.SerializeCacheContractIdentity(identities).ToArray();
        return LedgerCanonicalizer.ComputeSha256Hex(envelope);
    }

    public static string ComputeCacheContractDigestFromHeader(LedgerHeader header)
    {
        var envelope = LedgerCanonicalizer.SerializeCacheContractIdentity(new ExpectedIdentities(
            Repository: header.Repository,
            HeadRepository: header.HeadRepository,
            PullRequest: header.PullRequest,
            WorkflowIdentity: header.WorkflowIdentity,
            TrustedExecutionDomain: header.TrustedExecutionDomain,
            ProviderId: header.ProviderId,
            ModelId: header.ModelId,
            AdapterId: header.AdapterId,
            TemplateId: header.TemplateId,
            PolicyId: header.PolicyId,
            ToolDefinitionId: header.ToolDefinitionId,
            CacheConfigId: header.CacheConfigId)).ToArray();
        return LedgerCanonicalizer.ComputeSha256Hex(envelope);
    }
}
