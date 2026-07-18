using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AgenticPrReview.Runtime.Canonical;

/// <summary>
/// Reads raw UTF-8 token spans directly from <see cref="JsonElement"/> and
/// <see cref="JsonProperty"/>. JSON escapes are decoded leniently so lone
/// UTF-16 surrogates remain observable code units instead of becoming an
/// exception. The .NET 10 JsonMarshal views avoid copying an entire object
/// subtree merely to recover one invalid property name.
/// </summary>
internal static class LenientJsonObjectEnumerator
{
    internal readonly record struct Entry(string Name, bool NameValid, JsonElement Value);

    internal static ImmutableArray<Entry> Enumerate(JsonElement element, int maxEntries = int.MaxValue)
    {
        var builder = ImmutableArray.CreateBuilder<Entry>();
        foreach (var property in element.EnumerateObject())
        {
            if (builder.Count >= maxEntries)
            {
                break;
            }

            var rawName = JsonMarshal.GetRawUtf8PropertyName(property);
            var (name, valid) = DecodeStringToken(Encoding.UTF8.GetString(rawName));
            builder.Add(new Entry(name, valid, property.Value));
        }

        return builder.ToImmutable();
    }

    /// <summary>Decodes a JSON string value while preserving lone surrogate code units.</summary>
    internal static string DecodeStringValue(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("The JSON value is not a string.");
        }

        var raw = JsonMarshal.GetRawUtf8Value(element);
        if (raw.Length < 2 || raw[0] != (byte)'"' || raw[^1] != (byte)'"')
        {
            throw new JsonException("Malformed JSON string token.");
        }

        return DecodeStringToken(Encoding.UTF8.GetString(raw[1..^1])).Name;
    }

    /// <summary>Decodes raw JSON string content, preserving lone surrogates.</summary>
    private static (string Name, bool Valid) DecodeStringToken(ReadOnlySpan<char> raw)
    {
        var builder = new StringBuilder(raw.Length);
        var valid = true;
        var i = 0;
        var runStart = 0;

        while (i < raw.Length)
        {
            if (raw[i] != '\\')
            {
                i++;
                continue;
            }

            AppendRun(builder, raw, runStart, i, ref valid);
            if (i + 1 >= raw.Length)
            {
                valid = false;
                i++;
                runStart = i;
                continue;
            }

            var esc = raw[i + 1];
            switch (esc)
            {
                case '"': builder.Append('"'); break;
                case '\\': builder.Append('\\'); break;
                case '/': builder.Append('/'); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case 'u':
                {
                    if (i + 5 >= raw.Length)
                    {
                        valid = false;
                        i = raw.Length - 2;
                        break;
                    }

                    var unit = (char)ParseHex4(raw, i + 2);
                    if (char.IsHighSurrogate(unit)
                        && i + 11 < raw.Length
                        && raw[i + 6] == '\\'
                        && raw[i + 7] == 'u')
                    {
                        var low = (char)ParseHex4(raw, i + 8);
                        if (char.IsLowSurrogate(low))
                        {
                            builder.Append(unit);
                            builder.Append(low);
                            i += 10;
                            break;
                        }
                    }

                    builder.Append(unit);
                    if (char.IsSurrogate(unit))
                    {
                        valid = false;
                    }

                    i += 4;
                    break;
                }
                default:
                    builder.Append(esc);
                    valid = false;
                    break;
            }

            i += 2;
            runStart = i;
        }

        AppendRun(builder, raw, runStart, raw.Length, ref valid);
        var result = builder.ToString();
        return (result, valid && IsWellFormedUtf16(result));
    }

    private static void AppendRun(
        StringBuilder builder,
        ReadOnlySpan<char> raw,
        int start,
        int end,
        ref bool valid)
    {
        if (end <= start)
        {
            return;
        }

        var run = raw[start..end];
        builder.Append(run);
        if (!IsWellFormedUtf16(run))
        {
            valid = false;
        }
    }

    private static int ParseHex4(ReadOnlySpan<char> raw, int offset)
    {
        var value = 0;
        for (var i = 0; i < 4; i++)
        {
            var c = raw[offset + i];
            value = (value << 4) | (c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => 0,
            });
        }

        return value;
    }

    private static bool IsWellFormedUtf16(ReadOnlySpan<char> value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                return false;
            }
        }

        return true;
    }
}
