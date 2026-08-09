namespace AgenticPrReview.Runtime.Host.Publishing.Rendering;

internal static class R4StickyMarker
{
    internal const int MarkerLength = 237;
    internal const string LookingPrefix = "<!-- agentic-pr-review:r4:";

    private const string MarkerPrefix =
        "<!-- agentic-pr-review:r4:v1 scope_sha256=";
    private const string BodyField = " body_sha256=";
    private const string HeadField = " head_sha=";
    private const string MarkerSuffix = " -->";

    internal static string Create(R4PublicationIdentityV1 identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!R4PublicationIdentityV1.IsLowerHex(identity.ScopeSha256, 64) ||
            !R4PublicationIdentityV1.IsLowerHex(identity.BodySha256, 64) ||
            !R4PublicationIdentityV1.IsLowerHex(identity.HeadSha, 40))
        {
            throw new R4PublicationException(
                R4PublicationFailureCodes.IdentityInvalid);
        }

        var marker = string.Concat(
            MarkerPrefix,
            identity.ScopeSha256,
            BodyField,
            identity.BodySha256,
            HeadField,
            identity.HeadSha,
            MarkerSuffix);
        if (marker.Length != MarkerLength)
        {
            throw new R4PublicationException(
                R4PublicationFailureCodes.IdentityInvalid);
        }

