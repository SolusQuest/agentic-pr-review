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
        var segmentText = Encoding.UTF8.GetString(segmentCanonicalJson);
        var writer = new Rfc8785Writer(segmentCanonicalJson.Length * 2 + 64);
        writer.WriteObjectStart();
        writer.WriteProperty("content");
        writer.WriteArrayStart();
        writer.WriteObjectStart();
        writer.WriteProperty("text");
        writer.WriteString(segmentText);
        writer.WriteProperty("type");
        writer.WriteString("text");
        writer.WriteObjectEnd();
        writer.WriteArrayEnd();
        writer.WriteProperty("role");
        writer.WriteString(RoleFor(segmentKind));
        writer.WriteObjectEnd();
        return writer.ToImmutableArray();
    }
}
