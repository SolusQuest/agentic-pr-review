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
}
