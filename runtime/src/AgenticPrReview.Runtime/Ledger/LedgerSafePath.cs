using System.Globalization;
using System.Text;

namespace AgenticPrReview.Runtime.Ledger;

/// <summary>
/// Safe diagnostic path encoding shared with the M4 Batch #1 sidecars. The
/// output is an RFC 6901-style JSON Pointer where every property-name segment
/// is either a schema-known ASCII identifier or one of the fixed literal
/// markers listed below; array-index segments are ASCII decimal. Attacker-
/// controlled content is NEVER echoed into the message.
///
/// Rules for a property-name segment (six-rule sanitizer, closed set):
///   1. empty name           -> literal marker "&lt;empty-name&gt;"
///   2. schema-known         -> the escaped RFC 6901 name (only when the caller
///                              can prove the name is declared at this position)
///   3. lone surrogate       -> literal marker "&lt;invalid-utf16&gt;"
///   4. NUL character        -> literal marker "&lt;invalid-nul&gt;"
///   5. other control char   -> literal marker "&lt;invalid-control&gt;" (schema stage only,
///                              or on identity strings; NOT emitted by the pre-schema
///                              Unicode-safety stage as an independent finalSegment)
///   6. any other name       -> literal marker "&lt;untrusted-property&gt;"
///
/// Dual caps for the emitted diagnostic message (applied AFTER the fixed code
/// prefix): 256 UTF-16 code units total AND 1024 UTF-8 bytes total. When either
/// cap would be exceeded, the greedy truncation algorithm from the design
/// contract emits `/prefix/&lt;path-truncated&gt;/&lt;finalSegment&gt;`.
/// </summary>
internal static class LedgerSafePath
{
    public const string MarkerEmptyName = "<empty-name>";
    public const string MarkerInvalidUtf16 = "<invalid-utf16>";
    public const string MarkerInvalidNul = "<invalid-nul>";
    public const string MarkerInvalidControl = "<invalid-control>";
    public const string MarkerUntrustedProperty = "<untrusted-property>";
    public const string MarkerPathTruncated = "<path-truncated>";

    // Shared dual caps for message (per M4 Batch #1 frozen vocabulary).
    public const int MessageMaxChars = 256;
    public const int MessageMaxUtf8Bytes = 1024;

    /// <summary>
    /// Encode a raw path (list of raw property names / array indices) as a safe
    /// diagnostic path segment, applying the six-rule sanitizer to every
    /// property-name segment. Every property-name segment resolves to
    /// <see cref="MarkerUntrustedProperty"/> unless it matches one of the more
    /// specific markers (invalid-utf16 / invalid-nul / empty-name); the caller
    /// is responsible for supplying an already-sanitized set of schema-known
    /// segments when applicable (see <see cref="EncodeSegments"/>).
    /// </summary>
    public static string Encode(IReadOnlyList<string> rawSegments)
    {
        var sanitized = new List<string>(rawSegments.Count);
        foreach (var raw in rawSegments)
        {
            sanitized.Add(SanitizePropertyOrIndex(raw, schemaKnown: false));
        }
        return EncodeSegments(sanitized, codePrefixChars: 0);
    }

