using SolusAgent.Api.Usage;

namespace AgenticPrReview.Runtime.SolusEconomicsFixture;

// Explicit R6 synthetic reference only: USD 1/4/5 per million. No tariff lookup,
// execution-time applicability, invoice, discount or cache-control assertion.
internal static class SyntheticPrice
{
    internal const string Reference = "synthetic-usd-1-4-5-per-million";
    internal static PriceFact Create(RunFact? run, bool provenNoCurrentWork = false) =>
        new(Reference, View(run, false, provenNoCurrentWork), View(run, true, provenNoCurrentWork));

    private static PriceView View(RunFact? run, bool allMiss, bool zero)
    {
        if (run is null) return zero ? new(0, 0, true, true, 0) : new(null, null, false, false, null);
        decimal input = 0, output = 0;
        var inputKnown = run.Coverage == UsageInventoryCoverage.Complete && run.Attempts.Length == 0;
        var outputKnown = inputKnown;
        var inputComplete = run.Coverage == UsageInventoryCoverage.Complete && run.ContinuedCalls == 0 && run.Input.Coverage == TokenObservationCoverage.Complete;
        var outputComplete = run.Coverage == UsageInventoryCoverage.Complete && run.ContinuedCalls == 0 && run.Output.Coverage == TokenObservationCoverage.Complete;
        foreach (var attempt in run.Attempts)
        {
            if (attempt.Exposure == DispatchExposure.NotDispatched) { inputKnown = outputKnown = true; continue; }
            if (attempt.Exposure == DispatchExposure.Unknown) inputComplete = outputComplete = false;
            if (attempt.OutputTokens is { } measuredOutput) { output += (decimal)measuredOutput * 5; outputKnown = true; }
            else outputComplete = false;
            var hit = Detail(attempt, ProviderTokenCounterKind.CacheRead);
            var miss = Detail(attempt, ProviderTokenCounterKind.UncachedInput);
            if (allMiss && attempt.InputTokens is { } measuredInput)
            { input += (decimal)measuredInput * 4; inputKnown = true; }
            else if (!allMiss && attempt.InputTokens == 0) inputKnown = true;
            else if (!allMiss && attempt.InputTokens is { } parent && hit is { } h && miss is { } m &&
                h <= parent && m == parent - h)
            { input += (decimal)h + (decimal)m * 4; inputKnown = true; }
            else inputComplete = false;
        }
        decimal? inputAmount = inputKnown ? decimal.Round(input / 1_000_000m, 6, MidpointRounding.ToEven) : null;
        decimal? outputAmount = outputKnown ? decimal.Round(output / 1_000_000m, 6, MidpointRounding.ToEven) : null;
        return new(inputAmount, outputAmount, inputComplete, outputComplete,
            inputComplete && outputComplete ? inputAmount + outputAmount : null);
    }
    internal static long? Detail(AttemptFact value, ProviderTokenCounterKind kind) => value.Details
        .SingleOrDefault(d => d.Kind == kind && d.Relationship == TokenCounterRelationship.IncludedInInput)?.Value;
}
