using System.Collections.Immutable;
using System.Text.Json;

namespace AgenticPrReview.Runtime.Prefix;

/// <summary>A cache-contract envelope that passed validation and canonicalization.</summary>
internal sealed class ValidatedEnvelope
{
    internal ValidatedEnvelope(JsonElement raw, ImmutableArray<byte> canonicalBytes, string digest)
    {
        Raw = raw.Clone();
        CanonicalBytes = canonicalBytes;
        Digest = digest;
    }

    /// <summary>Raw envelope JSON (deep-cloned on construction).</summary>
    internal JsonElement Raw { get; }

    internal ImmutableArray<byte> CanonicalBytes { get; }

    /// <summary>Lowercase hex digestId over the canonical envelope bytes.</summary>
    internal string Digest { get; }
}
