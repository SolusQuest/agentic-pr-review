using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Agent.Tools;

internal static partial class AgentToolArguments
{
    private static byte[]? StrictInputBytes(
        string json,
        int maximumBytes = AgentLimits.ToolArgumentsBytes)
    {
        try
        {
            var bytes = new UTF8Encoding(false, true).GetBytes(json);
            return bytes.Length <= maximumBytes ? bytes : null;
        }
        catch (EncoderFallbackException)
        {
            return null;
        }
    }

    private static byte[]? ProviderComparisonBytes(
        byte[] input,
        int maximumBytes = AgentLimits.ToolArgumentsBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(
                input,
                new JsonDocumentOptions { MaxDepth = 16 });
            var canonical = JsonElementCanonicalizer.Canonicalize(
                document.RootElement,
                maxDepth: 8,
                maxProperties: 64,
                maxArrayItems: 64,
                maxBytes: maximumBytes,
                out var capExceeded);
            return capExceeded ? null : canonical.ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
        catch (Rfc8785CanonicalizationException)
        {
            return null;
        }
    }

    private static byte[]? ProviderDeserializationBytes(
        byte[] input,
        int maximumBytes = AgentLimits.ToolArgumentsBytes)
    {
        try
        {
            var reader = new Utf8JsonReader(
                input,
                new JsonReaderOptions { MaxDepth = 16 });
            var buffer = new ArrayBufferWriter<byte>(Math.Max(input.Length, 1));
            using var writer = new Utf8JsonWriter(
                buffer,
                new JsonWriterOptions
                {
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });

            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        writer.WriteStartObject();
                        break;
                    case JsonTokenType.EndObject:
                        writer.WriteEndObject();
                        break;
                    case JsonTokenType.StartArray:
                        writer.WriteStartArray();
                        break;
                    case JsonTokenType.EndArray:
                        writer.WriteEndArray();
                        break;
                    case JsonTokenType.PropertyName:
                        writer.WritePropertyName(reader.GetString()!);
                        break;
                    case JsonTokenType.String:
                        writer.WriteStringValue(reader.GetString());
                        break;
                    case JsonTokenType.Number:
                        if (!TryProviderInt32(reader.ValueSpan, out var number))
                        {
                            return null;
                        }

                        writer.WriteNumberValue(number);
                        break;
                    case JsonTokenType.True:
                        writer.WriteBooleanValue(true);
                        break;
                    case JsonTokenType.False:
                        writer.WriteBooleanValue(false);
                        break;
                    case JsonTokenType.Null:
                        writer.WriteNullValue();
                        break;
                    default:
                        return null;
                }
            }

            writer.Flush();
            return buffer.WrittenCount <= maximumBytes
                ? buffer.WrittenSpan.ToArray()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    internal static bool TryProviderInt32(
        ReadOnlySpan<byte> token,
        out int value)
    {
        value = 0;
        if (token.IsEmpty)
        {
            return false;
        }

        var negative = token[0] == (byte)'-';
        var mantissaStart = negative ? 1 : 0;
        var exponentMarker = token.IndexOfAny((byte)'e', (byte)'E');
        var mantissaEnd = exponentMarker < 0 ? token.Length : exponentMarker;
        var decimalPoint = token[mantissaStart..mantissaEnd].IndexOf((byte)'.');
        if (decimalPoint >= 0)
        {
            decimalPoint += mantissaStart;
        }

        var digitCount = 0;
        var leadingZeroCount = 0;
        var trailingZeroCount = 0;
        var foundNonZero = false;
        for (var index = mantissaStart; index < mantissaEnd; index++)
        {
            var scalar = token[index];
            if (scalar == (byte)'.')
            {
                continue;
            }

            digitCount++;
            if (!foundNonZero && scalar == (byte)'0')
            {
                leadingZeroCount++;
                continue;
            }

            foundNonZero = true;
            trailingZeroCount = scalar == (byte)'0'
                ? trailingZeroCount + 1
                : 0;
        }

        if (!foundNonZero)
        {
            return true;
        }

        var fractionDigits = decimalPoint < 0
            ? 0
            : mantissaEnd - decimalPoint - 1;
        var exponent = 0L;
        if (exponentMarker >= 0)
        {
            var exponentIndex = exponentMarker + 1;
            var negativeExponent = token[exponentIndex] == (byte)'-';
            if (negativeExponent || token[exponentIndex] == (byte)'+')
            {
                exponentIndex++;
            }

            var exponentCap = token.Length + 32L;
            for (; exponentIndex < token.Length; exponentIndex++)
            {
                var digit = token[exponentIndex] - (byte)'0';
                exponent = Math.Min(
                    exponentCap,
                    exponent * 10L + digit);
            }

            if (negativeExponent)
            {
                exponent = -exponent;
            }
        }

        var scale = exponent - fractionDigits;
        var removedDigits = 0L;
        if (scale < 0)
        {
            removedDigits = -scale;
            if (removedDigits > trailingZeroCount)
            {
                return false;
            }
        }

        var significantDigits = digitCount - leadingZeroCount;
        var finalDigitCount = scale < 0
            ? significantDigits - removedDigits
            : significantDigits + scale;
        if (finalDigitCount is < 1 or > 10)
        {
            return false;
        }

        var digitsToKeep = digitCount - removedDigits;
        var consumedDigits = 0;
        var magnitude = 0L;
        for (var index = mantissaStart; index < mantissaEnd; index++)
        {
            var scalar = token[index];
            if (scalar == (byte)'.')
            {
                continue;
            }

            consumedDigits++;
            if (consumedDigits > digitsToKeep)
            {
                break;
            }

            magnitude = magnitude * 10L + scalar - (byte)'0';
        }

        for (var index = 0L; index < scale; index++)
        {
            magnitude *= 10L;
        }

        var maximumMagnitude = negative
            ? -(long)int.MinValue
            : int.MaxValue;
        if (magnitude > maximumMagnitude)
        {
            return false;
        }

        value = negative ? (int)-magnitude : (int)magnitude;
        return true;
    }

    private static bool MatchesInput(
        ReadOnlySpan<byte> strictInput,
        byte[]? providerComparison,
        byte[] expected,
        int maximumBytes = AgentLimits.ToolArgumentsBytes)
    {
        if (providerComparison is null)
        {
            return strictInput.SequenceEqual(expected);
        }

        var expectedComparison = ProviderComparisonBytes(
            expected,
            maximumBytes);
        return expectedComparison is not null &&
            providerComparison.AsSpan().SequenceEqual(expectedComparison);
    }
}

internal static class AgentTextValidation
{
    internal static bool IsOnlyFixedWhitespace(string value)
    {
        var any = false;
        foreach (var rune in value.EnumerateRunes())
        {
            any = true;
            if (!IsFixedWhitespace(rune.Value))
            {
                return false;
            }
        }

        return any;
    }

    private static bool IsFixedWhitespace(int scalar) =>
        scalar is 0x0009 or 0x000A or 0x000B or 0x000C or 0x000D or
            0x0020 or 0x0085 or
            0x00A0 or 0x1680 or 0x2028 or 0x2029 or 0x202F or 0x205F or
            0x3000 ||
        scalar is >= 0x2000 and <= 0x200A;
}
