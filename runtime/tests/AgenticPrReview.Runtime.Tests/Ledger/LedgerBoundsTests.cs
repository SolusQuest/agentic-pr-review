using System.Text;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests.Ledger;

/// <summary>
/// Boundary coverage for the issue #49 §5 caps: values exactly at each cap are accepted
/// and values one past the cap are rejected. The raw-scanner caps (JSON depth, array
/// length, per-object property count) are asserted through the full pipeline: an at-cap
/// document clears the raw stage and fails later (here at the schema stage because the
/// synthetic documents carry no ledger fields), while a cap+1 document fails at the raw
/// stage with the specific structural code. The finding line-number and changed-file
/// counter domains are schema-level and are exercised inside otherwise valid ledgers.
/// </summary>
public sealed class LedgerBoundsTests
{
    // ---- Raw scanner: JSON depth (cap 64, root value at level 1) ----

    [Fact]
    public void JsonDepthAtCapClearsTheRawStage()
    {
        var outcome = LedgerParser.ParseAndValidate(Encoding.UTF8.GetBytes(NestedObjectDocument(64)));

        Assert.Null(outcome.Ledger);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void JsonDepthOnePastCapFailsAtTheRawStage()
    {
        var outcome = LedgerParser.ParseAndValidate(Encoding.UTF8.GetBytes(NestedObjectDocument(65)));

        Assert.Null(outcome.Ledger);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.JsonDepthExceeded, outcome.Diagnostics[0].Code);
    }

    // ---- Raw scanner: array length (cap 4,096 per array) ----

    [Fact]
    public void ArrayLengthAtCapClearsTheRawStage()
    {
        var outcome = LedgerParser.ParseAndValidate(Encoding.UTF8.GetBytes(ArrayDocument(4096)));

        Assert.Null(outcome.Ledger);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void ArrayLengthOnePastCapFailsAtTheRawStage()
    {
        var outcome = LedgerParser.ParseAndValidate(Encoding.UTF8.GetBytes(ArrayDocument(4097)));

        Assert.Null(outcome.Ledger);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.JsonArrayLengthExceeded, outcome.Diagnostics[0].Code);
    }

    // ---- Raw scanner: property count (cap 512 per object) ----

    [Fact]
    public void PropertyCountAtCapClearsTheRawStage()
    {
        var outcome = LedgerParser.ParseAndValidate(Encoding.UTF8.GetBytes(PropertiesDocument(512)));

        Assert.Null(outcome.Ledger);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void PropertyCountOnePastCapFailsAtTheRawStage()
    {
        var outcome = LedgerParser.ParseAndValidate(Encoding.UTF8.GetBytes(PropertiesDocument(513)));

        Assert.Null(outcome.Ledger);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.JsonPropertyCountExceeded, outcome.Diagnostics[0].Code);
    }

    // ---- Schema: finding startLine/endLine domain 1..2_147_483_647 ----

    [Fact]
    public void FindingLineMinimumIsAccepted()
    {
        var outcome = Parse(BootstrapWith("[]", "[" + Finding("1", "1") + "]"));

        Assert.NotNull(outcome.Ledger);
        Assert.Empty(outcome.Diagnostics);
    }

    [Fact]
    public void FindingLineMaximumIsAccepted()
    {
        var outcome = Parse(BootstrapWith("[]", "[" + Finding("2147483647", "2147483647") + "]"));

        Assert.NotNull(outcome.Ledger);
        Assert.Empty(outcome.Diagnostics);
    }

    [Fact]
    public void FindingLineBelowMinimumIsRejected()
    {
        var outcome = Parse(BootstrapWith("[]", "[" + Finding("0", "0") + "]"));

        Assert.Null(outcome.Ledger);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void FindingLineAboveMaximumIsRejected()
    {
        var outcome = Parse(BootstrapWith("[]", "[" + Finding("2147483648", "2147483648") + "]"));

        Assert.Null(outcome.Ledger);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, outcome.Diagnostics[0].Code);
    }

    // ---- Schema: changed-file counters domain 0..1_000_000 ----

    [Fact]
    public void ChangedFileCountersZeroAreAccepted()
    {
        var outcome = Parse(BootstrapWith("[" + ChangedFile(0) + "]", "[]"));

        Assert.NotNull(outcome.Ledger);
        Assert.Empty(outcome.Diagnostics);
    }

