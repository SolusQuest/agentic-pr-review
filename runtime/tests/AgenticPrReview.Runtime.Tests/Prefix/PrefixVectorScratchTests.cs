using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgenticPrReview.Runtime.Ledger;
using AgenticPrReview.Runtime.Prefix;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Prefix;

/// <summary>
/// Cross-language oracle verification: the C# production implementation must
/// reproduce every committed TS-oracle vector byte-exactly.
/// </summary>
public sealed class PrefixVectorScratchTests
{
    private static string FixtureRoot =>
        Path.Combine(AppContext.BaseDirectory, "protocol", "fixtures", "prefix-contract", "v1");

    private static JsonElement LoadVector(string relative)
    {
        var text = File.ReadAllText(Path.Combine(FixtureRoot, relative));
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public void DigestVectorsMatch()
    {
        foreach (var entry in ManifestEntries("digest-vector"))
        {
            var vector = LoadVector(entry);
            var envelope = vector.GetProperty("envelope");
            var tag = vector.GetProperty("tag").GetString()!;
            var expected = vector.GetProperty("expected");

            var outcome = tag switch
            {
                "agentic-pr-review/cache-contract/template/v1" => CacheContractDigests.ComputeTemplateId(envelope),
                "agentic-pr-review/cache-contract/policy/v1" => CacheContractDigests.ComputePolicyId(envelope),
                "agentic-pr-review/cache-contract/tools/v1" => CacheContractDigests.ComputeToolDefinitionId(envelope),
                "agentic-pr-review/cache-contract/config/v1" => CacheContractDigests.ComputeCacheConfigId(envelope),
                "agentic-pr-review/cache-contract/adapter/v1" => CacheContractDigests.ComputeAdapterId(envelope),
                _ => throw new InvalidOperationException(tag),
            };

            Assert.True(outcome.Digest is not null, $"{entry}: {string.Join(',', outcome.Diagnostics.Select(d => d.Code))}");
            Assert.Equal(expected.GetProperty("digestHex").GetString(), outcome.Digest);
        }
    }

    [Fact]
    public void InteractionVectorsMatch()
    {
        foreach (var entry in ManifestEntries("interaction-vector"))
        {
            var vector = LoadVector(entry);
            var predecessorElement = vector.GetProperty("predecessor");
            PredecessorLedgerReference predecessor =
                predecessorElement.TryGetProperty("bootstrap", out _)
                    ? PredecessorLedgerReference.Bootstrap.Instance
                    : new PredecessorLedgerReference.LedgerHash(predecessorElement.GetProperty("ledgerSha256").GetString()!);

            var outcome = InteractionIdDeriver.Derive(
                predecessor,
                vector.GetProperty("consumedInputSha256").GetString()!,
                vector.GetProperty("currentHeadSha").GetString()!,
                vector.GetProperty("interactionOrdinal").GetInt64());

            Assert.True(outcome.InteractionId is not null, $"{entry}: {string.Join(',', outcome.Diagnostics.Select(d => d.Code))}");
            Assert.Equal(vector.GetProperty("expected").GetProperty("interactionId").GetString(), outcome.InteractionId);
        }
    }

    [Fact]
    public void MaterializationVectorsMatch()
    {
        foreach (var entry in ManifestEntries("materialization-vector"))
        {
            var vector = LoadVector(entry);
            var input = BuildInput(vector.GetProperty("input"));
            var outcome = PrefixMaterializer.Materialize(input);

            Assert.True(
                outcome.Value is not null,
                $"{entry}: {string.Join(',', outcome.Diagnostics.Select(d => d.Code + ':' + d.Message))}");

            var expected = vector.GetProperty("expected");
            var value = outcome.Value;
            Assert.Equal(expected.GetProperty("logicalStreamHex").GetString(), Convert.ToHexString(value.StableLogicalStream.AsSpan()).ToLowerInvariant());
            Assert.Equal(expected.GetProperty("providerStreamHex").GetString(), Convert.ToHexString(value.StableProviderStream.AsSpan()).ToLowerInvariant());
            Assert.Equal(expected.GetProperty("logicalPrefixSha256").GetString(), value.LogicalPrefixSha256);
            Assert.Equal(expected.GetProperty("prefixSha256").GetString(), value.PrefixSha256);

            var digests = expected.GetProperty("digests");
            Assert.Equal(digests.GetProperty("templateId").GetString(), value.TemplateId);
            Assert.Equal(digests.GetProperty("policyId").GetString(), value.PolicyId);
            Assert.Equal(digests.GetProperty("toolDefinitionId").GetString(), value.ToolDefinitionId);
            Assert.Equal(digests.GetProperty("cacheConfigId").GetString(), value.CacheConfigId);
            Assert.Equal(digests.GetProperty("adapterId").GetString(), value.AdapterId);

            var boundary = expected.GetProperty("stableBoundary");
            Assert.Equal(boundary.GetProperty("segmentCount").GetInt32(), value.StableSegmentCount);
            Assert.Equal(boundary.GetProperty("logicalStreamBytes").GetInt64(), value.StableLogicalStreamBytes);
            Assert.Equal(boundary.GetProperty("providerStreamBytes").GetInt64(), value.StableProviderStreamBytes);

            var dynamicSuffix = expected.GetProperty("dynamicSuffix");
            Assert.Equal(dynamicSuffix.GetProperty("logicalHex").GetString(), Convert.ToHexString(value.DynamicLogicalStream.AsSpan()).ToLowerInvariant());
            Assert.Equal(dynamicSuffix.GetProperty("providerHex").GetString(), Convert.ToHexString(value.DynamicProviderStream.AsSpan()).ToLowerInvariant());
        }
    }

    private static System.Collections.Generic.IEnumerable<string> ManifestEntries(string kind)
    {
        var manifest = LoadVector("manifest.json");
        foreach (var entry in manifest.GetProperty("vectors").EnumerateArray())
        {
            if (entry.GetProperty("kind").GetString() == kind)
            {
                yield return entry.GetProperty("file").GetString()!;
            }
        }
    }

    private static PrefixMaterializationInput BuildInput(JsonElement input)
    {
        var historyElement = input.GetProperty("history");
        var historyKind = historyElement.GetProperty("kind").GetString()!;
        MaterializationHistory history = historyKind switch
        {
            "bootstrap" => MaterializationHistory.BootstrapHistory.Instance,
            "continuation" => new MaterializationHistory.ContinuationHistory(ParseLedger(historyElement.GetProperty("ledgerHex").GetString()!)),
            "reset" => new MaterializationHistory.ResetHistory(ParseLedger(historyElement.GetProperty("ledgerHex").GetString()!)),
            _ => throw new InvalidOperationException(historyKind),
        };

        var context = input.GetProperty("currentContext");
        var identitiesElement = input.GetProperty("expectedIdentities");
        var identities = new ExpectedIdentities(
            identitiesElement.GetProperty("repository").GetString()!,
            identitiesElement.GetProperty("headRepository").GetString()!,
            identitiesElement.GetProperty("pullRequest").GetInt32(),
            identitiesElement.GetProperty("workflowIdentity").GetString()!,
            identitiesElement.GetProperty("trustedExecutionDomain").GetString()!,
            identitiesElement.GetProperty("providerId").GetString()!,
            identitiesElement.GetProperty("modelId").GetString()!,
            identitiesElement.GetProperty("adapterId").GetString()!,
            identitiesElement.GetProperty("templateId").GetString()!,
            identitiesElement.GetProperty("policyId").GetString()!,
            identitiesElement.GetProperty("toolDefinitionId").GetString()!,
            identitiesElement.GetProperty("cacheConfigId").GetString()!);

        var interaction = input.GetProperty("interaction");
        var envelopes = input.GetProperty("envelopes");

        return new PrefixMaterializationInput(
            history,
            new ValidatedContextSource
            {
                SubjectDigest = context.GetProperty("subjectDigest").GetString()!,
                ReviewedHeadSha = context.GetProperty("reviewedHeadSha").GetString()!,
                ReviewedBaseSha = context.GetProperty("reviewedBaseSha").GetString()!,
                ChangedFiles = BuildChangedFiles(context.GetProperty("changedFiles")),
            },
            new InteractionIdentity(
                interaction.GetProperty("interactionId").GetString()!,
                interaction.GetProperty("interactionOrdinal").GetInt64()),
            identities,
            input.GetProperty("sessionEpoch").GetString()!,
            new RawCacheContractEnvelopes(
                envelopes.GetProperty("template"),
                envelopes.GetProperty("policy"),
                envelopes.GetProperty("tools"),
                envelopes.GetProperty("cacheConfig"),
                envelopes.GetProperty("adapter")));
    }

    private static ImmutableArray<LedgerChangedFile> BuildChangedFiles(JsonElement files)
    {
        var builder = ImmutableArray.CreateBuilder<LedgerChangedFile>();
        foreach (var file in files.EnumerateArray())
        {
            LedgerBoundedPatch? patch = null;
            if (file.TryGetProperty("patch", out var patchElement) && patchElement.ValueKind == JsonValueKind.Object)
            {
                patch = new LedgerBoundedPatch
                {
                    Sha256 = patchElement.GetProperty("sha256").GetString()!,
                    Truncated = patchElement.GetProperty("truncated").GetBoolean(),
                    MaxChars = patchElement.GetProperty("maxChars").GetInt64(),
                };
            }

            builder.Add(new LedgerChangedFile
            {
                Path = file.GetProperty("path").GetString()!,
                PreviousPath = file.TryGetProperty("previousPath", out var previous) && previous.ValueKind == JsonValueKind.String
                    ? previous.GetString()
                    : null,
                Status = file.GetProperty("status").GetString()!,
                Additions = file.GetProperty("additions").GetInt64(),
                Deletions = file.GetProperty("deletions").GetInt64(),
                Changes = file.GetProperty("changes").GetInt64(),
                Patch = patch,
            });
        }

        return builder.ToImmutable();
    }

    private static ValidatedLedger ParseLedger(string ledgerHex)
    {
        var bytes = Convert.FromHexString(ledgerHex);
        var outcome = LedgerParser.ParseAndValidate(bytes);
        Assert.True(outcome.Ledger is not null, string.Join(',', outcome.Diagnostics.Select(d => d.Code)));
        return outcome.Ledger!;
    }
}
