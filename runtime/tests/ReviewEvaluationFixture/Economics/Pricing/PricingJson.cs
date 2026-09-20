using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Contracts;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
    RespectRequiredConstructorParameters = true, MaxDepth = 16)]
[JsonSerializable(typeof(TariffInput))]
[JsonSerializable(typeof(TariffSnapshot))]
[JsonSerializable(typeof(PricingReportDocument))]
internal sealed partial class PricingJsonContext : JsonSerializerContext;

internal static class PricingJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Write(PricingReport report)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report.Document, PricingJsonContext.Default.PricingReportDocument);
        if (bytes.Length > PricingLimits.ReportBytes) throw new InvalidOperationException("pricing_report_limit");
        return bytes;
    }

    internal static PricingReport? Read(ReadOnlySpan<byte> bytes)
    {
        var document = ReadValue(bytes, PricingJsonContext.Default.PricingReportDocument, PricingLimits.ReportBytes, 16);
        if (document is null) return null;
        var journal = UsageJournal.Admit(document.Journal);
        var tariff = AdmittedTariff.Admit(document.Tariff, out _);
        if (journal is null || tariff is null) return null;
        try
        {
            var rebuilt = PricingReport.Create(journal, tariff);
            using var raw = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            var expected = JsonSerializer.SerializeToElement(rebuilt.Document, PricingJsonContext.Default.PricingReportDocument);
            return JsonElement.DeepEquals(raw.RootElement, expected) ? rebuilt : null;
        }
        catch (Exception error) when (error is OverflowException or JsonException) { return null; }
    }

    internal static T? ReadValue<T>(ReadOnlySpan<byte> bytes, JsonTypeInfo<T> type, int maximumBytes, int depth)
        where T : class
    {
        if (bytes.Length is < 1 || bytes.Length > maximumBytes) return null;
        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = depth });
            var value = JsonSerializer.Deserialize(ref reader, type);
            return reader.Read() ? null : value;
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or NotSupportedException)
        { return null; }
    }
}