        return marker;
    }

    internal static R4StickyInspection Inspect(string comment)
    {
        ArgumentNullException.ThrowIfNull(comment);
        var markerStart = comment.IndexOf(
            LookingPrefix,
            StringComparison.OrdinalIgnoreCase);
        if (markerStart < 0)
        {
            return R4StickyInspection.NoMarker();
        }

        if (comment.IndexOf(
                LookingPrefix,
                markerStart + LookingPrefix.Length,
                StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return R4StickyInspection.Invalid(
                R4StickyInvalidReason.Duplicate);
        }

        if (!comment.AsSpan(markerStart).StartsWith(
                LookingPrefix,
                StringComparison.Ordinal))
        {
            return R4StickyInspection.Invalid(
                R4StickyInvalidReason.WrongCase);
        }

        if (!R4Markdown.TryMeasure(comment, out _, out _))
        {
            return R4StickyInspection.Invalid(
                R4StickyInvalidReason.InvalidUnicode);
        }

        if (comment.Contains('\r', StringComparison.Ordinal))
        {
            return R4StickyInspection.Invalid(
                R4StickyInvalidReason.InvalidLf);
        }

        var markerEnd = comment.IndexOf(
            MarkerSuffix,
            markerStart,
            StringComparison.Ordinal);
        if (markerEnd < 0)
        {
            return R4StickyInspection.Invalid(
                R4StickyInvalidReason.Malformed);
        }

        markerEnd += MarkerSuffix.Length;
        var marker = comment[markerStart..markerEnd];
        if (markerEnd != comment.Length)
        {
            return R4StickyInspection.Invalid(
                TryParseExact(marker, out _)
                    ? R4StickyInvalidReason.TrailingBytes
                    : R4StickyInvalidReason.NonTerminal);
        }

        if (!TryParseExact(marker, out var identity))
        {
            return R4StickyInspection.Invalid(ClassifyGrammarFailure(marker));
        }

        if (markerStart < 2 ||
            comment[markerStart - 2] != '\n' ||
            comment[markerStart - 1] != '\n')
        {
            return R4StickyInspection.Invalid(
                R4StickyInvalidReason.Separator);
        }

        var body = comment[..(markerStart - 2)];
        if (!StringComparer.Ordinal.Equals(
                R4PublicationIdentityV1.ComputeBodySha256(body),
                identity!.BodySha256))
        {
            return R4StickyInspection.Invalid(
                R4StickyInvalidReason.BodyDigestMismatch);
        }

        return R4StickyInspection.Valid(body, identity);
    }

    private static bool TryParseExact(
        string marker,
        out R4PublicationIdentityV1? identity)
    {
        identity = null;
        if (marker.Length != MarkerLength)
        {
            return false;
        }

        var offset = 0;
        if (!ReadLiteral(marker, ref offset, MarkerPrefix) ||
            !ReadLowerHex(marker, ref offset, 64, out var scopeSha256) ||
            !ReadLiteral(marker, ref offset, BodyField) ||
            !ReadLowerHex(marker, ref offset, 64, out var bodySha256) ||
            !ReadLiteral(marker, ref offset, HeadField) ||
            !ReadLowerHex(marker, ref offset, 40, out var headSha) ||
            !ReadLiteral(marker, ref offset, MarkerSuffix) ||
            offset != marker.Length)
        {
            return false;
        }

        identity = new R4PublicationIdentityV1(
            scopeSha256!,
            bodySha256!,
            headSha!);
        return true;
    }

    private static R4StickyInvalidReason ClassifyGrammarFailure(string marker)
    {
        if (!marker.StartsWith(LookingPrefix, StringComparison.Ordinal))
        {
            return R4StickyInvalidReason.WrongCase;
        }

        if (!marker.AsSpan(LookingPrefix.Length).StartsWith(
                "v1 ",
                StringComparison.OrdinalIgnoreCase))
        {
            return R4StickyInvalidReason.WrongVersion;
        }

        if (!marker.AsSpan(LookingPrefix.Length).StartsWith(
                "v1 ",
                StringComparison.Ordinal) ||
            marker.StartsWith(MarkerPrefix, StringComparison.OrdinalIgnoreCase) &&
            !marker.StartsWith(MarkerPrefix, StringComparison.Ordinal))
        {
            return R4StickyInvalidReason.WrongCase;
        }

        if (!marker.StartsWith(MarkerPrefix, StringComparison.Ordinal))
        {
            return R4StickyInvalidReason.Malformed;
        }

        if (marker.Length > MarkerLength &&
            marker.EndsWith(MarkerSuffix, StringComparison.Ordinal))
        {
            return R4StickyInvalidReason.ExtraField;
        }

        if (FixedGrammarMatchesIgnoringCase(marker))
        {
            return R4StickyInvalidReason.WrongCase;
        }

        return R4StickyInvalidReason.Malformed;
    }

    private static bool FixedGrammarMatchesIgnoringCase(string marker)
    {
        if (marker.Length != MarkerLength)
        {
            return false;
        }

        var offset = 0;
        return ReadLiteral(
                marker,
                ref offset,
                MarkerPrefix,
                StringComparison.OrdinalIgnoreCase) &&
            ReadHexIgnoringCase(marker, ref offset, 64) &&
            ReadLiteral(
                marker,
                ref offset,
                BodyField,
                StringComparison.OrdinalIgnoreCase) &&
            ReadHexIgnoringCase(marker, ref offset, 64) &&
            ReadLiteral(
                marker,
                ref offset,
                HeadField,
                StringComparison.OrdinalIgnoreCase) &&
            ReadHexIgnoringCase(marker, ref offset, 40) &&
            ReadLiteral(marker, ref offset, MarkerSuffix) &&
            offset == marker.Length;
    }

    private static bool ReadLiteral(
        string value,
        ref int offset,
        string literal,
        StringComparison comparison = StringComparison.Ordinal)
    {
        if (offset > value.Length - literal.Length ||
            !value.AsSpan(offset, literal.Length).Equals(
                literal,
                comparison))
        {
            return false;
        }

        offset += literal.Length;
        return true;
    }

    private static bool ReadLowerHex(
        string value,
        ref int offset,
        int length,
        out string? result)
    {
        result = null;
        if (offset > value.Length - length)
        {
            return false;
        }

        var candidate = value.Substring(offset, length);
        if (!R4PublicationIdentityV1.IsLowerHex(candidate, length))
        {
            return false;
        }

        offset += length;
        result = candidate;
        return true;
    }

    private static bool ReadHexIgnoringCase(
        string value,
        ref int offset,
        int length)
    {
        if (offset > value.Length - length)
        {
            return false;
        }

        for (var index = 0; index < length; index++)
        {
            var character = value[offset + index];
            if (!(character is >= '0' and <= '9') &&
                !(character is >= 'a' and <= 'f') &&
                !(character is >= 'A' and <= 'F'))
            {
                return false;
            }
        }

        offset += length;
        return true;
    }
}
