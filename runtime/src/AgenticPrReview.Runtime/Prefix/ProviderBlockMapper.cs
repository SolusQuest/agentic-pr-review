using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Prefix;

/// <summary>
/// Reference canonical provider-block projection (issue #50, D6): each
/// logical segment maps to exactly one canonical provider block; no merging,
/// splitting, or reordering.
/// </summary>
internal static class ProviderBlockMapper
{
    internal static string RoleFor(string segmentKind) => segmentKind switch
    {
        LogicalProjection.TemplateKind => "system",
        LogicalProjection.PolicyKind => "system",
        LogicalProjection.ToolsKind => "system",
        LogicalProjection.ReviewContextKind => "user",
        LogicalProjection.ReviewOutcomeKind => "assistant",
        _ => throw new ArgumentOutOfRangeException(nameof(segmentKind), segmentKind, null),
    };

    internal static ImmutableArray<byte> MapBlock(string segmentKind, ReadOnlySpan<byte> segmentCanonicalJson)
    {
        return MapTextBlock(RoleFor(segmentKind), Encoding.UTF8.GetString(segmentCanonicalJson));
    }

    internal static ImmutableArray<byte> MapSystemTextBlock(string text, out bool capExceeded)
    {
        return MapTextBlock("system", text, bounded: true, out capExceeded);
    }

    private static ImmutableArray<byte> MapTextBlock(string role, string text)
    {
        return MapTextBlock(role, text, bounded: false, out _);
    }

    private static ImmutableArray<byte> MapTextBlock(
        string role,
        string text,
        bool bounded,
        out bool capExceeded)
    {
        var initialCapacity = bounded
            ? (int)Math.Min(PrefixBounds.MaxProviderBlockPayloadBytes, (long)Encoding.UTF8.GetByteCount(text) + 64)
            : checked(Encoding.UTF8.GetByteCount(text) * 2 + 64);
        var writer = new Rfc8785Writer(initialCapacity)
        {
            DiscardLimit = bounded ? PrefixBounds.MaxProviderBlockPayloadBytes : -1,
        };
        writer.WriteObjectStart();
        writer.WriteProperty("content");
        writer.WriteArrayStart();
        writer.WriteObjectStart();
        writer.WriteProperty("text");
        writer.WriteString(text);
        writer.WriteProperty("type");
        writer.WriteString("text");
        writer.WriteObjectEnd();
        writer.WriteArrayEnd();
        writer.WriteProperty("role");
        writer.WriteString(role);
        writer.WriteObjectEnd();
        capExceeded = writer.Exceeded;
        return writer.ToImmutableArray();
    }
}
