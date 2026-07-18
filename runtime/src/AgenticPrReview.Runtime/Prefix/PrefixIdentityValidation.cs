using System.Text.RegularExpressions;
using System.Text.Json;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Prefix;

/// <summary>Validation of shared identity domains (host-declared inputs).</summary>
internal static class PrefixIdentityValidation
{
    private static readonly Regex LowerHex64 = new("^[a-f0-9]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GitSha = new("^([a-f0-9]{40}|[a-f0-9]{64})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EpochId = new("^[A-Za-z0-9_-]{22}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Shared identity-string domain: well-formed UTF-16, non-empty, bounded UTF-8, no controls.</summary>
    internal static bool IsValidIdentity(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var utf8Bytes = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var c = value[index];
            if (c <= 0x1F || c == 0x7F)
            {
                return false;
            }

            if (c <= 0x7F)
            {
                utf8Bytes += 1;
            }
            else if (c <= 0x7FF)
            {
                utf8Bytes += 2;
            }
            else if (char.IsHighSurrogate(c))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }

                utf8Bytes += 4;
                index++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return false;
            }
            else
            {
                utf8Bytes += 3;
            }

            if (utf8Bytes > PrefixBounds.MaxIdentityUtf8Bytes)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates a JSON string identity directly from its raw UTF-8 token.
    /// Once the 256-byte identity cap is crossed, validation terminates without
    /// allocating or decoding the remainder into a managed string.
    /// </summary>
    internal static bool IsValidIdentity(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var enumerator = LenientJsonObjectEnumerator.EnumerateRawToken(
            LenientJsonObjectEnumerator.RawStringValue(element));
        var utf8Bytes = 0;
        var codeUnits = 0;
        while (enumerator.MoveNext(out var unit))
        {
            codeUnits++;
            if (unit <= 0x1F || unit == 0x7F)
            {
                return false;
            }

            if (unit <= 0x7F)
            {
                utf8Bytes++;
            }
            else if (unit <= 0x7FF)
            {
                utf8Bytes += 2;
            }
            else if (char.IsHighSurrogate(unit))
            {
                if (!enumerator.MoveNext(out var low) || !char.IsLowSurrogate(low))
                {
                    return false;
                }

                codeUnits++;
                utf8Bytes += 4;
            }
            else if (char.IsLowSurrogate(unit))
            {
                return false;
            }
            else
            {
                utf8Bytes += 3;
            }

            if (utf8Bytes > PrefixBounds.MaxIdentityUtf8Bytes)
            {
                return false;
            }
        }

        return codeUnits > 0 && !enumerator.Malformed;
    }

    internal static bool IsValidDigest(string? value) => value is not null && LowerHex64.IsMatch(value);

    internal static bool IsValidGitSha(string? value) => value is not null && GitSha.IsMatch(value);

    internal static bool IsValidEpoch(string? value) => value is not null && EpochId.IsMatch(value);

    internal static bool IsModelAliasLiteral(string? value) => string.Equals(value, "latest", StringComparison.Ordinal);

    internal static bool IsValidOrdinal(long value) => value >= 0 && value <= PrefixBounds.MaxInteractionOrdinal;
}
