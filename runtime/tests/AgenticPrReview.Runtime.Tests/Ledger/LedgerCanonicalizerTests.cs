using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests.Ledger;

/// <summary>
/// Round-trip and shape tests for <see cref="LedgerCanonicalizer"/>. The
/// canonicalizer is <c>internal</c>; the ledger test assembly reaches it via
/// InternalsVisibleTo.
/// </summary>
public sealed class LedgerCanonicalizerTests
{
    [Fact]
    public void SerializeCanonical_Roundtrips_Through_Parser()
    {
        var model = BuildMinimalModel();
        var bytes = LedgerCanonicalizer.SerializeCanonical(model).ToArray();

        var outcome = LedgerParser.ParseAndValidate(bytes);
        Assert.NotNull(outcome.Ledger);
        Assert.Equal(bytes, outcome.Ledger!.ToCanonicalByteArray());
    }

    [Fact]
    public void SerializeRecord_ProducesStableCanonicalBytes()
    {
        var model = BuildMinimalModel();
        var recordBytes1 = LedgerCanonicalizer.SerializeRecord(model.Records[0]).ToArray();
        var recordBytes2 = LedgerCanonicalizer.SerializeRecord(model.Records[0]).ToArray();
        Assert.Equal(recordBytes1, recordBytes2);
    }

    [Fact]
    public void SerializeCacheContractIdentity_HasSortedPropertyOrder()
    {
        var identities = new ExpectedIdentities(
            Repository: "acme/example",
            HeadRepository: "acme/example",
            PullRequest: 1,
            WorkflowIdentity: "acme/example/.github/workflows/ci.yml",
            TrustedExecutionDomain: "github-actions",
            ProviderId: "provider.reference",
            ModelId: "model-2026-01",
            AdapterId: new string('a', 64),
            TemplateId: new string('b', 64),
            PolicyId: new string('c', 64),
            ToolDefinitionId: new string('d', 64),
            CacheConfigId: new string('e', 64));
        var envelope = Encoding.UTF8.GetString(LedgerCanonicalizer.SerializeCacheContractIdentity(identities).ToArray());
        // Property order must be adapterId, cacheConfigId, modelId, policyId, providerId, templateId, toolDefinitionId.
        var expectedOrder = new[] { "adapterId", "cacheConfigId", "modelId", "policyId", "providerId", "templateId", "toolDefinitionId" };
        var lastIndex = -1;
        foreach (var key in expectedOrder)
        {
            var idx = envelope.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            Assert.True(idx > lastIndex, $"Property '{key}' out of order in canonical envelope.");
            lastIndex = idx;
        }
    }

    private static LedgerModel BuildMinimalModel()
    {
        var identities = new ExpectedIdentities(
            Repository: "acme/example",
            HeadRepository: "acme/example",
            PullRequest: 1,
            WorkflowIdentity: "acme/example/.github/workflows/ci.yml",
            TrustedExecutionDomain: "github-actions",
            ProviderId: "provider.reference",
            ModelId: "model-2026-01",
            AdapterId: new string('a', 64),
            TemplateId: new string('b', 64),
            PolicyId: new string('c', 64),
            ToolDefinitionId: new string('d', 64),
            CacheConfigId: new string('e', 64));
        var digest = LedgerDigests.ComputeCacheContractDigest(identities);
        var subject = LedgerCanonicalizer.ComputeSha256Hex(Encoding.UTF8.GetBytes("subject-fixture"));
        var ctx = new ReviewContextRecord
        {
            Role = "review_context",
            InteractionId = new string('0', 63) + "1",
            InteractionOrdinal = 0,
            SubjectDigest = subject,
            CacheContractDigest = digest,
            ReviewedHeadSha = new string('1', 40),
            ReviewedBaseSha = new string('2', 40),
            ChangedFiles = ImmutableArray<LedgerChangedFile>.Empty,
        };
        var oc = new ReviewOutcomeRecord
        {
            Role = "review_outcome",
            InteractionId = ctx.InteractionId,
            InteractionOrdinal = 0,
            Summary = "Fixture summary.",
            Findings = ImmutableArray<LedgerFinding>.Empty,
            Limitations = ImmutableArray<string>.Empty,
        };
        return new LedgerModel
        {
            SchemaVersion = 1,
            PrefixContractVersion = 1,
            Header = new LedgerHeader
            {
                Kind = "bootstrap",
                SessionEpoch = "AAAAAAAAAAAAAAAAAAAAAA",
                LedgerEpoch = "BBBBBBBBBBBBBBBBBBBBBB",
                StateGeneration = 0,
                PredecessorLedgerSha256 = "bootstrap",
                Repository = identities.Repository,
                HeadRepository = identities.HeadRepository,
                PullRequest = identities.PullRequest,
                WorkflowIdentity = identities.WorkflowIdentity,
                TrustedExecutionDomain = identities.TrustedExecutionDomain,
                ProviderId = identities.ProviderId,
                ModelId = identities.ModelId,
                AdapterId = identities.AdapterId,
                TemplateId = identities.TemplateId,
                PolicyId = identities.PolicyId,
                ToolDefinitionId = identities.ToolDefinitionId,
                CacheConfigId = identities.CacheConfigId,
            },
            Records = ImmutableArray.Create<LedgerRecord>(ctx, oc),
        };
    }
}
