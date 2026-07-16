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
        var json = "{\"secretToken\":{\"nested\":\"\\uD800\"}}";
        using var document = JsonDocument.Parse(json);
        var diagnostic = LedgerSafePath.ScanForUnicodeViolation(document.RootElement);

        Assert.NotNull(diagnostic);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUnicode, diagnostic!.Code);
        Assert.Equal("ledger_invalid_unicode:/<untrusted-property>/<untrusted-property>", diagnostic.Message);
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
}
