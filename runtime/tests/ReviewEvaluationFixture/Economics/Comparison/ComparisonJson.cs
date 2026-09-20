using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Histories;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Prefix;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Comparison;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
    RespectRequiredConstructorParameters = true, UseStringEnumConverter = true, MaxDepth = 32)]
[JsonSerializable(typeof(ComparisonInput))]
[JsonSerializable(typeof(ComparisonDeclaration))]
[JsonSerializable(typeof(ComparisonEvidence))]
[JsonSerializable(typeof(ComparisonReportDocument))]
[JsonSerializable(typeof(PrefixObservation))]
[JsonSerializable(typeof(PrefixDomain))]
internal sealed partial class ComparisonJsonContext : JsonSerializerContext;

internal static class ComparisonJson
{
    internal static string OutcomeHash(EvaluationOutcome value) =>
        AgentCanonical.HashDomain("apr.r6.comparison.outcome", EvaluationJson.Write(value));
    internal static string HistoryHash(HistoryReport value) =>
        AgentCanonical.HashDomain("apr.r6.comparison.history", HistoryJson.Write(value));
    internal static string ObservationHash(PrefixObservation value) => AgentCanonical.HashDomain(
        "apr.r6.comparison.observation", JsonSerializer.SerializeToUtf8Bytes(value, ComparisonJsonContext.Default.PrefixObservation));
    internal static string EvidenceHash(ComparisonEvidence value) => AgentCanonical.HashDomain(
        "apr.r6.comparison.evidence", JsonSerializer.SerializeToUtf8Bytes(value, ComparisonJsonContext.Default.ComparisonEvidence));
    internal static string DeclarationHash(ComparisonDeclaration value) => AgentCanonical.HashDomain(
        "apr.r6.comparison.declaration", JsonSerializer.SerializeToUtf8Bytes(value, ComparisonJsonContext.Default.ComparisonDeclaration));

    internal static ComparisonSelection Select(PricingReportDocument price, ComparisonEvidence evidence)
    {
        var source = price.Journal.Provenance;
        return new(source.SourceCommit, source.SourceTree, source.SourceClean, source.BuildId,
            source.PlanSha256, price.JournalSha256, price.TariffSha256, EvidenceHash(evidence));
    }

    internal static byte[] WriteInput(ComparisonInput input) =>
        JsonSerializer.SerializeToUtf8Bytes(input, ComparisonJsonContext.Default.ComparisonInput);

    internal static ComparisonInput? ReadInput(ReadOnlySpan<byte> bytes)
    {
        var input = PricingJson.ReadValue(bytes, ComparisonJsonContext.Default.ComparisonInput, ComparisonLimits.InputBytes, 24);
        if (input is null) return null;
        try
        {
            using var json = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 24 });
            var raw = json.RootElement;
            // Admit original nested bytes: materialization must not normalize invalid enum strings/numbers.
            if (PricingJson.Read(Bytes(raw.GetProperty("pricing"))) is null) return null;
            var evidence = raw.GetProperty("evidence");
            foreach (var outcome in evidence.GetProperty("outcomes").EnumerateArray())
                if (EvaluationJson.ReadOutcome(Bytes(outcome)) is null) return null;
            foreach (var history in evidence.GetProperty("histories").EnumerateArray())
                if (HistoryJson.Read(Bytes(history)) is null) return null;
            return ComparisonAdmission.Valid(input) ? input : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or
            NullReferenceException or OverflowException or KeyNotFoundException)
        { return null; }
    }

    internal static byte[] Write(ComparisonReportDocument report)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, ComparisonJsonContext.Default.ComparisonReportDocument);
        if (bytes.Length > ComparisonLimits.ReportBytes) throw new InvalidOperationException("comparison_report_limit");
        return bytes;
    }

    internal static ComparisonReportDocument? Read(ReadOnlySpan<byte> bytes)
    {
        var report = PricingJson.ReadValue(bytes, ComparisonJsonContext.Default.ComparisonReportDocument,
            ComparisonLimits.ReportBytes, 32);
        if (report is null) return null;
        try
        {
            using var raw = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            var left = ReadInput(Bytes(raw.RootElement.GetProperty("left")));
            var right = ReadInput(Bytes(raw.RootElement.GetProperty("right")));
            if (left is null || right is null) return null;
            var rebuilt = ComparisonReport.Create(left, right);
            var expected = JsonSerializer.SerializeToElement(rebuilt, ComparisonJsonContext.Default.ComparisonReportDocument);
            return JsonElement.DeepEquals(raw.RootElement, expected) ? rebuilt : null;
        }
        catch (Exception error) when (error is ComparisonInputException or JsonException or InvalidOperationException or
            ArgumentException or OverflowException or KeyNotFoundException)
        { return null; }
    }

    private static byte[] Bytes(JsonElement value) => Encoding.UTF8.GetBytes(value.GetRawText());
}
