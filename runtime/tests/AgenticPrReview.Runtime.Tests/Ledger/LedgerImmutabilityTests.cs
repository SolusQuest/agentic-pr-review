using System.Runtime.InteropServices;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests.Ledger;

/// <summary>
/// Enforces that a successful ValidatedLedger is a deeply immutable snapshot.
/// Caller-owned inputs and outputs must never provide a mutation path to the
/// ledger's canonical bytes, model, or content hash through any documented
/// .NET API on the public runtime surface.
/// </summary>
public sealed class LedgerImmutabilityTests
{
    private static byte[] BootstrapBytes()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "protocol", "fixtures", "v1", "provider-session-ledger");
        return File.ReadAllBytes(Path.Combine(root, "bootstrap-minimal.json"));
    }

    [Fact]
    public void ValidatedLedger_MutatingInputBytes_DoesNotChangeLedgerContent()
    {
        var bytes = BootstrapBytes();
        var result = LedgerParser.ParseAndValidate(bytes);
        Assert.NotNull(result.Ledger);
        var originalSha = result.Ledger!.ContentSha256;
        var originalByteLength = result.Ledger.ByteLength;
        var beforeMutation = result.Ledger.ToCanonicalByteArray();
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = 0xFF;
        }
        Assert.Equal(originalSha, result.Ledger.ContentSha256);
        Assert.Equal(originalByteLength, result.Ledger.ByteLength);
        Assert.Equal(beforeMutation, result.Ledger.ToCanonicalByteArray());
    }

    [Fact]
    public void ValidatedLedger_ToCanonicalByteArray_ReturnsIndependentCopy()
    {
        var result = LedgerParser.ParseAndValidate(BootstrapBytes());
        Assert.NotNull(result.Ledger);
        var ledger = result.Ledger!;
        var copy1 = ledger.ToCanonicalByteArray();
        var copy2 = ledger.ToCanonicalByteArray();
        Assert.Equal(copy1, copy2);
        Assert.NotSame(copy1, copy2);
        for (var i = 0; i < copy1.Length; i++) copy1[i] = 0xFF;
        Assert.NotEqual(copy1, ledger.ToCanonicalByteArray());
        Assert.Equal(copy2, ledger.ToCanonicalByteArray());
    }

    [Fact]
    public void ValidatedLedger_CopyCanonicalBytesTo_WritesFullSequence()
    {
        var result = LedgerParser.ParseAndValidate(BootstrapBytes());
        Assert.NotNull(result.Ledger);
        var ledger = result.Ledger!;
        var buffer = new byte[ledger.ByteLength];
        var written = ledger.CopyCanonicalBytesTo(buffer);
        Assert.Equal(ledger.ByteLength, written);
        Assert.Equal(ledger.ToCanonicalByteArray(), buffer);
    }

    [Fact]
    public void ValidatedLedger_CopyCanonicalBytesTo_TooShort_Throws()
    {
        var result = LedgerParser.ParseAndValidate(BootstrapBytes());
        Assert.NotNull(result.Ledger);
        var ledger = result.Ledger!;
        var tooSmall = new byte[ledger.ByteLength - 1];
        Assert.Throws<ArgumentException>(() => ledger.CopyCanonicalBytesTo(tooSmall));
    }

    [Fact]
    public void ValidatedLedger_HasNoPublicReadOnlyMemoryProperty()
    {
        // The class must NOT expose a ReadOnlyMemory<byte>-typed property, because
        // System.Runtime.InteropServices.MemoryMarshal.TryGetMemoryManager /
        // MemoryMarshal.TryGetArray / MemoryMarshal.CreateSpan can otherwise be
        // used to obtain a mutable alias to the internal buffer. Encoding this as
        // a reflection-level assertion guards against re-introducing such a
        // property in a future refactor.
        var byteMemoryType = typeof(ReadOnlyMemory<byte>);
        var byteSpanType = typeof(ReadOnlySpan<byte>);
        var props = typeof(ValidatedLedger).GetProperties(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        foreach (var prop in props)
        {
            Assert.NotEqual(byteMemoryType, prop.PropertyType);
            Assert.NotEqual(byteSpanType, prop.PropertyType);
        }
    }

    [Fact]
    public void ValidatedLedger_Model_ImmutableCollectionsMarshal_DoesNotChangePublicBytesOrHash()
    {
        // ImmutableArray<T> exposes its underlying array through
        // ImmutableCollectionsMarshal.AsArray. This is a documented "unsafe" API,
        // but a well-behaved caller who tries it must at least be unable to
        // change the ledger's public byte view or SHA-256 through it. That
        // invariant holds because ContentSha256 is measured once at construction
        // and ToCanonicalByteArray() returns a copy of the private byte[].
        var result = LedgerParser.ParseAndValidate(BootstrapBytes());
        Assert.NotNull(result.Ledger);
        var ledger = result.Ledger!;
        var originalSha = ledger.ContentSha256;
        var originalBytes = ledger.ToCanonicalByteArray();

        var recordArray = ImmutableCollectionsMarshal.AsArray(ledger.Model.Records);
        Assert.NotNull(recordArray);
        // Attempt a destructive mutation on the underlying array.
        recordArray![0] = ledger.Model.Records[1];

        // Public accessors must still agree; hash and canonical bytes are
        // captured snapshots and cannot be recomputed from the (now-broken)
        // model.
        Assert.Equal(originalSha, ledger.ContentSha256);
        Assert.Equal(originalBytes, ledger.ToCanonicalByteArray());
    }

    [Fact]
    public void ValidatedLedger_Model_RecordsIsImmutableArray()
    {
        var result = LedgerParser.ParseAndValidate(BootstrapBytes());
        Assert.NotNull(result.Ledger);
        var records = result.Ledger!.Model.Records;
        Assert.IsType<ImmutableArray<LedgerRecord>>(records);
        Assert.Equal(2, records.Length);
    }

    [Fact]
    public void ValidatedLedger_Model_NestedCollectionsAreImmutable()
    {
        var result = LedgerParser.ParseAndValidate(BootstrapBytes());
        Assert.NotNull(result.Ledger);
        var record = result.Ledger!.Model.Records[0];
        Assert.NotNull(record.Context);
        Assert.IsType<ImmutableArray<ChangedFileEntry>>(record.Context!.ChangedFiles);
        var oc = result.Ledger.Model.Records[1].Outcome!;
        Assert.IsType<ImmutableArray<LedgerFinding>>(oc.Findings);
        Assert.IsType<ImmutableArray<string>>(oc.Limitations);
    }
}
