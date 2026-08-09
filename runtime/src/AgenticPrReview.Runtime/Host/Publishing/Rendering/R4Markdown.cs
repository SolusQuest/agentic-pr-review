using System.Buffers;
using System.Globalization;
using System.Text;

namespace AgenticPrReview.Runtime.Host.Publishing.Rendering;

internal enum R4BodyTextValidation
{
    Valid,
    InvalidUnicode,
    InvalidLineBreak,
}

internal static class R4Markdown
{
    private const string UnsafeAscii = "\\`*_{}[]()#+-.!|:>~=";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length);
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(
                remaining,
                out var rune,
                out var consumed);
            if (status != OperationStatus.Done)
            {
                throw new R4PublicationException(
                    R4PublicationFailureCodes.ReviewInvalid);
            }

            remaining = remaining[consumed..];
            if (rune.Value == '\n')
            {
                builder.Append('\n');
                continue;
            }

            if (rune.Value == '&')
            {
                builder.Append("&amp;");
                continue;
            }

            if (rune.Value == '<')
            {
                builder.Append("&lt;");
                continue;
            }

            if (rune.IsAscii && UnsafeAscii.Contains((char)rune.Value))
            {
                AppendScalarEntity(builder, rune.Value);
                continue;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or
                UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or
                UnicodeCategory.ParagraphSeparator)
            {
                AppendVisibleScalarNotation(builder, rune.Value);
                continue;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    internal static bool TryMeasure(
        string? value,
        out int scalarCount,
        out int utf8Bytes)
    {
        scalarCount = 0;
        utf8Bytes = 0;
        if (value is null)
        {
            return false;
        }

        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(
                remaining,
                out _,
                out var consumed);
            if (status != OperationStatus.Done)
            {
                return false;
            }

            scalarCount++;
            remaining = remaining[consumed..];
        }

        try
        {
            utf8Bytes = StrictUtf8.GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            scalarCount = 0;
            utf8Bytes = 0;
            return false;
        }
    }

    internal static R4BodyTextValidation ValidateBodyText(string? value)
    {
        if (value is null)
        {
            return R4BodyTextValidation.InvalidUnicode;
        }

        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(
                remaining,
                out var rune,
                out var consumed);
            if (status != OperationStatus.Done)
            {
                return R4BodyTextValidation.InvalidUnicode;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (rune.Value is '\r' or '\v' or '\f' or 0x85 ||
                category is UnicodeCategory.LineSeparator or
                    UnicodeCategory.ParagraphSeparator)
            {
                return R4BodyTextValidation.InvalidLineBreak;
            }

            remaining = remaining[consumed..];
        }

        return R4BodyTextValidation.Valid;
    }

    private static void AppendScalarEntity(StringBuilder builder, int value)
    {
        builder.Append("&#x");
        builder.Append(value.ToString("X", CultureInfo.InvariantCulture));
        builder.Append(';');
    }

    private static void AppendVisibleScalarNotation(
        StringBuilder builder,
        int value)
    {
        builder.Append("U+");
        builder.Append(value.ToString(
            value <= 0xffff ? "X4" : "X6",
            CultureInfo.InvariantCulture));
    }
}

internal static class R4PublicationBudget
{
    internal const int MaximumScalars = 50_000;
    internal const int MaximumUtf8Bytes = 204_800;

    internal static bool Fits(
        string value,
        int maximumScalars,
        int maximumUtf8Bytes) =>
        maximumScalars >= 0 &&
        maximumUtf8Bytes >= 0 &&
        R4Markdown.TryMeasure(value, out var scalars, out var bytes) &&
        scalars <= maximumScalars &&
        bytes <= maximumUtf8Bytes;
}
