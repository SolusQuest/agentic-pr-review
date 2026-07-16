using System.Collections.Immutable;

namespace AgenticPrReview.Runtime.Ledger;

public sealed record ParseOutcome(ValidatedLedger? Ledger, ImmutableArray<LedgerDiagnostic> Diagnostics);

public sealed record TransitionOutcome(ImmutableArray<LedgerDiagnostic> Diagnostics);

public sealed record BuildOutcome<T>(T? Value, ImmutableArray<LedgerDiagnostic> Diagnostics) where T : class;

public sealed record CandidateOutcome(ValidatedLedger? Candidate, ImmutableArray<LedgerDiagnostic> Diagnostics);

public sealed record InteractionIdentity(string InteractionId, long InteractionOrdinal);

public sealed class ValidatedLedger
{
    internal ValidatedLedger(LedgerModel model, ImmutableArray<byte> canonicalBytes)
    {
        Model = model;
        CanonicalBytes = canonicalBytes;
        ContentSha256 = ComputeSha256Hex(canonicalBytes);
        ByteLength = canonicalBytes.Length;

        var reserialized = LedgerCanonicalizer.SerializeCanonical(model);
        if (!reserialized.SequenceEqual(canonicalBytes))
        {
            throw new InvalidOperationException("Canonical bytes do not round-trip for the validated ledger model.");
        }
    }

    public LedgerModel Model { get; }
    public ImmutableArray<byte> CanonicalBytes { get; }
    public string ContentSha256 { get; }
    public long ByteLength { get; }

    private static string ComputeSha256Hex(ImmutableArray<byte> bytes)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(bytes.AsSpan());
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed class LedgerDiagnostic
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? CauseCode { get; init; }
}