    /// <summary>
    /// Encode a set of pre-sanitized segments (produced by callers that have
    /// resolved schema-known names) into a JSON Pointer, applying dual caps
    /// and the greedy truncation algorithm.
    /// </summary>
    public static string EncodeSegments(IReadOnlyList<string> sanitizedSegments, int codePrefixChars = 0)
    {
        // Pre-check: fully-sanitized untruncated path.
        var untruncated = BuildPointer(sanitizedSegments);
        var charBudget = MessageMaxChars - codePrefixChars;
        var byteBudget = MessageMaxUtf8Bytes - codePrefixChars;
        if (untruncated.Length <= charBudget && Utf8ByteCount(untruncated) <= byteBudget)
            return untruncated;

        // Greedy truncation: reserve "/<finalSegment>" and "/<path-truncated>".
        var finalSegment = sanitizedSegments[sanitizedSegments.Count - 1];
        var reservedTailChars = 1 + finalSegment.Length; // "/" + finalSegment
        var reservedTailBytes = 1 + Utf8ByteCount(finalSegment);
        var truncMarkerChars = 1 + MarkerPathTruncated.Length; // "/<path-truncated>"
        var truncMarkerBytes = 1 + Utf8ByteCount(MarkerPathTruncated);

        var reservedChars = reservedTailChars + truncMarkerChars;
        var reservedBytes = reservedTailBytes + truncMarkerBytes;

        var availableChars = charBudget - reservedChars;
        var availableBytes = byteBudget - reservedBytes;
        if (availableChars < 0) availableChars = 0;
        if (availableBytes < 0) availableBytes = 0;

        // Greedily append leading segments while both budgets are satisfied.
        var prefix = new StringBuilder();
        var usedChars = 0;
        var usedBytes = 0;
        for (var i = 0; i < sanitizedSegments.Count - 1; i++)
        {
            var seg = sanitizedSegments[i];
            var segChars = 1 + seg.Length;
            var segBytes = 1 + Utf8ByteCount(seg);
            if (usedChars + segChars > availableChars) break;
            if (usedBytes + segBytes > availableBytes) break;
            prefix.Append('/');
            prefix.Append(seg);
            usedChars += segChars;
            usedBytes += segBytes;
        }
        return prefix.ToString() + "/" + MarkerPathTruncated + "/" + finalSegment;
    }

    /// <summary>
    /// Apply the six-rule sanitizer to a single raw property-name or array-
    /// index segment. Array indices (all-decimal-digit strings) are returned
    /// verbatim; property names route through the marker rules.
    /// </summary>
    public static string SanitizePropertyOrIndex(string raw, bool schemaKnown)
    {
        // Array indices: bounded ASCII-decimal string.
        if (IsAsciiDecimal(raw)) return raw;
        return SanitizeName(raw, schemaKnown);
    }

    public static string SanitizeName(string raw, bool schemaKnown)
    {
        if (raw.Length == 0) return MarkerEmptyName;

        // Rule 3: unpaired surrogate anywhere.
        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 >= raw.Length || !char.IsLowSurrogate(raw[i + 1])) return MarkerInvalidUtf16;
                i++;
                continue;
            }
            if (char.IsLowSurrogate(ch)) return MarkerInvalidUtf16;
        }
        // Rule 4: NUL character.
        if (raw.IndexOf('\u0000') >= 0) return MarkerInvalidNul;
        // Rule 5: other control character.
        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];
            if ((ch >= '\u0001' && ch <= '\u001F') || ch == '\u007F') return MarkerInvalidControl;
        }
        // Rule 2: schema-known name is echoed with RFC 6901 escaping when caller vouches.
        if (schemaKnown) return EscapeJsonPointer(raw);
        // Rule 6: any other name is untrusted.
        return MarkerUntrustedProperty;
    }

    private static string BuildPointer(IReadOnlyList<string> segments)
    {
        var sb = new StringBuilder(segments.Count * 8);
        foreach (var s in segments)
        {
            sb.Append('/');
            sb.Append(s);
        }
        return sb.ToString();
    }

    private static bool IsAsciiDecimal(string s)
    {
        if (s.Length == 0) return false;
        foreach (var ch in s)
        {
            if (ch < '0' || ch > '9') return false;
        }
        return true;
    }

    private static string EscapeJsonPointer(string s)
    {
        if (s.IndexOf('~') < 0 && s.IndexOf('/') < 0) return s;
        var sb = new StringBuilder(s.Length + 4);
        foreach (var ch in s)
        {
            if (ch == '~') sb.Append("~0");
            else if (ch == '/') sb.Append("~1");
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    private static int Utf8ByteCount(string s) => Encoding.UTF8.GetByteCount(s);
}
