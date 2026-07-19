using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace AgenticPrReview.Runtime.Canonical;

/// <summary>
/// Low-level RFC 8785 canonical JSON writer used by the ledger (#49) and the
/// prefix contract (#50). Extracted verbatim from LedgerCanonicalizer; the
/// ledger call paths must remain byte-identical.
/// </summary>
internal struct CanonicalJsonWriter
{
    private readonly ArrayBufferWriter<byte> _writer;
    private bool _needsComma;

    internal CanonicalJsonWriter(int initialCapacity)
    {
        _writer = new ArrayBufferWriter<byte>(initialCapacity);
        _needsComma = false;
    }

    internal ImmutableArray<byte> ToImmutableArray() => _writer.WrittenSpan.ToArray().ToImmutableArray();

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
    internal void WriteNull() { _writer.Write("null"u8); _needsComma = true; }
    internal void WriteBoolean(bool value) { _writer.Write(value ? "true"u8 : "false"u8); _needsComma = true; }

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
                    throw new LedgerCanonicalizationException("Unpaired UTF-16 surrogate encountered during canonicalization.");
                }
            }
            else if (char.IsLowSurrogate(c))
            {
                throw new LedgerCanonicalizationException("Unpaired UTF-16 surrogate encountered during canonicalization.");
            }
            else if (c == '\0')
            {
                throw new LedgerCanonicalizationException("U+0000 encountered during canonicalization.");
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

internal sealed class LedgerCanonicalizationException : Exception
{
    public LedgerCanonicalizationException(string message) : base(message)
    {
    }
}
