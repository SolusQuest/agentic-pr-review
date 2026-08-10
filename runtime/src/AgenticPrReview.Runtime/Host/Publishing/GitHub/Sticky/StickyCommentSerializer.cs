using System.Text.Encodings.Web;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;

namespace AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;

internal sealed record StickyCommentMutationDocument(
    [property: JsonPropertyName("body")] string Body);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    WriteIndented = false, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(StickyCommentMutationDocument))]
internal sealed partial class StickyCommentJsonContext : JsonSerializerContext;

internal static class StickyCommentSerializer
{
    private static readonly StickyCommentJsonContext Context = new(
        new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.Default,
            PropertyNamingPolicy = null,
            WriteIndented = false,
        });

    internal static bool TrySerialize(string body, out byte[]? bytes)
    {
        bytes = null;
        try
        {
            var candidate = JsonSerializer.SerializeToUtf8Bytes(
                new StickyCommentMutationDocument(body),
                Context.StickyCommentMutationDocument);
            if (candidate.Length >
                BoundedGitHubPublisherPolicy.MaximumStickyRequestBytes)
                return false;
            bytes = candidate;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or
            NotSupportedException or EncoderFallbackException)
        {
            return false;
        }
    }
}
