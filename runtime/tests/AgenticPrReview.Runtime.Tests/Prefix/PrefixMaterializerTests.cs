using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using AgenticPrReview.Runtime.Prefix;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Prefix;

public sealed class PrefixMaterializerTests
{
    private static PrefixMaterializationInput BootstrapInput()
    {
        var vector = PrefixFixtureLoader.LoadVector("materialization/bootstrap.json");
        return PrefixFixtureLoader.BuildMaterializeInput(vector.GetProperty("input"));
    }

    private static PrefixMaterializationInput BootstrapInputWithEnvelopes(string envelopeJson)
    {
        var input = BootstrapInput();
        var envelopes = JsonDocument.Parse(envelopeJson).RootElement;
        return input with
        {
            Envelopes = new RawCacheContractEnvelopes(
                envelopes.GetProperty("template"),
                envelopes.GetProperty("policy"),
                envelopes.GetProperty("tools"),
                envelopes.GetProperty("cacheConfig"),
                envelopes.GetProperty("adapter")),
        };
    }

    [Fact]
    public void SameInputProducesByteIdenticalOutput()
    {
        var first = PrefixMaterializer.Materialize(BootstrapInput());
        var second = PrefixMaterializer.Materialize(BootstrapInput());
        Assert.NotNull(first.Value);
        Assert.NotNull(second.Value);
        Assert.True(first.Value.StableLogicalStream.SequenceEqual(second.Value.StableLogicalStream));
        Assert.True(first.Value.StableProviderStream.SequenceEqual(second.Value.StableProviderStream));
        Assert.Equal(first.Value.LogicalPrefixSha256, second.Value.LogicalPrefixSha256);
        Assert.Equal(first.Value.PrefixSha256, second.Value.PrefixSha256);
    }

    [Fact]
    public void EnvelopeKeyOrderDoesNotChangeOutput()
    {
        var ordered = BootstrapInput();
        var shuffledJson = """
            {
              "adapter": {"adapterBuildVersion":"0.0.0-fixture","capabilityProfileVersion":1,"schemaVersion":1},
              "cacheConfig": {"statelessMode":false,"eligibility":"min-prefix-1024","markerPolicy":"stable-boundary","cacheConfigVersion":1,"schemaVersion":1},
              "tools": {"toolsetVersion":1,"schemaVersion":1,"definitions":[{"policyMetadata":{"risk":"low"},"inputSchema":{"type":"object","properties":{"summary":{"type":"string"}},"required":["summary"]},"description":"Submit the structured review.","name":"submit_review"}]},
              "policy": {"schemaVersion":1,"policyVersion":2,"instructions":"Review the delta carefully.","constraints":{"tone":"strict","maxFindings":10}},
              "template": {"templateVersion":3,"schemaVersion":1,"definition":{"text":"You are a precise code reviewer.","role":"system"}}
            }
            """;
        var shuffled = BootstrapInputWithEnvelopes(shuffledJson);
        var a = PrefixMaterializer.Materialize(ordered);
        var b = PrefixMaterializer.Materialize(shuffled);
        Assert.NotNull(a.Value);
        Assert.NotNull(b.Value);
        Assert.Equal(a.Value.PrefixSha256, b.Value.PrefixSha256);
        Assert.True(a.Value.StableLogicalStream.SequenceEqual(b.Value.StableLogicalStream));
    }

    [Fact]
    public void NonSemanticInteractionIdDoesNotChangeStreamsOrHashes()
    {
        var a = BootstrapInput();
        var b = a with { Interaction = new AgenticPrReview.Runtime.Ledger.InteractionIdentity(new string('f', 64), a.Interaction.InteractionOrdinal) };
        var first = PrefixMaterializer.Materialize(a);
        var second = PrefixMaterializer.Materialize(b);
        Assert.NotNull(first.Value);
        Assert.NotNull(second.Value);
        Assert.Equal(first.Value.LogicalPrefixSha256, second.Value.LogicalPrefixSha256);
        Assert.Equal(first.Value.PrefixSha256, second.Value.PrefixSha256);
        Assert.True(first.Value.StableLogicalStream.SequenceEqual(second.Value.StableLogicalStream));
        Assert.True(first.Value.DynamicLogicalStream.SequenceEqual(second.Value.DynamicLogicalStream));
    }

