using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using AgenticPrReview.Runtime.Canonical;
using AgenticPrReview.Runtime.Prefix;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Prefix;

/// <summary>
/// Golden-vector conformance: the C# production implementation reproduces
/// every committed TS-oracle vector (issue #50, D12).
/// </summary>
public sealed class PrefixGoldenVectorTests
{
    [Fact]
    public void ManifestIsWellFormed()
    {
        var entries = PrefixFixtureLoader.LoadManifest();
        Assert.NotEmpty(entries);
    }

    [Fact]
    public void FramingVectorsMatch()
    {
        foreach (var entry in PrefixFixtureLoader.OfKind("framing-vector"))
        {
            var vector = PrefixFixtureLoader.LoadVector(entry.File);
            var input = vector.GetProperty("input");
            var expected = vector.GetProperty("expected");

            if (input.TryGetProperty("tag", out var tagElement))
            {
                // Domain-tag preimage: ASCII tag + exactly one NUL octet.
                var preimage = Convert.FromHexString(expected.GetProperty("preimageHex").GetString()!);
                var tag = tagElement.GetString()!;
                Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(tag) + 1, preimage.Length);
                Assert.Equal(0, preimage[^1]);
                Assert.DoesNotContain('\\', tag);
                Assert.Equal(tag, System.Text.Encoding.UTF8.GetString(preimage.AsSpan(0, preimage.Length - 1)));
            }
            else if (input.TryGetProperty("value", out var valueElement))
            {
                var writer = new ArrayBufferWriter<byte>();
                PrefixHashPrimitives.WriteIdentity(writer, valueElement.GetString()!);
                Assert.Equal(
                    expected.GetProperty("framedHex").GetString(),
                    Convert.ToHexString(writer.WrittenSpan).ToLowerInvariant());
            }
            else if (input.TryGetProperty("payloadHex", out var payloadElement))
            {
                var framed = PrefixHashPrimitives.FrameSegment(Convert.FromHexString(payloadElement.GetString()!));
                Assert.Equal(
                    expected.GetProperty("framedHex").GetString(),
                    Convert.ToHexString(framed.AsSpan()).ToLowerInvariant());
            }
            else
            {
                var hash = PrefixMaterializer.ComputeLogicalPrefixSha256(
                    ReadOnlySpan<byte>.Empty,
                    input.GetProperty("ledgerSchemaVersion").GetInt64(),
                    input.GetProperty("prefixContractVersion").GetInt64());
                Assert.Equal(expected.GetProperty("logicalPrefixSha256").GetString(), hash);
            }
        }
    }

    [Fact]
    public void DigestVectorsMatch()
    {
        foreach (var entry in PrefixFixtureLoader.OfKind("digest-vector"))
        {
            var vector = PrefixFixtureLoader.LoadVector(entry.File);
            var envelope = vector.GetProperty("envelope");
            var tag = vector.GetProperty("tag").GetString()!;
            var expected = vector.GetProperty("expected");

            var outcome = DigestFor(tag, envelope);
            Assert.True(outcome.Digest is not null, $"{entry.Id}: {string.Join(',', outcome.Diagnostics.Select(d => d.Code))}");
            Assert.Equal(expected.GetProperty("digestHex").GetString(), outcome.Digest);
            PrefixFixtureLoader.AssertHex(outcome.Digest!, exactLength: 64);

            // The preimage is tag || 0x00 || canonical envelope bytes.
            var preimage = Convert.FromHexString(expected.GetProperty("preimageHex").GetString()!);
            var tagBytes = System.Text.Encoding.UTF8.GetBytes(tag);
            Assert.True(preimage.AsSpan().StartsWith(tagBytes), $"{entry.Id}: preimage must start with the ASCII tag");
            Assert.Equal(0, preimage[tagBytes.Length]);
            Assert.Equal(
                expected.GetProperty("digestHex").GetString(),
                PrefixHashPrimitives.Sha256Hex(preimage));
        }
    }

    [Fact]
    public void InteractionVectorsMatch()
    {
        foreach (var entry in PrefixFixtureLoader.OfKind("interaction-vector"))
        {
            var vector = PrefixFixtureLoader.LoadVector(entry.File);
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

            Assert.True(outcome.InteractionId is not null, $"{entry.Id}: {string.Join(',', outcome.Diagnostics.Select(d => d.Code))}");
            Assert.Equal(vector.GetProperty("expected").GetProperty("interactionId").GetString(), outcome.InteractionId);
        }
    }

    [Fact]
    public void MaterializationVectorsMatch()
    {
        foreach (var entry in PrefixFixtureLoader.OfKind("materialization-vector"))
        {
            var vector = PrefixFixtureLoader.LoadVector(entry.File);
            var input = PrefixFixtureLoader.BuildMaterializeInput(vector.GetProperty("input"));
            var outcome = PrefixMaterializer.Materialize(input);

            Assert.True(
                outcome.Value is not null,
                $"{entry.Id}: {string.Join(',', outcome.Diagnostics.Select(d => d.Code + ':' + d.Message))}");

            var expected = vector.GetProperty("expected");
            AssertMaterializationEqual(expected, outcome.Value!);
        }
    }

    [Fact]
    public void AppendVectorsMatch()
    {
        foreach (var entry in PrefixFixtureLoader.OfKind("append-vector"))
        {
            var vector = PrefixFixtureLoader.LoadVector(entry.File);
            var baseVector = PrefixFixtureLoader.LoadVector(FindById(vector.GetProperty("baseVectorId").GetString()!).File);
            var successorVector = PrefixFixtureLoader.LoadVector(FindById(vector.GetProperty("successorVectorId").GetString()!).File);

            var baseExpected = baseVector.GetProperty("expected");
            var successorExpected = successorVector.GetProperty("expected");

            var baseLogical = Convert.FromHexString(baseExpected.GetProperty("logicalStreamHex").GetString()!);
            var successorLogical = Convert.FromHexString(successorExpected.GetProperty("logicalStreamHex").GetString()!);
            var baseProvider = Convert.FromHexString(baseExpected.GetProperty("providerStreamHex").GetString()!);
            var successorProvider = Convert.FromHexString(successorExpected.GetProperty("providerStreamHex").GetString()!);

            Assert.True(successorLogical.Length > baseLogical.Length, "successor stable logical stream must be longer");
            Assert.True(successorLogical.AsSpan().StartsWith(baseLogical), "strict logical byte prefix");
            Assert.True(successorProvider.AsSpan().StartsWith(baseProvider), "strict provider byte prefix");

            // Promotion byte-identity: the base dynamic suffix is the successor's
            // first newly appended stable segment.
            var baseDynamicLogical = Convert.FromHexString(
                baseExpected.GetProperty("dynamicSuffix").GetProperty("logicalHex").GetString()!);
            var baseDynamicProvider = Convert.FromHexString(
                baseExpected.GetProperty("dynamicSuffix").GetProperty("providerHex").GetString()!);

            var promotedLogical = successorLogical.AsSpan(baseLogical.Length, baseDynamicLogical.Length);
            Assert.True(promotedLogical.SequenceEqual(baseDynamicLogical), "promoted logical context bytes must be identical");

            var promotedProvider = successorProvider.AsSpan(baseProvider.Length, baseDynamicProvider.Length);
            Assert.True(promotedProvider.SequenceEqual(baseDynamicProvider), "promoted provider block bytes must be identical");

            var expected = vector.GetProperty("expected");
            Assert.True(expected.GetProperty("logicalStrictPrefix").GetBoolean());
            Assert.True(expected.GetProperty("providerStrictPrefix").GetBoolean());
            Assert.True(expected.GetProperty("promotedContextLogicalBytesEqual").GetBoolean());
            Assert.True(expected.GetProperty("promotedContextProviderBytesEqual").GetBoolean());
        }
    }

    [Fact]
    public void InvalidationVectorsMatch()
    {
        foreach (var entry in PrefixFixtureLoader.OfKind("invalidation-vector"))
        {
            var vector = PrefixFixtureLoader.LoadVector(entry.File);
            var mode = vector.GetProperty("mode").GetString()!;
            var expected = vector.GetProperty("expected");

            if (mode == "materializer")
            {
                var baseExpected = PrefixFixtureLoader
                    .LoadVector(FindById(vector.GetProperty("baseVectorId").GetString()!).File)
                    .GetProperty("expected");
                var successorExpected = PrefixFixtureLoader
                    .LoadVector(FindById(vector.GetProperty("successorVectorId").GetString()!).File)
                    .GetProperty("expected");

                AssertChanged(expected, "logicalStreamChanged",
                    baseExpected.GetProperty("logicalStreamHex").GetString()!,
                    successorExpected.GetProperty("logicalStreamHex").GetString()!);
                AssertChanged(expected, "providerStreamChanged",
                    baseExpected.GetProperty("providerStreamHex").GetString()!,
                    successorExpected.GetProperty("providerStreamHex").GetString()!);
                AssertChanged(expected, "logicalHashChanged",
                    baseExpected.GetProperty("logicalPrefixSha256").GetString()!,
                    successorExpected.GetProperty("logicalPrefixSha256").GetString()!);
                AssertChanged(expected, "prefixHashChanged",
                    baseExpected.GetProperty("prefixSha256").GetString()!,
                    successorExpected.GetProperty("prefixSha256").GetString()!);
            }
            else
            {
                var baseInput = vector.GetProperty("baseInput");
                var mutatedInput = vector.GetProperty("mutatedInput");
                var stableLogical = Convert.FromHexString(baseInput.GetProperty("logicalStreamHex").GetString()!);
                var stableProvider = Convert.FromHexString(baseInput.GetProperty("providerStreamHex").GetString()!);

                var identities = PrefixFixtureLoader
                    .BuildMaterializeInput(
                        PrefixFixtureLoader.LoadVector("materialization/bootstrap.json").GetProperty("input"))
                    .ExpectedIdentities;

                var baseLogicalHash = PrefixMaterializer.ComputeLogicalPrefixSha256(
                    stableLogical,
                    baseInput.GetProperty("ledgerSchemaVersion").GetInt64(),
                    baseInput.GetProperty("prefixContractVersion").GetInt64());
                var mutatedLogicalHash = PrefixMaterializer.ComputeLogicalPrefixSha256(
                    stableLogical,
                    mutatedInput.GetProperty("ledgerSchemaVersion").GetInt64(),
                    mutatedInput.GetProperty("prefixContractVersion").GetInt64());

                Assert.Equal(expected.GetProperty("baseLogicalPrefixSha256").GetString(), baseLogicalHash);
                Assert.Equal(expected.GetProperty("mutatedLogicalPrefixSha256").GetString(), mutatedLogicalHash);
                Assert.NotEqual(baseLogicalHash, mutatedLogicalHash);

                var digests = identities;
                var basePrefixHash = PrefixMaterializer.ComputePrefixSha256(
                    digests,
                    digests.TemplateId,
                    digests.PolicyId,
                    digests.ToolDefinitionId,
                    digests.CacheConfigId,
                    digests.AdapterId,
                    stableProvider,
                    baseInput.GetProperty("ledgerSchemaVersion").GetInt64(),
                    baseInput.GetProperty("prefixContractVersion").GetInt64());
                var mutatedPrefixHash = PrefixMaterializer.ComputePrefixSha256(
                    digests,
                    digests.TemplateId,
                    digests.PolicyId,
                    digests.ToolDefinitionId,
                    digests.CacheConfigId,
                    digests.AdapterId,
                    stableProvider,
                    mutatedInput.GetProperty("ledgerSchemaVersion").GetInt64(),
                    mutatedInput.GetProperty("prefixContractVersion").GetInt64());

                Assert.Equal(expected.GetProperty("basePrefixSha256").GetString(), basePrefixHash);
                Assert.Equal(expected.GetProperty("mutatedPrefixSha256").GetString(), mutatedPrefixHash);
                Assert.NotEqual(basePrefixHash, mutatedPrefixHash);
            }
        }
    }

    [Fact]
    public void InvalidVectorsMatch()
    {
        foreach (var entry in PrefixFixtureLoader.OfKind("invalid-vector"))
        {
            var vector = PrefixFixtureLoader.LoadVector(entry.File);
            var target = vector.GetProperty("target").GetString()!;
            var input = vector.GetProperty("input");
            var expected = vector.GetProperty("expected");

            // TS-only targets are exercised by the TypeScript suite.
            if (target is "identity" or "model-snapshot")
            {
                continue;
            }

            var expectedCode = expected.GetProperty("csharpCode").GetString()!;
            var expectedPath = expected.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
            var expectedCause = expected.TryGetProperty("causeCode", out var causeElement) ? causeElement.GetString() : null;

            PrefixDiagnostic? diagnostic = target switch
            {
                "materialize" => RunMaterializeExpectingFailure(input),
                "template-id" or "policy-id" or "tools-id" or "config-id" or "adapter-id" =>
                    RunDigestExpectingFailure(target, input),
                "canonical-json" => RunDigestExpectingFailure(target: input.GetProperty("envelopeKind").GetString()! + "-id", input),
                "interaction-id" => RunInteractionExpectingFailure(input),
                "stream-guard" => RunStreamGuard(input),
                "length-guard" => RunLengthGuard(),
                _ => throw new InvalidOperationException($"unknown target {target}"),
            };

            Assert.True(diagnostic is not null, $"{entry.Id}: expected {expectedCode} but operation succeeded");
            Assert.Equal(expectedCode, diagnostic!.Code);
            if (expectedPath is not null)
            {
                Assert.Equal(expectedCode + ":" + expectedPath, diagnostic.Message);
            }

            if (expectedCause is not null)
            {
                Assert.Equal(expectedCause, diagnostic.CauseCode);
            }
        }
    }

    // ------------------------------------------------------------------

    private static PrefixFixtureLoader.ManifestEntry FindById(string id) =>
        PrefixFixtureLoader.LoadManifest().Single(entry => entry.Id == id);

    private static void AssertChanged(JsonElement expected, string flag, string baseValue, string successorValue)
    {
        if (expected.GetProperty(flag).GetBoolean())
        {
            Assert.NotEqual(baseValue, successorValue);
        }
        else
        {
            Assert.Equal(baseValue, successorValue);
        }
    }

    private static void AssertMaterializationEqual(JsonElement expected, PrefixMaterialization value)
    {
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

    private static DigestOutcome DigestFor(string tag, JsonElement envelope) => tag switch
    {
        "agentic-pr-review/cache-contract/template/v1" => CacheContractDigests.ComputeTemplateId(envelope),
        "agentic-pr-review/cache-contract/policy/v1" => CacheContractDigests.ComputePolicyId(envelope),
        "agentic-pr-review/cache-contract/tools/v1" => CacheContractDigests.ComputeToolDefinitionId(envelope),
        "agentic-pr-review/cache-contract/config/v1" => CacheContractDigests.ComputeCacheConfigId(envelope),
        "agentic-pr-review/cache-contract/adapter/v1" => CacheContractDigests.ComputeAdapterId(envelope),
        _ => throw new InvalidOperationException(tag),
    };

    private static PrefixDiagnostic? RunDigestExpectingFailure(string target, JsonElement input)
    {
        JsonElement envelope = input.TryGetProperty("envelope", out var inline)
            ? inline
            : JsonDocument.Parse(input.GetProperty("envelopeJson").GetString()!).RootElement.Clone();
        var outcome = target switch
        {
            "template-id" => CacheContractDigests.ComputeTemplateId(envelope),
            "policy-id" => CacheContractDigests.ComputePolicyId(envelope),
            "tools-id" => CacheContractDigests.ComputeToolDefinitionId(envelope),
            "config-id" => CacheContractDigests.ComputeCacheConfigId(envelope),
            "adapter-id" => CacheContractDigests.ComputeAdapterId(envelope),
            _ => throw new InvalidOperationException(target),
        };

        return outcome.Diagnostics.Length > 0 ? outcome.Diagnostics[0] : null;
    }

    private static PrefixDiagnostic? RunInteractionExpectingFailure(JsonElement input)
    {
        var predecessorElement = input.GetProperty("predecessor");
        PredecessorLedgerReference predecessor =
            predecessorElement.TryGetProperty("bootstrap", out _)
                ? PredecessorLedgerReference.Bootstrap.Instance
                : new PredecessorLedgerReference.LedgerHash(predecessorElement.GetProperty("ledgerSha256").GetString()!);
        var outcome = InteractionIdDeriver.Derive(
            predecessor,
            input.GetProperty("consumedInputSha256").GetString()!,
            input.GetProperty("currentHeadSha").GetString()!,
            input.GetProperty("interactionOrdinal").GetInt64());
        return outcome.Diagnostics.Length > 0 ? outcome.Diagnostics[0] : null;
    }

    private static PrefixDiagnostic? RunMaterializeExpectingFailure(JsonElement input)
    {
        var outcome = PrefixMaterializer.Materialize(PrefixFixtureLoader.BuildMaterializeInput(input));
        return outcome.Diagnostics.Length > 0 ? outcome.Diagnostics[0] : null;
    }

    private static PrefixDiagnostic? RunStreamGuard(JsonElement input)
    {
        var total = input.GetProperty("totalBytes").GetInt64();
        return input.GetProperty("stream").GetString()! switch
        {
            "logical-stable" => PrefixGuards.CheckLogicalStableTotal(total),
            "logical-dynamic" => PrefixGuards.CheckLogicalDynamicTotal(total),
            "provider-stable" => PrefixGuards.CheckProviderStableTotal(total),
            "provider-dynamic" => PrefixGuards.CheckProviderDynamicTotal(total),
            "logical-segment" => PrefixGuards.CheckSegmentPayload(total),
            var other => throw new InvalidOperationException(other),
        };
    }

    private static PrefixDiagnostic? RunLengthGuard()
    {
        PrefixGuards.TryCheckedTotal(long.MaxValue, 4, out _, out var overflow);
        return overflow;
    }
}
