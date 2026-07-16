using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests.Ledger;

/// <summary>
/// Guards deep immutability of <see cref="ValidatedLedger"/>: caller-supplied
/// input bytes may be mutated after parsing without altering the ledger's
/// content hash, canonical bytes, or model, and independent byte accessors
/// return independent copies.
/// </summary>
public sealed class LedgerImmutabilityTests
{
    [Fact]
    public void MutatingInputBytes_DoesNotAffectLedgerContent()
    {
        var seed = Fixtures.Bootstrap().ToCanonicalByteArray();
        var buffer = new byte[seed.Length];
        Buffer.BlockCopy(seed, 0, buffer, 0, seed.Length);

        var outcome = LedgerParser.ParseAndValidate(buffer);
        Assert.NotNull(outcome.Ledger);
        var originalSha = outcome.Ledger!.ContentSha256;
        var snapshot = outcome.Ledger.ToCanonicalByteArray();

        // Mutate every byte after parsing.
        for (var i = 0; i < buffer.Length; i++) buffer[i] = 0xFF;

        Assert.Equal(originalSha, outcome.Ledger.ContentSha256);
        Assert.Equal(snapshot, outcome.Ledger.ToCanonicalByteArray());
    }

    [Fact]
    public void ToCanonicalByteArray_ReturnsIndependentCopies()
    {
        var ledger = Fixtures.Bootstrap();
        var a = ledger.ToCanonicalByteArray();
        var b = ledger.ToCanonicalByteArray();
        Assert.Equal(a, b);
        Assert.NotSame(a, b);
        a[0] ^= 0xFF;
        Assert.NotEqual(a[0], b[0]);
        Assert.Equal(a.Length, b.Length);
    }
}
