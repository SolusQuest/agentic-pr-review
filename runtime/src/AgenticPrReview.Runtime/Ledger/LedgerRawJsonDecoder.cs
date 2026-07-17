using System.Buffers;
using System.Text;
using System.Text.Json;

namespace AgenticPrReview.Runtime.Ledger;

/// <summary>
/// Restricted raw-JSON string decoder for the raw-transport scanner and the
/// Unicode-safety stage. It decodes the content span of a JSON string token — the
/// bytes between the surrounding quotes with escapes still intact, as exposed by
/// <see cref="Utf8JsonReader.ValueSpan"/> — into the exact UTF-16 code-unit sequence
/// the token denotes. Unlike System.Text.Json materialization, unpaired surrogates
/// are preserved as lone UTF-16 code units instead of throwing
/// <see cref="InvalidOperationException"/>; that is what duplicate-property
/// detection and the &lt;invalid-utf16&gt; classification both need.
/// The input has already passed the raw JSON syntax stage, so the escape grammar is
/// well-formed; the decoder still never throws on any input.
/// </summary>
internal static class LedgerRawJsonDecoder
{
    internal static string DecodeStringTokenContent(ReadOnlySpan<byte> rawContent)
    {
        // Fast path: no escapes — the content is direct (already validated) UTF-8.
        if (rawContent.IndexOf((byte)'\\') < 0)
        {
            return Encoding.UTF8.GetString(rawContent);
        }

        var builder = new StringBuilder(rawContent.Length);
        var literalStart = 0;
        var i = 0;
        while (i < rawContent.Length)
        {
            // A 0x5C byte can only be an escape introducer: UTF-8 multi-byte
            // sequences consist of bytes >= 0x80.
            if (rawContent[i] != (byte)'\\')
            {
                i++;
                continue;
            }

            if (i > literalStart)
            {
                builder.Append(Encoding.UTF8.GetString(rawContent.Slice(literalStart, i - literalStart)));
            }

            // Every branch below is unreachable for input that passed the raw syntax
            // stage; they exist only to keep the decoder non-throwing for any caller.
            if (i + 1 >= rawContent.Length)
            {
                builder.Append('\\');
                break;
            }

            switch (rawContent[i + 1])
            {
                case (byte)'"': builder.Append('"'); i += 2; break;
                case (byte)'\\': builder.Append('\\'); i += 2; break;
                case (byte)'/': builder.Append('/'); i += 2; break;
                case (byte)'b': builder.Append('\b'); i += 2; break;
                case (byte)'f': builder.Append('\f'); i += 2; break;
                case (byte)'n': builder.Append('\n'); i += 2; break;
                case (byte)'r': builder.Append('\r'); i += 2; break;
                case (byte)'t': builder.Append('\t'); i += 2; break;
                case (byte)'u':
                    if (i + 6 <= rawContent.Length && TryDecodeHex4(rawContent.Slice(i + 2, 4), out var codeUnit))
                    {
                        // One \uXXXX escape contributes exactly one UTF-16 code unit;
                        // an unpaired surrogate is preserved as a lone code unit.
                        builder.Append((char)codeUnit);
                        i += 6;
                    }
                    else
                    {
                        builder.Append('\\');
                        i += 1;
                    }

                    break;
                default:
                    builder.Append('\\');
                    i += 1;
                    break;
            }

            literalStart = i;
        }

        if (literalStart < rawContent.Length)
        {
            builder.Append(Encoding.UTF8.GetString(rawContent.Slice(literalStart)));
        }

        return builder.ToString();
    }

    private static bool TryDecodeHex4(ReadOnlySpan<byte> hex, out int codeUnit)
    {
        codeUnit = 0;
        foreach (var b in hex)
        {
            var digit = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
                >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
                >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
                _ => -1
            };
            if (digit < 0)
            {
                return false;
            }

            codeUnit = (codeUnit << 4) | digit;
        }

        return true;
    }
}

