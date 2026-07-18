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

    private static long MeasureValidationAllocations(JsonElement envelope, PrefixEnvelopeValidator.EnvelopeKind kind)
    {
        // Warm the relevant code paths before measuring per-call managed
        // allocations; the JsonDocument's own retained input buffer is outside
        // this validation measurement.
        using (var warm = JsonDocument.Parse("{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":\"x\"}"))
        {
            _ = CacheContractDigests.ComputeTemplateId(warm.RootElement);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = kind switch
        {
            PrefixEnvelopeValidator.EnvelopeKind.Template => CacheContractDigests.ComputeTemplateId(envelope),
            PrefixEnvelopeValidator.EnvelopeKind.Policy => CacheContractDigests.ComputePolicyId(envelope),
            PrefixEnvelopeValidator.EnvelopeKind.Tools => CacheContractDigests.ComputeToolDefinitionId(envelope),
            PrefixEnvelopeValidator.EnvelopeKind.CacheConfig => CacheContractDigests.ComputeCacheConfigId(envelope),
            PrefixEnvelopeValidator.EnvelopeKind.Adapter => CacheContractDigests.ComputeAdapterId(envelope),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

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
    public void OversizePlainStringUsesTheNormalBoundedCanonicalPath()
    {
        var error = ValidateTemplate(
            "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":\"" + new string('x', 1_000_000) + "\"}");
        Assert.Equal("prefix_envelope_too_large", error?.Code);
    }

    [Fact]
    public void OversizeStringValidationAllocationIsBoundedBelowTokenSize()
    {
        const int tokenBytes = 4_000_000;
        using var doc = JsonDocument.Parse(
            "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":\"" + BigString(tokenBytes) + "\"}");
        var allocated = MeasureValidationAllocations(
            doc.RootElement, PrefixEnvelopeValidator.EnvelopeKind.Template);
        Assert.True(allocated < tokenBytes / 2, $"validation allocated {allocated} bytes for a {tokenBytes}-byte token");
    }

    [Fact]
    public void OversizePropertyNameValidationAllocationIsBoundedBelowTokenSize()
    {
        const int tokenBytes = 4_000_000;
        using var doc = JsonDocument.Parse(
            "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":{\"" + BigString(tokenBytes) + "\":1}}");
        var allocated = MeasureValidationAllocations(
            doc.RootElement, PrefixEnvelopeValidator.EnvelopeKind.Template);
        Assert.True(allocated < tokenBytes / 2, $"validation allocated {allocated} bytes for a {tokenBytes}-byte name");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OversizeEmbeddedIdentityValidationAllocationIsBounded(bool toolsEnvelope)
    {
        const int tokenBytes = 4_000_000;
        var json = toolsEnvelope
            ? "{\"schemaVersion\":1,\"toolsetVersion\":1,\"definitions\":[{\"name\":\"" + BigString(tokenBytes) + "\",\"description\":\"d\",\"inputSchema\":{}}]}"
            : "{\"schemaVersion\":1,\"capabilityProfileVersion\":1,\"adapterBuildVersion\":\"" + BigString(tokenBytes) + "\"}";
        using var doc = JsonDocument.Parse(json);
        var kind = toolsEnvelope
            ? PrefixEnvelopeValidator.EnvelopeKind.Tools
            : PrefixEnvelopeValidator.EnvelopeKind.Adapter;
        var allocated = MeasureValidationAllocations(doc.RootElement, kind);
        Assert.True(allocated < tokenBytes / 8, $"identity validation allocated {allocated} bytes for a {tokenBytes}-byte token");
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

    [Fact]
    public void PrefixedSurrogateNameSortsBeforeAsciiValueDefect()
    {
        // Real UTF-16 order: "a\ud800" (0x61 prefix) sorts before "b", so the
        // invalid name is the first defect.
        var error = ValidateTemplate(
            "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":{\"a\\ud800\":1,\"b\":1e999}}");
        Assert.Equal("prefix_canonical_input_rejected", error?.Code);
        Assert.Equal("prefix_canonical_input_rejected:/definition/<invalid-utf16>", error?.Message);
    }

    [Fact]
    public void AstralKeyValueDefectBeatsLoneLowSurrogateName()
    {
        // Real UTF-16 order: the astral key's first unit (U+D83x) sorts before
        // a lone low surrogate (U+DC00), so the astral key's defect wins.
        var error = ValidateTemplate(
            "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":{\"\\udc00\":1,\"\\ud83d\\ude00\":1e999}}");
        Assert.Equal("prefix_canonical_input_rejected", error?.Code);
        Assert.Equal("prefix_canonical_input_rejected:/definition/<untrusted-property>", error?.Message);
    }

    [Fact]
    public void TwoDifferentInvalidNamesDoNotCollideAndEarlierValueWins()
    {
        var error = ValidateTemplate(
            "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":{\"a\":1e999,\"x\\ud800\":1,\"y\\ud801\":2}}");
        Assert.Equal("prefix_canonical_input_rejected", error?.Code);
        Assert.Equal("prefix_canonical_input_rejected:/definition/<untrusted-property>", error?.Message);
    }

    [Fact]
    public void TwoDifferentInvalidNamesAreNotFalseDuplicates()
    {
        // Without a leading valid defect, the first invalid name (sorted) is
        // reported — proving the two names never collapsed into one sentinel.
        var error = ValidateTemplate(
            "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":{\"x\\ud800\":1,\"y\\ud801\":2}}");
        Assert.Equal("prefix_canonical_input_rejected", error?.Code);
        Assert.Equal("prefix_canonical_input_rejected:/definition/<invalid-utf16>", error?.Message);
    }
    [Fact]
    public void RelaxedParserWithTrailingCommaAndInvalidNameYieldsTypedFailure()
    {
        var options = new JsonDocumentOptions { AllowTrailingCommas = true };
        using var doc = JsonDocument.Parse(
            "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":{\"\\ud800\":1,},}",
            options);
        var error = PrefixEnvelopeValidator.Validate(
            PrefixEnvelopeValidator.EnvelopeKind.Template, doc.RootElement, out _);
        Assert.Equal("prefix_canonical_input_rejected", error?.Code);
        Assert.Equal("prefix_canonical_input_rejected:/definition/<invalid-utf16>", error?.Message);
    }

    [Fact]
    public void CommentSkippingParserWithInvalidNameYieldsTypedFailure()
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip };
        using var doc = JsonDocument.Parse(
            "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":{\"\\ud800\":1}/*c*/}",
            options);
        var error = PrefixEnvelopeValidator.Validate(
            PrefixEnvelopeValidator.EnvelopeKind.Template, doc.RootElement, out _);
        Assert.Equal("prefix_canonical_input_rejected", error?.Code);
        Assert.Equal("prefix_canonical_input_rejected:/definition/<invalid-utf16>", error?.Message);
    }

    [Fact]
    public void PublicHelpersDoNotThrowOnRelaxedInputs()
    {
        var options = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
        using var doc = JsonDocument.Parse(
            "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":{\"\\ud800\":1,},}",
            options);
        var outcome = CacheContractDigests.ComputeTemplateId(doc.RootElement);
        Assert.Null(outcome.Digest);
        Assert.Equal("prefix_canonical_input_rejected", Assert.Single(outcome.Diagnostics).Code);
    }
}