    [Fact]
    public void CurrentContextFailureMapsToSingleDiagnosticWithFirstCauseCode()
    {
        var input = BootstrapInput();
        // Two defects: bad subject digest AND unsafe path; #49 reports the first
        // in its deterministic order, which must become the single CauseCode.
        input = input with
        {
            CurrentContext = new AgenticPrReview.Runtime.Ledger.ValidatedContextSource
            {
                SubjectDigest = "not-a-digest",
                ReviewedHeadSha = input.CurrentContext.ReviewedHeadSha,
                ReviewedBaseSha = input.CurrentContext.ReviewedBaseSha,
                ChangedFiles = input.CurrentContext.ChangedFiles,
            },
        };
        var outcome = PrefixMaterializer.Materialize(input);
        Assert.Null(outcome.Value);
        var diagnostic = Assert.Single(outcome.Diagnostics);
        Assert.Equal("prefix_current_context_invalid", diagnostic.Code);
        Assert.Equal("ledger_schema_violation", diagnostic.CauseCode);
        Assert.DoesNotContain("not-a-digest", diagnostic.Message);
    }

    [Fact]
    public void IdentityStagePrecedesEnvelopeStage()
    {
        var input = BootstrapInput();
        input = input with
        {
            ExpectedIdentities = input.ExpectedIdentities with { ModelId = "latest" },
            Envelopes = input.Envelopes with
            {
                Template = JsonDocument.Parse("""{"schemaVersion":1,"templateVersion":3,"definition":{},"bogus":1}""").RootElement,
            },
        };
        var outcome = PrefixMaterializer.Materialize(input);
        Assert.Equal("prefix_model_alias_literal", Assert.Single(outcome.Diagnostics).Code);
    }

    [Fact]
    public void TemplateEnvelopeErrorPrecedesPolicyError()
    {
        var json = """
            {
              "adapter": {"adapterBuildVersion":"0.0.0-fixture","capabilityProfileVersion":1,"schemaVersion":1},
              "cacheConfig": {"statelessMode":false,"eligibility":"min-prefix-1024","markerPolicy":"stable-boundary","cacheConfigVersion":1,"schemaVersion":1},
              "tools": {"toolsetVersion":1,"schemaVersion":1,"definitions":[]},
              "policy": {"schemaVersion":1,"policyVersion":2,"instructions":42,"constraints":{}},
              "template": {"templateVersion":3,"schemaVersion":1,"bogus":1}
            }
            """;
        var outcome = PrefixMaterializer.Materialize(BootstrapInputWithEnvelopes(json));
        var diagnostic = Assert.Single(outcome.Diagnostics);
        Assert.Equal("prefix_envelope_invalid", diagnostic.Code);
        Assert.Equal("prefix_envelope_invalid:/<untrusted-property>", diagnostic.Message);
    }

    [Fact]
    public void DisposedEnvelopeDocumentYieldsTypedFailure()
    {
        var input = BootstrapInput();
        JsonElement disposed;
        using (var doc = JsonDocument.Parse("""{"schemaVersion":1,"templateVersion":3,"definition":{}}"""))
        {
            disposed = doc.RootElement;
        }

        input = input with { Envelopes = input.Envelopes with { Template = disposed } };
        var outcome = PrefixMaterializer.Materialize(input);
        Assert.Equal("prefix_envelope_invalid", Assert.Single(outcome.Diagnostics).Code);
    }

