using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests.Ledger;

public sealed class LedgerCanonicalizerTests
{
    [Fact]
    public void MinimalBootstrapLedgerProducesDeterministicSortedKeys()
    {
        var model = new LedgerModel
        {
            SchemaVersion = 1,
            PrefixContractVersion = 1,
            Header = new LedgerHeader
            {
                Kind = "bootstrap",
                SessionEpoch = "aaaaaaaaaaaaaaaaaaaaaa",
                LedgerEpoch = "bbbbbbbbbbbbbbbbbbbbbb",
                StateGeneration = 0,
                PredecessorLedgerSha256 = "bootstrap",
                Repository = "owner/repo",
                HeadRepository = "owner/repo",
                PullRequest = 1,
                WorkflowIdentity = "ci",
                TrustedExecutionDomain = "trusted",
                ProviderId = "provider",
                ModelId = "model-2024-01-01",
                AdapterId = "adapter",
                TemplateId = "template",
                PolicyId = "policy",
                ToolDefinitionId = "tools",
                CacheConfigId = "cacheconfig"
            },
            Records = ImmutableArray.Create<LedgerRecord>(
                new ReviewContextRecord
                {
                    Role = "review_context",
                    InteractionId = "0000000000000000000000000000000000000000000000000000000000000000",
                    InteractionOrdinal = 0,
                    SubjectDigest = "1111111111111111111111111111111111111111111111111111111111111111",
                    CacheContractDigest = "2222222222222222222222222222222222222222222222222222222222222222",
                    ReviewedHeadSha = "0000000000000000000000000000000000000000",
                    ReviewedBaseSha = "1111111111111111111111111111111111111111",
                    ChangedFiles = ImmutableArray<LedgerChangedFile>.Empty
                },
                new ReviewOutcomeRecord
                {
                    Role = "review_outcome",
                    InteractionId = "0000000000000000000000000000000000000000000000000000000000000000",
                    InteractionOrdinal = 0,
                    Summary = "Summary text.",
                    Findings = ImmutableArray<LedgerFinding>.Empty,
                    Limitations = ImmutableArray<string>.Empty
                })
        };

        var bytes = LedgerCanonicalizer.SerializeCanonical(model);
        var json = Encoding.UTF8.GetString(bytes.AsSpan());

        Assert.Contains("\"adapterId\":\"adapter\",\"cacheConfigId\":\"cacheconfig\",\"headRepository\":\"owner/repo\",\"kind\":\"bootstrap\",\"ledgerEpoch\":\"bbbbbbbbbbbbbbbbbbbbbb\",\"modelId\":\"model-2024-01-01\",\"policyId\":\"policy\",\"predecessorLedgerSha256\":\"bootstrap\",\"providerId\":\"provider\",\"pullRequest\":1,\"repository\":\"owner/repo\",\"sessionEpoch\":\"aaaaaaaaaaaaaaaaaaaaaa\",\"stateGeneration\":0,\"templateId\":\"template\",\"toolDefinitionId\":\"tools\",\"trustedExecutionDomain\":\"trusted\",\"workflowIdentity\":\"ci\"", json);
        Assert.Contains("\"schemaVersion\":1", json);
    }

    [Fact]
    public void CacheContractIdentityDigestIsDeterministic()
    {
        var identities = new ExpectedIdentities(
            Repository: "owner/repo",
            HeadRepository: "owner/repo",
            PullRequest: 42,
            WorkflowIdentity: "ci",
            TrustedExecutionDomain: "trusted",
            ProviderId: "provider",
            ModelId: "model",
            AdapterId: "adapter",
            TemplateId: "template",
            PolicyId: "policy",
            ToolDefinitionId: "tools",
            CacheConfigId: "cacheconfig");

        var bytes = LedgerCanonicalizer.SerializeCacheContractIdentity(identities);
        var json = Encoding.UTF8.GetString(bytes.AsSpan());

        Assert.Equal("""{"adapterId":"adapter","cacheConfigId":"cacheconfig","modelId":"model","policyId":"policy","providerId":"provider","templateId":"template","toolDefinitionId":"tools"}""", json);
    }
}
