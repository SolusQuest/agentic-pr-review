using System.Text.Json;
using AgenticPrReview.Runtime.Prefix;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Prefix;

public sealed class PrefixStagePrecedenceTests
{
    private static JsonElement ToolsEnvelope(string definitionsJson)
    {
        return JsonDocument.Parse(
            $$"""{"schemaVersion":1,"toolsetVersion":1,"definitions":{{definitionsJson}}}""").RootElement;
    }

    [Fact]
    public void StructureErrorBeatsEmbeddedIdentityWithinSameTool()
    {
        // Empty name (identity) + wrong description type (structure): structure wins.
        var envelope = ToolsEnvelope("""[{"name":"","description":42,"inputSchema":{}}]""");
        var error = PrefixEnvelopeValidator.ValidateStructure(
            PrefixEnvelopeValidator.EnvelopeKind.Tools, envelope);
        Assert.Equal("prefix_envelope_invalid", error?.Code);
        Assert.Equal(
            "prefix_envelope_invalid:/definitions/0/description",
            error?.Message);
    }

    [Fact]
    public void StructureErrorInSecondToolBeatsIdentityErrorInFirstTool()
    {
        // First tool has an empty name (identity); second tool has an unknown
        // wrapper field (structure). Structure stage must win.
        var envelope = ToolsEnvelope(
            """[{"name":"","description":"d","inputSchema":{}},{"name":"ok","description":"d","inputSchema":{},"bogus":1}]""");
        var error = PrefixEnvelopeValidator.ValidateStructure(
            PrefixEnvelopeValidator.EnvelopeKind.Tools, envelope);
        Assert.Equal("prefix_envelope_invalid", error?.Code);
        Assert.Equal(
            "prefix_envelope_invalid:/definitions/1/<untrusted-property>",
            error?.Message);
    }

    [Fact]
    public void CacheConfigStructureErrorBeatsToolsIdentityError()
    {
        var vector = PrefixFixtureLoader.LoadVector("materialization/bootstrap.json");
        var baseInput = PrefixFixtureLoader.BuildMaterializeInput(vector.GetProperty("input"));
        var input = baseInput with
        {
            Envelopes = baseInput.Envelopes with
            {
                Tools = ToolsEnvelope("""[{"name":"","description":"d","inputSchema":{}}]"""),
                CacheConfig = JsonDocument.Parse("""{"schemaVersion":1,"cacheConfigVersion":1,"markerPolicy":42,"eligibility":"e","statelessMode":false}""").RootElement,
            },
        };
        var outcome = PrefixMaterializer.Materialize(input);
        Assert.Equal("prefix_envelope_invalid", Assert.Single(outcome.Diagnostics).Code);
    }

    [Fact]
    public void AdapterIdentityErrorBeatsCanonicalDefect()
    {
        // Stage 3 (embedded identity: adapter build version with a control
        // character) must precede stage 4 (canonical JSON: a lone surrogate
        // inside the tools envelope's open inputSchema).
        var vector = PrefixFixtureLoader.LoadVector("materialization/bootstrap.json");
        var baseInput = PrefixFixtureLoader.BuildMaterializeInput(vector.GetProperty("input"));
        var toolsWithSurrogate = JsonDocument.Parse(
            "{\"schemaVersion\":1,\"toolsetVersion\":1,\"definitions\":[{\"name\":\"t\",\"description\":\"d\",\"inputSchema\":{\"x\":\"bad\\ud800\"}}]}").RootElement;
        var adapterWithControl = JsonDocument.Parse(
            "{\"schemaVersion\":1,\"capabilityProfileVersion\":1,\"adapterBuildVersion\":\"bad\\u0001\"}").RootElement;
        var input = baseInput with
        {
            Envelopes = baseInput.Envelopes with
            {
                Tools = toolsWithSurrogate,
                Adapter = adapterWithControl,
            },
        };
        var outcome = PrefixMaterializer.Materialize(input);
        Assert.Equal("prefix_identity_invalid", Assert.Single(outcome.Diagnostics).Code);
    }

    [Fact]
    public void NullLedgerInsideHistoryVariantsYieldsTypedFailure()
    {
        var vector = PrefixFixtureLoader.LoadVector("materialization/bootstrap.json");
        var input = PrefixFixtureLoader.BuildMaterializeInput(vector.GetProperty("input"));

        var withNullContinuation = input with { History = new MaterializationHistory.ContinuationHistory(null!) };
        Assert.Equal(
            "prefix_identity_invalid",
            Assert.Single(PrefixMaterializer.Materialize(withNullContinuation).Diagnostics).Code);

        var withNullReset = input with { History = new MaterializationHistory.ResetHistory(null!) };
        Assert.Equal(
            "prefix_identity_invalid",
            Assert.Single(PrefixMaterializer.Materialize(withNullReset).Diagnostics).Code);
    }
}
