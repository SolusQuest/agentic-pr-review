using System.Text.Json;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Contracts;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using static AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification.GateContracts;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;

// Expectations are authored independently of the scenario executor. Admit the
// original bytes before inspecting values or producing a parity projection.
internal static class GateTokenOracle
{
    internal static JsonElement Verify(GateCase item, GateSelection selection)
    {
        var bytes = Bytes(item.Evidence);
        if (item.Id is "t1-malformed-partition" or "t3-overflow")
        {
            var scalar = PricingJson.ReadValue(bytes, GateJson.Default.GateScalar, 4096, 4);
            Require(scalar is not null && scalar.Values.IsEmpty && scalar.Facts.IsEmpty &&
                scalar.Code == (item.Id == "t3-overflow" ? "arithmetic_overflow" : "parser_invalid"));
            return item.Evidence;
        }
        if (item.Id == "t2-terminal-order")
        {
            Require(UsageJournalJson.Read(bytes) is null);
            var invalid = PricingJson.ReadValue(bytes, UsageJournalJsonContext.Default.UsageJournalDocument,
                UsageJournalLimits.JsonBytes, UsageJournalLimits.Depth)!;
            Require(invalid is not null && invalid.Calls.Length == 2 && invalid.Calls[0].Usage is null &&
                invalid.Calls[0].TransportOutcome == "transport_failure" && invalid.Calls[1].Usage is not null);
            // Repair only the selected terminal call. An unrelated malformed
            // artifact must not masquerade as this particular negative probe.
            var first = invalid!.Calls[0] with
            {
                TransportOutcome = "success", ChatOutcome = "returned", UsageStatus = "known",
                Usage = invalid.Calls[1].Usage,
            };
            var calls = invalid.Calls.SetItem(0, first);
            Require(UsageJournal.Admit(invalid with { Calls = calls, Totals = UsageJournal.Totals(invalid.Attempts, calls) }) is not null);
            Bound(invalid, selection);
            return item.Evidence;
        }
        if (!item.Id.StartsWith("t3-", StringComparison.Ordinal))
        {
            var journal = UsageJournalJson.Read(bytes) ?? throw new InvalidOperationException("r6_gate_journal");
            var doc = journal.Document;
            Bound(doc, selection);
            var t = doc.Totals;
            Require(t.Completed == 0 && t.Invalid == 0 && t.Attempted == 1 && t.Failed == 1);
            Require(t.Scheduled == (item.Id == "t2-unattempted-tail" ? 3 : 1));
            Require(t.Unattempted == (item.Id == "t2-unattempted-tail" ? 2 : 0));
            Require(t.ActualSends == 1 && t.Calls == (item.Id == "t2-local-refusal" ? 2 : 1));
            Require(t.LocalRefusals == (item.Id == "t2-local-refusal" ? 1 : 0));
            var unknown = item.Id is "t2-unknown" or "t2-late-finalization";
            Require(t.UnknownUsageSends == (unknown ? 1 : 0) && t.KnownUsageSends == (unknown ? 0 : 1) && t.UsageComplete != unknown);
            var zero = item.Id is "t1-zero" or "t2-local-refusal";
            Require(t.KnownInputTokens == (unknown || zero ? 0 : 10) && t.KnownOutputTokens == (unknown || zero ? 0 : 4));
            var partition = !unknown && !zero && item.Id != "t1-total-only";
            Require(t.CacheReadInputTokens == (partition ? 6 : item.Id == "t1-zero" ? 0 : null) &&
                t.UncachedInputTokens == (partition ? 4 : item.Id == "t1-zero" ? 0 : null));
            if (partition) Require(doc.Calls[0].Usage!.Cache!.ResponseModel ==
                (item.Id == "t1-alias" ? "deepseek-flash" : DeepSeekAdapterContext.Model));
            Require(doc.StopReason == (item.Id == "t2-local-refusal" ? "bound_stop" :
                item.Id is "t2-unattempted-tail" or "t2-late-finalization" ? "caller_cancelled" : "complete"));
            return item.Evidence;
        }
        var price = PricingJson.Read(bytes) ?? throw new InvalidOperationException("r6_gate_price");
        var document = price.Document;
        Bound(document.Journal, selection);
        SelectedPriceUsage(item.Id, document.Journal);
        var view = document.ObservedUsage;
        var other = document.SameTokenAllMiss;
        var terms = document.Tariff.Terms;
        var rounding = item.Id is "t3-half-even-low" or "t3-half-even-even" or "t3-half-even-high";
        var aggregate = item.Id == "t3-aggregate";
        var zeroRates = item.Id == "t3-unknown-zero-rate";
        Require(terms.TokenUnit == (rounding ? 2 : aggregate ? 1000 : 100) && terms.RateDecimalPlaces == 0 &&
            terms.Arithmetic.DecimalPlaces == (rounding ? 0 : aggregate ? 2 : 3) &&
            terms.Rates.CacheHitInput.Units == (zeroRates ? 0 : 1) && terms.Rates.Output.Units == (zeroRates ? 0 : 5) &&
            terms.Rates.CacheMissInput.Units == (item.Id == "t3-half-even-low" ? 3 : item.Id == "t3-half-even-high" ? 7 :
                item.Id == "t3-half-even-even" || aggregate ? 5 : zeroRates ? 0 : 4));
        Require(document.Journal.Totals.ActualSends == (aggregate || zeroRates ? 2 : 1) &&
            document.Journal.Totals.KnownInputTokens == (rounding ? 1 : aggregate ? 2 : item.Id == "t3-zero-denominator" ? 0 : 10) &&
            document.Journal.Totals.KnownOutputTokens == (rounding || aggregate || item.Id == "t3-zero-denominator" ? 0 : 4));
        Require(view.Currency == (item.Id == "t3-cny" ? "CNY" : "USD"));
        Require(document.ExecutionTimeApplicability == "execution_time_unknown" && document.ExecutionTimeTotalAmount is null &&
            document.InvoiceStatus == "not_evidenced" && document.BackendSnapshotStatus == "not_exposed");
        decimal? amount = item.Id switch
        {
            "t3-usd" or "t3-cny" => .42m,
            "t3-half-even-low" or "t3-half-even-even" => 2,
            "t3-half-even-high" => 4,
            "t3-aggregate" => .01m,
            "t3-zero-denominator" => 0,
            "t3-missing-partition" or "t3-unknown-zero-rate" => null,
            _ => throw new InvalidOperationException("r6_gate_price_case"),
        };
        Require(view.TotalAmount == amount && view.TotalComplete == amount.HasValue);
        if (item.Id is "t3-usd" or "t3-cny")
            Require(view.KnownInputAmount == .22m && view.KnownOutputAmount == .20m && other.TotalAmount == .60m &&
                view.InputAmountPerInputToken.Value == .022m && view.TotalAmountPerInputToken.Value == .042m);
        if (item.Id == "t3-zero-denominator") Require(view.TotalAmountPerInputToken.Availability == "zero_input_denominator");
        if (item.Id == "t3-missing-partition")
            Require(view.KnownInputAmount is null && view.KnownOutputAmount == .20m &&
                view.Coverage.MissingCachePartitionSends == 1 && other.TotalAmount == .60m);
        if (item.Id == "t3-unknown-zero-rate")
            Require(view.KnownTotalSubtotal == 0 && view.Coverage.UnknownUsageSends == 1 &&
                view.TotalAmountPerInputToken.Availability == "input_denominator_incomplete" && other.TotalAmount is null);
        if (item.Id == "t3-aggregate") Require(document.Journal.Totals.ActualSends == 2 && document.Journal.Totals.KnownInputTokens == 2);
        return item.Evidence;
    }

