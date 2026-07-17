using System.Text;

namespace AgenticPrReview.Runtime.Prefix;

/// <summary>
/// Safe diagnostic path encoding for the prefix contract (inherits the shared
/// safe-path and schema-position rules from the design contract; #50 owns only
/// its envelope key sets and code mapping). Property names that are not
/// schema-known at their exact position are never echoed into diagnostics.
/// </summary>
internal static class PrefixSafePath
{
    internal const int MaxDiagnosticMessageChars = 256;
    internal const int MaxDiagnosticMessageUtf8Bytes = 1024;

    private const string EmptyName = "<empty-name>";
    private const string InvalidUtf16 = "<invalid-utf16>";
    private const string InvalidNul = "<invalid-nul>";
    private const string InvalidControl = "<invalid-control>";
    private const string UntrustedProperty = "<untrusted-property>";
    private const string PathTruncated = "<path-truncated>";

    private static readonly IReadOnlyDictionary<PrefixEnvelopeValidator.EnvelopeKind, HashSet<string>> EnvelopeRootKeys =
        new Dictionary<PrefixEnvelopeValidator.EnvelopeKind, HashSet<string>>
        {
            [PrefixEnvelopeValidator.EnvelopeKind.Template] = new(StringComparer.Ordinal) { "definition", "schemaVersion", "templateVersion" },
            [PrefixEnvelopeValidator.EnvelopeKind.Policy] = new(StringComparer.Ordinal) { "constraints", "instructions", "policyVersion", "schemaVersion" },
            [PrefixEnvelopeValidator.EnvelopeKind.Tools] = new(StringComparer.Ordinal) { "definitions", "schemaVersion", "toolsetVersion" },
            [PrefixEnvelopeValidator.EnvelopeKind.CacheConfig] = new(StringComparer.Ordinal) { "cacheConfigVersion", "eligibility", "markerPolicy", "schemaVersion", "statelessMode" },
            [PrefixEnvelopeValidator.EnvelopeKind.Adapter] = new(StringComparer.Ordinal) { "adapterBuildVersion", "capabilityProfileVersion", "schemaVersion" },
        };

    private static readonly HashSet<string> ToolWrapperKeys = new(StringComparer.Ordinal)
    {
        "description", "inputSchema", "name", "policyMetadata",
    };

    private static readonly HashSet<string> OpenJsonRoots = new(StringComparer.Ordinal)
    {
        "constraints", "definition", "inputSchema", "policyMetadata",
    };

    /// <summary>
    /// Encodes raw path segments (property names or ASCII-decimal array
    /// indices, root first) into a sanitized RFC 6901 path for the given
    /// envelope kind, applying the six-rule sanitizer table and the greedy
    /// truncation algorithm with final-segment preservation.
    /// </summary>
    internal static string Encode(IReadOnlyList<string> rawSegments, PrefixEnvelopeValidator.EnvelopeKind kind)
    {
        var sanitized = new List<string>(rawSegments.Count);
        var belowOpenJson = false;
        for (var i = 0; i < rawSegments.Count; i++)
        {
            var segment = rawSegments[i];
            if (IsArrayIndex(segment))
            {
                sanitized.Add(segment);
                continue;
            }

            if (belowOpenJson)
            {
                sanitized.Add(SanitizeUnknownName(segment));
                continue;
            }

            if (i == 0)
            {
                if (EnvelopeRootKeys[kind].Contains(segment))
                {
                    sanitized.Add(EscapeRfc6901(segment));
                    belowOpenJson = OpenJsonRoots.Contains(segment);
                }
                else
                {
                    sanitized.Add(SanitizeUnknownName(segment));
                    belowOpenJson = true;
                }

                continue;
            }

            // i == 1 here means the parent was "definitions" (array of tool
            // wrappers); wrapper keys are schema-known, everything below an
            // unknown or open-JSON parent is unknown.
            if (i == 2 && rawSegments[0] == "definitions" && EnvelopeRootKeys[kind].Contains("definitions"))
            {
                if (ToolWrapperKeys.Contains(segment))
                {
                    sanitized.Add(EscapeRfc6901(segment));
                    belowOpenJson = OpenJsonRoots.Contains(segment);
                }
                else
                {
                    sanitized.Add(SanitizeUnknownName(segment));
                    belowOpenJson = true;
                }

                continue;
            }

            sanitized.Add(SanitizeUnknownName(segment));
            belowOpenJson = true;
        }

        return Truncate(sanitized, kind);
    }

    /// <summary>Truncates a sanitized path so code + ":" + path fits the dual caps.</summary>
    private static string Truncate(List<string> segments, PrefixEnvelopeValidator.EnvelopeKind kind)
    {
        _ = kind;
        // The path budget is evaluated against the longest producer code.
        const int codePrefixChars = 31; // prefix_canonical_input_rejected
        var charBudget = MaxDiagnosticMessageChars - codePrefixChars - 1;
        var byteBudget = MaxDiagnosticMessageUtf8Bytes - codePrefixChars - 1;

        var joined = "/" + string.Join('/', segments);
        if (joined.Length <= charBudget && Encoding.UTF8.GetByteCount(joined) <= byteBudget)
        {
            return joined;
        }

        var finalSegment = segments.Count > 0 ? segments[^1] : string.Empty;
        var reserved = ("/" + finalSegment).Length + ("/" + PathTruncated).Length;
        var reservedBytes = Encoding.UTF8.GetByteCount("/" + finalSegment) + Encoding.UTF8.GetByteCount("/" + PathTruncated);

        var prefix = new StringBuilder();
        for (var i = 0; i < segments.Count - 1; i++)
        {
            var candidate = prefix.ToString() + "/" + segments[i];
            if (candidate.Length > charBudget - reserved
                || Encoding.UTF8.GetByteCount(candidate) > byteBudget - reservedBytes)
            {
                break;
            }

            prefix.Clear();
            prefix.Append(candidate);
        }

        return prefix + "/" + PathTruncated + "/" + finalSegment;
    }

    private static string SanitizeUnknownName(string name)
    {
        if (name.Length == 0)
        {
            return EmptyName;
        }

        if (ContainsUnpairedSurrogate(name))
        {
            return InvalidUtf16;
        }

        if (name.IndexOf('\0') >= 0)
        {
            return InvalidNul;
        }

        foreach (var c in name)
        {
            if (c <= 0x1F || c == 0x7F)
            {
                return InvalidControl;
            }
        }

        return UntrustedProperty;
    }

    private static bool ContainsUnpairedSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsArrayIndex(string segment)
    {
        if (segment.Length == 0)
        {
            return false;
        }

        foreach (var c in segment)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static string EscapeRfc6901(string name) =>
        name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
