using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AgenticPrReview.Runtime.Canonical;

/// <summary>
/// Exposes JSON property names and string values as raw-token-backed UTF-16
/// streams. No operation here materializes the complete decoded token: callers
/// can compare, validate, sort, and canonicalize even a very large single name
/// or value while retaining only bounded state.
/// </summary>
internal static class LenientJsonObjectEnumerator
{
    private const int MaxDiagnosticNameCodeUnits = 128;

    internal readonly record struct Entry(JsonProperty Property)
    {
        internal JsonElement Value => Property.Value;
    }

    internal static IEnumerable<Entry> Enumerate(JsonElement element, int maxEntries = int.MaxValue)
    {
        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (count >= maxEntries)
            {
                yield break;
            }

            count++;
            yield return new Entry(property);
        }
    }

    internal static ReadOnlySpan<byte> RawName(Entry entry) =>
        JsonMarshal.GetRawUtf8PropertyName(entry.Property);

    internal static ReadOnlySpan<byte> RawStringValue(JsonElement element)
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

        return raw[1..^1];
    }

    internal static int CompareNames(Entry left, Entry right) =>
        CompareRawTokens(RawName(left), RawName(right));

    internal static int CompareNameTo(Entry left, string right) =>
        CompareRawTokenToString(RawName(left), right);

    internal static bool NameEquals(Entry entry, string expected) =>
        CompareNameTo(entry, expected) == 0;

    internal static bool StringValuesEqual(JsonElement left, JsonElement right) =>
        CompareRawTokens(RawStringValue(left), RawStringValue(right)) == 0;

    internal static bool NameIsWellFormed(Entry entry) => IsWellFormedUtf16(RawName(entry));

    /// <summary>
    /// Produces only the bounded information needed by the safe-path encoder.
    /// Known short schema names remain exact; long untrusted names never cause
    /// a proportional allocation.
    /// </summary>
    internal static string DiagnosticName(Entry entry)
    {
        Span<char> prefix = stackalloc char[MaxDiagnosticNameCodeUnits];
        var enumerator = new Utf16TokenEnumerator(RawName(entry));
        var count = 0;
        var valid = true;
        var sawNul = false;
        var sawControl = false;
        char pendingHigh = '\0';

        while (enumerator.MoveNext(out var unit))
        {
            if (count < prefix.Length)
            {
                prefix[count] = unit;
            }

            count++;
            if (unit == '\0')
            {
                sawNul = true;
            }
            else if (unit <= 0x1F || unit == 0x7F)
            {
                sawControl = true;
            }

            if (pendingHigh != '\0')
            {
                if (char.IsLowSurrogate(unit))
                {
                    pendingHigh = '\0';
                    continue;
                }

                valid = false;
                pendingHigh = '\0';
            }

            if (char.IsHighSurrogate(unit))
            {
                pendingHigh = unit;
            }
            else if (char.IsLowSurrogate(unit))
            {
                valid = false;
            }
        }

        valid &= pendingHigh == '\0' && !enumerator.Malformed;
        if (!valid)
        {
            return JsonElementCanonicalizer.InvalidNameSentinel;
        }

        if (sawNul)
        {
            return "\0";
        }

        if (sawControl)
        {
            return "\u0001";
        }

        if (count == 0)
        {
            return string.Empty;
        }

        return count <= prefix.Length ? new string(prefix[..count]) : "x";
    }

    internal static Utf16TokenEnumerator EnumerateRawToken(ReadOnlySpan<byte> raw) => new(raw);

    private static int CompareRawTokens(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var leftEnumerator = new Utf16TokenEnumerator(left);
        var rightEnumerator = new Utf16TokenEnumerator(right);
        while (true)
        {
            var hasLeft = leftEnumerator.MoveNext(out var leftUnit);
            var hasRight = rightEnumerator.MoveNext(out var rightUnit);
            if (!hasLeft || !hasRight)
            {
                return hasLeft ? 1 : hasRight ? -1 : 0;
            }

            var comparison = leftUnit.CompareTo(rightUnit);
            if (comparison != 0)
            {
                return comparison;
            }
        }
    }

    private static int CompareRawTokenToString(ReadOnlySpan<byte> left, ReadOnlySpan<char> right)
    {
        var enumerator = new Utf16TokenEnumerator(left);
        var index = 0;
        while (enumerator.MoveNext(out var unit))
        {
            if (index >= right.Length)
            {
                return 1;
            }

            var comparison = unit.CompareTo(right[index]);
            if (comparison != 0)
            {
                return comparison;
            }

            index++;
        }

        return index == right.Length ? 0 : -1;
    }

    private static bool IsWellFormedUtf16(ReadOnlySpan<byte> raw)
    {
        var enumerator = new Utf16TokenEnumerator(raw);
        char pendingHigh = '\0';
        while (enumerator.MoveNext(out var unit))
        {
            if (pendingHigh != '\0')
            {
                if (!char.IsLowSurrogate(unit))
                {
                    return false;
                }

                pendingHigh = '\0';
                continue;
            }

            if (char.IsHighSurrogate(unit))
            {
                pendingHigh = unit;
            }
            else if (char.IsLowSurrogate(unit))
            {
                return false;
            }
        }

        return pendingHigh == '\0' && !enumerator.Malformed;
    }

    internal ref struct Utf16TokenEnumerator
    {
        private readonly ReadOnlySpan<byte> _raw;
        private int _offset;
        private char _pendingLow;

        internal Utf16TokenEnumerator(ReadOnlySpan<byte> raw)
        {
            _raw = raw;
            _offset = 0;
            _pendingLow = '\0';
            Malformed = false;
        }

        internal bool Malformed { get; private set; }

        internal bool MoveNext(out char unit)
        {
            if (_pendingLow != '\0')
            {
                unit = _pendingLow;
                _pendingLow = '\0';
                return true;
            }

            if (_offset >= _raw.Length)
            {
                unit = default;
                return false;
            }

            var first = _raw[_offset];
            if (first == (byte)'\\')
            {
                return DecodeEscape(out unit);
            }

            var status = Rune.DecodeFromUtf8(_raw[_offset..], out var rune, out var consumed);
            if (status != OperationStatus.Done || consumed <= 0)
            {
                Malformed = true;
                _offset++;
                unit = '\uFFFD';
                return true;
            }

            _offset += consumed;
            if (rune.Value <= 0xFFFF)
            {
                unit = (char)rune.Value;
                return true;
            }

            var scalar = rune.Value - 0x10000;
            unit = (char)(0xD800 + (scalar >> 10));
            _pendingLow = (char)(0xDC00 + (scalar & 0x3FF));
            return true;
        }

        private bool DecodeEscape(out char unit)
        {
            if (_offset + 1 >= _raw.Length)
            {
                Malformed = true;
                _offset = _raw.Length;
                unit = '\uFFFD';
                return true;
            }

            var escape = _raw[_offset + 1];
            _offset += 2;
            unit = escape switch
            {
                (byte)'"' => '"',
                (byte)'\\' => '\\',
                (byte)'/' => '/',
                (byte)'b' => '\b',
                (byte)'f' => '\f',
                (byte)'n' => '\n',
                (byte)'r' => '\r',
                (byte)'t' => '\t',
                (byte)'u' => DecodeHexEscape(),
                _ => '\uFFFD',
            };
            if (escape is not ((byte)'"' or (byte)'\\' or (byte)'/' or (byte)'b' or (byte)'f' or (byte)'n' or (byte)'r' or (byte)'t' or (byte)'u'))
            {
                Malformed = true;
            }

            return true;
        }

        private char DecodeHexEscape()
        {
            if (_offset + 4 > _raw.Length)
            {
                Malformed = true;
                _offset = _raw.Length;
                return '\uFFFD';
            }

            var value = 0;
            for (var i = 0; i < 4; i++)
            {
                var b = _raw[_offset + i];
                var nibble = b switch
                {
                    >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
                    >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
                    >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
                    _ => -1,
                };
                if (nibble < 0)
                {
                    Malformed = true;
                    nibble = 0;
                }

                value = (value << 4) | nibble;
            }

            _offset += 4;
            return (char)value;
        }
    }
}
