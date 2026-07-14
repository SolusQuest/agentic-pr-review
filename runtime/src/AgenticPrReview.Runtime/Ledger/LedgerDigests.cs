using System.Security.Cryptography;
using System.Text;

namespace AgenticPrReview.Runtime.Ledger;

/// <summary>
/// Provider-neutral digests bound into every review_context record.
/// </summary>
public static class LedgerDigests
{
    public const string SubjectDomainTag = "agentic-pr-review/ledger-subject/v1";
    public const string CacheContractDomainTag = "agentic-pr-review/ledger-cache-contract/v1";

    public static string ComputeSubjectDigest(
        string repository,
        string headRepository,
        int pullRequest,
        string reviewedHeadSha,
        string reviewedBaseSha)
    {
        var envelope = new Dictionary<string, object>
        {
            ["headRepository"] = headRepository,
            ["pullRequest"] = pullRequest,
            ["repository"] = repository,
            ["reviewedBaseSha"] = reviewedBaseSha,
            ["reviewedHeadSha"] = reviewedHeadSha,
        };
        return ComputeDigest(SubjectDomainTag, envelope);
    }

    public static string ComputeCacheContractDigest(ExpectedIdentities identities)
    {
        var envelope = new Dictionary<string, object>
        {
            ["adapterId"] = identities.AdapterId,
            ["cacheConfigId"] = identities.CacheConfigId,
            ["modelId"] = identities.ModelId,
            ["policyId"] = identities.PolicyId,
            ["providerId"] = identities.ProviderId,
            ["templateId"] = identities.TemplateId,
            ["toolDefinitionId"] = identities.ToolDefinitionId,
        };
        return ComputeDigest(CacheContractDomainTag, envelope);
    }

    public static string ComputeCacheContractDigestFromHeader(LedgerHeader header)
    {
        var envelope = new Dictionary<string, object>
        {
            ["adapterId"] = header.AdapterId,
            ["cacheConfigId"] = header.CacheConfigId,
            ["modelId"] = header.ModelId,
            ["policyId"] = header.PolicyId,
            ["providerId"] = header.ProviderId,
            ["templateId"] = header.TemplateId,
            ["toolDefinitionId"] = header.ToolDefinitionId,
        };
        return ComputeDigest(CacheContractDomainTag, envelope);
    }

    private static string ComputeDigest(string tag, Dictionary<string, object> envelope)
    {
        var envelopeBytes = LedgerCanonicalizer.SerializeEnvelope(envelope);
        var tagBytes = Encoding.UTF8.GetBytes(tag);
        var preimage = new byte[tagBytes.Length + 1 + envelopeBytes.Length];
        Buffer.BlockCopy(tagBytes, 0, preimage, 0, tagBytes.Length);
        preimage[tagBytes.Length] = 0x00;
        Buffer.BlockCopy(envelopeBytes, 0, preimage, tagBytes.Length + 1, envelopeBytes.Length);
        return LedgerCanonicalizer.ComputeSha256Hex(preimage);
    }
}
