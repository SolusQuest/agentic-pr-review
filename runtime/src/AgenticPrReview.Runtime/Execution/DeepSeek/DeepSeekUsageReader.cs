using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;

namespace AgenticPrReview.Runtime.Execution.DeepSeek;

// Shared bounded numeric observation only; no response/tool admission or transport
// capability. Raw failed-response bytes remain inside the credential-owning adapter.
internal static class DeepSeekUsageReader
{
    internal static void ObserveError(ReadOnlyMemory<byte> body, IProviderUsageObserver? observer) =>
        observer?.RecordUsage(ReadErrorUsage(body));

    private static ProviderUsageObservation ReadErrorUsage(ReadOnlyMemory<byte> body)
    {
        if (body.Length >= DeepSeekTransportPolicy.ErrorBodyDiscardMaxBytes)
            return ProviderUsageObservation.Unknown;
        try
        {
            if (!HasUniquePropertyNames(body.Span)) return ProviderUsageObservation.Unknown;
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 64 });
            return ReadAccountingUsage(document.RootElement);
        }
        catch (JsonException) { return ProviderUsageObservation.Unknown; }
    }

    internal static ProviderUsageObservation ReadAccountingUsage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("usage", out var usage) ||
            usage.ValueKind != JsonValueKind.Object)
            return ProviderUsageObservation.Unknown;

        return ProviderUsageObservation.Create(
            ReadCounter(usage, "prompt_tokens"),
            ReadCounter(usage, "completion_tokens"),
            ReadCounter(usage, "prompt_cache_hit_tokens"),
            ReadCounter(usage, "prompt_cache_miss_tokens"),
            ReadCounter(usage, "total_tokens"));
    }

    private static long? ReadCounter(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number &&
        element.TryGetInt64(out var value) && value >= 0 ? value : null;

    internal static bool HasUniquePropertyNames(ReadOnlySpan<byte> body)
    {
        var reader = new Utf8JsonReader(
            body,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
        var scopes = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                continue;
            }

            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (scopes.Count == 0)
                {
                    return false;
                }

                scopes.Pop();
                continue;
            }

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                if (name is null || scopes.Count == 0 ||
                    !scopes.Peek().Add(name))
                {
                    return false;
                }
            }
        }

        return scopes.Count == 0;
    }
}
