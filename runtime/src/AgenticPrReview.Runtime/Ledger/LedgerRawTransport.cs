using System.Text;
using System.Text.Json;

namespace AgenticPrReview.Runtime.Ledger;

/// <summary>
/// Raw-transport stage per Issue #49 section 9. Sub-stages run in order,
/// each with its own fail-fast rule:
///   1a. byte cap                        -> ledger_raw_byte_limit_exceeded
///   1b. UTF-8 decode                    -> ledger_invalid_utf8
///   1c. Complete JSON syntax validation -> ledger_invalid_json
///   1d. Structural scan (only on a syntactically valid document); returns
///       the first hit under the FIXED priority
///       (1) ledger_duplicate_json_property
///       (2) ledger_json_depth_exceeded
///       (3) ledger_json_array_length_exceeded
///       (4) ledger_json_property_count_exceeded
///       even if a lower-priority defect appears earlier in the token stream.
///
/// Unicode-safety (lone surrogates, NUL) is stage 2 and lives in
/// <see cref="LedgerUnicodeSafety"/>; it is NOT emitted from the raw stage.
/// </summary>
internal static class LedgerRawTransport
{
    public static LedgerDiagnostic? Validate(ReadOnlySpan<byte> bytes, out JsonDocument? document)
    {
        document = null;

        // 1a: raw byte cap.
        if (bytes.Length > LedgerLimits.MaxRawBytes)
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.RawByteLimitExceeded);

        // 1b: strict UTF-8 decode (no replacements).
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

        // 1c: complete JSON syntax gate. We parse with a MaxDepth well above
        // the ledger structural cap so that STJ never returns a depth error
        // masquerading as a syntax error: any thrown JsonException at this
        // stage is genuinely a grammar error, not a structural-cap violation.
        JsonDocument parsed;
        try
        {
            var options = new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 1024,
            };
            parsed = JsonDocument.Parse(bytes.ToArray(), options);
        }
        catch (JsonException)
        {
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidJson);
        }

        // 1d: structural scan on the syntactically valid document. Collects
        // ALL candidate structural defects and returns the highest-priority
        // one under the frozen order (dup > depth > array-length > property-count).
        var structuralFailure = LedgerJsonStructuralScanner.Scan(text);
        if (structuralFailure is not null)
        {
            parsed.Dispose();
            return structuralFailure;
        }

        document = parsed;
        return null;
    }
}

/// <summary>
/// Structural JSON scanner: on a syntactically valid JSON text, scans for
/// duplicate properties, depth overflow, over-cap arrays, and over-cap total
/// property count. Collects EVERY candidate defect and then applies the
/// frozen intra-stage priority.
///
/// This scanner assumes valid syntax; syntax errors are the caller's
/// responsibility (see <see cref="LedgerRawTransport.Validate"/> stage 1c).
/// Unicode-safety (NUL, lone surrogate) is out of scope and belongs to
/// <see cref="LedgerUnicodeSafety"/>.
/// </summary>
internal static class LedgerJsonStructuralScanner
{
    private enum Container { Object, Array }

    private struct Frame
    {
        public Container Kind;
        public int ArrayLength;
        public HashSet<string>? Keys;
        public bool AwaitingValue;
        public bool AwaitingKey;
    }

    public static LedgerDiagnostic? Scan(string text)
    {
        var stack = new Stack<Frame>();
        var totalProperties = 0;

        bool hasDuplicate = false;
        bool hasDepth = false;
        bool hasArrayLength = false;
        bool hasPropertyCount = false;

        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (char.IsWhiteSpace(ch)) { i++; continue; }
            switch (ch)
            {
                case '{':
                    OnValueStart(stack, ref hasArrayLength);
                    if (stack.Count + 1 > LedgerLimits.MaxJsonDepth) hasDepth = true;
                    stack.Push(new Frame
                    {
                        Kind = Container.Object,
                        Keys = new HashSet<string>(StringComparer.Ordinal),
                        AwaitingKey = true,
                    });
                    i++;
                    break;
                case '[':
                    OnValueStart(stack, ref hasArrayLength);
                    if (stack.Count + 1 > LedgerLimits.MaxJsonDepth) hasDepth = true;
                    stack.Push(new Frame
                    {
                        Kind = Container.Array,
                        ArrayLength = 0,
                        AwaitingValue = true,
                    });
                    i++;
                    break;
                case '}':
                case ']':
                    if (stack.Count > 0) stack.Pop();
                    i++;
                    break;
                case ',':
                    if (stack.Count > 0)
                    {
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
                    }
                    i++;
                    break;
                case ':':
                    if (stack.Count > 0)
                    {
                        var top = stack.Pop();
                        top.AwaitingKey = false;
                        top.AwaitingValue = true;
                        stack.Push(top);
                    }
                    i++;
                    break;
                case '"':
                {
                    var stringEnd = FindStringEnd(text, i);
                    if (stringEnd < 0)
                    {
                        // Syntax gate is supposed to have caught this; if it
                        // still happens, abort the structural pass and let
                        // the caller treat the input as invalid JSON.
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidJson);
                    }
                    var rawKey = text.Substring(i + 1, stringEnd - i - 1);
                    var unescaped = TryUnescape(rawKey);
                    if (stack.Count > 0)
                    {
                        var top = stack.Pop();
                        if (top.Kind == Container.Object && top.AwaitingKey)
                        {
                            if (unescaped is not null && !top.Keys!.Add(unescaped))
                            {
                                hasDuplicate = true;
                            }
                            totalProperties++;
                            if (totalProperties > LedgerLimits.MaxTotalProperties)
                                hasPropertyCount = true;
                            top.AwaitingKey = false;
                            stack.Push(top);
                        }
                        else if (top.Kind == Container.Object && top.AwaitingValue)
                        {
                            top.AwaitingValue = false;
                            stack.Push(top);
                        }
                        else if (top.Kind == Container.Array && top.AwaitingValue)
                        {
                            top.ArrayLength++;
                            if (top.ArrayLength > LedgerLimits.MaxArrayLength) hasArrayLength = true;
                            top.AwaitingValue = false;
                            stack.Push(top);
                        }
                        else
                        {
                            stack.Push(top);
                        }
                    }
                    i = stringEnd + 1;
                    break;
                }
                default:
                    OnValueStart(stack, ref hasArrayLength);
                    while (i < text.Length && !",}]".Contains(text[i]) && !char.IsWhiteSpace(text[i]))
                        i++;
                    break;
            }
        }

        // Frozen priority: duplicate > depth > array-length > property-count.
        if (hasDuplicate) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.DuplicateJsonProperty);
        if (hasDepth) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.JsonDepthExceeded);
        if (hasArrayLength) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.JsonArrayLengthExceeded);
        if (hasPropertyCount) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.JsonPropertyCountExceeded);
        return null;
    }

    private static void OnValueStart(Stack<Frame> stack, ref bool hasArrayLength)
    {
        if (stack.Count == 0) return;
        var top = stack.Pop();
        if (top.Kind == Container.Array && top.AwaitingValue)
        {
            top.ArrayLength++;
            if (top.ArrayLength > LedgerLimits.MaxArrayLength) hasArrayLength = true;
            top.AwaitingValue = false;
        }
        else if (top.Kind == Container.Object && top.AwaitingValue)
        {
            top.AwaitingValue = false;
        }
        stack.Push(top);
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

    private static string? TryUnescape(string raw)
    {
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
                    sb.Append((char)codeUnit);
                    i += 6;
                    break;
                default: return null;
            }
        }
        return sb.ToString();
    }
}
