using System.Text;
using System.Text.Json;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Quality.Incremental;

// Test-only provider input substitution. It reads real current tool output; it never creates an observation.
internal static class IncrementalArguments
{
    internal static string Expand(string arguments, JsonElement request)
    {
        if (!arguments.Contains("$current:", StringComparison.Ordinal)) return arguments;
        using var terminal = JsonDocument.Parse(arguments);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) Write(terminal.RootElement, writer, request);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static JsonElement? CurrentResult(JsonElement request, string callId)
    {
        var messages = request.GetProperty("messages").EnumerateArray().ToArray();
        var current = Array.FindLastIndex(messages, message => message.GetProperty("role").GetString() == "user");
        if (current < 0) return null;
        var matches = messages.Skip(current + 1).Where(message => message.GetProperty("role").GetString() == "tool" &&
            message.GetProperty("tool_call_id").GetString() == callId).ToArray();
        if (matches.Length != 1) return null;
        using var result = JsonDocument.Parse(matches[0].GetProperty("content").GetString()!);
        return result.RootElement.Clone();
    }

    private static void Write(JsonElement element, Utf8JsonWriter writer, JsonElement request)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (property.Name == "observation_id" && property.Value.ValueKind == JsonValueKind.String &&
                    property.Value.GetString() is { } text && text.StartsWith("$current:", StringComparison.Ordinal))
                {
                    var result = CurrentResult(request, text[9..]);
                    if (result is null || !result.Value.TryGetProperty("observation_id", out var id) ||
                        id.GetString() is not { Length: 64 } observation) throw new InvalidOperationException("incremental_current_missing");
                    writer.WriteStringValue(observation);
                }
                else Write(property.Value, writer, request);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var value in element.EnumerateArray()) Write(value, writer, request);
            writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }
}