    [Fact]
    public void MaterializationIsCultureIndependent()
    {
        var input = BootstrapInput();
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            var cultured = PrefixMaterializer.Materialize(input);
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            var french = PrefixMaterializer.Materialize(input);
            Assert.NotNull(cultured.Value);
            Assert.NotNull(french.Value);
            Assert.Equal(cultured.Value.PrefixSha256, french.Value.PrefixSha256);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void EmptyToolsetStillHasToolsSegment()
    {
        var json = """
            {
              "adapter": {"adapterBuildVersion":"0.0.0-fixture","capabilityProfileVersion":1,"schemaVersion":1},
              "cacheConfig": {"statelessMode":false,"eligibility":"min-prefix-1024","markerPolicy":"stable-boundary","cacheConfigVersion":1,"schemaVersion":1},
              "tools": {"toolsetVersion":1,"schemaVersion":1,"definitions":[]},
              "policy": {"schemaVersion":1,"policyVersion":2,"instructions":"Review the delta carefully.","constraints":{"maxFindings":10,"tone":"strict"}},
              "template": {"templateVersion":3,"schemaVersion":1,"definition":{"role":"system","text":"You are a precise code reviewer."}}
            }
            """;
        var input = BootstrapInputWithEnvelopes(json);
        // Align expected toolDefinitionId with the empty toolset.
        var digest = CacheContractDigests.ComputeToolDefinitionId(
            JsonDocument.Parse("""{"toolsetVersion":1,"schemaVersion":1,"definitions":[]}""").RootElement);
        Assert.NotNull(digest.Digest);
        input = input with { ExpectedIdentities = input.ExpectedIdentities with { ToolDefinitionId = digest.Digest! } };

        var outcome = PrefixMaterializer.Materialize(input);
        Assert.NotNull(outcome.Value);
        var streamText = System.Text.Encoding.UTF8.GetString(outcome.Value.StableLogicalStream.AsSpan());
        Assert.Contains("\"definitions\":[]", streamText);
    }

    [Fact]
    public void ResetWithDifferentCacheContractSucceeds()
    {
        var resetVector = PrefixFixtureLoader.LoadVector("materialization/reset.json");
        var input = PrefixFixtureLoader.BuildMaterializeInput(resetVector.GetProperty("input"));
        var differentEnvelopes = JsonDocument.Parse("""
            {
              "adapter": {"adapterBuildVersion":"0.0.1-fixture","capabilityProfileVersion":1,"schemaVersion":1},
              "cacheConfig": {"statelessMode":true,"eligibility":"min-prefix-1024","markerPolicy":"stable-boundary","cacheConfigVersion":1,"schemaVersion":1},
              "tools": {"toolsetVersion":1,"schemaVersion":1,"definitions":[]},
              "policy": {"schemaVersion":1,"policyVersion":2,"instructions":"Review the delta carefully.","constraints":{"maxFindings":10,"tone":"strict"}},
              "template": {"templateVersion":3,"schemaVersion":1,"definition":{"role":"system","text":"You are a precise code reviewer."}}
            }
            """).RootElement;
        var adapterDigest = CacheContractDigests.ComputeAdapterId(differentEnvelopes.GetProperty("adapter"));
        var configDigest = CacheContractDigests.ComputeCacheConfigId(differentEnvelopes.GetProperty("cacheConfig"));
        var toolsDigest = CacheContractDigests.ComputeToolDefinitionId(differentEnvelopes.GetProperty("tools"));
        input = input with
        {
            Envelopes = new RawCacheContractEnvelopes(
                differentEnvelopes.GetProperty("template"),
                differentEnvelopes.GetProperty("policy"),
                differentEnvelopes.GetProperty("tools"),
                differentEnvelopes.GetProperty("cacheConfig"),
                differentEnvelopes.GetProperty("adapter")),
            ExpectedIdentities = input.ExpectedIdentities with
            {
                AdapterId = adapterDigest.Digest!,
                CacheConfigId = configDigest.Digest!,
                ToolDefinitionId = toolsDigest.Digest!,
            },
        };

        var outcome = PrefixMaterializer.Materialize(input);
        Assert.NotNull(outcome.Value);
        // Reset: no prior records in the stable stream; only the 3 static segments.
        Assert.Equal(3, outcome.Value.StableSegmentCount);
    }
}

public sealed class PrefixMaterializerInputGuardTests
{
    [Fact]
    public void NullInputYieldsTypedFailure()
    {
        var outcome = PrefixMaterializer.Materialize(null!);
        Assert.Null(outcome.Value);
        Assert.Equal("prefix_identity_invalid", Assert.Single(outcome.Diagnostics).Code);
    }

