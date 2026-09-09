using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Reporting;

[JsonSerializable(typeof(EvaluationReportDocument))]
[JsonSerializable(typeof(ReportComparisonDocument))]
[JsonSerializable(typeof(ReportingDiagnostic))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    UseStringEnumConverter = true,
    MaxDepth = ReportingLimits.Depth)]
internal sealed partial class EvaluationReportJsonContext : JsonSerializerContext;

internal sealed record ReportingDiagnostic(
    [property: JsonRequired] ReportingError Error,
    [property: JsonRequired] int SubmittedRows);

internal static class EvaluationReportJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static ReportingResult<byte[]> Write(EvaluationReport? report, int maximumBytes = ReportingLimits.JsonBytes)
    {
        if (report is null) return new(null, ReportingError.InvalidInput);
        return Bound(JsonSerializer.SerializeToUtf8Bytes(report.Document, EvaluationReportJsonContext.Default.EvaluationReportDocument), maximumBytes);
    }

    internal static ReportingResult<byte[]> Write(EvaluationReportComparison? comparison, int maximumBytes = ReportingLimits.JsonBytes)
    {
        if (comparison is null) return new(null, ReportingError.InvalidInput);
        return Bound(JsonSerializer.SerializeToUtf8Bytes(comparison.Document, EvaluationReportJsonContext.Default.ReportComparisonDocument), maximumBytes);
    }

    internal static byte[] WriteFailure(ReportingError error, int submittedRows) => JsonSerializer.SerializeToUtf8Bytes(
        new ReportingDiagnostic(Enum.IsDefined(error) && error != ReportingError.None ? error : ReportingError.InvalidInput,
            Math.Max(0, submittedRows)), EvaluationReportJsonContext.Default.ReportingDiagnostic);

    internal static ReportingResult<EvaluationReport> Read(ReadOnlySpan<byte> input)
    {
        ReportingResult<EvaluationReport> Reject() => new(null, ReportingError.InvalidReport);
        if (input.Length is < 1 or > ReportingLimits.JsonBytes) return Reject();
        try
        {
            _ = StrictUtf8.GetCharCount(input);
            var parsed = JsonSerializer.Deserialize(input, EvaluationReportJsonContext.Default.EvaluationReportDocument);
            if (parsed is null || parsed.Outcomes.IsDefault || parsed.Outcomes.Length > ReportingLimits.Rows) return Reject();
            using var raw = JsonDocument.Parse(input.ToArray(), new JsonDocumentOptions { MaxDepth = ReportingLimits.Depth });
            // Preserve the original embedded bytes/tokens for Q1's strict enum and input admission.
            var rows = raw.RootElement.GetProperty("outcomes").EnumerateArray()
                .Select(row => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(row.GetRawText())).ToImmutableArray();
            var rebuilt = EvaluationReport.Create(rows);
            if (!rebuilt.Succeeded) return Reject();
            var expected = JsonSerializer.SerializeToElement(rebuilt.Value!.Document, EvaluationReportJsonContext.Default.EvaluationReportDocument);
            return JsonElement.DeepEquals(raw.RootElement, expected) ? rebuilt : Reject();
        }
        catch (Exception e) when (e is JsonException or DecoderFallbackException or NotSupportedException)
        {
            return Reject();
        }
    }

    // A comparison is checked against its selected reports, not accepted as proof of unseen inputs.
    internal static ReportingResult<EvaluationReportComparison> ReadComparison(ReadOnlySpan<byte> input,
        EvaluationReport? baseline, EvaluationReport? candidate)
    {
        ReportingResult<EvaluationReportComparison> Reject() => new(null, ReportingError.InvalidReport);
        if (input.Length is < 1 or > ReportingLimits.JsonBytes) return Reject();
        var expected = EvaluationReportComparison.Create(baseline, candidate);
        if (!expected.Succeeded) return Reject();
        try
        {
            _ = StrictUtf8.GetCharCount(input);
            if (JsonSerializer.Deserialize(input, EvaluationReportJsonContext.Default.ReportComparisonDocument) is null) return Reject();
            using var raw = JsonDocument.Parse(input.ToArray(), new JsonDocumentOptions { MaxDepth = ReportingLimits.Depth });
            var projected = JsonSerializer.SerializeToElement(expected.Value!.Document, EvaluationReportJsonContext.Default.ReportComparisonDocument);
            return JsonElement.DeepEquals(raw.RootElement, projected) ? expected : Reject();
        }
        catch (Exception e) when (e is JsonException or DecoderFallbackException or NotSupportedException)
        {
            return Reject();
        }
    }

    private static ReportingResult<byte[]> Bound(byte[] value, int maximumBytes) =>
        maximumBytes is < 1 or > ReportingLimits.JsonBytes ? new(null, ReportingError.InvalidOutputBudget) :
        value.Length > maximumBytes ? new(null, ReportingError.OutputLimit) : new(value, ReportingError.None);
}
