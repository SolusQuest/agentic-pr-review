using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace AgenticPrReview.Runtime.Canonical;

/// <summary>
/// General RFC 8785 canonical JSON writer for the prefix contract (#50).
/// Unlike the ledger writer, U+0000 in string content is emitted as
/// <c>\u0000</c> (RFC 8785 escaping), and numbers cover the full finite
/// IEEE-754 binary64 domain via ECMAScript Number::toString. Unpaired
/// surrogates and non-finite numbers are rejected.
/// </summary>
internal struct Rfc8785Writer
{
    private readonly ArrayBufferWriter<byte> _writer;
    private bool _needsComma;

    internal Rfc8785Writer(int initialCapacity)
    {
        _writer = new ArrayBufferWriter<byte>(initialCapacity);
        _needsComma = false;
    }

    /// <summary>
    /// When set to a non-negative value, appends stop once the buffer exceeds
    /// the limit (discard/count-only mode): no more bytes are allocated, the
    /// caller's traversal continues, and <see cref="Exceeded"/> records that
    /// the cap was crossed. Domain validation must always run to completion.
    /// </summary>
    internal long DiscardLimit { get; set; } = -1;

    /// <summary>True once any append was skipped because of <see cref="DiscardLimit"/>.</summary>
    internal bool Exceeded { get; private set; }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        if (Exceeded)
        {
            return;
        }

        if (DiscardLimit >= 0 && _writer.WrittenCount + bytes.Length > DiscardLimit)
        {
            Exceeded = true;
            return;
        }

