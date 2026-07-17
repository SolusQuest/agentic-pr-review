using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests.Ledger;

public sealed class LedgerSafePathTests
{
    private const string DeepPathNoTruncation =
        "ledger_invalid_unicode:/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>";

    private const string DeepPathTruncation =
        "ledger_invalid_unicode:/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<path-truncated>/<untrusted-property>";

    [Fact]
    public void DeepPathNoTruncationVectorProducesExactMessage()
    {
        var json = BuildDeepPathJson(segmentCount: 9);
        using var document = JsonDocument.Parse(json);
        var diagnostic = LedgerSafePath.ScanForUnicodeViolation(document.RootElement);

        Assert.NotNull(diagnostic);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUnicode, diagnostic!.Code);
        Assert.Equal(DeepPathNoTruncation, diagnostic.Message);
    }

    [Fact]
    public void DeepPathTruncationVectorProducesExactMessage()
    {
        var json = BuildDeepPathJson(segmentCount: 13);
        using var document = JsonDocument.Parse(json);
        var diagnostic = LedgerSafePath.ScanForUnicodeViolation(document.RootElement);

        Assert.NotNull(diagnostic);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUnicode, diagnostic!.Code);
        Assert.Equal(DeepPathTruncation, diagnostic.Message);
    }

    [Fact]
    public void RootScalarLoneSurrogateProducesCodeOnlyMessage()
    {
        var json = "\"\\uD800\"";
        using var document = JsonDocument.Parse(json);
        var diagnostic = LedgerSafePath.ScanForUnicodeViolation(document.RootElement);

        Assert.NotNull(diagnostic);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUnicode, diagnostic!.Code);
        Assert.Equal("ledger_invalid_unicode:", diagnostic.Message);
    }

    [Fact]
    public void NulInPropertyNameProducesInvalidNulMarker()
    {
        // G4: NUL in a top-level property name is rejected at the Unicode stage.
        var json = "{\"\\u0000\":1}";
        using var document = JsonDocument.Parse(json);
        var diagnostic = LedgerSafePath.ScanForUnicodeViolation(document.RootElement);

        Assert.NotNull(diagnostic);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUnicode, diagnostic!.Code);
        Assert.Equal("ledger_invalid_unicode:/<invalid-nul>", diagnostic.Message);
    }

    [Fact]
    public void UnknownAncestorWithValueLevelSurrogateSanitizesAllSegments()
    {
        // G1: attacker-controlled ancestor, descendant lone-surrogate value.
        var json = "{\"secretToken\":{\"nested\":\"\\uD800\"}}";
        using var document = JsonDocument.Parse(json);
        var diagnostic = LedgerSafePath.ScanForUnicodeViolation(document.RootElement);

        Assert.NotNull(diagnostic);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUnicode, diagnostic!.Code);
        Assert.Equal("ledger_invalid_unicode:/<untrusted-property>/<untrusted-property>", diagnostic.Message);
    }

    [Fact]
    public void ControlCharacterAncestorSanitizesToInvalidControlSegment()
    {
        // G2: ancestor property name contains U+000A (non-NUL control); descendants of an
        // unknown ancestor stay <untrusted-property>.
        var json = "{\"attacker\\ncontrolled\":{\"nestedProp\":\"\\uD800\"}}";
        using var document = JsonDocument.Parse(json);
        var diagnostic = LedgerSafePath.ScanForUnicodeViolation(document.RootElement);

        Assert.NotNull(diagnostic);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUnicode, diagnostic!.Code);
        Assert.Equal("ledger_invalid_unicode:/<invalid-control>/<untrusted-property>", diagnostic.Message);
    }

    [Fact]
    public void LoneSurrogatePropertyNameAtTopLevelProducesInvalidUtf16Segment()
    {
        // G3: lone-surrogate property name at the top level; parent pointer is empty.
        // System.Text.Json cannot materialize the name; the scan classifies it as the
        // terminal <invalid-utf16> property-name violation.
        var json = "{\"\\uD800\":1}";
        using var document = JsonDocument.Parse(json);
        var diagnostic = LedgerSafePath.ScanForUnicodeViolation(document.RootElement);

        Assert.NotNull(diagnostic);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUnicode, diagnostic!.Code);
        Assert.Equal("ledger_invalid_unicode:/<invalid-utf16>", diagnostic.Message);
    }

    [Fact]
    public void EmptyPropertyNameAtTopLevelReachesSchemaStageAsUnknownField()
    {
        // G5: an empty property name is well-formed UTF-16, so it passes the Unicode stage
        // and is rejected by additionalProperties as ledger_unknown_field with the
        // <empty-name> safe-path marker.
        var json = MinimalBootstrapJson().Insert(1, "\"\":1,");
        var outcome = LedgerParser.ParseAndValidate(Encoding.UTF8.GetBytes(json));

        Assert.Null(outcome.Ledger);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.UnknownField, outcome.Diagnostics[0].Code);
        Assert.Equal("ledger_unknown_field:/<empty-name>", outcome.Diagnostics[0].Message);
    }

    [Fact]
    public void SchemaKnownAncestorChainEchoesRealPropertyNames()
    {
        // G6: a fully schema-known ancestor chain keeps the real property names.
        var json = MinimalBootstrapJson().Replace("\"workflowIdentity\":\"ci\"", "\"workflowIdentity\":\"\\uD800\"");
        using var document = JsonDocument.Parse(json);
        var diagnostic = LedgerSafePath.ScanForUnicodeViolation(document.RootElement);

        Assert.NotNull(diagnostic);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUnicode, diagnostic!.Code);
        Assert.Equal("ledger_invalid_unicode:/header/workflowIdentity", diagnostic.Message);
    }

    [Fact]
    public void WellFormedSurrogatePairInSchemaValidStringIsAccepted()
    {
        // G7: a well-formed surrogate pair in a schema-valid string passes the Unicode
        // stage, and canonical serialization preserves it byte-exact (the restore
        // pipeline includes the canonical byte-identity check).
        var json = MinimalBootstrapJson().Replace("Summary text.", "Summary \U0001F600 text.");
        var bytes = Encoding.UTF8.GetBytes(json);

        using var document = JsonDocument.Parse(bytes);
        Assert.Null(LedgerSafePath.ScanForUnicodeViolation(document.RootElement));

        var outcome = LedgerParser.ParseAndValidate(bytes);
        Assert.NotNull(outcome.Ledger);
        Assert.Empty(outcome.Diagnostics);
    }

    [Fact]
    public void V1UnknownAncestorChainSanitizesEverySegment()
    {
        // V1 (shared-unknown-ancestor-with-value-level-surrogate): the ledger schema
        // declares none of a/b/c, which is the V1 hypothetical-schema shape; expected
        // untruncated safe path /<untrusted-property>x3 with the ledger code prefix.
        var json = "{\"a\":{\"b\":{\"c\":\"\\uD800\"}}}";
        using var document = JsonDocument.Parse(json);
        var diagnostic = LedgerSafePath.ScanForUnicodeViolation(document.RootElement);

        Assert.NotNull(diagnostic);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUnicode, diagnostic!.Code);
        Assert.Equal(
            "ledger_invalid_unicode:/<untrusted-property>/<untrusted-property>/<untrusted-property>",
            diagnostic.Message);
    }

    [Fact]
    public void V2TerminalInvalidUtf16InUnknownPropertyName()
    {
        // V2 (shared-terminal-invalid-utf16-in-unknown-property-name): expected safe path
        // /<untrusted-property>/<invalid-utf16> with the ledger code prefix.
        var json = "{\"a\":{\"\\uD800\":1}}";
        using var document = JsonDocument.Parse(json);
        var diagnostic = LedgerSafePath.ScanForUnicodeViolation(document.RootElement);

        Assert.NotNull(diagnostic);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUnicode, diagnostic!.Code);
        Assert.Equal("ledger_invalid_unicode:/<untrusted-property>/<invalid-utf16>", diagnostic.Message);
    }

    [Fact]
    public void V3ResolverUnionChildPositionOnTheLedgerHeaderOneOf()
    {
        // V3 (shared-resolver-union-child-position): the C# SchemaResolver is bound to
        // the embedded ledger schema and cannot be fed V3's hypothetical oneOf schema,
        // so the vector's assertion points are realized against the ledger schema's own
        // oneOf union position (the "header" property, whose position is the union of
        // the four header variant branches plus the closed container properties).
        // Analog mapping: payload <-> a key declared by every branch (sessionEpoch);
        // branch-only beta <-> resetReason (reset branch) / recoveryReason (recovery_root
        // branch); extraneous <-> a key no branch declares.
        var root = LedgerSafePath.RootSchemaPosition;

        // V3: resolveProperty(rootPos, "payload") is schema-known.
        var header = LedgerSafePath.ResolveProperty(root, "header");
        Assert.True(header.SchemaKnown);
        var union = header.ChildSchemaPosition;

        // V3: the union child of a key declared by all branches is known.
        Assert.True(LedgerSafePath.ResolveProperty(union, "sessionEpoch").SchemaKnown);

        // V3: the union child of a key declared by exactly one branch is known
        // (resolveProperty(P_payload, "beta")).
        var resetReason = LedgerSafePath.ResolveProperty(union, "resetReason");
        Assert.True(resetReason.SchemaKnown);

        // V3: that child position is observationally equivalent to UnknownPosition
        // (subsequent resolution on it knows nothing).
        Assert.False(LedgerSafePath.ResolveProperty(resetReason.ChildSchemaPosition, "anything").SchemaKnown);

        // V3: resolveProperty(P_payload, "extraneous") is not schema-known.
        Assert.False(LedgerSafePath.ResolveProperty(union, "extraneous").SchemaKnown);

        // V3 per-branch assertions (branchA knows alpha only / branchB knows beta):
        // among the union children, a single-branch key is known on exactly the closed
        // container child plus its declaring branch child.
        var composite = Assert.IsType<CompositeSchemaPosition>(union);
        var resetKnown = 0;
        var recoveryKnown = 0;
        var sessionKnown = 0;
        var extraneousKnown = 0;
        foreach (var child in composite.Children)
        {
            if (LedgerSafePath.ResolveProperty(child, "resetReason").SchemaKnown) resetKnown++;
            if (LedgerSafePath.ResolveProperty(child, "recoveryReason").SchemaKnown) recoveryKnown++;
            if (LedgerSafePath.ResolveProperty(child, "sessionEpoch").SchemaKnown) sessionKnown++;
            if (LedgerSafePath.ResolveProperty(child, "extraneous").SchemaKnown) extraneousKnown++;
        }

        Assert.Equal(2, resetKnown);     // container + resetHeader branch
        Assert.Equal(2, recoveryKnown);  // container + recoveryRootHeader branch
        Assert.Equal(5, sessionKnown);   // container + all four variant branches
        Assert.Equal(0, extraneousKnown);

        // V3 terminal scan: the fully trusted ancestor chain keeps schema-known names
        // (the [payload, beta] segment expectation), here for a value-level NUL.
        using var known = JsonDocument.Parse("{\"header\":{\"resetReason\":\"\\u0000\"}}");
        var knownDiagnostic = LedgerSafePath.ScanForUnicodeViolation(known.RootElement);
        Assert.NotNull(knownDiagnostic);
        Assert.Equal("ledger_invalid_unicode:/header/resetReason", knownDiagnostic!.Message);

        // A key no branch declares sanitizes to <untrusted-property> below the known parent.
        using var unknown = JsonDocument.Parse("{\"header\":{\"extraneous\":\"\\u0000\"}}");
        var unknownDiagnostic = LedgerSafePath.ScanForUnicodeViolation(unknown.RootElement);
        Assert.NotNull(unknownDiagnostic);
        Assert.Equal("ledger_invalid_unicode:/header/<untrusted-property>", unknownDiagnostic!.Message);
    }

    private static string BuildDeepPathJson(int segmentCount)
    {
        // The chain is a single unknown top-level property whose value is the nested chain.
        var sb = new StringBuilder();
        sb.Append('{');
        for (var i = 0; i < segmentCount - 1; i++)
        {
            sb.Append("\"a").Append(i).Append("\":{");
        }

        sb.Append("\"leaf\":\"\\uD800\"");
        for (var i = 0; i < segmentCount - 1; i++)
        {
            sb.Append('}');
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static string MinimalBootstrapJson()
    {
        // Byte-level canonical (compact, RFC 8785 key order) minimal ledger over the shared
        // identity baseline; mirrors the committed bootstrap-minimal.json fixture.
        return $$"""{"header":{"adapterId":"{{LedgerTestBaseline.AdapterId}}","cacheConfigId":"{{LedgerTestBaseline.CacheConfigId}}","headRepository":"owner/repo","kind":"bootstrap","ledgerEpoch":"bbbbbbbbbbbbbbbbbbbbbb","modelId":"{{LedgerTestBaseline.ModelId}}","policyId":"{{LedgerTestBaseline.PolicyId}}","predecessorLedgerSha256":"bootstrap","providerId":"provider","pullRequest":1,"repository":"owner/repo","sessionEpoch":"aaaaaaaaaaaaaaaaaaaaaa","stateGeneration":0,"templateId":"{{LedgerTestBaseline.TemplateId}}","toolDefinitionId":"{{LedgerTestBaseline.ToolDefinitionId}}","trustedExecutionDomain":"trusted","workflowIdentity":"ci"},"prefixContractVersion":1,"records":[{"cacheContractDigest":"{{LedgerTestBaseline.CacheContractDigest}}","changedFiles":[],"interactionId":"0000000000000000000000000000000000000000000000000000000000000000","interactionOrdinal":0,"reviewedBaseSha":"1111111111111111111111111111111111111111","reviewedHeadSha":"0000000000000000000000000000000000000000","role":"review_context","subjectDigest":"1111111111111111111111111111111111111111111111111111111111111111"},{"findings":[],"interactionId":"0000000000000000000000000000000000000000000000000000000000000000","interactionOrdinal":0,"limitations":[],"role":"review_outcome","summary":"Summary text."}],"schemaVersion":1}""";
    }
}
