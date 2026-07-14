using System.Text;
using System.Text.Json;

namespace AgenticPrReview.Runtime.Ledger;

/// <summary>
/// Raw-transport stage: enforces raw byte cap, UTF-8 validity, JSON validity,
/// structural caps (depth/array-length/property-count), duplicate JSON
/// property names, and invalid Unicode code points (NUL, lone surrogates).
/// </summary>
internal static class LedgerRawTransport
{
    public static LedgerDiagnostic? Validate(ReadOnlySpan<byte> bytes, out JsonDocument? document)
    {
        document = null;

        if (bytes.Length > LedgerLimits.MaxRawBytes)
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.RawByteLimitExceeded);

        // Validate UTF-8 by decoding without replacements. We use Encoding.UTF8 with strict
        // fallbacks so invalid sequences throw a DecoderFallbackException.
        string text;
        try
        {
            var strict = Encoding.GetEncoding("utf-8", new EncoderExceptionFallback(), new DecoderExceptionFallback());
            text = strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUtf8);
        }

        // Duplicate property scan (System.Text.Json permits duplicate names by default;
        // we must detect them ourselves before JSON parsing succeeds).
        var scan = LedgerJsonScanner.Scan(text);
        if (scan is LedgerDiagnostic scanFailure)
            return scanFailure;

        try
        {
            var options = new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = LedgerLimits.MaxJsonDepth,
            };
            document = JsonDocument.Parse(text, options);
        }
        catch (JsonException ex)
        {
            if (ex.Message.Contains("depth", StringComparison.OrdinalIgnoreCase))
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.JsonDepthExceeded);
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidJson);
        }

        return null;
    }
}

