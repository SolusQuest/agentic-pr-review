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

        // Structural / duplicate-key scan (System.Text.Json permits duplicate names by default;
        // structural caps must be enforced before its recursion could blow through them).
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
/// Single-pass structural JSON scanner. Enforces depth, per-array length,
/// total property count, duplicate-property, and Unicode invariants on the
/// caller-supplied text. Uses an explicit container stack so that array
/// elements — including nested objects and arrays — are counted correctly
/// and no O(n^2) backscan is required.
/// </summary>
internal static class LedgerJsonScanner
{
    private enum Container { Object, Array }

    private struct Frame
    {
        public Container Kind;
        public int ArrayLength;      // Only meaningful for arrays.
        public HashSet<string>? Keys; // Only allocated for objects.
        public bool AwaitingValue;   // Object: true after ':'; Array: true after '[' / ','.
        public bool AwaitingKey;     // Object: true after '{' or ','.
    }

    public static LedgerDiagnostic? Scan(string text)
    {
        var stack = new Stack<Frame>();
        var totalProperties = 0;
        var i = 0;
        var sawRoot = false;
        while (i < text.Length)
        {
            var ch = text[i];
            if (char.IsWhiteSpace(ch)) { i++; continue; }
            switch (ch)
            {
                case '{':
                {
                    var failure = OnValueStart(stack);
                    if (failure is not null) return failure;
                    if (stack.Count + 1 > LedgerLimits.MaxJsonDepth)
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.JsonDepthExceeded);
                    stack.Push(new Frame
                    {
                        Kind = Container.Object,
                        Keys = new HashSet<string>(StringComparer.Ordinal),
                        AwaitingKey = true,
                    });
                    sawRoot = true;
                    i++;
                    break;
                }
                case '[':
                {
                    var failure = OnValueStart(stack);
                    if (failure is not null) return failure;
                    if (stack.Count + 1 > LedgerLimits.MaxJsonDepth)
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.JsonDepthExceeded);
                    stack.Push(new Frame
                    {
                        Kind = Container.Array,
                        ArrayLength = 0,
                        AwaitingValue = true,
                    });
                    sawRoot = true;
                    i++;
                    break;
                }
                case '}':
                    if (stack.Count == 0 || stack.Peek().Kind != Container.Object)
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidJson);
                    stack.Pop();
                    i++;
                    break;
                case ']':
                    if (stack.Count == 0 || stack.Peek().Kind != Container.Array)
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidJson);
                    stack.Pop();
                    i++;
                    break;
                case ',':
                {
                    if (stack.Count == 0)
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidJson);
                    // Update the top frame in place.
                    var top = stack.Pop();
                    if (top.Kind == Container.Object)
                    {
                        top.AwaitingKey = true;
                        top.AwaitingValue = false;
                    }
                    else
                    {
                        top.AwaitingValue = true;
                    }
                    stack.Push(top);
                    i++;
                    break;
                }
                case ':':
                {
                    if (stack.Count == 0 || stack.Peek().Kind != Container.Object)
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidJson);
                    var top = stack.Pop();
                    top.AwaitingKey = false;
                    top.AwaitingValue = true;
                    stack.Push(top);
                    i++;
                    break;
                }
                case '"':
                {
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
                    if (stack.Count > 0)
                    {
                        var top = stack.Pop();
                        if (top.Kind == Container.Object && top.AwaitingKey)
                        {
                            if (!top.Keys!.Add(unescaped))
                            {
                                stack.Push(top);
                                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.DuplicateJsonProperty);
                            }
                            totalProperties++;
                            if (totalProperties > LedgerLimits.MaxTotalProperties)
                            {
                                stack.Push(top);
                                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.JsonPropertyCountExceeded);
                            }
                            top.AwaitingKey = false;
                            // We expect ':' next; that transition sets AwaitingValue=true.
                            stack.Push(top);
                        }
                        else if (top.Kind == Container.Array && top.AwaitingValue)
                        {
                            top.ArrayLength++;
                            if (top.ArrayLength > LedgerLimits.MaxArrayLength)
                            {
                                stack.Push(top);
                                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.JsonArrayLengthExceeded);
                            }
                            top.AwaitingValue = false;
                            stack.Push(top);
                        }
                        else if (top.Kind == Container.Object && top.AwaitingValue)
                        {
                            // Object property value that happens to be a string.
                            top.AwaitingValue = false;
                            stack.Push(top);
                        }
                        else
                        {
                            stack.Push(top);
                            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidJson);
                        }
                    }
                    sawRoot = true;
                    i = stringEnd + 1;
                    break;
                }
                default:
                {
                    // Value tokens: numbers, true, false, null.
                    var failure = OnValueStart(stack);
                    if (failure is not null) return failure;
                    sawRoot = true;
                    while (i < text.Length && !",}]".Contains(text[i]) && !char.IsWhiteSpace(text[i]))
                        i++;
                    break;
                }
            }
        }
        if (!sawRoot)
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidJson);
        if (stack.Count != 0)
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidJson);
        return null;
    }

    /// <summary>
    /// Applies "a value is starting here" bookkeeping to the current top
    /// frame: counts array elements, clears AwaitingValue on objects.
    /// </summary>
    private static LedgerDiagnostic? OnValueStart(Stack<Frame> stack)
    {
        if (stack.Count == 0) return null;
        var top = stack.Pop();
        if (top.Kind == Container.Array)
        {
            if (top.AwaitingValue)
            {
                top.ArrayLength++;
                if (top.ArrayLength > LedgerLimits.MaxArrayLength)
                {
                    stack.Push(top);
                    return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.JsonArrayLengthExceeded);
                }
                top.AwaitingValue = false;
            }
        }
        else if (top.Kind == Container.Object)
        {
            if (!top.AwaitingValue)
            {
                stack.Push(top);
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidJson);
            }
            top.AwaitingValue = false;
        }
        stack.Push(top);
        return null;
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
