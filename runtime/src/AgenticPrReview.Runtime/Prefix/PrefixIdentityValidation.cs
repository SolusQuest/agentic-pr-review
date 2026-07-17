using System.Text;
using System.Text.RegularExpressions;

namespace AgenticPrReview.Runtime.Prefix;

/// <summary>Validation of shared identity domains (host-declared inputs).</summary>
internal static class PrefixIdentityValidation
{
    private static readonly Regex LowerHex64 = new("^[a-f0-9]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GitSha = new("^([a-f0-9]{40}|[a-f0-9]{64})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EpochId = new("^[A-Za-z0-9_-]{22}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Shared identity-string domain: non-empty, ≤ 256 UTF-8 bytes, no control characters.</summary>
    internal static bool IsValidIdentity(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (Encoding.UTF8.GetByteCount(value) > PrefixBounds.MaxIdentityUtf8Bytes)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c <= 0x1F || c == 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsValidDigest(string? value) => value is not null && LowerHex64.IsMatch(value);

    internal static bool IsValidGitSha(string? value) => value is not null && GitSha.IsMatch(value);

    internal static bool IsValidEpoch(string? value) => value is not null && EpochId.IsMatch(value);

    internal static bool IsModelAliasLiteral(string? value) => string.Equals(value, "latest", StringComparison.Ordinal);

    internal static bool IsValidOrdinal(long value) => value >= 0 && value <= PrefixBounds.MaxInteractionOrdinal;
}
