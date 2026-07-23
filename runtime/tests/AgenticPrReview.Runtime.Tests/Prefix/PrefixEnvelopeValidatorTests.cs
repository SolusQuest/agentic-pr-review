using System.Text.Json;
using AgenticPrReview.Runtime.Prefix;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Prefix;

public sealed class PrefixEnvelopeValidatorTests
{
    private static PrefixDiagnostic? ValidateTemplate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return PrefixEnvelopeValidator.Validate(
            PrefixEnvelopeValidator.EnvelopeKind.Template, doc.RootElement, out _);
    }

    private static PrefixDiagnostic? ValidateTemplateDeep(string json)
    {
        // The validator's own depth bound must be exercised, not the parser's
        // default MaxDepth of 64.
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 });
        return PrefixEnvelopeValidator.Validate(
            PrefixEnvelopeValidator.EnvelopeKind.Template, doc.RootElement, out _);
    }

    private static PrefixDiagnostic? ValidateToolsDeep(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 });
        return PrefixEnvelopeValidator.Validate(
            PrefixEnvelopeValidator.EnvelopeKind.Tools, doc.RootElement, out _);
    }

    private static string TooManyProperties() =>
        "{" + string.Join(",", Enumerable.Range(0, 257).Select(i => $"\"k{i}\":{i}")) + "}";

    [Fact]
    public void SchemaVersionTwoIsLegal()
    {
        using var doc = JsonDocument.Parse("""{"schemaVersion":2,"templateVersion":3,"definition":{}}""");
        var error = PrefixEnvelopeValidator.Validate(
            PrefixEnvelopeValidator.EnvelopeKind.Template, doc.RootElement, out var validated);
        Assert.Null(error);
        Assert.NotNull(validated);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2_147_483_648L)]
    public void VersionOutOfRangeFails(long version)
    {
        var error = ValidateTemplate("{\"schemaVersion\":" + version + ",\"templateVersion\":3,\"definition\":{}}");
        Assert.Equal("prefix_envelope_invalid", error?.Code);
    }

    [Fact]
    public void ExponentFormIntegerVersionIsAccepted()
    {
        var error = ValidateTemplate("""{"schemaVersion":1e0,"templateVersion":3,"definition":{}}""");
        Assert.Null(error);
    }

    [Fact]
    public void DepthSixtyFourPassesSixtyFiveFails()
    {
        // Depth counts the definition root value as 1, per object/array level.
        string Nest(int depth) => depth == 0 ? "1" : $"[{Nest(depth - 1)}]";
        Assert.Null(ValidateTemplateDeep($$"""{"schemaVersion":1,"templateVersion":1,"definition":{{Nest(64)}}}"""));
        var error = ValidateTemplateDeep($$"""{"schemaVersion":1,"templateVersion":1,"definition":{{Nest(65)}}}""");
        Assert.Equal("prefix_envelope_invalid", error?.Code);
    }

    [Fact]
    public void ParserDepthAboveOneThousandStillFailsInStructureStage()
    {
        string Nest(int depth) => depth == 0 ? "1" : $"[{Nest(depth - 1)}]";
        using var doc = JsonDocument.Parse(
            $"{{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":{Nest(1025)}}}",
            new JsonDocumentOptions { MaxDepth = 2048 });
        var error = PrefixEnvelopeValidator.Validate(
            PrefixEnvelopeValidator.EnvelopeKind.Template, doc.RootElement, out _);
        Assert.Equal("prefix_envelope_invalid", error?.Code);
    }

    [Fact]
    public void PropertyCountBoundIsEnforced()
    {
        string Obj(int n) =>
            "{" + string.Join(",", Enumerable.Range(0, n).Select(i => $"\"k{i}\":{i}")) + "}";
        Assert.Null(ValidateTemplate($$"""{"schemaVersion":1,"templateVersion":1,"definition":{{Obj(256)}}}"""));
        var error = ValidateTemplate($$"""{"schemaVersion":1,"templateVersion":1,"definition":{{Obj(257)}}}""");
        Assert.Equal("prefix_envelope_invalid", error?.Code);
    }

    [Fact]
    public void ArrayItemBoundIsEnforced()
    {
        string Arr(int n) => "[" + string.Join(",", Enumerable.Repeat("1", n)) + "]";
        Assert.Null(ValidateTemplate($$"""{"schemaVersion":1,"templateVersion":1,"definition":{{Arr(1024)}}}"""));
        var error = ValidateTemplate($$"""{"schemaVersion":1,"templateVersion":1,"definition":{{Arr(1025)}}}""");
        Assert.Equal("prefix_envelope_invalid", error?.Code);
    }

    [Fact]
    public void OpenJsonKeysArePreserved()
    {
        using var doc = JsonDocument.Parse("""{"schemaVersion":1,"templateVersion":1,"definition":{"anything":"goes","nested":{"x":[1,2,3]}}}""");
        var error = PrefixEnvelopeValidator.Validate(
            PrefixEnvelopeValidator.EnvelopeKind.Template, doc.RootElement, out var validated);
        Assert.Null(error);
        var canonical = System.Text.Encoding.UTF8.GetString(validated!.CanonicalBytes.AsSpan());
        Assert.Contains("\"anything\":\"goes\"", canonical);
        Assert.Contains("\"x\":[1,2,3]", canonical);
    }

    [Fact]
    public void DuplicatePropertyInEnvelopeRootFailsWithEnvelopeInvalid()
    {
        var error = ValidateTemplate("""{"schemaVersion":1,"schemaVersion":1,"templateVersion":3,"definition":{}}""");
        Assert.Equal("prefix_envelope_invalid", error?.Code);
        Assert.Equal("prefix_envelope_invalid:/schemaVersion", error?.Message);
    }

    [Fact]
    public void NumericUnknownRootFieldPrecedesInvalidUtf16Sentinel()
    {
        var error = ValidateTemplate(
            """{"schemaVersion":1,"templateVersion":1,"definition":{},"\ud800":1,"2":2}""");
        Assert.Equal("prefix_envelope_invalid:/<untrusted-property>", error?.Message);
    }

    [Fact]
    public void ToolWrapperUnknownFieldsUseUnsignedUtf16Order()
    {
        var error = ValidateToolsDeep(
            """{"schemaVersion":1,"toolsetVersion":1,"definitions":[{"name":"t","description":"d","inputSchema":{},"\ud800":1,"2":2}]}""");
        Assert.Equal("prefix_envelope_invalid:/definitions/0/<untrusted-property>", error?.Message);
    }

    [Fact]
    public void SchemaVersionPrecedesEnvelopeSpecificVersion()
    {
        using var doc = JsonDocument.Parse(
            """{"schemaVersion":0,"policyVersion":0,"instructions":"i","constraints":{}}""");
        var error = PrefixEnvelopeValidator.Validate(
            PrefixEnvelopeValidator.EnvelopeKind.Policy, doc.RootElement, out _);
        Assert.Equal("prefix_envelope_invalid:/schemaVersion", error?.Message);
    }

    [Fact]
    public void InvalidUtf16PropertyInEnvelopeRootFailsWithEnvelopeInvalid()
    {
        var error = ValidateTemplate("""{"schemaVersion":1,"templateVersion":1,"definition":{},"\ud800":1}""");
        Assert.Equal("prefix_envelope_invalid", error?.Code);
        Assert.Equal("prefix_envelope_invalid:/<invalid-utf16>", error?.Message);
    }

    [Fact]
    public void DuplicateInvalidUtf16PropertyInEnvelopeRootFailsWithEnvelopeInvalid()
    {
        var error = ValidateTemplate("""{"schemaVersion":1,"templateVersion":1,"definition":{},"\ud800":1,"\ud800":2}""");
        Assert.Equal("prefix_envelope_invalid", error?.Code);
        Assert.Equal("prefix_envelope_invalid:/<invalid-utf16>", error?.Message);
    }

    [Fact]
    public void InvalidUtf16PropertyInToolWrapperFailsWithEnvelopeInvalid()
    {
        var error = ValidateToolsDeep("""{"schemaVersion":1,"toolsetVersion":1,"definitions":[{"name":"t","description":"d","inputSchema":{},"\ud800":1}]}""");
        Assert.Equal("prefix_envelope_invalid", error?.Code);
        Assert.Equal("prefix_envelope_invalid:/definitions/0/<invalid-utf16>", error?.Message);
    }

    [Fact]
    public void DuplicateInvalidUtf16PropertyInToolWrapperFailsWithEnvelopeInvalid()
    {
        var error = ValidateToolsDeep("""{"schemaVersion":1,"toolsetVersion":1,"definitions":[{"name":"t","description":"d","inputSchema":{},"\ud800":1,"\ud800":2}]}""");
        Assert.Equal("prefix_envelope_invalid", error?.Code);
        Assert.Equal("prefix_envelope_invalid:/definitions/0/<invalid-utf16>", error?.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StructuralObjectTraversalUsesUnsignedUtf16OrderRegardlessOfInsertion(bool reverse)
    {
        var fat = TooManyProperties();
        var definition = reverse
            ? $"{{\"a\":[{fat}],\"z\":[0,{fat}]}}"
            : $"{{\"z\":[0,{fat}],\"a\":[{fat}]}}";
        var error = ValidateTemplate(
            $"{{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":{definition}}}");
        Assert.Equal("prefix_envelope_invalid:/definition/<untrusted-property>/0", error?.Message);
    }

    [Fact]
    public void StructuralObjectTraversalSortsNumericNamesLexically()
    {
        var fat = TooManyProperties();
        var error = ValidateTemplate(
            $"{{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":{{\"2\":[0,{fat}],\"10\":[{fat}]}}}}");
        Assert.Equal("prefix_envelope_invalid:/definition/<untrusted-property>/0", error?.Message);
    }

    [Fact]
    public void StructuralArrayTraversalUsesAscendingIndices()
    {
        var fat = TooManyProperties();
        var error = ValidateTemplate(
            $"{{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":[{fat},[{fat}]]}}");
        Assert.Equal("prefix_envelope_invalid:/definition/0", error?.Message);
    }

    [Fact]
    public void ContractOwnedFieldDefectBeatsNestedStructuralBound()
    {
        string Nest(int depth) => depth == 0 ? "0" : $"[{Nest(depth - 1)}]";
        var error = ValidateToolsDeep(
            $"{{\"schemaVersion\":1,\"toolsetVersion\":1,\"definitions\":[{{\"name\":\"t\",\"description\":42,\"inputSchema\":{{\"deep\":{Nest(65)}}}}}]}}");
        Assert.Equal("prefix_envelope_invalid:/definitions/0/description", error?.Message);
    }

    [Fact]
    public void DuplicatePropertyInOpenJsonFailsWithCanonicalRejected()
    {
        var error = ValidateTemplate("""{"schemaVersion":1,"templateVersion":1,"definition":{"a":1,"a":2}}""");
        Assert.Equal("prefix_canonical_input_rejected", error?.Code);
    }

    [Fact]
    public void NulInOpenJsonIsEscapedNotRejected()
    {
        using var doc = JsonDocument.Parse("{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":\"a\\u0000b\"}");
        var error = PrefixEnvelopeValidator.Validate(
            PrefixEnvelopeValidator.EnvelopeKind.Template, doc.RootElement, out var validated);
        Assert.Null(error);
        var canonical = System.Text.Encoding.UTF8.GetString(validated!.CanonicalBytes.AsSpan());
        Assert.Contains("a\\u0000b", canonical);
    }

    [Fact]
    public void AdapterBuildVersionByteCapIsEnforced()
    {
        static string Adapter(string v) =>
            $$"""{"schemaVersion":1,"capabilityProfileVersion":1,"adapterBuildVersion":"{{v}}"}""";
        using var ok = JsonDocument.Parse(Adapter(new string('x', 256)));
        Assert.Null(PrefixEnvelopeValidator.Validate(PrefixEnvelopeValidator.EnvelopeKind.Adapter, ok.RootElement, out _));
        using var over = JsonDocument.Parse(Adapter(new string('x', 257)));
        var error = PrefixEnvelopeValidator.Validate(PrefixEnvelopeValidator.EnvelopeKind.Adapter, over.RootElement, out _);
        Assert.Equal("prefix_identity_invalid", error?.Code);
    }

    [Fact]
    public void LoneSurrogateToolNameFailsAtEmbeddedIdentityStage()
    {
        var error = ValidateToolsDeep(
            """{"schemaVersion":1,"toolsetVersion":1,"definitions":[{"name":"\ud800","description":"d","inputSchema":{}}]}""");
        Assert.Equal("prefix_identity_invalid", error?.Code);
        Assert.Equal("prefix_identity_invalid:/definitions/0/name", error?.Message);
    }

    [Fact]
    public void DuplicateLoneSurrogateToolNamesFailAtStructureStage()
    {
        var error = ValidateToolsDeep(
            """{"schemaVersion":1,"toolsetVersion":1,"definitions":[{"name":"\ud800","description":"d","inputSchema":{}},{"name":"\ud800","description":"d","inputSchema":{}}]}""");
        Assert.Equal("prefix_envelope_invalid", error?.Code);
        Assert.Equal("prefix_envelope_invalid:/definitions/1/name", error?.Message);
    }

    [Fact]
    public void LoneSurrogateAdapterVersionFailsAtEmbeddedIdentityStage()
    {
        using var doc = JsonDocument.Parse(
            """{"schemaVersion":1,"capabilityProfileVersion":1,"adapterBuildVersion":"\ud800"}""");
        var error = PrefixEnvelopeValidator.Validate(
            PrefixEnvelopeValidator.EnvelopeKind.Adapter, doc.RootElement, out _);
        Assert.Equal("prefix_identity_invalid", error?.Code);
        Assert.Equal("prefix_identity_invalid:/adapterBuildVersion", error?.Message);
    }

    [Fact]
    public void RecursiveTraversalSortsRealInvalidNameCodeUnits()
    {
        var fat = TooManyProperties();
        var error = ValidateTemplate(
            $"{{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":{{\"b\":[0,{fat}],\"a\\ud800\":[{fat}]}}}}\n");
        Assert.Equal("prefix_envelope_invalid:/definition/<invalid-utf16>/0", error?.Message);
    }

    [Fact]
    public void RecursiveTraversalOrdersAstralBeforeLoneLowSurrogate()
    {
        var fat = TooManyProperties();
        var error = ValidateTemplate(
            $"{{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":{{\"\\udc00\":[0,{fat}],\"😀\":[{fat}]}}}}");
        Assert.Equal("prefix_envelope_invalid:/definition/<untrusted-property>/0", error?.Message);
    }

    [Fact]
    public void ClosedRootUsesFixedInvalidNameSentinelOrdering()
    {
        var error = ValidateTemplate(
            "{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":{},\"a\\ud800\":1,\"b\":2}");
        Assert.Equal("prefix_envelope_invalid:/<untrusted-property>", error?.Message);
    }

    [Fact]
    public void ClosedToolWrapperUsesFixedInvalidNameSentinelOrdering()
    {
        var error = ValidateToolsDeep(
            "{\"schemaVersion\":1,\"toolsetVersion\":1,\"definitions\":[{" +
            "\"name\":\"t\",\"description\":\"d\",\"inputSchema\":{},\"a\\ud800\":1,\"b\":2}]}");
        Assert.Equal("prefix_envelope_invalid:/definitions/0/<untrusted-property>", error?.Message);
    }

    [Fact]
    public void InvalidNameDoesNotCopyItsLargeSiblingSubtree()
    {
        var payload = new string('x', 1_000_000);
        var error = ValidateTemplate(
            $"{{\"schemaVersion\":1,\"templateVersion\":1,\"definition\":{{\"\\ud800\":0,\"payload\":\"{payload}\"}}}}");
        Assert.Equal("prefix_canonical_input_rejected", error?.Code);
    }

    [Fact]
    public void ToolNamesAreCaseSensitive()
    {
        using var doc = JsonDocument.Parse("""
            {"schemaVersion":1,"toolsetVersion":1,"definitions":[
              {"name":"ReadFile","description":"d","inputSchema":{}},
              {"name":"readfile","description":"d","inputSchema":{}}]}
            """);
        var error = PrefixEnvelopeValidator.Validate(
            PrefixEnvelopeValidator.EnvelopeKind.Tools, doc.RootElement, out _);
        Assert.Null(error);
    }

    [Fact]
    public void EnvelopeCanonicalCapUsesCanonicalBytes()
    {
        // Compute the exact padding from an actual canonicalization instead of
        // hand-counting the wrapper bytes.
        static int CanonicalLength(int pad)
        {
            using var doc = JsonDocument.Parse($$"""{"schemaVersion":1,"templateVersion":3,"definition":"{{new string('x', pad)}}"}""");
            var error = PrefixEnvelopeValidator.Validate(
                PrefixEnvelopeValidator.EnvelopeKind.Template, doc.RootElement, out var validated);
            return error is null ? validated!.CanonicalBytes.Length : -1;
        }

        var padAtCap = -1;
        for (var pad = 262_144 - 100; pad < 262_144; pad++)
        {
            if (CanonicalLength(pad) == 262_144)
            {
                padAtCap = pad;
                break;
            }
        }

        Assert.True(padAtCap >= 0, "no padding length lands exactly on the envelope cap");
        Assert.Equal(262_144, CanonicalLength(padAtCap));
        Assert.Equal(-1, CanonicalLength(padAtCap + 1));
    }

    [Fact]
    public void AdapterEnvelopeV2RequiresRequestContractDigestWhileV1RemainsClosed()
    {
        using var validV2 = JsonDocument.Parse(
            $$"""{"schemaVersion":2,"capabilityProfileVersion":1,"adapterBuildVersion":"deepseek-openai-chat-v1","requestContractSha256":"{{new string('f', 64)}}"}""");
        Assert.Null(PrefixEnvelopeValidator.Validate(
            PrefixEnvelopeValidator.EnvelopeKind.Adapter, validV2.RootElement, out _));

        using var missing = JsonDocument.Parse(
            "{\"schemaVersion\":2,\"capabilityProfileVersion\":1,\"adapterBuildVersion\":\"deepseek-openai-chat-v1\"}");
        Assert.Equal("prefix_envelope_invalid:/requestContractSha256", PrefixEnvelopeValidator.Validate(
            PrefixEnvelopeValidator.EnvelopeKind.Adapter, missing.RootElement, out _)?.Message);

        using var stale = JsonDocument.Parse(
            $$"""{"schemaVersion":1,"capabilityProfileVersion":1,"adapterBuildVersion":"fixture","requestContractSha256":"{{new string('f', 64)}}"}""");
        Assert.Equal("prefix_envelope_invalid:/requestContractSha256", PrefixEnvelopeValidator.Validate(
            PrefixEnvelopeValidator.EnvelopeKind.Adapter, stale.RootElement, out _)?.Message);

        using var future = JsonDocument.Parse(
            "{\"schemaVersion\":3,\"capabilityProfileVersion\":1,\"adapterBuildVersion\":\"future\"}");
        Assert.Equal("prefix_envelope_invalid:/schemaVersion", PrefixEnvelopeValidator.Validate(
            PrefixEnvelopeValidator.EnvelopeKind.Adapter, future.RootElement, out _)?.Message);

        using var invalidDigest = JsonDocument.Parse(
            "{\"schemaVersion\":2,\"capabilityProfileVersion\":1,\"adapterBuildVersion\":\"deepseek-openai-chat-v1\",\"requestContractSha256\":\"BAD\"}");
        Assert.Equal("prefix_envelope_invalid:/requestContractSha256", PrefixEnvelopeValidator.Validate(
            PrefixEnvelopeValidator.EnvelopeKind.Adapter, invalidDigest.RootElement, out _)?.Message);

        using var exact = JsonDocument.Parse(
            $$"""{"schemaVersion":2,"capabilityProfileVersion":1,"adapterBuildVersion":"deepseek-openai-chat-v1","requestContractSha256":"{{new string('f', 64)}}"}""");
        var exactDigest = CacheContractDigests.ComputeAdapterId(exact.RootElement);
        Assert.Equal("71044d2d1685969ce900ccd7ef4b716204cf9a852a5cd8fffc65f105ae6be1fd", exactDigest.Digest);
    }
}