/// <summary>
/// Lightweight JSON tokenizer used to enforce structural caps before
/// System.Text.Json parses the document. Only tokens are inspected; the
/// tokenizer does not build any model.
/// </summary>
internal static class LedgerJsonScanner
{
    public static LedgerDiagnostic? Scan(string text)
    {
        var depth = 0;
        var totalProperties = 0;
        var arrayLengthStack = new Stack<int>();
        var propertySetStack = new Stack<HashSet<string>>();
        var expectingKey = false;
        var expectingArrayElement = false;
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (char.IsWhiteSpace(ch)) { i++; continue; }
            switch (ch)
            {
                case '{':
                    depth++;
                    if (depth > LedgerLimits.MaxJsonDepth)
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.JsonDepthExceeded);
                    propertySetStack.Push(new HashSet<string>(StringComparer.Ordinal));
                    expectingKey = true;
                    expectingArrayElement = false;
                    i++;
                    break;
                case '}':
                    depth--;
                    propertySetStack.Pop();
                    expectingKey = false;
                    i++;
                    break;
                case '[':
                    depth++;
                    if (depth > LedgerLimits.MaxJsonDepth)
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.JsonDepthExceeded);
                    arrayLengthStack.Push(0);
                    expectingKey = false;
                    expectingArrayElement = true;
                    i++;
                    break;
                case ']':
                    depth--;
                    arrayLengthStack.Pop();
                    expectingArrayElement = false;
                    i++;
                    break;
                case ',':
                    if (arrayLengthStack.Count > 0 && propertySetStack.Count > 0)
                    {
                        // Ambiguous — infer by top-of-depth: if we just closed an element in an object,
                        // the next token is a key; if in an array, an element. Simpler: check if we're
                        // more recently inside an object or an array by scanning stacks (we push both).
                        // Instead track by looking at what the most recent open bracket was via
                        // heuristics: rely on next non-whitespace char.
                    }
                    // Determine context using position of most recent { or [
                    expectingKey = MostRecentOpen(text, i) == '{';
                    expectingArrayElement = !expectingKey;
                    i++;
                    break;
                case ':':
                    expectingKey = false;
                    i++;
                    break;
                case '"':
                    var stringEnd = FindStringEnd(text, i);
                    if (stringEnd < 0) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidJson);
                    var rawKey = text.Substring(i + 1, stringEnd - i - 1);
                    var unescaped = TryUnescape(rawKey, out var unicodeError);
                    if (unicodeError)
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);
                    if (unescaped is null)
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidJson);
                    if (unescaped.Contains('\0'))
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);
                    if (HasLoneSurrogate(unescaped))
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);
                    if (expectingKey && propertySetStack.Count > 0)
                    {
                        var set = propertySetStack.Peek();
                        if (!set.Add(unescaped))
                            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.DuplicateJsonProperty);
                        totalProperties++;
                        if (totalProperties > LedgerLimits.MaxTotalProperties)
                            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.JsonPropertyCountExceeded);
                        expectingKey = false;
                    }
                    else if (expectingArrayElement && arrayLengthStack.Count > 0)
                    {
                        var count = arrayLengthStack.Pop();
                        count++;
                        if (count > LedgerLimits.MaxArrayLength)
                            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.JsonArrayLengthExceeded);
                        arrayLengthStack.Push(count);
                        expectingArrayElement = false;
                    }
                    i = stringEnd + 1;
                    break;
                default:
                    // Value tokens: numbers, true, false, null. Just skip.
                    // Also count as an array element if applicable.
                    if (expectingArrayElement && arrayLengthStack.Count > 0)
                    {
                        var count = arrayLengthStack.Pop();
                        count++;
                        if (count > LedgerLimits.MaxArrayLength)
                            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.JsonArrayLengthExceeded);
                        arrayLengthStack.Push(count);
                        expectingArrayElement = false;
                    }
                    while (i < text.Length && !",}]".Contains(text[i]) && !char.IsWhiteSpace(text[i])) i++;
                    break;
            }
        }
        return null;
    }

    private static char MostRecentOpen(string text, int endExclusive)
    {
        // Scan back to find the most recent unmatched { or [ before endExclusive.
        var closes = 0;
        for (var i = endExclusive - 1; i >= 0; i--)
        {
            var ch = text[i];
            if (ch == '}' || ch == ']') closes++;
            else if (ch == '{' || ch == '[')
            {
                if (closes == 0) return ch;
                closes--;
            }
            else if (ch == '"')
            {
                // Skip over string contents (backwards). Find matching opening quote.
                i--;
                while (i >= 0 && text[i] != '"') i--;
            }
        }
        return '{';
    }

    private static int FindStringEnd(string text, int startQuote)
    {
        var i = startQuote + 1;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '\\')
            {
                i += 2;
                continue;
            }
            if (ch == '"') return i;
            i++;
        }
        return -1;
    }

    private static string? TryUnescape(string raw, out bool unicodeError)
    {
        unicodeError = false;
        var sb = new StringBuilder(raw.Length);
        var i = 0;
        while (i < raw.Length)
        {
            var ch = raw[i];
            if (ch != '\\')
            {
                sb.Append(ch);
                i++;
                continue;
            }
            if (i + 1 >= raw.Length) return null;
            var next = raw[i + 1];
            switch (next)
            {
                case '"': sb.Append('"'); i += 2; break;
                case '\\': sb.Append('\\'); i += 2; break;
                case '/': sb.Append('/'); i += 2; break;
                case 'b': sb.Append('\b'); i += 2; break;
                case 'f': sb.Append('\f'); i += 2; break;
                case 'n': sb.Append('\n'); i += 2; break;
                case 'r': sb.Append('\r'); i += 2; break;
                case 't': sb.Append('\t'); i += 2; break;
                case 'u':
                    if (i + 6 > raw.Length) return null;
                    if (!ushort.TryParse(raw.AsSpan(i + 2, 4), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var codeUnit))
                        return null;
                    if (codeUnit == 0) { unicodeError = true; return null; }
                    sb.Append((char)codeUnit);
                    i += 6;
                    break;
                default: return null;
            }
        }
        return sb.ToString();
    }

    private static bool HasLoneSurrogate(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return true;
                i++;
            }
            else if (char.IsLowSurrogate(s[i])) return true;
        }
        return false;
    }
}