    [Fact]
    public void ChangedFileCountersMaximumIsAccepted()
    {
        var outcome = Parse(BootstrapWith("[" + ChangedFile(1_000_000) + "]", "[]"));

        Assert.NotNull(outcome.Ledger);
        Assert.Empty(outcome.Diagnostics);
    }

    [Fact]
    public void ChangedFileCounterAboveMaximumIsRejected()
    {
        var outcome = Parse(BootstrapWith("[" + ChangedFile(1_000_001) + "]", "[]"));

        Assert.Null(outcome.Ledger);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void ChangedFileCounterNegativeIsRejected()
    {
        var outcome = Parse(BootstrapWith("[" + ChangedFile(-1) + "]", "[]"));

        Assert.Null(outcome.Ledger);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, outcome.Diagnostics[0].Code);
    }

    private static ParseOutcome Parse(string json)
    {
        return LedgerParser.ParseAndValidate(Encoding.UTF8.GetBytes(json));
    }

    private static string Finding(string startLine, string endLine)
    {
        return "{\"body\":\"b\",\"category\":\"correctness\",\"confidence\":\"high\",\"endLine\":" + endLine
            + ",\"path\":\"src/a.cs\",\"severity\":\"high\",\"startLine\":" + startLine + ",\"title\":\"t\"}";
    }

    private static string ChangedFile(long counter)
    {
        return "{\"additions\":" + counter + ",\"changes\":" + counter + ",\"deletions\":" + counter
            + ",\"path\":\"src/a.cs\",\"status\":\"modified\"}";
    }

    private static string NestedObjectDocument(int depth)
    {
        var sb = new StringBuilder("{");
        for (var i = 1; i < depth; i++)
        {
            sb.Append("\"a\":{");
        }

        sb.Append("\"a\":0");
        for (var i = 0; i < depth; i++)
        {
            sb.Append('}');
        }

        return sb.ToString();
    }

    private static string ArrayDocument(int length)
    {
        var sb = new StringBuilder("{\"a\":[");
        for (var i = 0; i < length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append('0');
        }

        sb.Append("]}");
        return sb.ToString();
    }

    private static string PropertiesDocument(int count)
    {
        var sb = new StringBuilder("{");
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("\"k").Append(i).Append("\":0");
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static string BootstrapWith(string changedFilesJson, string findingsJson)
    {
        // Byte-level canonical (compact, RFC 8785 key order) minimal ledger over the shared
        // identity baseline with caller-supplied changedFiles/findings fragments.
        return $$"""{"header":{"adapterId":"{{LedgerTestBaseline.AdapterId}}","cacheConfigId":"{{LedgerTestBaseline.CacheConfigId}}","headRepository":"owner/repo","kind":"bootstrap","ledgerEpoch":"bbbbbbbbbbbbbbbbbbbbbb","modelId":"{{LedgerTestBaseline.ModelId}}","policyId":"{{LedgerTestBaseline.PolicyId}}","predecessorLedgerSha256":"bootstrap","providerId":"provider","pullRequest":1,"repository":"owner/repo","sessionEpoch":"aaaaaaaaaaaaaaaaaaaaaa","stateGeneration":0,"templateId":"{{LedgerTestBaseline.TemplateId}}","toolDefinitionId":"{{LedgerTestBaseline.ToolDefinitionId}}","trustedExecutionDomain":"trusted","workflowIdentity":"ci"},"prefixContractVersion":1,"records":[{"cacheContractDigest":"{{LedgerTestBaseline.CacheContractDigest}}","changedFiles":{{changedFilesJson}},"interactionId":"0000000000000000000000000000000000000000000000000000000000000000","interactionOrdinal":0,"reviewedBaseSha":"1111111111111111111111111111111111111111","reviewedHeadSha":"0000000000000000000000000000000000000000","role":"review_context","subjectDigest":"1111111111111111111111111111111111111111111111111111111111111111"},{"findings":{{findingsJson}},"interactionId":"0000000000000000000000000000000000000000000000000000000000000000","interactionOrdinal":0,"limitations":[],"role":"review_outcome","summary":"Summary text."}],"schemaVersion":1}""";
    }
}
