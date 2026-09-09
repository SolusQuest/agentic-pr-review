using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

[JsonSerializable(typeof(ReplayManifest))]
[JsonSerializable(typeof(ReplayDiffDocument))]
[JsonSerializable(typeof(ReplayScript))]
[JsonSerializable(typeof(ReplayAssertions))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
    PropertyNameCaseInsensitive = false, MaxDepth = ReplayLimits.Depth)]
internal sealed partial class ReplayJsonContext : JsonSerializerContext;

internal static class ReplayJson
{
    internal static T? Read<T>(ReadOnlySpan<byte> bytes, JsonTypeInfo<T> type) where T : class
    {
        if (bytes.Length is < 1 or > ReplayLimits.ManifestBytes) return null;
        try
        {
            _ = ReplayLimits.Utf8.GetCharCount(bytes);
            return JsonSerializer.Deserialize(bytes, type);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or NotSupportedException)
        {
            return null;
        }
    }

    internal static byte[] Write(ReplayManifest manifest) => JsonSerializer.SerializeToUtf8Bytes(manifest, ReplayJsonContext.Default.ReplayManifest);
}
