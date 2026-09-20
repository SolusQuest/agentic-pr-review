using System.Globalization;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;

internal sealed class AdmittedTariff
{
    private AdmittedTariff(TariffSnapshot document)
    {
        Document = document;
        Sha256 = AgentCanonical.HashDomain("apr.r6.tariff.snapshot",
            JsonSerializer.SerializeToUtf8Bytes(document, PricingJsonContext.Default.TariffSnapshot));
    }

    internal TariffSnapshot Document { get; }
    internal string Sha256 { get; }

    internal static AdmittedTariff? Admit(TariffSnapshot? document, out string error)
    {
        error = "r6_pricing_tariff_invalid";
        if (document is null || document.Format != PricingLimits.TariffFormat ||
            !EvaluationLimits.Hash(document.SourceUrlSha256) || !Date(document.RetrievedOn) ||
            document.Terms is not { EffectivePeriod: { } period, Rates: { } rates, Arithmetic: { } arithmetic } terms ||
            rates.CacheHitInput is null || rates.CacheMissInput is null || rates.Output is null ||
            !EvaluationLimits.Id(terms.Formula) || !EvaluationLimits.Id(terms.ProviderId) ||
            !EvaluationLimits.Id(terms.RequestedModel) ||
            terms.ResponseModel is not null && !EvaluationLimits.Id(terms.ResponseModel) ||
            !Timestamp(terms.ReferenceAt) || terms.TokenUnit is < 1 or > 1_000_000_000 ||
            terms.RateDecimalPlaces is < 0 or > 12 || arithmetic.DecimalPlaces is < 0 or > 12 ||
            !Rate(rates.CacheHitInput) || !Rate(rates.CacheMissInput) || !Rate(rates.Output) ||
            !Period(period)) return null;

        if (rates.CacheHitInput.Currency != rates.CacheMissInput.Currency ||
            rates.CacheHitInput.Currency != rates.Output.Currency)
            error = "r6_pricing_tariff_currency_mismatch";
        else if (rates.CacheHitInput.Currency is not ("USD" or "CNY"))
            error = "r6_pricing_tariff_currency_unsupported";
        else if (terms.Formula != PricingLimits.Formula)
            error = "r6_pricing_tariff_formula_unsupported";
        else if (terms.ProviderId != DeepSeekAdapterContext.Provider)
            error = "r6_pricing_tariff_provider_unsupported";
        else if (terms.RequestedModel != DeepSeekAdapterContext.Model ||
            terms.ResponseModel is not (null or DeepSeekAdapterContext.Model or "deepseek-flash"))
            error = "r6_pricing_tariff_model_mismatch";
        else if (terms.PriceClass is not ("standard" or "peak" or "off_peak" or "unknown"))
            error = "r6_pricing_tariff_price_class_unsupported";
        else if (arithmetic.Rounding != "half_even" || arithmetic.Aggregation != PricingLimits.Aggregation ||
            arithmetic.Normalization != PricingLimits.Normalization)
            error = "r6_pricing_tariff_arithmetic_unsupported";
        else
        {
            error = "";
            return new(document);
        }
        return null;
    }

    internal static AdmittedTariff? Read(ReadOnlySpan<byte> bytes, out string error)
    {
        error = "r6_pricing_tariff_invalid";
        var input = PricingJson.ReadValue(bytes, PricingJsonContext.Default.TariffInput, PricingLimits.TariffBytes, 8);
        if (input is null || !SourceUrl(input.SourceUrl)) return null;
        return Admit(new(input.Format, AgentCanonical.HashDomain("apr.r6.tariff.source-url",
            Encoding.UTF8.GetBytes(input.SourceUrl)), input.RetrievedOn, input.Terms), out error);
    }

    private static bool Rate(TariffRate rate) => rate.Units >= 0 && rate.Currency is { Length: 3 } currency &&
        currency.All(c => c is >= 'A' and <= 'Z');

    private static bool Date(string? value) => value is { Length: 10 } && DateOnly.TryParseExact(value,
        "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static bool Timestamp(string? value) => value is { Length: 20 } && DateTimeOffset.TryParseExact(value,
        "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _);

    private static bool Period(TariffPeriod period) => period.Status switch
    {
        "unknown" => period.FromInclusive is null && period.UntilExclusive is null,
        "known" => Timestamp(period.FromInclusive) && Timestamp(period.UntilExclusive) &&
            string.CompareOrdinal(period.FromInclusive, period.UntilExclusive) < 0,
        _ => false,
    };

    private static bool SourceUrl(string? value) => value is { Length: >= 1 and <= 2048 } &&
        value.All(c => c is >= (char)0x21 and <= (char)0x7e) &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.HostNameType == UriHostNameType.Dns && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
}
