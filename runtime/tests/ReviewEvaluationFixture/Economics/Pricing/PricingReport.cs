using System.Numerics;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Contracts;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;

internal sealed class PricingReport
{
    private PricingReport(PricingReportDocument document) => Document = document;
    internal PricingReportDocument Document { get; }

    internal static PricingReport Create(UsageJournal journal, AdmittedTariff tariff)
    {
        var terms = tariff.Document.Terms;
        var period = terms.EffectivePeriod;
        var applicability = period.Status == "unknown" ? "effective_period_unknown" :
            string.CompareOrdinal(terms.ReferenceAt, period.FromInclusive) >= 0 &&
            string.CompareOrdinal(terms.ReferenceAt, period.UntilExclusive) < 0 ?
                "reference_period_match" : "reference_period_mismatch";
        return new(new(PricingLimits.ReportFormat, journal.Document, tariff.Document,
            JournalHash(journal), tariff.Sha256,
            new(applicability, terms.PriceClass == "unknown" ? "price_class_unknown" : "reference_class_declared"),
            "execution_time_unknown", null, "not_exposed", "not_evidenced",
            Calculate(journal.Document, terms, allMiss: false), Calculate(journal.Document, terms, allMiss: true)));
    }

    internal bool Matches(UsageJournal selectedJournal, AdmittedTariff selectedTariff) =>
        Document.JournalSha256 == JournalHash(selectedJournal) && Document.TariffSha256 == selectedTariff.Sha256;

    private static string JournalHash(UsageJournal journal) => AgentCanonical.HashDomain("apr.r6.pricing.journal",
        UsageJournalJson.Write(journal));

    private static PricedUsageView Calculate(UsageJournalDocument journal, TariffTerms terms, bool allMiss)
    {
        BigInteger input = 0, output = 0;
        var inputSends = 0;
        var outputSends = 0;
        var missingPartition = 0;
        var modelUnknown = 0;
        var modelMismatch = 0;
        foreach (var call in journal.Calls)
        {
            if (!call.Dispatched || call.Usage is not { } usage) continue;
            if (usage.Cache is null) missingPartition++;
            if (terms.ResponseModel is { } requiredModel)
            {
                if (usage.Cache is null) { modelUnknown++; continue; }
                if (usage.Cache.ResponseModel != requiredModel) { modelMismatch++; continue; }
            }
            // Cast before multiplication. Completion tokens are charged once;
            // combined/reasoning/cache-write counters are not billing partitions.
            output += (BigInteger)usage.OutputTokens * terms.Rates.Output.Units;
            outputSends++;
            if (allMiss)
            {
                input += (BigInteger)usage.InputTokens * terms.Rates.CacheMissInput.Units;
                inputSends++;
            }
            else if (usage.Cache is { } cache)
            {
                input += (BigInteger)cache.CacheReadInputTokens * terms.Rates.CacheHitInput.Units +
                    (BigInteger)cache.UncachedInputTokens * terms.Rates.CacheMissInput.Units;
                inputSends++;
            }
            else if (usage.InputTokens == 0) inputSends++;
        }
        var totals = journal.Totals;
        var inputComplete = inputSends == totals.ActualSends;
        var outputComplete = outputSends == totals.ActualSends;
        var complete = inputComplete && outputComplete;
        var places = terms.Arithmetic.DecimalPlaces;
        var denominator = terms.TokenUnit * BigInteger.Pow(10, terms.RateDecimalPlaces);
        var inputUnits = Quantize(input, denominator, places);
        var outputUnits = Quantize(output, denominator, places);
        decimal? knownInput = inputSends > 0 || inputComplete ? Decimal(inputUnits, places) : null;
        decimal? knownOutput = outputSends > 0 || outputComplete ? Decimal(outputUnits, places) : null;
        // Sum quantized integer coefficients, not decimal values that may lose
        // fractional precision even inside a checked addition.
        decimal? subtotal = knownInput is not null || knownOutput is not null ?
            Decimal(inputUnits + outputUnits, places) : null;
        return new(allMiss ? "same_token_all_miss_counterfactual" : "observed_usage_fixed_reference",
            terms.Rates.Output.Currency, knownInput, knownOutput, subtotal,
            inputComplete, outputComplete, complete, complete ? subtotal : null,
            new(inputSends, outputSends, totals.UnknownUsageSends, missingPartition, modelUnknown, modelMismatch),
            Normalize(input, denominator, inputComplete, totals, places),
            Normalize(input + output, denominator, complete, totals, places));
    }

    private static NormalizedPrice Normalize(BigInteger numerator, BigInteger denominator, bool complete,
        UsageJournalTotals totals, int places)
    {
        if (!totals.UsageComplete) return new("input_denominator_incomplete", null, null);
        if (totals.KnownInputTokens == 0) return new("zero_input_denominator", null, 0);
        if (!complete) return new("amount_incomplete", null, totals.KnownInputTokens);
        return new("available", Decimal(Quantize(numerator,
            denominator * (BigInteger)totals.KnownInputTokens, places), places), totals.KnownInputTokens);
    }

    // Every operand is bounded by the admitted 2048-call population, Int64
    // rates/counters, token unit and scales. No arbitrary-size input integers.
    private static BigInteger Quantize(BigInteger numerator, BigInteger denominator, int places)
    {
        var quotient = BigInteger.DivRem(numerator * BigInteger.Pow(10, places), denominator, out var remainder);
        var midpoint = (remainder * 2).CompareTo(denominator);
        return midpoint > 0 || midpoint == 0 && !quotient.IsEven ? quotient + 1 : quotient;
    }

    private static decimal Decimal(BigInteger coefficient, int places)
    {
        while (places > 0 && coefficient % 10 == 0) { coefficient /= 10; places--; }
        // A decimal cast can discard fractional digits; this cast is of an
        // integer coefficient only. The constructor then assigns its exact scale.
        var bits = decimal.GetBits(checked((decimal)coefficient));
        return new(bits[0], bits[1], bits[2], false, (byte)places);
    }
}
