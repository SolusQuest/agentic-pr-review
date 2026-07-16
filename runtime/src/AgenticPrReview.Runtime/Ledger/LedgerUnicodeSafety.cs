using System.Text.Json;

namespace AgenticPrReview.Runtime.Ledger;

/// <summary>
/// Pre-schema Unicode-safety scan over the parsed JSON tree. Detects the first
/// invalid Unicode element (a lone surrogate or a NUL) in canonical JSON
/// Pointer traversal order (root, then children in canonical property order,
/// then array elements by index) and emits <c>ledger_invalid_unicode</c> with
/// a safe-path suffix produced by <see cref="LedgerSafePath"/>.
/// </summary>
internal static class LedgerUnicodeSafety
{
    public static LedgerDiagnostic? Scan(JsonElement root)
    {
        var pathStack = new List<string>();
        return ScanRecursive(root, pathStack);
    }

    private static LedgerDiagnostic? ScanRecursive(JsonElement element, List<string> pathStack)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
            {
                var s = element.GetString();
                if (s is not null && ContainsInvalidUnicode(s))
                {
                    var safePath = LedgerSafePath.Encode(pathStack);
                    return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode, safePath);
                }
                return null;
            }
            case JsonValueKind.Object:
            {
                // Property names first: a property name with invalid Unicode terminates the scan.
                foreach (var prop in element.EnumerateObject())
                {
                    var name = prop.Name;
                    if (ContainsInvalidUnicode(name))
                    {
                        pathStack.Add(name);
                        var safePath = LedgerSafePath.Encode(pathStack);
                        pathStack.RemoveAt(pathStack.Count - 1);
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode, safePath);
                    }
                }
                foreach (var prop in element.EnumerateObject())
                {
                    pathStack.Add(prop.Name);
                    var childFailure = ScanRecursive(prop.Value, pathStack);
                    pathStack.RemoveAt(pathStack.Count - 1);
                    if (childFailure is not null) return childFailure;
                }
                return null;
            }
            case JsonValueKind.Array:
            {
                var idx = 0;
                foreach (var child in element.EnumerateArray())
                {
                    pathStack.Add(idx.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    var childFailure = ScanRecursive(child, pathStack);
                    pathStack.RemoveAt(pathStack.Count - 1);
                    if (childFailure is not null) return childFailure;
                    idx++;
                }
                return null;
            }
            default:
                return null;
        }
    }

    private static bool ContainsInvalidUnicode(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch == '\u0000') return true;
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return true;
                i++;
                continue;
            }
            if (char.IsLowSurrogate(ch)) return true;
        }
        return false;
    }
}
