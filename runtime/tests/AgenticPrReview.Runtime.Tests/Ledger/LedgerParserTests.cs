using System.Text;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests.Ledger;

public sealed class LedgerParserTests
{
    // Byte-level canonical (compact, RFC 8785 key order) minimal ledger over the shared
    // identity baseline; the parser's canonical-form stage compares raw bytes.
    private static readonly string MinimalBootstrapLedger = $$"""{"header":{"adapterId":"{{LedgerTestBaseline.AdapterId}}","cacheConfigId":"{{LedgerTestBaseline.CacheConfigId}}","headRepository":"owner/repo","kind":"bootstrap","ledgerEpoch":"bbbbbbbbbbbbbbbbbbbbbb","modelId":"{{LedgerTestBaseline.ModelId}}","policyId":"{{LedgerTestBaseline.PolicyId}}","predecessorLedgerSha256":"bootstrap","providerId":"provider","pullRequest":1,"repository":"owner/repo","sessionEpoch":"aaaaaaaaaaaaaaaaaaaaaa","stateGeneration":0,"templateId":"{{LedgerTestBaseline.TemplateId}}","toolDefinitionId":"{{LedgerTestBaseline.ToolDefinitionId}}","trustedExecutionDomain":"trusted","workflowIdentity":"ci"},"prefixContractVersion":1,"records":[{"cacheContractDigest":"{{LedgerTestBaseline.CacheContractDigest}}","changedFiles":[],"interactionId":"0000000000000000000000000000000000000000000000000000000000000000","interactionOrdinal":0,"reviewedBaseSha":"1111111111111111111111111111111111111111","reviewedHeadSha":"0000000000000000000000000000000000000000","role":"review_context","subjectDigest":"1111111111111111111111111111111111111111111111111111111111111111"},{"findings":[],"interactionId":"0000000000000000000000000000000000000000000000000000000000000000","interactionOrdinal":0,"limitations":[],"role":"review_outcome","summary":"Summary text."}],"schemaVersion":1}""";

    [Fact]
    public void MinimalBootstrapLedgerParsesSuccessfully()
    {
        var bytes = Encoding.UTF8.GetBytes(MinimalBootstrapLedger);
        var outcome = LedgerParser.ParseAndValidate(bytes);

        Assert.NotNull(outcome.Ledger);
        Assert.Empty(outcome.Diagnostics);
        Assert.Equal("bootstrap", outcome.Ledger!.Model.Header.Kind);
    }

