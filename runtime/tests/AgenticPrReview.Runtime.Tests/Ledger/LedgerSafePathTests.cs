using System.Collections.Generic;
using System.Text;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests.Ledger;

/// <summary>
/// Ledger-side realizations of the M4 Batch #1 shared safe-path oracle:
/// two deep-path vectors from the design contract. Each vector's expected
/// output is byte-exact and matches the frozen literal in
/// <c>docs/20_architecture/session-ledger-and-prefix-contract.md</c>.
/// </summary>
public sealed class LedgerSafePathTests
{
    // codePrefixChars for the ledger_invalid_unicode: producer is 23.
    // Full producer output = "ledger_invalid_unicode:" + safe-path.
    private const string CodePrefix = "ledger_invalid_unicode:";
    private const int CodePrefixChars = 23;

    [Fact]
    public void LedgerDeepPathNoTruncation_MatchesFrozenLiteral()
    {
        // fullSanitizedSegmentCount = 9 (8 unknown ancestors + terminal leaf).
        // charBudget = 256 - 23 = 233; total_chars = 212, no truncation.
        var segments = new List<string>();
        for (var i = 0; i < 9; i++) segments.Add(LedgerSafePath.MarkerUntrustedProperty);
        var encoded = LedgerSafePath.EncodeSegments(segments, CodePrefixChars);
        var full = CodePrefix + encoded;
        var expected = "ledger_invalid_unicode:/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>";
        Assert.Equal(expected, full);
        Assert.Equal(212, full.Length);
    }

    [Fact]
    public void LedgerDeepPathTruncation_MatchesFrozenLiteral()
    {
        // fullSanitizedSegmentCount = 13 (12 unknown ancestors + terminal leaf).
        // reserved_chars = 38, allowance_chars = 195, leadingCount = 9, total_chars = 250.
        var segments = new List<string>();
        for (var i = 0; i < 13; i++) segments.Add(LedgerSafePath.MarkerUntrustedProperty);
        var encoded = LedgerSafePath.EncodeSegments(segments, CodePrefixChars);
        var full = CodePrefix + encoded;
        var expected = "ledger_invalid_unicode:/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<path-truncated>/<untrusted-property>";
        Assert.Equal(expected, full);
        Assert.Equal(250, full.Length);
    }

    [Fact]
    public void SanitizeName_LoneSurrogate_ReturnsInvalidUtf16Marker()
    {
        var s = "\uD800foo"; // Unpaired high surrogate.
        Assert.Equal(LedgerSafePath.MarkerInvalidUtf16, LedgerSafePath.SanitizeName(s, schemaKnown: false));
    }

    [Fact]
    public void SanitizeName_NulCharacter_ReturnsInvalidNulMarker()
    {
        var s = "foo\u0000bar";
        Assert.Equal(LedgerSafePath.MarkerInvalidNul, LedgerSafePath.SanitizeName(s, schemaKnown: false));
    }

    [Fact]
    public void SanitizeName_EmptyName_ReturnsEmptyNameMarker()
    {
        Assert.Equal(LedgerSafePath.MarkerEmptyName, LedgerSafePath.SanitizeName("", schemaKnown: false));
    }

    [Fact]
    public void SanitizeName_UnknownAscii_ReturnsUntrustedPropertyMarker()
    {
        Assert.Equal(LedgerSafePath.MarkerUntrustedProperty, LedgerSafePath.SanitizeName("secretToken", schemaKnown: false));
    }

    [Fact]
    public void SanitizeName_SchemaKnown_EchoesWithJsonPointerEscape()
    {
        Assert.Equal("header", LedgerSafePath.SanitizeName("header", schemaKnown: true));
        Assert.Equal("a~1b~0c", LedgerSafePath.SanitizeName("a/b~c", schemaKnown: true));
    }
}
