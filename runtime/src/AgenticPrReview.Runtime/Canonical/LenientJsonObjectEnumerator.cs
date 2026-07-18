using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

    internal sealed class TokenWorkCounter
    {
        internal long CodeUnitsRead { get; set; }
    }

    internal readonly record struct TokenFingerprint(ulong High, ulong Low, int Utf16Length);

    internal readonly record struct TokenMetadata(
        TokenFingerprint Fingerprint,
        bool WellFormed,
        string DiagnosticName);

    internal readonly record struct Entry(
        JsonProperty Property,
        JsonElement Container,
        int RawNameOffset,
        int RawNameLength,
        TokenMetadata Metadata)
    {
        internal JsonElement Value => Property.Value;
    }

    internal static IEnumerable<Entry> Enumerate(
        JsonElement element,
        int maxEntries = int.MaxValue,
        TokenWorkCounter? workCounter = null)
    {
        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (count >= maxEntries)
            {
                break;
            }

            count++;
            yield return CreateEntry(element, property, workCounter);
        }
    }

    internal static ReadOnlySpan<byte> RawName(Entry entry) =>
        JsonMarshal.GetRawUtf8Value(entry.Container).Slice(entry.RawNameOffset, entry.RawNameLength);

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

    internal static TokenMetadata AnalyzeStringValue(
        JsonElement element,
        TokenWorkCounter? workCounter = null) =>
        AnalyzeToken(RawStringValue(element), workCounter);

    internal static bool NameIsWellFormed(Entry entry) => entry.Metadata.WellFormed;

    /// <summary>
    /// Produces only the bounded information needed by the safe-path encoder.
    /// Known short schema names remain exact; long untrusted names never cause
    /// a proportional allocation.
    /// </summary>
    internal static string DiagnosticName(Entry entry) => entry.Metadata.DiagnosticName;

    internal static int CompareClosedNames(Entry left, Entry right)
    {
        if (!left.Metadata.WellFormed)
        {
            return right.Metadata.WellFormed
                ? -CompareRawTokenToString(RawName(right), "\uD800")
                : 0;
        }

        if (!right.Metadata.WellFormed)
        {
            return CompareRawTokenToString(RawName(left), "\uD800");
        }

        return CompareNames(left, right);
    }

    internal static int CompareClosedNameTo(Entry left, string right)
    {
        if (!left.Metadata.WellFormed)
        {
            return string.CompareOrdinal("\uD800", right);
        }

        return CompareNameTo(left, right);
    }

    /// <summary>
    /// Exact ordinal UTF-16 sort that advances every token cursor at most once
    /// per code unit within its unresolved prefix group. Long common prefixes
    /// are scanned linearly rather than once per comparison.
    /// </summary>
    internal static List<Entry> SortEntries(
        IReadOnlyCollection<Entry> entries,
        TokenWorkCounter? workCounter = null)
    {
        var output = new List<Entry>(entries.Count);
        SortGroup(entries.Select(static entry => new TokenCursor(entry)).ToList(), output, workCounter);
        return output;
    }

    internal static Utf16TokenEnumerator EnumerateRawToken(ReadOnlySpan<byte> raw) => new(raw);

    private static Entry CreateEntry(
        JsonElement container,
        JsonProperty property,
        TokenWorkCounter? workCounter)
    {
        var containerRaw = JsonMarshal.GetRawUtf8Value(container);
        var rawName = JsonMarshal.GetRawUtf8PropertyName(property);
        if (!containerRaw.Overlaps(rawName, out var rawNameOffset))
        {
            throw new JsonException("Property-name token is outside its containing object.");
        }

        return new Entry(
            property,
            container,
            rawNameOffset,
            rawName.Length,
            AnalyzeToken(rawName, workCounter));
    }

    private static TokenMetadata AnalyzeToken(
        ReadOnlySpan<byte> raw,
        TokenWorkCounter? workCounter)
    {
        Span<char> prefix = stackalloc char[MaxDiagnosticNameCodeUnits];
        Span<byte> hashBuffer = stackalloc byte[256];
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var hashBytes = 0;
        var enumerator = new Utf16TokenEnumerator(raw);
        var count = 0;
        var valid = true;
        var sawNul = false;
        var sawControl = false;
        char pendingHigh = '\0';

        while (enumerator.MoveNext(out var unit))
        {
            if (workCounter is not null)
            {
                workCounter.CodeUnitsRead++;
            }
            if (count < prefix.Length)
            {
                prefix[count] = unit;
            }

            count++;
            BinaryPrimitives.WriteUInt16BigEndian(hashBuffer[hashBytes..], unit);
            hashBytes += 2;
            if (hashBytes == hashBuffer.Length)
            {
                hasher.AppendData(hashBuffer);
                hashBytes = 0;
            }

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

        if (hashBytes > 0)
        {
            hasher.AppendData(hashBuffer[..hashBytes]);
        }

        valid &= pendingHigh == '\0' && !enumerator.Malformed;
        var digest = hasher.GetHashAndReset();
        var fingerprint = new TokenFingerprint(
            BinaryPrimitives.ReadUInt64BigEndian(digest),
            BinaryPrimitives.ReadUInt64BigEndian(digest.AsSpan(8)),
            count);
        var diagnosticName = !valid
            ? JsonElementCanonicalizer.InvalidNameSentinel
            : sawNul
                ? "\0"
                : sawControl
                    ? "\u0001"
                    : count == 0
                        ? string.Empty
                        : count <= prefix.Length
                            ? new string(prefix[..count])
                            : "x";
        return new TokenMetadata(fingerprint, valid, diagnosticName);
    }

    private static void SortGroup(
        List<TokenCursor> group,
        List<Entry> output,
        TokenWorkCounter? workCounter)
    {
        if (group.Count == 0)
        {
            return;
        }

        if (group.Count == 1)
        {
            output.Add(group[0].Entry);
            return;
        }

        while (true)
        {
            int? common = null;
            var allSame = true;
            for (var index = 0; index < group.Count; index++)
            {
                var cursor = group[index];
                cursor.Current = cursor.MoveNext(out var unit) ? unit : -1;
                if (cursor.Current >= 0 && workCounter is { } counter)
                {
                    counter.CodeUnitsRead++;
                }

                group[index] = cursor;
                common ??= cursor.Current;
                allSame &= common.Value == cursor.Current;
            }

            if (allSame)
            {
                if (common == -1)
                {
                    output.AddRange(group.Select(static cursor => cursor.Entry));
                    return;
                }

                continue;
            }

            var partitions = new SortedDictionary<int, List<TokenCursor>>();
            foreach (var cursor in group)
            {
                if (!partitions.TryGetValue(cursor.Current, out var partition))
                {
                    partition = new List<TokenCursor>();
                    partitions.Add(cursor.Current, partition);
                }

                partition.Add(cursor);
            }

            foreach (var partition in partitions)
            {
                if (partition.Key == -1)
                {
                    output.AddRange(partition.Value.Select(static cursor => cursor.Entry));
                }
                else
                {
                    SortGroup(partition.Value, output, workCounter);
                }
            }

            return;
        }
    }

    private struct TokenCursor
    {
        private int _offset;
        private char _pendingLow;
        private bool _malformed;

        internal TokenCursor(Entry entry)
        {
            Entry = entry;
            _offset = 0;
            _pendingLow = '\0';
            _malformed = false;
            Current = -1;
        }

        internal Entry Entry { get; }

        internal int Current { get; set; }

        internal bool MoveNext(out char unit) =>
            DecodeNext(RawName(Entry), ref _offset, ref _pendingLow, ref _malformed, out unit);
    }

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

    private static bool DecodeNext(
        ReadOnlySpan<byte> raw,
        ref int offset,
        ref char pendingLow,
        ref bool malformed,
        out char unit)
    {
        if (pendingLow != '\0')
        {
            unit = pendingLow;
            pendingLow = '\0';
            return true;
        }

        if (offset >= raw.Length)
        {
            unit = default;
            return false;
        }

        var first = raw[offset];
        if (first == (byte)'\\')
        {
            if (offset + 1 >= raw.Length)
            {
                malformed = true;
                offset = raw.Length;
                unit = '\uFFFD';
                return true;
            }

            var escape = raw[offset + 1];
            offset += 2;
            if (escape == (byte)'u')
            {
                if (offset + 4 > raw.Length)
                {
                    malformed = true;
                    offset = raw.Length;
                    unit = '\uFFFD';
                    return true;
                }

                var value = 0;
                for (var index = 0; index < 4; index++)
                {
                    var b = raw[offset + index];
                    var nibble = b switch
                    {
                        >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
                        >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
                        >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
                        _ => -1,
                    };
                    if (nibble < 0)
                    {
                        malformed = true;
                        nibble = 0;
                    }

                    value = (value << 4) | nibble;
                }

                offset += 4;
                unit = (char)value;
                return true;
            }

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
                _ => '\uFFFD',
            };
            if (unit == '\uFFFD')
            {
                malformed = true;
            }

            return true;
        }

        var status = Rune.DecodeFromUtf8(raw[offset..], out var rune, out var consumed);
        if (status != OperationStatus.Done || consumed <= 0)
        {
            malformed = true;
            offset++;
            unit = '\uFFFD';
            return true;
        }

        offset += consumed;
        if (rune.Value <= 0xFFFF)
        {
            unit = (char)rune.Value;
            return true;
        }

        var scalar = rune.Value - 0x10000;
        unit = (char)(0xD800 + (scalar >> 10));
        pendingLow = (char)(0xDC00 + (scalar & 0x3FF));
        return true;
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
            var malformed = Malformed;
            var moved = DecodeNext(_raw, ref _offset, ref _pendingLow, ref malformed, out unit);
            Malformed = malformed;
            return moved;
        }
    }
}
