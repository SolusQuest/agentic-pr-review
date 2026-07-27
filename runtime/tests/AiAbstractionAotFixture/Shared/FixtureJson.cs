using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AgenticPrReview.Runtime.AiAbstractionFixture;

internal static class FixtureJson
{
    internal static FirstFixtureInput ReadFirstInput(string path) =>
        Read(path, FixtureJsonContext.Default.FirstFixtureInput);

    internal static ResumeFixtureInput ReadResumeInput(string path) =>
        Read(path, FixtureJsonContext.Default.ResumeFixtureInput);

    internal static ProofState ReadState(string path) =>
        Read(path, FixtureJsonContext.Default.ProofState);

    internal static FirstEvidence ReadFirstEvidence(string path) =>
        Read(path, FixtureJsonContext.Default.FirstEvidence);

    internal static byte[] Serialize(ProofState value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, FixtureJsonContext.Default.ProofState);

    internal static byte[] Serialize(FirstEvidence value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, FixtureJsonContext.Default.FirstEvidence);

    internal static byte[] Serialize(ResumeEvidence value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, FixtureJsonContext.Default.ResumeEvidence);

    internal static byte[] Serialize(CombinedEvidence value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, FixtureJsonContext.Default.CombinedEvidence);

    internal static byte[] Serialize(NativeRequestObservation[] value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, FixtureJsonContext.Default.NativeRequestObservationArray);

    internal static byte[] Serialize(LogicalRecord[] value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, FixtureJsonContext.Default.LogicalRecordArray);

    internal static T Deserialize<T>(byte[] bytes, JsonTypeInfo<T> typeInfo)
    {
        EnsureNoDuplicateProperties(bytes);
        return JsonSerializer.Deserialize(bytes, typeInfo) ??
            throw new FixtureFailure("APR_AI_SERIALIZATION");
    }

    private static T Read<T>(string path, JsonTypeInfo<T> typeInfo)
    {
        var bytes = File.ReadAllBytes(path);
        return Deserialize(bytes, typeInfo);
    }

    private static void EnsureNoDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64,
        });
        var objectPropertySets = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objectPropertySets.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    objectPropertySets.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (objectPropertySets.Count == 0 ||
                        !objectPropertySets.Peek().Add(reader.GetString()!))
                    {
                        throw new FixtureFailure("APR_AI_SERIALIZATION");
                    }
                    break;
            }
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(FirstFixtureInput))]
[JsonSerializable(typeof(ResumeFixtureInput))]
[JsonSerializable(typeof(ProofState))]
[JsonSerializable(typeof(FirstEvidence))]
[JsonSerializable(typeof(ResumeEvidence))]
[JsonSerializable(typeof(CombinedEvidence))]
[JsonSerializable(typeof(NativeRequestObservation[]))]
[JsonSerializable(typeof(LogicalRecord[]))]
internal partial class FixtureJsonContext : JsonSerializerContext;
