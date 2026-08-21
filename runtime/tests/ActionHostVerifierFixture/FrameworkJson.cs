using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

internal static class FrameworkJson
{
    internal static string Serialize(JsonNode value) =>
        JsonSerializer.Serialize(value, FrameworkJsonContext.Default.JsonNode);

    internal static string SerializeIndented(JsonNode value) =>
        JsonSerializer.Serialize(
            value,
            FrameworkIndentedJsonContext.Default.JsonNode);

    internal static byte[] SerializeToUtf8Bytes(JsonNode value) =>
        JsonSerializer.SerializeToUtf8Bytes(
            value,
            FrameworkJsonContext.Default.JsonNode);

    internal static JsonObject Object(
        params (string Name, object? Value)[] properties)
    {
        var value = new JsonObject();
        foreach (var property in properties)
        {
            value.Add(property.Name, Node(property.Value));
        }

        return value;
    }

    internal static JsonArray Array(IEnumerable<object?> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(Node(value));
        }

        return array;
    }

    internal static JsonNode? Element(JsonElement value) =>
        JsonNode.Parse(value.GetRawText());

    private static JsonNode? Node(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        string text => JsonValue.Create(text),
        bool boolean => JsonValue.Create(boolean),
        int integer => JsonValue.Create(integer),
        long integer => JsonValue.Create(integer),
        _ => throw new ArgumentException("Unsupported JSON proof value.",
            nameof(value)),
    };
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(JsonNode))]
internal sealed partial class FrameworkJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    WriteIndented = true,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(JsonNode))]
internal sealed partial class FrameworkIndentedJsonContext :
    JsonSerializerContext;
