using AgenticPrReview.Runtime.Agent.Chat;
using System.Text.Json;

namespace AgenticPrReview.Runtime.AiAbstractionFixture;

internal enum FixturePhase
{
    First,
    Resume,
}

internal interface ICandidateHarness
{
    string CandidateName { get; }
    string AdapterId { get; }
    IProjectChatClient ChatClient { get; }
    CandidateProbe Probe { get; }
    object MaterializeCandidateRequest(ProjectChatRequest request);
}

internal sealed class CandidateProbe
{
    private readonly List<NativeRequestObservation> _requests = [];

    internal IReadOnlyList<NativeRequestObservation> Requests => _requests;

    internal void Add(NativeRequestObservation observation) => _requests.Add(observation);
}

internal sealed record NativeRequestObservation(
    ObservedMessage[] Messages,
    ObservedToolDeclaration[] Tools,
    ObservedContinuation[] Continuations);

internal sealed record ObservedToolDeclaration(
    string Name,
    string DescriptionSha256,
    string CanonicalSchemaSha256);

internal sealed record ObservedMessage(
    string Role,
    ObservedContent[] Contents);

internal sealed record ObservedContent(
    string Kind,
    string? CallId,
    string? Name,
    string? TextSha256,
    string? OpaqueSha256,
    string? Framing,
    string? AssociatedCallId,
    int Position);

internal sealed record ObservedContinuation(
    string ProviderId,
    string ModelId,
    string AdapterId,
    string SessionId,
    string ReadableSha256,
    string OpaqueSha256,
    string Framing,
    string? AssociatedCallId,
    int MessagePosition,
    int ContentPosition);

internal static class NativeObservation
{
    internal static ObservedToolDeclaration Tool(
        string name,
        string description,
        string schemaJson)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(description))
        {
            throw new FixtureFailure("APR_AI_TOOL_SCHEMA");
        }
        using var document = JsonDocument.Parse(schemaJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("type", out var type) ||
            type.GetString() != "object" ||
            !root.TryGetProperty("additionalProperties", out var additional) ||
            additional.ValueKind != JsonValueKind.False ||
            !root.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("required", out var required) ||
            required.ValueKind != JsonValueKind.Array)
        {
            throw new FixtureFailure("APR_AI_TOOL_SCHEMA");
        }
        var propertyNames = properties.EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        var requiredNames = required.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : throw new FixtureFailure("APR_AI_TOOL_SCHEMA"))
            .ToArray();
        if (propertyNames.Distinct(StringComparer.Ordinal).Count() !=
                propertyNames.Length ||
            requiredNames.Distinct(StringComparer.Ordinal).Count() !=
                requiredNames.Length ||
            !propertyNames.ToHashSet(StringComparer.Ordinal)
                .SetEquals(requiredNames))
        {
            throw new FixtureFailure("APR_AI_TOOL_SCHEMA");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, root);
        }
        return new ObservedToolDeclaration(
            name,
            FixtureHash.Text(description),
            FixtureHash.Bytes(stream.ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new FixtureFailure("APR_AI_TOOL_SCHEMA");
        }
    }
}
