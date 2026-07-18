using System.Text.Json;
using AgenticPrReview.Runtime.Prefix;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Prefix;

/// <summary>
/// Round-5 review coverage: the byte cap never masks a canonical-domain
/// defect in the same envelope, and invalid property names produce the
/// structured terminal marker at their exact position.
/// </summary>
public sealed class PrefixCanonicalBoundaryTests
{
    private static PrefixDiagnostic? ValidateTemplate(string envelopeJson)
    {
        using var doc = JsonDocument.Parse(envelopeJson);
        return PrefixEnvelopeValidator.Validate(
            PrefixEnvelopeValidator.EnvelopeKind.Template, doc.RootElement, out _);
    }

    private static string BigString(int bytes) => new('x', bytes);

    [Fact]
    public void EarlyOversizeStringDoesNotMaskLaterLoneSurrogate()
    {
        // "a" (oversize) sorts before "z" (lone surrogate value): the domain
        // defect must win over the byte cap.
        var error = ValidateTemplate(
            "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":{\"a\":\"" + BigString(300_000) + "\",\"z\":\"bad\\ud800\"}}");
        Assert.Equal("prefix_canonical_input_rejected", error?.Code);
        Assert.Equal("prefix_canonical_input_rejected:/definition/<untrusted-property>", error?.Message);
    }

    [Fact]
    public void EarlyOversizeStringDoesNotMaskLaterDuplicateProperty()
    {
        var error = ValidateTemplate(
            "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":{\"a\":\"" + BigString(300_000) + "\",\"z\":1,\"z\":2}}");
        Assert.Equal("prefix_canonical_input_rejected", error?.Code);
        Assert.Equal("prefix_canonical_input_rejected:/definition/<untrusted-property>", error?.Message);
    }

    [Fact]
    public void EarlyOversizeStringDoesNotMaskLaterNonFiniteNumber()
    {
        var error = ValidateTemplate(
            "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":{\"a\":\"" + BigString(300_000) + "\",\"z\":1e999}}");
        Assert.Equal("prefix_canonical_input_rejected", error?.Code);
        Assert.Equal("prefix_canonical_input_rejected:/definition/<untrusted-property>", error?.Message);
    }

    [Fact]
    public void InvalidPropertyNameAtOpenJsonRoot()
    {
        var error = ValidateTemplate("{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":{\"\\ud800\":1}}");
        Assert.Equal("prefix_canonical_input_rejected", error?.Code);
        Assert.Equal("prefix_canonical_input_rejected:/definition/<invalid-utf16>", error?.Message);
    }

    [Fact]
    public void InvalidPropertyNameUnderUnknownAncestor()
    {
        var error = ValidateTemplate(
            "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":{\"secretToken\":{\"\\ud800\":1}}}");
        Assert.Equal("prefix_canonical_input_rejected", error?.Code);
        Assert.Equal("prefix_canonical_input_rejected:/definition/<untrusted-property>/<invalid-utf16>", error?.Message);
    }

    [Fact]
    public void StructuralBoundInLaterEnvelopeBeatsInvalidPropertyName()
    {
        // Template has an invalid property name (canonical, stage 4); the tools
        // envelope has a depth violation (structure, stage 2). Stage 2 wins.
        var vector = PrefixFixtureLoader.LoadVector("materialization/bootstrap.json");
        var baseInput = PrefixFixtureLoader.BuildMaterializeInput(vector.GetProperty("input"));
        string Nest(int depth) => depth == 0 ? "1" : $"[{Nest(depth - 1)}]";
        var badTemplate = JsonDocument.Parse(
            "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":{\"\\ud800\":1}}").RootElement;
        var deepTools = JsonDocument.Parse(
            "{\"schemaVersion\":1,\"toolsetVersion\":1,\"definitions\":[{\"name\":\"t\",\"description\":\"d\",\"inputSchema\":" + Nest(65) + "}]}",
            new JsonDocumentOptions { MaxDepth = 256 }).RootElement;
        var input = baseInput with
        {
            Envelopes = baseInput.Envelopes with
            {
                Template = badTemplate,
                Tools = deepTools,
            },
        };
        var outcome = PrefixMaterializer.Materialize(input);
        Assert.Equal("prefix_envelope_invalid", Assert.Single(outcome.Diagnostics).Code);
    }

    [Fact]
    public void ExactEnvelopeCapPassesAndCapPlusOneFails()
    {
        // Find the padding that lands the template envelope exactly on the cap.
        const string prefix = "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":\"";
        const string suffix = "\"}";
        var pad = 262_144 - prefix.Length - suffix.Length;
        Assert.Null(ValidateTemplate(prefix + new string('x', pad) + suffix));
        var error = ValidateTemplate(prefix + new string('x', pad + 1) + suffix);
        Assert.Equal("prefix_envelope_too_large", error?.Code);
    }

    [Fact]
    public void HighEscapeInflationStringIsRejectedAtCap()
    {
        // 100_000 control characters inflate ~6x in canonical form (~600 KB).
        var error = ValidateTemplate(
            "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":\"" + string.Concat(Enumerable.Repeat("\\u0001", 100_000)) + "\"}");
        Assert.Equal("prefix_envelope_too_large", error?.Code);
    }
    [Fact]
    public void EarlierNonFiniteValueBeatsLaterInvalidName()
    {
        var error = ValidateTemplate(
            "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":{\"a\":1e999,\"\\ud800\":1}}");
        Assert.Equal("prefix_canonical_input_rejected", error?.Code);
        Assert.Equal("prefix_canonical_input_rejected:/definition/<untrusted-property>", error?.Message);
    }

    [Fact]
    public void EarlierSurrogateValueBeatsLaterInvalidName()
    {
        var error = ValidateTemplate(
            "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":{\"a\":\"\\ud800\",\"\\ud801\":1}}");
        Assert.Equal("prefix_canonical_input_rejected", error?.Code);
        Assert.Equal("prefix_canonical_input_rejected:/definition/<untrusted-property>", error?.Message);
    }

    [Fact]
    public void InvalidNameWinsAtItsOwnSortedPosition()
    {
        // The invalid name (sentinel sorts at U+D800) precedes an astral key
        // (first UTF-16 unit U+D83x) with a non-finite value.
        var error = ValidateTemplate(
            "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":{\"\\ud800\":1,\"\\ud83d\\ude00\":1e999}}");
        Assert.Equal("prefix_canonical_input_rejected", error?.Code);
        Assert.Equal("prefix_canonical_input_rejected:/definition/<invalid-utf16>", error?.Message);
    }
}
