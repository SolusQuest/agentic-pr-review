using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticPrReview.Runtime.AgentLoopAotFixture;

[JsonSerializable(typeof(ProofOutput))]
[JsonSerializable(typeof(HostLineageFile))]
[JsonSerializable(typeof(ContinuationPayload))]
[JsonSerializable(typeof(ProviderRequestDto))]
[JsonSerializable(typeof(ProviderResponseDto))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Default)]
internal sealed partial class ProofJsonContext : JsonSerializerContext;

internal static class ProofJson
{
    internal static byte[] Write(ProofOutput value) =>
        JsonSerializer.SerializeToUtf8Bytes(
            value,
            ProofJsonContext.Default.ProofOutput);

    internal static byte[] Write(HostLineageFile value) =>
        JsonSerializer.SerializeToUtf8Bytes(
            value,
            ProofJsonContext.Default.HostLineageFile);

    internal static HostLineageFile? ReadLineage(ReadOnlySpan<byte> bytes) =>
        JsonSerializer.Deserialize(
            bytes,
            ProofJsonContext.Default.HostLineageFile);

    internal static byte[] Write(ContinuationPayload value) =>
        JsonSerializer.SerializeToUtf8Bytes(
            value,
            ProofJsonContext.Default.ContinuationPayload);

    internal static ContinuationPayload? ReadContinuation(
        ReadOnlySpan<byte> bytes) =>
        JsonSerializer.Deserialize(
            bytes,
            ProofJsonContext.Default.ContinuationPayload);
}
