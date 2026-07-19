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
    [Fact]
    public void CanonicalDefectBeatsEnvelopeCap()
    {
        // Template is legal but oversize (stage 5a cap); policy has a canonical
        // defect (stage 4). All canonical-domain checks must complete before
        // any cap check, so prefix_canonical_input_rejected wins.
        var vector = PrefixFixtureLoader.LoadVector("materialization/bootstrap.json");
        var baseInput = PrefixFixtureLoader.BuildMaterializeInput(vector.GetProperty("input"));
        var oversizeTemplate = JsonDocument.Parse(
            "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":\"" + new string('x', 300_000) + "\"}").RootElement;
        var surrogatePolicy = JsonDocument.Parse(
            "{\"schemaVersion\":1,\"policyVersion\":1,\"instructions\":\"bad\\ud800\",\"constraints\":{}}").RootElement;
        var input = baseInput with
        {
            Envelopes = baseInput.Envelopes with
            {
                Template = oversizeTemplate,
                Policy = surrogatePolicy,
            },
        };
        var outcome = PrefixMaterializer.Materialize(input);
        Assert.Equal("prefix_canonical_input_rejected", Assert.Single(outcome.Diagnostics).Code);
    }
    [Fact]
    public void TemplateDepthOverflowBeatsToolsIdentityError()
    {
        // Structural bounds are part of the structure stage: a depth overflow in
        // the template envelope must beat the tools embedded-identity error.
        var vector = PrefixFixtureLoader.LoadVector("materialization/bootstrap.json");
        var baseInput = PrefixFixtureLoader.BuildMaterializeInput(vector.GetProperty("input"));
        string Nest(int depth) => depth == 0 ? "1" : $"[{Nest(depth - 1)}]";
        var deepTemplate = JsonDocument.Parse(
            "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":" + Nest(65) + "}",
            new JsonDocumentOptions { MaxDepth = 256 }).RootElement;
        var input = baseInput with
        {
            Envelopes = baseInput.Envelopes with
            {
                Template = deepTemplate,
                Tools = ToolsEnvelope("""[{"name":"","description":"d","inputSchema":{}}]"""),
            },
        };
        var outcome = PrefixMaterializer.Materialize(input);
        Assert.Equal("prefix_envelope_invalid", Assert.Single(outcome.Diagnostics).Code);
    }

    [Fact]
    public void TemplatePropertyCountOverflowBeatsAdapterIdentityError()
    {
        var vector = PrefixFixtureLoader.LoadVector("materialization/bootstrap.json");
        var baseInput = PrefixFixtureLoader.BuildMaterializeInput(vector.GetProperty("input"));
        var props = "{" + string.Join(",", Enumerable.Range(0, 257).Select(i => $"\"k{i}\":{i}")) + "}";
        var fatTemplate = JsonDocument.Parse(
            "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":" + props + "}").RootElement;
        var adapterWithControl = JsonDocument.Parse(
            "{\"schemaVersion\":1,\"capabilityProfileVersion\":1,\"adapterBuildVersion\":\"bad\\u0001\"}").RootElement;
        var input = baseInput with
        {
            Envelopes = baseInput.Envelopes with
            {
                Template = fatTemplate,
                Adapter = adapterWithControl,
            },
        };
        var outcome = PrefixMaterializer.Materialize(input);
        Assert.Equal("prefix_envelope_invalid", Assert.Single(outcome.Diagnostics).Code);
    }

    [Fact]
    public void StructuralBoundBeatsCanonicalDefectRegardlessOfPosition()
    {
        // Earlier structural bound (template depth) + later canonical defect
        // (tools lone surrogate): structural bound wins.
        var vector = PrefixFixtureLoader.LoadVector("materialization/bootstrap.json");
        var baseInput = PrefixFixtureLoader.BuildMaterializeInput(vector.GetProperty("input"));
        string Nest(int depth) => depth == 0 ? "1" : $"[{Nest(depth - 1)}]";
        var deepTemplate = JsonDocument.Parse(
            "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":" + Nest(65) + "}",
            new JsonDocumentOptions { MaxDepth = 256 }).RootElement;
        var surrogateTools = JsonDocument.Parse(
            "{\"schemaVersion\":1,\"toolsetVersion\":1,\"definitions\":[{\"name\":\"t\",\"description\":\"d\",\"inputSchema\":{\"x\":\"bad\\ud800\"}}]}").RootElement;
        var input = baseInput with
        {
            Envelopes = baseInput.Envelopes with
            {
                Template = deepTemplate,
                Tools = surrogateTools,
            },
        };
        var outcome = PrefixMaterializer.Materialize(input);
        Assert.Equal("prefix_envelope_invalid", Assert.Single(outcome.Diagnostics).Code);
    }

    [Fact]
    public void LaterStructuralBoundStillBeatsEarlierCanonicalDefect()
    {
        // Earlier canonical defect (template lone surrogate) + later structural
        // bound (tools depth overflow): structural bound still wins (stage 2 < stage 4).
        var vector = PrefixFixtureLoader.LoadVector("materialization/bootstrap.json");
        var baseInput = PrefixFixtureLoader.BuildMaterializeInput(vector.GetProperty("input"));
        string Nest(int depth) => depth == 0 ? "1" : $"[{Nest(depth - 1)}]";
        var surrogateTemplate = JsonDocument.Parse(
            "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":\"bad\\ud800\"}").RootElement;
        var deepTools = JsonDocument.Parse(
            "{\"schemaVersion\":1,\"toolsetVersion\":1,\"definitions\":[{\"name\":\"t\",\"description\":\"d\",\"inputSchema\":" + Nest(65) + "}]}",
            new JsonDocumentOptions { MaxDepth = 256 }).RootElement;
        var input = baseInput with
        {
            Envelopes = baseInput.Envelopes with
            {
                Template = surrogateTemplate,
                Tools = deepTools,
            },
        };
        var outcome = PrefixMaterializer.Materialize(input);
        Assert.Equal("prefix_envelope_invalid", Assert.Single(outcome.Diagnostics).Code);
    }
}
