using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

[JsonSerializable(typeof(EvaluationCaseInput))]
[JsonSerializable(typeof(EvaluationRunInput))]
[JsonSerializable(typeof(EvaluationAdjudication))]
[JsonSerializable(typeof(EvaluationOutcome))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    UseStringEnumConverter = true,
    MaxDepth = EvaluationLimits.Depth,
    GenerationMode = JsonSourceGenerationMode.Default)]
internal sealed partial class EvaluationJsonContext : JsonSerializerContext;

internal static class EvaluationJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal static byte[] Write(EvaluationCaseInput input) =>
        JsonSerializer.SerializeToUtf8Bytes(input, EvaluationJsonContext.Default.EvaluationCaseInput);
    internal static byte[] Write(EvaluationRunInput input) =>
        JsonSerializer.SerializeToUtf8Bytes(input, EvaluationJsonContext.Default.EvaluationRunInput);
    internal static byte[] Write(EvaluationAdjudication input) =>
        JsonSerializer.SerializeToUtf8Bytes(input, EvaluationJsonContext.Default.EvaluationAdjudication);
    internal static byte[] Write(EvaluationOutcome outcome) =>
        JsonSerializer.SerializeToUtf8Bytes(outcome, EvaluationJsonContext.Default.EvaluationOutcome);
    internal static EvaluationCase? ReadCase(ReadOnlySpan<byte> input) =>
        EvaluationCase.Admit(Read(input, EvaluationJsonContext.Default.EvaluationCaseInput));
    internal static EvaluationRunInput? ReadRun(ReadOnlySpan<byte> input) =>
        Read(input, EvaluationJsonContext.Default.EvaluationRunInput) is { Valid: true } run ? run : null;
    internal static EvaluationAdjudication? ReadAdjudication(ReadOnlySpan<byte> input) =>
        Read(input, EvaluationJsonContext.Default.EvaluationAdjudication) is { Valid: true } annotation ? annotation : null;

    internal static EvaluationOutcome? ReadOutcome(ReadOnlySpan<byte> input)
    {
        var outcome = Read(input, EvaluationJsonContext.Default.EvaluationOutcome);
        if (outcome is null || !EvaluationOutcomeAdmission.Valid(outcome)) return null;
        // Source-generated enum metadata also accepts numeric enum values; reports use exact named codes only.
        using var document = JsonDocument.Parse(input.ToArray(), new JsonDocumentOptions { MaxDepth = EvaluationLimits.Depth });
        foreach (var (name, expected) in new[]
        {
            ("execution_status", outcome.ExecutionStatus.ToString()), ("evidence_status", outcome.EvidenceStatus.ToString()),
            ("scenario_status", outcome.ScenarioStatus.ToString()), ("model_status", outcome.ModelStatus.ToString()),
            ("code", outcome.Code.ToString()), ("failure_source", outcome.FailureSource.ToString()),
            ("failure_kind", outcome.FailureKind.ToString()),
        })
        {
            var value = document.RootElement.GetProperty(name);
            if (value.ValueKind != JsonValueKind.String || value.GetString() != expected) return null;
        }
        return outcome;
    }

    private static T? Read<T>(ReadOnlySpan<byte> input, JsonTypeInfo<T> type) where T : class
    {
        if (input.Length is < 1 or > EvaluationLimits.InputBytes) return null;
        try
        {
            _ = StrictUtf8.GetCharCount(input);
            return JsonSerializer.Deserialize(input, type);
        }
        catch (Exception e) when (e is JsonException or DecoderFallbackException or NotSupportedException)
        {
            return null;
        }
    }
}
