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

    internal ImmutableArray<byte> ToImmutableArray() => _writer.WrittenSpan.ToArray().ToImmutableArray();

    internal long WrittenCount => _writer.WrittenCount;

    internal void WriteObjectStart()
    {
        _writer.Write("{"u8);
        _needsComma = false;
    }

    internal void WriteObjectEnd()
    {
        _writer.Write("}"u8);
        _needsComma = true;
    }

    internal void WriteArrayStart()
    {
        _writer.Write("["u8);
        _needsComma = false;
    }

    internal void WriteArrayEnd()
    {
        _writer.Write("]"u8);
        _needsComma = true;
    }

    internal void WriteComma() => _writer.Write(","u8);

    internal void WriteNull()
    {
        _writer.Write("null"u8);
        _needsComma = true;
    }

    internal void WriteBoolean(bool value)
    {
        _writer.Write(value ? "true"u8 : "false"u8);
        _needsComma = true;
    }

    internal void WriteProperty(string name)
    {
        if (_needsComma)
        {
            _writer.Write(","u8);
        }

        WriteEscapedString(name);
        _writer.Write(":"u8);
        _needsComma = false;
    }

    internal void WriteNumber(long value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        _writer.Write(Encoding.UTF8.GetBytes(text));
        _needsComma = true;
    }

    internal void WriteNumber(double value)
    {
        var text = EcmaScriptNumberFormatter.Format(value);
        _writer.Write(Encoding.UTF8.GetBytes(text));
        _needsComma = true;
    }

    internal void WriteString(string value)
    {
        WriteEscapedString(value);
        _needsComma = true;
    }

    private void WriteEscapedString(string value)
    {
        _writer.Write("\""u8);
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

        _writer.Write("\""u8);
    }

    private void WriteEscapedCodepoint(char c)
    {
        switch (c)
        {
            case '"':
                _writer.Write("\\\""u8);
                return;
            case '\\':
                _writer.Write("\\\\"u8);
                return;
            case '\b':
                _writer.Write("\\b"u8);
                return;
            case '\f':
                _writer.Write("\\f"u8);
                return;
            case '\n':
                _writer.Write("\\n"u8);
                return;
            case '\r':
                _writer.Write("\\r"u8);
                return;
            case '\t':
                _writer.Write("\\t"u8);
                return;
        }

        if (c < 0x20)
        {
            // Includes U+0000: emitted as \u0000 per RFC 8785, not rejected.
            _writer.Write(Encoding.UTF8.GetBytes($"\\u{(int)c:x4}"));
            return;
        }

        WriteUtf8Codepoint(c);
    }

    private void WriteUtf8Codepoint(int codepoint)
    {
        var chars = char.ConvertFromUtf32(codepoint);
        _writer.Write(Encoding.UTF8.GetBytes(chars));
    }
}