    [Fact]
    public void RawByteLimitExceededFailsFirst()
    {
        var bytes = new byte[LedgerParser.LedgerRawByteLimit + 1];
        Array.Fill<byte>(bytes, 0x20);
        var outcome = LedgerParser.ParseAndValidate(bytes);

        Assert.Null(outcome.Ledger);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.RawByteLimitExceeded, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void LeadingBomFailsAsInvalidUtf8()
    {
        var payload = Encoding.UTF8.GetBytes(MinimalBootstrapLedger);
        var bytes = new byte[payload.Length + 3];
        bytes[0] = 0xEF;
        bytes[1] = 0xBB;
        bytes[2] = 0xBF;
        Array.Copy(payload, 0, bytes, 3, payload.Length);

        var outcome = LedgerParser.ParseAndValidate(bytes);

        Assert.Null(outcome.Ledger);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUtf8, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void InvalidJsonFails()
    {
        var bytes = Encoding.UTF8.GetBytes("{not json");
        var outcome = LedgerParser.ParseAndValidate(bytes);

        Assert.Null(outcome.Ledger);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.InvalidJson, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void DuplicateJsonPropertyFails()
    {
        var json = MinimalBootstrapLedger.Replace("\"prefixContractVersion\":1,", "\"prefixContractVersion\":1,\"schemaVersion\":1,");
        var bytes = Encoding.UTF8.GetBytes(json);
        var outcome = LedgerParser.ParseAndValidate(bytes);

        Assert.Null(outcome.Ledger);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.DuplicateJsonProperty, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void UnsupportedSchemaVersionFails()
    {
        var json = MinimalBootstrapLedger.Replace("\"schemaVersion\":1", "\"schemaVersion\":2");
        var bytes = Encoding.UTF8.GetBytes(json);
        var outcome = LedgerParser.ParseAndValidate(bytes);

        Assert.Null(outcome.Ledger);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.UnsupportedSchemaVersion, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void UnknownTopLevelFieldFailsSchemaStage()
    {
        var json = MinimalBootstrapLedger.Replace("\"schemaVersion\":1", "\"extraField\":1,\"schemaVersion\":1");
        var bytes = Encoding.UTF8.GetBytes(json);
        var outcome = LedgerParser.ParseAndValidate(bytes);

        Assert.Null(outcome.Ledger);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.UnknownField, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void ModelAliasLatestFailsSemanticStage()
    {
        var json = MinimalBootstrapLedger
            .Replace($"\"modelId\":\"{LedgerTestBaseline.ModelId}\"", "\"modelId\":\"latest\"")
            .Replace(
                $"\"cacheContractDigest\":\"{LedgerTestBaseline.CacheContractDigest}\"",
                $"\"cacheContractDigest\":\"{LedgerTestBaseline.ModelAliasCacheContractDigest}\"");
        var bytes = Encoding.UTF8.GetBytes(json);
        var outcome = LedgerParser.ParseAndValidate(bytes);

        Assert.Null(outcome.Ledger);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.ModelAliasLiteral, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void NulInSummaryFailsUnicodeStage()
    {
        var json = MinimalBootstrapLedger.Replace("Summary text.", "Sum\\u0000mary");
        var bytes = Encoding.UTF8.GetBytes(json);
        var outcome = LedgerParser.ParseAndValidate(bytes);

        Assert.Null(outcome.Ledger);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUnicode, outcome.Diagnostics[0].Code);
    }

    // ---- Mathematical-integer number forms (schema draft-07 numeric equality) ----
    // JsonSchema.Net accepts 1.0 / 1e0 / 0e0 / 0.0 for {"type":"integer","const":1}
    // (verified empirically on 9.2.2), so those tokens must materialize without a
    // FormatException and then fail the canonical byte comparison; 2e0 fails const.

    [Fact]
    public void SchemaVersionDecimalFormMaterializesAndFailsCanonicalStage()
    {
        var json = MinimalBootstrapLedger.Replace("\"schemaVersion\":1", "\"schemaVersion\":1.0");
        var bytes = Encoding.UTF8.GetBytes(json);
        var outcome = LedgerParser.ParseAndValidate(bytes);

        Assert.Null(outcome.Ledger);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.NonCanonical, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void SchemaVersionExponentFormMaterializesAndFailsCanonicalStage()
    {
        var json = MinimalBootstrapLedger.Replace("\"schemaVersion\":1", "\"schemaVersion\":1e0");
        var bytes = Encoding.UTF8.GetBytes(json);
        var outcome = LedgerParser.ParseAndValidate(bytes);

        Assert.Null(outcome.Ledger);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.NonCanonical, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void SchemaVersionTwoExponentFormFailsSchemaStage()
    {
        // 2e0 is mathematically integral, so TryGetInt64-based version routing does not
        // fire; the schema's const 1 rejects it as ledger_schema_violation.
        var json = MinimalBootstrapLedger.Replace("\"schemaVersion\":1", "\"schemaVersion\":2e0");
        var bytes = Encoding.UTF8.GetBytes(json);
        var outcome = LedgerParser.ParseAndValidate(bytes);

        Assert.Null(outcome.Ledger);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void StateGenerationZeroExponentFormMaterializesAndFailsCanonicalStage()
    {
        var json = MinimalBootstrapLedger.Replace("\"stateGeneration\":0", "\"stateGeneration\":0e0");
        var bytes = Encoding.UTF8.GetBytes(json);
        var outcome = LedgerParser.ParseAndValidate(bytes);

        Assert.Null(outcome.Ledger);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.NonCanonical, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void InteractionOrdinalDecimalFormMaterializesAndFailsCanonicalStage()
    {
        var json = MinimalBootstrapLedger.Replace("\"interactionOrdinal\":0", "\"interactionOrdinal\":0.0");
        var bytes = Encoding.UTF8.GetBytes(json);
        var outcome = LedgerParser.ParseAndValidate(bytes);

        Assert.Null(outcome.Ledger);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.NonCanonical, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void BoundedFieldExponentFormMaterializesAndFailsCanonicalStage()
    {
        // pullRequest is schema-bounded to 1..2147483647; 1e0 passes the schema and
        // materializes to 1, then the raw-vs-canonical byte comparison rejects it.
        var json = MinimalBootstrapLedger.Replace("\"pullRequest\":1", "\"pullRequest\":1e0");
        var bytes = Encoding.UTF8.GetBytes(json);
        var outcome = LedgerParser.ParseAndValidate(bytes);

        Assert.Null(outcome.Ledger);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.NonCanonical, outcome.Diagnostics[0].Code);
    }

    // ---- Duplicate detection over decoded property names ----

    [Fact]
    public void DuplicateEscapedSurrogatePropertyNamesFailAsDuplicates()
    {
        // Both escapes decode to the same lone-surrogate UTF-16 code unit; duplicate
        // detection runs on the decoded sequence at the raw stage, before the Unicode
        // stage could classify the name.
        var bytes = Encoding.UTF8.GetBytes("{\"\\uD800\":1,\"\\ud800\":2}");
        var outcome = LedgerParser.ParseAndValidate(bytes);

        Assert.Null(outcome.Ledger);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.DuplicateJsonProperty, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void InvalidUtf16KeySortsBeforeHigherSiblingAtUnicodeStage()
    {
        // Multi-defect object: key  has a lone-surrogate VALUE, key \uD800 is a
        // lone-surrogate NAME. Unsigned UTF-16 ordinal sorts D800 before E000, so the
        // terminal property-name check on \uD800 fires before the value scan of .
        var bytes = Encoding.UTF8.GetBytes("{\"\\uE000\":\"\\uD800\",\"\\uD800\":1}");
        var outcome = LedgerParser.ParseAndValidate(bytes);

        Assert.Null(outcome.Ledger);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUnicode, outcome.Diagnostics[0].Code);
        Assert.Equal("ledger_invalid_unicode:/<invalid-utf16>", outcome.Diagnostics[0].Message);
    }
}
