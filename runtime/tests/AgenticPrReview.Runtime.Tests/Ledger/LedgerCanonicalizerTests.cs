using System.Text;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests.Ledger;

public sealed class LedgerCanonicalizerTests
{
    [Fact]
    public void ComputeSha256HexProducesLowerHexOfLength64()
    {
        var hex = LedgerCanonicalizer.ComputeSha256Hex(Encoding.UTF8.GetBytes("hello"));
        Assert.Equal(64, hex.Length);
        Assert.Equal(hex, hex.ToLowerInvariant());
    }

    [Fact]
    public void SerializeEnvelopeIsDeterministicAndCanonical()
    {
        var envelope1 = new Dictionary<string, object>
        {
            ["b"] = "beta",
            ["a"] = 1,
        };
        var envelope2 = new Dictionary<string, object>
        {
            ["a"] = 1,
            ["b"] = "beta",
        };
        var b1 = LedgerCanonicalizer.SerializeEnvelope(envelope1);
        var b2 = LedgerCanonicalizer.SerializeEnvelope(envelope2);
        Assert.Equal(b1, b2);
        var text = Encoding.UTF8.GetString(b1);
        Assert.Equal("{\"a\":1,\"b\":\"beta\"}", text);
    }

    [Fact]
    public void SerializeEnvelopeUsesUtf16LexicographicKeyOrdering()
    {
        // BMP: 'a' = 0x0061; 'z' = 0x007A; 'A' = 0x0041.
        // Under unsigned UTF-16 code-unit order: 'A' (0x0041) < 'a' (0x0061) < 'z' (0x007A).
        var envelope = new Dictionary<string, object>
        {
            ["z"] = 1,
            ["A"] = 2,
            ["a"] = 3,
        };
        var text = Encoding.UTF8.GetString(LedgerCanonicalizer.SerializeEnvelope(envelope));
        Assert.Equal("{\"A\":2,\"a\":3,\"z\":1}", text);
    }

    [Fact]
    public void SerializeEnvelopeEscapesOnlyRfc8785RequiredCharacters()
    {
        var envelope = new Dictionary<string, object>
        {
            ["key"] = "quote:\" backslash:\\ newline:\n tab:\t null:\u0000",
        };
        var text = Encoding.UTF8.GetString(LedgerCanonicalizer.SerializeEnvelope(envelope));
        // Should escape ", \, and control characters below \u0020.
        Assert.Contains(@"\""", text);
        Assert.Contains(@"\\", text);
        Assert.Contains(@"\n", text);
        Assert.Contains(@"\t", text);
        Assert.Contains(@"\u0000", text);
        // Non-ASCII / non-control characters must NOT be \u-escaped.
        var envelope2 = new Dictionary<string, object>
        {
            ["k"] = "é 中",
        };
        var literal = Encoding.UTF8.GetString(LedgerCanonicalizer.SerializeEnvelope(envelope2));
        Assert.Contains("é", literal);
        Assert.Contains("中", literal);
    }

    [Fact]
    public void CanonicalizerRoundTripsEveryValidFixture()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "protocol", "fixtures", "v1", "provider-session-ledger");
        foreach (var name in new[]
        {
            "bootstrap-minimal.json",
            "continuation-one-append.json",
            "reset-cache-contract-changed.json",
            "reset-base-changed.json",
            "recovery-predecessor-unavailable.json",
            "continuation-max-interactions.json",
            "continuation-near-byte-limit.json",
        })
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, name));
            var parseResult = LedgerParser.ParseAndValidate(bytes);
            Assert.NotNull(parseResult.Ledger);
            var reserialized = LedgerCanonicalizer.SerializeCanonical(parseResult.Ledger!.Model);
            Assert.Equal(bytes, reserialized);
            Assert.Equal(bytes.Length, parseResult.Ledger.ByteLength);
        }
    }
}

public sealed class LedgerPropertyNameComparerTests
{
    [Fact]
    public void ComparerUsesUnsignedUtf16CodeUnitOrdering()
    {
        // Verify RFC 8785 property ordering rule by comparing arbitrary strings via the
        // internal comparer accessed through envelope serialization. Supplementary-plane
        // code points cannot appear as ProviderSessionLedgerV1 property names, but the
        // comparer must still support them at the primitive level.
        var envelope = new Dictionary<string, object>
        {
            // "\uD83D\uDE00" is emoji 😀 (surrogate pair). Its first code unit is 0xD83D,
            // above 0xFF10 ("full-width digit 0"). Under unsigned UTF-16 order, "\uD83D..."
            // must sort AFTER "\uFF10..." because 0xFF10 > 0xD83D.
            //
            // Wait: 0xD83D < 0xFF10, so emoji key should sort BEFORE fullwidth digit key.
            ["\uD83D\uDE00"] = 1,
            ["\uFF10"] = 2,
        };
        var text = System.Text.Encoding.UTF8.GetString(LedgerCanonicalizer.SerializeEnvelope(envelope));
        // The emoji key (starts with 0xD83D) should appear before the fullwidth digit key (0xFF10)
        // under unsigned UTF-16 lexicographic ordering.
        var emojiIdx = text.IndexOf("\uD83D\uDE00", StringComparison.Ordinal);
        var digitIdx = text.IndexOf("\uFF10", StringComparison.Ordinal);
        Assert.True(emojiIdx >= 0 && digitIdx >= 0, "keys must be present");
        Assert.True(emojiIdx < digitIdx, "emoji surrogate-pair key must sort before fullwidth key");
    }
}
