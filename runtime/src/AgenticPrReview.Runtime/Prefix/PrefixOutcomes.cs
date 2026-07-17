using System.Collections.Immutable;
using System.Text.Json;

namespace AgenticPrReview.Runtime.Prefix;

public sealed record DigestOutcome(string? Digest, ImmutableArray<PrefixDiagnostic> Diagnostics);

public sealed record InteractionIdOutcome(string? InteractionId, ImmutableArray<PrefixDiagnostic> Diagnostics);

/// <summary>Predecessor reference for interaction-id derivation.</summary>
public abstract record PredecessorLedgerReference
{
    private PredecessorLedgerReference() { }

    /// <summary>The literal "bootstrap" sentinel (new session or recovery root).</summary>
    public sealed record Bootstrap : PredecessorLedgerReference
    {
        public static readonly Bootstrap Instance = new();

        private Bootstrap() { }
    }

    /// <summary>The actual accepted predecessor ledger hash (reset).</summary>
    public sealed record LedgerHash(string Sha256Hex) : PredecessorLedgerReference;
}