    private static void SelectedPriceUsage(string id, UsageJournalDocument journal)
    {
        // Equal campaign totals cannot establish the selected per-call probe.
        // These observations are authored here, independently of the producer.
        var known = new UsageJournalUsage(10, 4, 14, new(DeepSeekAdapterContext.Model, 6, 4));
        var unit = new UsageJournalUsage(1, 0, 1, new(DeepSeekAdapterContext.Model, 0, 1));
        UsageJournalUsage?[] expected = id switch
        {
            "t3-usd" or "t3-cny" => [known],
            "t3-half-even-low" or "t3-half-even-even" or "t3-half-even-high" => [unit],
            "t3-aggregate" => [unit, unit],
            "t3-zero-denominator" => [new(0, 0, 0, null)],
            "t3-missing-partition" => [new(10, 4, 14, null)],
            "t3-unknown-zero-rate" => [known, null],
            _ => throw new InvalidOperationException("r6_gate_price_case"),
        };
        Require(journal.StopReason == "complete" && journal.Attempts.Length == expected.Length && journal.Calls.Length == expected.Length &&
            journal.Totals.Scheduled == expected.Length && journal.Totals.Attempted == expected.Length &&
            journal.Totals.Failed == expected.Length && journal.Totals.Completed == 0 && journal.Totals.Invalid == 0 &&
            journal.Totals.Unattempted == 0 && journal.Totals.LocalRefusals == 0);
        for (var index = 0; index < expected.Length; index++)
        {
            var attempt = journal.Attempts[index]; var call = journal.Calls[index]; var usage = expected[index];
            Require(attempt.ScheduleIndex == index && attempt.Calls == 1 && attempt.Sends == 1 &&
                call.AttemptId == attempt.AttemptId && call.Ordinal == 1 && call.Dispatched && call.Usage == usage &&
                call.UsageStatus == (usage is null ? "unknown" : "known") &&
                call.TransportOutcome == (usage is null ? "transport_failure" : "success") &&
                call.ChatOutcome == (usage is null ? "threw" : "returned"));
        }
    }

    private static void Bound(UsageJournalDocument doc, GateSelection selection) => Require(
        doc.Provenance.SourceCommit == selection.SourceCommit && doc.Provenance.SourceTree == selection.SourceTree &&
        doc.Provenance.SourceClean == selection.SourceClean && doc.Provenance.CorpusSha256 == selection.ReplaySha256 &&
        doc.Provenance.CampaignId == "r6-gate-tokens" && doc.Provenance.BuildId == "r6-gate" &&
        doc.Provenance.ExecutionKind == "loopback" && doc.Plan.Schedule.All(id => id == "cs-safe"));
}
