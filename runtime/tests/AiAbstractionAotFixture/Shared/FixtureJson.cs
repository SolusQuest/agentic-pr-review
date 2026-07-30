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

    internal static byte[] Serialize(ProofState value)
    {
        EnsureProofStateAdmissible(value);
        return JsonSerializer.SerializeToUtf8Bytes(
            value,
            FixtureJsonContext.Default.ProofState);
    }

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

    internal static void EnsureProofStateAdmissible(object? value)
    {
        switch (value)
        {
            case null:
            case string:
            case int:
                return;
            case ProofState state:
                EnsureProofStateAdmissible(state.Format);
                EnsureProofStateAdmissible(state.Candidate);
                EnsureProofStateAdmissible(state.ProviderId);
                EnsureProofStateAdmissible(state.ModelId);
                EnsureProofStateAdmissible(state.AdapterId);
                EnsureProofStateAdmissible(state.SessionId);
                EnsureProofStateAdmissible(state.Records);
                EnsureProofStateAdmissible(state.Continuations);
                EnsureProofStateAdmissible(state.Review);
                return;
            case LogicalRecord record:
                EnsureProofStateAdmissible(record.Kind);
                EnsureProofStateAdmissible(record.Role);
                EnsureProofStateAdmissible(record.Text);
                EnsureProofStateAdmissible(record.CallId);
                EnsureProofStateAdmissible(record.ToolName);
                EnsureProofStateAdmissible(record.Result);
                EnsureProofStateAdmissible(record.MessagePosition);
                EnsureProofStateAdmissible(record.ContentPosition);
                return;
            case StoredContinuation continuation:
                EnsureProofStateAdmissible(continuation.Readable);
                EnsureProofStateAdmissible(continuation.Opaque);
                EnsureProofStateAdmissible(continuation.Framing);
                EnsureProofStateAdmissible(continuation.AssociatedCallId);
                EnsureProofStateAdmissible(continuation.MessagePosition);
                EnsureProofStateAdmissible(continuation.ContentPosition);
                EnsureProofStateAdmissible(continuation.ReadableSha256);
                EnsureProofStateAdmissible(continuation.OpaqueSha256);
                return;
            case TerminalReview review:
                EnsureProofStateAdmissible(review.Summary);
                EnsureProofStateAdmissible(review.Findings);
                return;
            case LogicalRecord[] records:
                foreach (var record in records)
                {
                    EnsureProofStateAdmissible(record);
                }
                return;
            case StoredContinuation[] continuations:
                foreach (var continuation in continuations)
                {
                    EnsureProofStateAdmissible(continuation);
                }
                return;
            case string[] strings:
                foreach (var text in strings)
                {
                    EnsureProofStateAdmissible(text);
                }
                return;
            default:
                throw new FixtureFailure("APR_AI_SERIALIZATION");
        }
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
