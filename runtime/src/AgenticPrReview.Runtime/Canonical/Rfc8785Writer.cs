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
            Append(Encoding.UTF8.GetBytes($"\\u{(int)c:x4}"));
            return;
        }

        WriteUtf8Codepoint(c);
    }

    private void WriteUtf8Codepoint(int codepoint)
    {
        var chars = char.ConvertFromUtf32(codepoint);
        Append(Encoding.UTF8.GetBytes(chars));
    }
}
