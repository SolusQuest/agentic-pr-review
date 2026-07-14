using System.Runtime.InteropServices;
using System.Text;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests.Ledger;

/// <summary>
/// Enforces that a successful ValidatedLedger is a deeply immutable snapshot.
/// Caller-owned inputs and outputs must never provide a mutation path to the
/// ledger's canonical bytes, model, or content hash.
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
        // Mutate every byte of the caller-owned array after parsing.
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = 0xFF;
        }
        // Ledger must be unaffected.
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
    public void ValidatedLedger_MemoryMarshal_CannotObtainMutableInternalArray()
    {
        // The contractual invariant: MemoryMarshal.TryGetArray must not surface
        // the ledger's internal buffer, so callers cannot mutate ledger state
        // through the array segment.
        var result = LedgerParser.ParseAndValidate(BootstrapBytes());
        Assert.NotNull(result.Ledger);
        var ledger = result.Ledger!;
        var mem = ledger.CanonicalBytes;
        var gotSegment = MemoryMarshal.TryGetArray(mem, out ArraySegment<byte> seg);
        Assert.False(gotSegment, "internal array must not be reachable via MemoryMarshal.TryGetArray");
        Assert.Equal(0, seg.Count);
        Assert.Null(seg.Array);

        // Even after any surface-level access, the ledger's public bytes and
        // hash must remain equal to a freshly-parsed value.
        var golden = ledger.ToCanonicalByteArray();
        var reparsed = LedgerParser.ParseAndValidate(golden);
        Assert.NotNull(reparsed.Ledger);
        Assert.Equal(reparsed.Ledger!.ContentSha256, ledger.ContentSha256);
        Assert.Equal(reparsed.Ledger!.ToCanonicalByteArray(), ledger.ToCanonicalByteArray());
    }

    [Fact]
    public void ValidatedLedger_ReadOnlyMemory_ToArray_ProducesFreshCopy()
    {
        var result = LedgerParser.ParseAndValidate(BootstrapBytes());
        Assert.NotNull(result.Ledger);
        var mem = result.Ledger!.CanonicalBytes;
        var a = mem.ToArray();
        var b = mem.ToArray();
        Assert.Equal(a, b);
        Assert.NotSame(a, b);
    }

    [Fact]
    public void ValidatedLedger_Model_RecordsIsImmutableArray()
    {
        var result = LedgerParser.ParseAndValidate(BootstrapBytes());
        Assert.NotNull(result.Ledger);
        var records = result.Ledger!.Model.Records;
        // ImmutableArray value-type: no add/remove operation exists.
        // Compile-time guarantee, checked here by asserting the type.
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
