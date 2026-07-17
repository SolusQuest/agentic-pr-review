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

    [Fact]
    public void ChangedFileWithPatchAndPreviousPathUsesUtf16OrdinalKeyOrder()
    {
        // Byte-exact golden: unsigned UTF-16 ordinal sorts "patch" before "path"
        // (shared "pat" prefix, then 'c' U+0063 < 'h' U+0068), so the writer must emit
        // patch, then path, then previousPath.
        var record = new ReviewContextRecord
        {
            Role = "review_context",
            InteractionId = "interaction",
            InteractionOrdinal = 0,
            SubjectDigest = "subject",
            CacheContractDigest = "digest",
            ReviewedHeadSha = "head",
            ReviewedBaseSha = "base",
            ChangedFiles = ImmutableArray.Create(new LedgerChangedFile
            {
                Path = "src/new.cs",
                PreviousPath = "src/old.cs",
                Status = "renamed",
                Additions = 10,
                Deletions = 2,
                Changes = 12,
                Patch = new LedgerBoundedPatch { Sha256 = "patchhash", Truncated = false, MaxChars = 4000 }
            })
        };

        var bytes = LedgerCanonicalizer.SerializeRecord(record);
        var json = Encoding.UTF8.GetString(bytes.AsSpan());

        Assert.Equal(
            """{"cacheContractDigest":"digest","changedFiles":[{"additions":10,"changes":12,"deletions":2,"patch":{"maxChars":4000,"sha256":"patchhash","truncated":false},"path":"src/new.cs","previousPath":"src/old.cs","status":"renamed"}],"interactionId":"interaction","interactionOrdinal":0,"reviewedBaseSha":"base","reviewedHeadSha":"head","role":"review_context","subjectDigest":"subject"}""",
            json);
    }
}