        _writer.Write(bytes);
    }

    internal ImmutableArray<byte> ToImmutableArray() => _writer.WrittenSpan.ToArray().ToImmutableArray();

    internal long WrittenCount => _writer.WrittenCount;

    internal void WriteObjectStart()
    {
        Append("{"u8);
        _needsComma = false;
    }

    internal void WriteObjectEnd()
    {
        Append("}"u8);
        _needsComma = true;
    }

    internal void WriteArrayStart()
    {
        Append("["u8);
        _needsComma = false;
    }

    internal void WriteArrayEnd()
    {
        Append("]"u8);
        _needsComma = true;
    }

    internal void WriteComma() => Append(","u8);

    internal void WriteNull()
    {
        Append("null"u8);
        _needsComma = true;
    }

    internal void WriteBoolean(bool value)
    {
        Append(value ? "true"u8 : "false"u8);
        _needsComma = true;
    }

    internal void WriteProperty(string name)
    {
        if (_needsComma)
        {
            Append(","u8);
        }

        WriteEscapedString(name);
        Append(":"u8);
        _needsComma = false;
    }

    internal void WriteRawProperty(ReadOnlySpan<byte> rawName)
    {
        if (_needsComma)
        {
            Append(","u8);
        }

        WriteRawEscapedString(rawName);
        Append(":"u8);
        _needsComma = false;
    }

    internal void WriteNumber(long value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        Append(Encoding.UTF8.GetBytes(text));
        _needsComma = true;
    }

    internal void WriteNumber(double value)
    {
        var text = EcmaScriptNumberFormatter.Format(value);
        Append(Encoding.UTF8.GetBytes(text));
        _needsComma = true;
    }

    internal void WriteString(string value)
    {
        WriteEscapedString(value);
        _needsComma = true;
    }

    internal void WriteRawString(ReadOnlySpan<byte> rawValue)
    {
        WriteRawEscapedString(rawValue);
        _needsComma = true;
    }

    private void WriteRawEscapedString(ReadOnlySpan<byte> raw)
    {
        Append("\""u8);
        var enumerator = LenientJsonObjectEnumerator.EnumerateRawToken(raw);
        while (enumerator.MoveNext(out var unit))
        {
            if (char.IsHighSurrogate(unit))
            {
                if (!enumerator.MoveNext(out var low) || !char.IsLowSurrogate(low))
                {
                    throw new Rfc8785CanonicalizationException(
                        Rfc8785RejectionReason.UnpairedSurrogate,
                        "Unpaired UTF-16 surrogate encountered during canonicalization.");
                }

                WriteUtf8Codepoint(char.ConvertToUtf32(unit, low));
            }
            else if (char.IsLowSurrogate(unit))
            {
                throw new Rfc8785CanonicalizationException(
                    Rfc8785RejectionReason.UnpairedSurrogate,
                    "Unpaired UTF-16 surrogate encountered during canonicalization.");
            }
            else
            {
                WriteEscapedCodepoint(unit);
            }
        }

        if (enumerator.Malformed)
        {
            throw new Rfc8785CanonicalizationException(
                Rfc8785RejectionReason.UnpairedSurrogate,
                "Malformed UTF-8 JSON string token encountered during canonicalization.");
        }

        Append("\""u8);
    }

    private void WriteEscapedString(string value)
    {
        Append("\""u8);
        var utf16 = value.AsSpan();
        for (var i = 0; i < utf16.Length; i++)
        {
            var c = utf16[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < utf16.Length && char.IsLowSurrogate(utf16[i + 1]))
                {
                    var codepoint = char.ConvertToUtf32(c, utf16[i + 1]);
                    WriteUtf8Codepoint(codepoint);
                    i++;
                }
                else
                {
                    throw new Rfc8785CanonicalizationException(
                        Rfc8785RejectionReason.UnpairedSurrogate,
                        "Unpaired UTF-16 surrogate encountered during canonicalization.");
                }
            }
            else if (char.IsLowSurrogate(c))
            {
                throw new Rfc8785CanonicalizationException(
                    Rfc8785RejectionReason.UnpairedSurrogate,
                    "Unpaired UTF-16 surrogate encountered during canonicalization.");
            }
            else
            {
                WriteEscapedCodepoint(c);
            }
        }

        Append("\""u8);
    }

    private void WriteEscapedCodepoint(char c)
    {
        switch (c)
        {
            case '"':
                Append("\\\""u8);
                return;
            case '\\':
                Append("\\\\"u8);
                return;
            case '\b':
                Append("\\b"u8);
                return;
            case '\f':
                Append("\\f"u8);
                return;
            case '\n':
                Append("\\n"u8);
                return;
            case '\r':
                Append("\\r"u8);
                return;
            case '\t':
                Append("\\t"u8);
                return;
        }

        if (c < 0x20)
        {
            // Includes U+0000: emitted as \u0000 per RFC 8785, not rejected.
            Span<byte> escape = stackalloc byte[6];
            escape[0] = (byte)'\\';
            escape[1] = (byte)'u';
            var value = (int)c;
            for (var index = 5; index >= 2; index--)
            {
                var nibble = value & 0xF;
                escape[index] = (byte)(nibble < 10 ? '0' + nibble : 'a' + nibble - 10);
                value >>= 4;
            }

            Append(escape);
            return;
        }

        WriteUtf8Codepoint(c);
    }

    private void WriteUtf8Codepoint(int codepoint)
    {
        Span<byte> utf8 = stackalloc byte[4];
        int length;
        if (codepoint <= 0x7F)
        {
            utf8[0] = (byte)codepoint;
            length = 1;
        }
        else if (codepoint <= 0x7FF)
        {
            utf8[0] = (byte)(0xC0 | (codepoint >> 6));
            utf8[1] = (byte)(0x80 | (codepoint & 0x3F));
            length = 2;
        }
        else if (codepoint <= 0xFFFF)
        {
            utf8[0] = (byte)(0xE0 | (codepoint >> 12));
            utf8[1] = (byte)(0x80 | ((codepoint >> 6) & 0x3F));
            utf8[2] = (byte)(0x80 | (codepoint & 0x3F));
            length = 3;
        }
        else
        {
            utf8[0] = (byte)(0xF0 | (codepoint >> 18));
            utf8[1] = (byte)(0x80 | ((codepoint >> 12) & 0x3F));
            utf8[2] = (byte)(0x80 | ((codepoint >> 6) & 0x3F));
            utf8[3] = (byte)(0x80 | (codepoint & 0x3F));
            length = 4;
        }

        Append(utf8[..length]);
    }
}