/// <summary>
/// One node of the lenient JSON tree used by the Unicode-safety stage. Property
/// names and string values are stored as their exact decoded UTF-16 code-unit
/// sequences (lone surrogates preserved); scalar nodes carry only their kind.
/// </summary>
internal sealed class LedgerJsonNode
{
    private LedgerJsonNode(JsonValueKind kind)
    {
        Kind = kind;
    }

    internal JsonValueKind Kind { get; }
    internal string? StringValue { get; private init; }
    internal List<LedgerJsonNode>? Items { get; private init; }
    internal List<LedgerJsonProperty>? Properties { get; private init; }

    internal static LedgerJsonNode Scalar(JsonValueKind kind) => new(kind);

    internal static LedgerJsonNode String(string value) => new(JsonValueKind.String) { StringValue = value };

    internal static LedgerJsonNode Array() => new(JsonValueKind.Array) { Items = new List<LedgerJsonNode>() };

    internal static LedgerJsonNode Object() => new(JsonValueKind.Object) { Properties = new List<LedgerJsonProperty>() };
}

internal readonly struct LedgerJsonProperty
{
    internal LedgerJsonProperty(string name, LedgerJsonNode value)
    {
        Name = name;
        Value = value;
    }

    internal string Name { get; }
    internal LedgerJsonNode Value { get; }
}

/// <summary>
/// Builds the lenient JSON tree for the Unicode-safety stage directly from the raw
/// bytes. The tree tolerates property names and string values that System.Text.Json
/// cannot materialize (unpaired UTF-16 surrogate escapes), so every key is available
/// for the unsigned UTF-16 ordinal sort and terminal-safety checks. Building is
/// iterative (no recursion), so no input shape can overflow the stack. The reader's
/// MaxDepth mirrors the raw scanner: the ledger's own depth cap is enforced there,
/// not here.
/// </summary>
internal static class LedgerJsonTree
{
    internal static LedgerJsonNode? Build(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = LedgerParser.LedgerRawByteLimit
        });

        try
        {
            LedgerJsonNode? root = null;
            var stack = new Stack<LedgerJsonNode>();
            string? pendingName = null;

            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        var container = reader.TokenType == JsonTokenType.StartObject
                            ? LedgerJsonNode.Object()
                            : LedgerJsonNode.Array();
                        Attach(container);
                        stack.Push(container);
                        break;

                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        stack.Pop();
                        break;

                    case JsonTokenType.PropertyName:
                        pendingName = LedgerRawJsonDecoder.DecodeStringTokenContent(TokenContent(ref reader));
                        break;

                    case JsonTokenType.String:
                        Attach(LedgerJsonNode.String(LedgerRawJsonDecoder.DecodeStringTokenContent(TokenContent(ref reader))));
                        break;

                    default:
                        Attach(LedgerJsonNode.Scalar(reader.TokenType switch
                        {
                            JsonTokenType.Number => JsonValueKind.Number,
                            JsonTokenType.True => JsonValueKind.True,
                            JsonTokenType.False => JsonValueKind.False,
                            _ => JsonValueKind.Null
                        }));
                        break;
                }
            }

            return root;

            void Attach(LedgerJsonNode node)
            {
                if (stack.Count == 0)
                {
                    root = node;
                    return;
                }

                var parent = stack.Peek();
                if (parent.Kind == JsonValueKind.Array)
                {
                    parent.Items!.Add(node);
                }
                else
                {
                    parent.Properties!.Add(new LedgerJsonProperty(pendingName ?? string.Empty, node));
                    pendingName = null;
                }
            }
        }
        catch (JsonException)
        {
            // Unreachable for input that passed the raw-transport stage; the caller
            // treats a null tree as "no Unicode verdict" and the JSON-syntax stage
            // owns the ledger_invalid_json classification.
            return null;
        }
    }

    private static ReadOnlySpan<byte> TokenContent(ref Utf8JsonReader reader)
    {
        // For a string/property-name token the span is the raw content between the
        // quotes with escapes intact (verified empirically on net10).
        return reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
    }
}
