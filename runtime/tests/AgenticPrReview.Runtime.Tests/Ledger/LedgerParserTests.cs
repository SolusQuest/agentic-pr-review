using System.Text;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests.Ledger;

public sealed class LedgerParserTests
{
    private const string MinimalBootstrapLedger = /*lang=json,strict*/ """
{
  "header": {
    "adapterId": "adapter",
    "cacheConfigId": "cacheconfig",
    "headRepository": "owner/repo",
    "kind": "bootstrap",
    "ledgerEpoch": "bbbbbbbbbbbbbbbbbbbbbb",
    "modelId": "model-2024-01-01",
    "policyId": "policy",
    "predecessorLedgerSha256": "bootstrap",
    "providerId": "provider",
    "pullRequest": 1,
    "repository": "owner/repo",
    "sessionEpoch": "aaaaaaaaaaaaaaaaaaaaaa",
    "stateGeneration": 0,
    "templateId": "template",
    "toolDefinitionId": "tools",
    "trustedExecutionDomain": "trusted",
    "workflowIdentity": "ci"
  },
  "prefixContractVersion": 1,
  "records": [
    {
      "interactionId": "0000000000000000000000000000000000000000000000000000000000000000",
      "interactionOrdinal": 0,
      "role": "review_context",
      "cacheContractDigest": "c67bf2569b74a5699f670791f30c731d728703d8ce2b6201866175526cd52a85",
      "changedFiles": [],
      "reviewedBaseSha": "1111111111111111111111111111111111111111",
      "reviewedHeadSha": "0000000000000000000000000000000000000000",
      "subjectDigest": "1111111111111111111111111111111111111111111111111111111111111111"
    },
    {
      "interactionId": "0000000000000000000000000000000000000000000000000000000000000000",
      "interactionOrdinal": 0,
      "role": "review_outcome",
      "findings": [],
      "limitations": [],
      "summary": "Summary text."
    }
  ],
  "schemaVersion": 1
}
""";

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
        var json = MinimalBootstrapLedger.Replace("\"prefixContractVersion\": 1,", "\"prefixContractVersion\": 1,\"schemaVersion\": 1,");
        var bytes = Encoding.UTF8.GetBytes(json);
        var outcome = LedgerParser.ParseAndValidate(bytes);

        Assert.Null(outcome.Ledger);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.DuplicateJsonProperty, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void UnsupportedSchemaVersionFails()
    {
        var json = MinimalBootstrapLedger.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2");
        var bytes = Encoding.UTF8.GetBytes(json);
        var outcome = LedgerParser.ParseAndValidate(bytes);

        Assert.Null(outcome.Ledger);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.UnsupportedSchemaVersion, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void UnknownTopLevelFieldFailsSchemaStage()
    {
        var json = MinimalBootstrapLedger.Replace("\"schemaVersion\": 1", "\"extraField\": 1,\"schemaVersion\": 1");
        var bytes = Encoding.UTF8.GetBytes(json);
        var outcome = LedgerParser.ParseAndValidate(bytes);

        Assert.Null(outcome.Ledger);
        Assert.NotEmpty(outcome.Diagnostics);
        Assert.Contains(outcome.Diagnostics, d => d.Code is LedgerDiagnosticCodes.UnknownField or LedgerDiagnosticCodes.SchemaViolation);
    }

    [Fact]
    public void ModelAliasLatestFailsSemanticStage()
    {
        var json = MinimalBootstrapLedger
            .Replace("\"modelId\": \"model-2024-01-01\"", "\"modelId\": \"latest\"")
            .Replace("\"cacheContractDigest\": \"c67bf2569b74a5699f670791f30c731d728703d8ce2b6201866175526cd52a85\"", "\"cacheContractDigest\": \"ed4da62537881c0b787345f55f078e29c2068fead8a170d183d5cdc78ad8631c\"");
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
