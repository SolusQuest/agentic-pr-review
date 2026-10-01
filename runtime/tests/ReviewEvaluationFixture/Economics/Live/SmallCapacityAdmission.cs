using System.Collections.Immutable;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Contracts;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;

internal static class SmallCapacityAdmission
{
    // This private, dense projection exists only to reuse canonical call admission.
    // It is never exported as a population, journal, price or comparison result.
    // Original schedule indices and every unused slot remain in the experiment report.
    internal static bool Valid(EconomicsPlan plan, string campaign, string transport,
        IReadOnlyList<EconomicsReceipt> receipts, string stop)
    {
        if (receipts.Count == 0 || receipts.Count > plan.Slots.Length ||
            receipts.Select(r => r.Index).Distinct().Count() != receipts.Count ||
            !receipts.Select(r => r.Index).SequenceEqual(receipts.Select(r => r.Index).Order())) return false;
        if (receipts.Any(r => r.Index < 0 || r.Index >= plan.Slots.Length || r.Evaluation is null)) return false;
        var current = receipts[^1];
        if ((current.Code == "prepared" || EconomicsJournal.Capacity(current)) &&
            current.Calls.Any(c => c.Dispatched && (c.UsageStatus != "known" || !UsageJournal.ValidUsage(c.Usage))))
            return false;
        var projection = plan.Projection with
        {
            Schedule = receipts.Select(r => plan.Slots[r.Index].CaseId).ToImmutableArray(),
        };
        var provenance = plan.Expectation(campaign, transport).Provenance with
        {
            PlanSha256 = LivePlanAdmission.Digest(projection),
        };
        var expected = new UsageJournalExpectation(provenance, projection);
        var attempts = ImmutableArray.CreateBuilder<UsageJournalAttempt>();
        var calls = ImmutableArray.CreateBuilder<UsageJournalCall>();
        long reservations = 0;
        for (var index = 0; index < receipts.Count; index++)
        {
            var receipt = receipts[index];
            var sends = receipt.Calls.Count(c => c.Dispatched);
            attempts.Add(new(expected.BindingSha256, expected.AttemptId(index), index, projection.Schedule[index],
                receipt.Evaluation!.ExecutionStatus.ToString().ToLowerInvariant(), receipt.AgentStatus,
                receipt.Evaluation.AttemptSha256, receipt.Calls.Length, sends, receipt.Calls.Length - sends));
            foreach (var call in receipt.Calls)
                calls.Add(new(expected.BindingSha256, expected.CallId(index, call.Ordinal), expected.AttemptId(index),
                    call.Ordinal, call.Dispatched, call.TransportOutcome, call.ChatOutcome, call.UsageStatus, call.Usage));
            reservations = checked(reservations + receipt.Accounting.Sends);
        }
        var per = plan.Input.Bounds.PerCall;
        var rows = attempts.ToImmutable(); var callRows = calls.ToImmutable();
        return UsageJournal.Admit(new(expected.Provenance, projection, expected.BindingSha256, stop,
            "not_applicable", new(reservations, checked(reservations * per.MaxInputTokens),
                checked(reservations * per.MaxOutputTokens), checked(reservations * (per.MaxInputTokens + per.MaxOutputTokens)),
                checked(reservations * per.MaxChargeMicroUsd)), rows, callRows, UsageJournal.Totals(rows, callRows)), expected) is not null;
    }
}
