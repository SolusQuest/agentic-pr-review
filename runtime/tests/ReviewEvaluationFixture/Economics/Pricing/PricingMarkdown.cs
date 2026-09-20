using System.Globalization;
using System.Text;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;

internal static class PricingMarkdown
{
    internal static string Write(PricingReport report)
    {
        var value = report.Document;
        var journal = value.Journal;
        var tariff = value.Tariff;
        var terms = tariff.Terms;
        var totals = journal.Totals;
        var text = new StringBuilder("# R6 usage pricing\n\n");
        text.Append("Fixed-reference usage pricing under a user-supplied tariff. This is not an invoice or proof of historical tariff applicability.\n\n");
        text.Append("Execution: `").Append(journal.Provenance.ExecutionKind).Append("`; campaign: `")
            .Append(journal.Provenance.CampaignId).Append("`; stop: `").Append(journal.StopReason).Append("`.\n\n");
        text.Append("Source commit: `").Append(journal.Provenance.SourceCommit).Append("`; tree: `")
            .Append(journal.Provenance.SourceTree).Append("`; clean: ").Append(journal.Provenance.SourceClean ? "true" : "false")
            .Append("; build: `").Append(journal.Provenance.BuildId).Append("`.\n\n");
        text.Append("Journal SHA-256: `").Append(value.JournalSha256).Append("`; tariff SHA-256: `")
            .Append(value.TariffSha256).Append("`.\n\n");
        text.Append("Tariff source URL SHA-256: `").Append(tariff.SourceUrlSha256).Append("`; retrieved: ")
            .Append(tariff.RetrievedOn).Append(". The URL is committed by digest, not fetched or authenticated.\n\n");
        text.Append("Provider/requested model: `").Append(terms.ProviderId).Append("` / `").Append(terms.RequestedModel)
            .Append("`; response restriction: `").Append(terms.ResponseModel ?? "requested_alias_scope").Append("`.\n\n");
        text.Append("Reference UTC: ").Append(terms.ReferenceAt).Append("; class: `").Append(terms.PriceClass)
            .Append("`; effective interval: ").Append(terms.EffectivePeriod.FromInclusive ?? "unknown").Append(" to ")
            .Append(terms.EffectivePeriod.UntilExclusive ?? "unknown").Append(" (end exclusive).\n\n");
        text.Append("Reference applicability: `").Append(value.ReferenceApplicability.Period).Append("`, `")
            .Append(value.ReferenceApplicability.PriceClass).Append("`. Execution-time price: unavailable (`")
            .Append(value.ExecutionTimeApplicability).Append("`); backend snapshot: `").Append(value.BackendSnapshotStatus)
            .Append("`; invoice: `").Append(value.InvoiceStatus).Append("`.\n\n");
        text.Append(CultureInfo.InvariantCulture,
            $"Rate integer units (hit/miss/output): {terms.Rates.CacheHitInput.Units}/{terms.Rates.CacheMissInput.Units}/{terms.Rates.Output.Units}; divide by 10^{terms.RateDecimalPlaces} {terms.Rates.Output.Currency} per {terms.TokenUnit} tokens.\n\n");
        text.Append(CultureInfo.InvariantCulture,
            $"Rounding: half-even to {terms.Arithmetic.DecimalPlaces} decimal places after campaign component aggregation; total sums rounded components. Normalized rates use exact unrounded amounts before their own final rounding. Values below the declared quantum can round to zero.\n\n");
        text.Append(CultureInfo.InvariantCulture,
            $"Scheduled/attempted/completed/failed/invalid/unattempted: {totals.Scheduled}/{totals.Attempted}/{totals.Completed}/{totals.Failed}/{totals.Invalid}/{totals.Unattempted}. Calls/sends/local refusals: {totals.Calls}/{totals.ActualSends}/{totals.LocalRefusals}.\n\n");
        text.Append(CultureInfo.InvariantCulture,
            $"Known input/output tokens: {totals.KnownInputTokens}/{totals.KnownOutputTokens}; usage complete: {Flag(totals.UsageComplete)}.\n\n");
        View(text, "Observed usage at the fixed reference tariff", value.ObservedUsage);
        View(text, "Same-token all-miss counterfactual", value.SameTokenAllMiss);
        text.Append("The all-miss view reprices identical known tokens hypothetically. It is not another execution, a cache-disabled control, or measured savings. Its coverage can differ from observed-partition pricing.\n\n");
        var reservations = journal.Reservations;
        text.Append("## Reservations (separate from usage prices)\n\n");
        text.Append(CultureInfo.InvariantCulture,
            $"Calls: {reservations.Calls}; input/output/combined tokens: {reservations.InputTokens}/{reservations.OutputTokens}/{reservations.CombinedTokens}; spend: {reservations.SpendMicroUsd} micro-USD. No currency conversion or addition to usage prices occurs.\n\n");
        text.Append("Cache-write billing is not applicable to this three-rate formula. Pricing does not establish completion, model quality, comparison eligibility or R7 readiness.\n");
        var result = text.ToString();
        if (Encoding.UTF8.GetByteCount(result) > PricingLimits.MarkdownBytes)
            throw new InvalidOperationException("pricing_markdown_limit");
        return result;
    }

    private static void View(StringBuilder text, string title, PricedUsageView view)
    {
        text.Append("## ").Append(title).Append("\n\n");
        text.Append("Currency: ").Append(view.Currency).Append(".\n\n");
        text.Append("| Amount | Known subtotal | Complete |\n| --- | --- | --- |\n")
            .Append("| Input | ").Append(Amount(view.KnownInputAmount)).Append(" | ").Append(Flag(view.InputComplete)).Append(" |\n")
            .Append("| Output | ").Append(Amount(view.KnownOutputAmount)).Append(" | ").Append(Flag(view.OutputComplete)).Append(" |\n")
            .Append("| Combined | ").Append(Amount(view.KnownTotalSubtotal)).Append(" | ").Append(Flag(view.TotalComplete)).Append(" |\n\n")
            .Append("Complete total: ").Append(Amount(view.TotalAmount)).Append(".\n\n");
        var coverage = view.Coverage;
        text.Append(CultureInfo.InvariantCulture,
            $"Priced input/output sends: {coverage.PricedInputSends}/{coverage.PricedOutputSends}; unknown usage: {coverage.UnknownUsageSends}; missing cache observation: {coverage.MissingCachePartitionSends}; unknown/mismatched required response model: {coverage.ModelUnknownSends}/{coverage.ModelMismatchSends}. These availability counts can overlap.\n\n");
        Rate(text, "Input amount per input token", view.InputAmountPerInputToken);
        Rate(text, "Total amount per input token", view.TotalAmountPerInputToken);
    }

    private static void Rate(StringBuilder text, string label, NormalizedPrice rate) => text.Append(label).Append(": ")
        .Append(Amount(rate.Value)).Append(" (`").Append(rate.Availability).Append("`; denominator: ")
        .Append(Amount(rate.InputTokens)).Append(").\n\n");

    private static string Amount(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";
    private static string Flag(bool value) => value ? "yes" : "no";
}
