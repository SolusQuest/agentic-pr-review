using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace AgenticPrReview.Runtime.Canonical;

/// <summary>
/// Enumerates object properties with their REAL names, decoding JSON string
/// escapes leniently so incomplete UTF-16 (lone surrogates from escaped forms)
/// is preserved as code units. System.Text.Json refuses to decode such names
/// through <c>JsonProperty.Name</c>, so this helper scans the object's raw
/// UTF-16 text directly. It does not create a second full UTF-8 copy or impose
/// a parser depth limit, and callers can cap the number of captured entries.
/// </summary>
internal static class LenientJsonObjectEnumerator
{
    internal readonly record struct Entry(string Name, bool NameValid, JsonElement Value);

    internal static ImmutableArray<Entry> Enumerate(JsonElement element, int maxEntries = int.MaxValue)
    {
        var raw = element.GetRawText();
        var names = ReadPropertyNames(raw, maxEntries);
        var builder = ImmutableArray.CreateBuilder<Entry>(names.Count);
        var index = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (index >= names.Count)
            {
                break;
            }

            var (name, valid) = names[index];
            builder.Add(new Entry(name, valid, property.Value));
            index++;
        }

        return builder.ToImmutable();
    }

    private static List<(string Name, bool Valid)> ReadPropertyNames(string json, int maxEntries)
    {
        var names = new List<(string Name, bool Valid)>();
        var index = 0;
        SkipTrivia(json, ref index);
        if (index >= json.Length || json[index] != '{')
        {
            return names;
        }
        index++;

        while (names.Count < maxEntries)
        {
            SkipTrivia(json, ref index);
            if (index >= json.Length || json[index] == '}')
            {
                break;
            }
            if (json[index] != '"')
            {
                break;
            }

            var nameStart = ++index;
            while (index < json.Length && json[index] != '"')
            {
                if (json[index] == '\\' && index + 1 < json.Length)
                {
                    index += 2;
                }
                else
                {
                    index++;
                }
            }
            if (index >= json.Length)
            {
                break;
            }

            names.Add(DecodeStringToken(json.AsSpan(nameStart, index - nameStart)));
            index++;
            SkipTrivia(json, ref index);
            if (index >= json.Length || json[index] != ':')
            {
                break;
            }
            index++;
            SkipJsonValue(json, ref index);
            SkipTrivia(json, ref index);
            if (index < json.Length && json[index] == ',')
            {
                index++;
                continue;
            }
            if (index >= json.Length || json[index] == '}')
            {
                break;
            }
        }

        return names;
    }

    private static void SkipTrivia(string json, ref int index)
    {
        while (index < json.Length)
        {
            if (char.IsWhiteSpace(json[index]))
            {
                index++;
                continue;
            }
            if (json[index] == '/' && index + 1 < json.Length && json[index + 1] == '/')
            {
                index += 2;
                while (index < json.Length && json[index] is not ('\r' or '\n')) index++;
                continue;
            }
            if (json[index] == '/' && index + 1 < json.Length && json[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < json.Length && !(json[index] == '*' && json[index + 1] == '/')) index++;
                index = Math.Min(index + 2, json.Length);
                continue;
            }
            break;
        }
    }

    private static void SkipJsonValue(string json, ref int index)
    {
        SkipTrivia(json, ref index);
        var depth = 0;
        var inString = false;
        while (index < json.Length)
        {
            var c = json[index];
            if (inString)
            {
                if (c == '\\' && index + 1 < json.Length)
                {
                    index += 2;
                    continue;
                }
                index++;
                if (c == '"') inString = false;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                index++;
            }
            else if (c is '{' or '[')
            {
                depth++;
                index++;
            }
            else if (c is '}' or ']')
            {
                if (depth == 0) return;
                depth--;
                index++;
            }
            else if (c == ',' && depth == 0)
            {
                return;
            }
            else if (c == '/' && index + 1 < json.Length && json[index + 1] is '/' or '*')
            {
                SkipTrivia(json, ref index);
            }
            else
            {
                index++;
            }
        }
    }

    /// <summary>Decodes a raw JSON property-name token, preserving lone surrogates.</summary>
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
                case '"':
                    builder.Append('"');
                    break;
                case '\\':
                    builder.Append('\\');
                    break;
                case '/':
                    builder.Append('/');
                    break;
                case 'b':
                    builder.Append('\b');
                    break;
                case 'f':
                    builder.Append('\f');
                    break;
                case 'n':
                    builder.Append('\n');
                    break;
                case 'r':
                    builder.Append('\r');
                    break;
                case 't':
                    builder.Append('\t');
                    break;
                case 'u':
                {
                    if (i + 5 >= raw.Length)
                    {
                        valid = false;
                        i = raw.Length - 2;
                        break;
                    }

                    var unit = (char)ParseHex4(raw, i + 2);
                    if (unit >= 0xD800 && unit <= 0xDBFF
                        && i + 11 < raw.Length
                        && raw[i + 6] == '\\'
                        && raw[i + 7] == 'u')
                    {
                        var low = (char)ParseHex4(raw, i + 8);
                        if (low >= 0xDC00 && low <= 0xDFFF)
                        {
                            builder.Append(unit);
                            builder.Append(low);
                            i += 10;
                            break;
                        }
                    }

                    // Lone surrogate (or a non-surrogate code unit) — preserved.
                    builder.Append(unit);
                    if (unit >= 0xD800 && unit <= 0xDFFF)
                    {
                        valid = false;
                    }

                    i += 4;
                    break;
                }

                default:
                    builder.Append(esc);
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
