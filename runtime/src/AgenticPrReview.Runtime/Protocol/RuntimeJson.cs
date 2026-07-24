using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticPrReview.Runtime;

public sealed record ReviewResult(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("runtimeVersion")] string RuntimeVersion,
    [property: JsonPropertyName("inputSha256")] string InputSha256,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("findings")] RuntimeFinding[] Findings,
    [property: JsonPropertyName("limitations")] string[] Limitations,
    [property: JsonPropertyName("warnings")] string[] Warnings,
    [property: JsonPropertyName("diagnostics")] RuntimeDiagnostic[] Diagnostics,
    [property: JsonPropertyName("trace")] ReviewTraceReference Trace);

public sealed record ReviewTrace(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("runtimeVersion")] string RuntimeVersion,
    [property: JsonPropertyName("inputSha256")] string InputSha256,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("fixture")] string? Fixture,
    [property: JsonPropertyName("toolCalls")] RuntimeToolCall[] ToolCalls,
    [property: JsonPropertyName("warnings")] string[] Warnings,
    [property: JsonPropertyName("diagnostics")] RuntimeDiagnostic[] Diagnostics);

public sealed record ReviewTraceReference(
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record RuntimeDiagnostic(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("level")] string Level);

public sealed record RuntimeFinding(
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("confidence")] string Confidence,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("startLine")] int? StartLine,
    [property: JsonPropertyName("endLine")] int? EndLine,
    [property: JsonPropertyName("evidence")] string? Evidence = null,
    [property: JsonPropertyName("suggestedAction")] string? SuggestedAction = null,
    [property: JsonPropertyName("inlinePreference")] string? InlinePreference = null);

public sealed record RuntimeToolCall(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status);

internal static class RuntimeJson
{
    public static byte[] SerializeResult(ReviewResult result) => JsonSerializer.SerializeToUtf8Bytes(result, RuntimeJsonContext.Default.ReviewResult);
    public static byte[] SerializeTrace(ReviewTrace trace) => JsonSerializer.SerializeToUtf8Bytes(trace, RuntimeJsonContext.Default.ReviewTrace);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ReviewResult))]
[JsonSerializable(typeof(ReviewTrace))]
[JsonSerializable(typeof(ReviewInput))]
internal partial class RuntimeJsonContext : JsonSerializerContext
{
}
