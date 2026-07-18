using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace AgenticPrReview.Runtime.Canonical;

/// <summary>
/// Enumerates object properties with their REAL names, decoding JSON string
/// escapes leniently so incomplete UTF-16 (lone surrogates from escaped forms)
/// is preserved as code units. System.Text.Json refuses to decode such names
/// through <c>JsonProperty.Name</c>, so this helper re-reads the object's raw
/// text with a Utf8JsonReader and decodes the escapes manually.
/// </summary>
internal static class LenientJsonObjectEnumerator
{
    internal readonly record struct Entry(string Name, bool NameValid, JsonElement Value);

    internal static ImmutableArray<Entry> Enumerate(JsonElement element)
    {
        var raw = element.GetRawText();
        var names = ReadPropertyNames(raw);
        var builder = ImmutableArray.CreateBuilder<Entry>(names.Count);
        var index = 0;
        foreach (var property in element.EnumerateObject())
        {
            var (name, valid) = names[index];
            builder.Add(new Entry(name, valid, property.Value));
            index++;
        }

        return builder.ToImmutable();
    }

    private static List<(string Name, bool Valid)> ReadPropertyNames(string json)
    {
        var names = new List<(string Name, bool Valid)>();
        var reader = new Utf8JsonReader(
            Encoding.UTF8.GetBytes(json),
            isFinalBlock: true,
            state: new JsonReaderState(new JsonReaderOptions { MaxDepth = 1024 }));
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return names;
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                var rawName = reader.HasValueSequence
                    ? reader.ValueSequence.ToArray()
                    : reader.ValueSpan.ToArray();
                names.Add(DecodeStringToken(rawName));
            }

            reader.Skip();
        }

        return names;
    }

    /// <summary>Decodes a JSON string token's raw bytes, preserving lone surrogates.</summary>
    private static (string Name, bool Valid) DecodeStringToken(byte[] raw)
    {
        // The token may be a plain UTF-8 string or one with \uXXXX / simple escapes.
        // Plain UTF-8 without escapes cannot contain lone surrogates (UTF-8 cannot
        // encode them), so only the escaped path needs lenient decoding.
        if (System.Text.Encoding.UTF8.GetString(raw) is var text && !text.Contains('\\'))
        {
            return (text, IsWellFormedUtf16(text));
        }

        var builder = new StringBuilder(raw.Length);
        var valid = true;
        var i = 0;
        while (i < raw.Length)
        {
            var b = raw[i];
            if (b != (byte)'\\')
            {
                // Plain ASCII inside a string token (escapes are the only
                // non-direct content we expect here).
                builder.Append((char)b);
                i++;
                continue;
            }

            if (i + 1 >= raw.Length)
            {
                valid = false;
                i++;
                continue;
            }

            var esc = (char)raw[i + 1];
            switch (esc)
            {
                case '"':
                    builder.Append('"');
                    i += 2;
                    break;
                case '\\':
                    builder.Append('\\');
                    i += 2;
                    break;
                case '/':
                    builder.Append('/');
                    i += 2;
                    break;
                case 'b':
                    builder.Append('\b');
                    i += 2;
                    break;
                case 'f':
                    builder.Append('\f');
                    i += 2;
                    break;
                case 'n':
                    builder.Append('\n');
                    i += 2;
                    break;
                case 'r':
                    builder.Append('\r');
                    i += 2;
                    break;
                case 't':
                    builder.Append('\t');
                    i += 2;
                    break;
                case 'u':
                {
                    if (i + 5 >= raw.Length)
                    {
                        valid = false;
                        i = raw.Length;
                        break;
                    }

                    var unit = (char)ParseHex4(raw, i + 2);
                    i += 6;
                    if (unit >= 0xD800 && unit <= 0xDBFF
                        && i + 5 < raw.Length
                        && raw[i] == (byte)'\\'
                        && raw[i + 1] == (byte)'u')
                    {
                        var low = (char)ParseHex4(raw, i + 2);
                        if (low >= 0xDC00 && low <= 0xDFFF)
                        {
                            builder.Append(unit);
                            builder.Append(low);
                            i += 6;
                            break;
                        }
                    }

                    // Lone surrogate (or non-surrogate code unit) — preserved.
                    builder.Append(unit);
                    if (unit >= 0xD800 && unit <= 0xDFFF)
                    {
                        valid = false;
                    }

                    break;
                }

                default:
                    builder.Append(esc);
                    i += 2;
                    break;
            }
        }

        var result = builder.ToString();
        return (result, valid && IsWellFormedUtf16(result));
    }

    private static int ParseHex4(byte[] raw, int offset)
    {
        var value = 0;
        for (var i = 0; i < 4; i++)
        {
            var c = raw[offset + i];
            value = (value << 4) | (c switch
            {
                >= (byte)'0' and <= (byte)'9' => c - '0',
                >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
                _ => 0,
            });
        }

        return value;
    }

    private static bool IsWellFormedUtf16(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return false;
            }
        }

        return true;
    }
}