    [Fact]
    public void NullNestedInputYieldsTypedFailure()
    {
        var vector = PrefixFixtureLoader.LoadVector("materialization/bootstrap.json");
        var input = PrefixFixtureLoader.BuildMaterializeInput(vector.GetProperty("input"));
        var outcome = PrefixMaterializer.Materialize(input with { ExpectedIdentities = null! });
        Assert.Equal("prefix_identity_invalid", Assert.Single(outcome.Diagnostics).Code);

        var outcome2 = PrefixMaterializer.Materialize(input with { Envelopes = null! });
        Assert.Equal("prefix_identity_invalid", Assert.Single(outcome2.Diagnostics).Code);

        var outcome3 = PrefixMaterializer.Materialize(input with { History = null! });
        Assert.Equal("prefix_identity_invalid", Assert.Single(outcome3.Diagnostics).Code);
    }

    [Fact]
    public void InvalidWorkflowIdentityLosesToNothingAndBeatsEnvelopeErrors()
    {
        var vector = PrefixFixtureLoader.LoadVector("materialization/bootstrap.json");
        var input = PrefixFixtureLoader.BuildMaterializeInput(vector.GetProperty("input")) with
        {
            ExpectedIdentities = new AgenticPrReview.Runtime.Ledger.ExpectedIdentities(
                "owner/repo", "owner/repo", 50, new string('w', 300), "trusted",
                "provider", "model-2024-01-01",
                new string('a', 64), new string('b', 64), new string('c', 64), new string('d', 64), new string('e', 64)),
        };
        // Identity stage (overlong workflowIdentity) must win over envelope errors.
        var withBadEnvelope = input with
        {
            Envelopes = input.Envelopes with
            {
                Template = JsonDocument.Parse("""{"schemaVersion":1,"templateVersion":3,"definition":{},"bogus":1}""").RootElement,
            },
        };
        var outcome = PrefixMaterializer.Materialize(withBadEnvelope);
        Assert.Equal("prefix_identity_invalid", Assert.Single(outcome.Diagnostics).Code);
    }

    [Fact]
    public void LargestReachableTemplateSegmentSucceeds()
    {
        // The template envelope at exactly its canonical cap yields a template
        // segment payload at exactly the segment cap (the wrappers are the same
        // length), which must still succeed.
        var wrapper = "{\"schemaVersion\":1,\"templateVersion\":3,\"definition\":\"\"}".Length;
        var pad = 262_144 - wrapper;
        var templateJson = "{\"schemaVersion\":1,\"templateVersion\":3,\"definition\":\"" + new string('x', pad) + "\"}";
        var template = JsonDocument.Parse(templateJson).RootElement;
        var digest = CacheContractDigests.ComputeTemplateId(template);
        Assert.NotNull(digest.Digest);

        var vector = PrefixFixtureLoader.LoadVector("materialization/bootstrap.json");
        var input = PrefixFixtureLoader.BuildMaterializeInput(vector.GetProperty("input")) with
        {
            Envelopes = PrefixFixtureLoader.BuildMaterializeInput(vector.GetProperty("input")).Envelopes with { Template = template },
            ExpectedIdentities = PrefixFixtureLoader.BuildMaterializeInput(vector.GetProperty("input")).ExpectedIdentities with { TemplateId = digest.Digest! },
        };
        var outcome = PrefixMaterializer.Materialize(input);
        Assert.NotNull(outcome.Value);

        // One byte over the envelope cap must fail before segment assembly.
        var overTemplate = JsonDocument.Parse(
            "{\"schemaVersion\":1,\"templateVersion\":3,\"definition\":\"" + new string('x', pad + 1) + "\"}").RootElement;
        var over = CacheContractDigests.ComputeTemplateId(overTemplate);
        Assert.Equal("prefix_envelope_too_large", Assert.Single(over.Diagnostics).Code);
    }
}
