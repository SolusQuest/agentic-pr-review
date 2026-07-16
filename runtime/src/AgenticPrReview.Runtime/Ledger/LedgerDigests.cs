using System.Text;

namespace AgenticPrReview.Runtime.Ledger;

/// <summary>
/// Ledger-computed digests. subjectDigest is host-supplied pass-through
/// per the M4 Batch #1 shared contract; only cacheContractDigest is derived
/// inside the runtime.
/// </summary>
internal static class LedgerDigests
{
    public const string CacheContractDomainTag = "agentic-pr-review/ledger-cache-contract/v1";

    public static string ComputeCacheContractDigest(ExpectedIdentities identities)
    {
        var envelope = LedgerCanonicalizer.SerializeCacheContractIdentity(identities).ToArray();
        return ComputeTaggedDigest(CacheContractDomainTag, envelope);
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
        return ComputeTaggedDigest(CacheContractDomainTag, envelope);
    }

    private static string ComputeTaggedDigest(string tag, byte[] envelope)
    {
        var tagBytes = Encoding.UTF8.GetBytes(tag);
        var preimage = new byte[tagBytes.Length + 1 + envelope.Length];
        Buffer.BlockCopy(tagBytes, 0, preimage, 0, tagBytes.Length);
        preimage[tagBytes.Length] = 0x00;
        Buffer.BlockCopy(envelope, 0, preimage, tagBytes.Length + 1, envelope.Length);
        return LedgerCanonicalizer.ComputeSha256Hex(preimage);
    }
}
