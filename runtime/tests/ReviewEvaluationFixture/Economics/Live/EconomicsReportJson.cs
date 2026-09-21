using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Contracts;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Comparison;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;

internal static class EconomicsReportJson
{
    internal static byte[] Write(EconomicsReport report)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, EconomicsLiveJson.Default.EconomicsReport);
        if (bytes.Length > EconomicsLiveLimits.ReportBytes || Read(bytes) is null)
            throw new InvalidOperationException("r6_economics_report_invalid");
        return bytes;
    }

    internal static EconomicsReport? Read(ReadOnlySpan<byte> bytes)
    {
        var report = PricingJson.ReadValue(bytes, EconomicsLiveJson.Default.EconomicsReport, EconomicsLiveLimits.ReportBytes, 32);
        if (report is null) return null;
        try
        {
            using var raw = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            foreach (var outcome in raw.RootElement.GetProperty("outcomes").EnumerateArray())
                if (EvaluationJson.ReadOutcome(System.Text.Encoding.UTF8.GetBytes(outcome.GetRawText())) is null) return null;
            return Valid(report) ? report : null;
        }
        catch (Exception error) when (error is ArgumentException or EconomicsRejected or OverflowException or NullReferenceException or
            JsonException or InvalidOperationException or KeyNotFoundException)
        { return null; }
    }

    private static bool Valid(EconomicsReport report)
    {
        if (report.Format != EconomicsLiveLimits.ReportFormat || report.ExecutionKind is not ("live" or "loopback") ||
            report.Plan?.Workload is not { } workload || !EvaluationLimits.Id(report.Campaign) ||
            report.StopReason is not ("complete" or "accounting_violation" or "rate_limited" or "caller_cancelled" or "deadline" or
                "infrastructure_failed" or "reset_failed" or "allocation_refused" or "receipt_invalid" or "ready_invalid" or
                "process_failed" or "credential_invalid" or "representative_history_insufficient" or "agent_failed" or
                "session_failed" or "state_failed" or "child_unreaped") ||
            report.Cleanup is not ("cleaned" or "cleanup_failed" or "not_created") || report.Steps.IsDefault || report.Outcomes.IsDefault)
            return false;
        var input = new EconomicsPlanInput(report.Plan.Format, report.Plan.Source, report.Plan.BuildSha256,
            new("selected-replay", workload.ReplaySha256), new("selected-growth", workload.GrowthSha256), report.Plan.Provider,
            report.Plan.Thinking, "selected-tariff", report.Plan.TariffSha256, workload.Scenarios,
            workload.ChildSeconds, workload.SpacingMilliseconds, workload.StopRule, report.Plan.Bounds);
        var plan = EconomicsPlan.Admit(input, report.ExecutionKind == "live", currentBuild: false);
        if (report.PlanSha256 != plan.Sha256 || report.WorkloadSha256 != plan.WorkloadSha256 ||
            report.Scheduled != plan.Slots.Length || report.Steps.Length != report.Scheduled ||
            report.Attempted < 0 || report.Attempted > report.Scheduled || report.ReceiptMissing is < 0 or > 1 ||
            report.ReceiptMissing > report.Attempted || report.Outcomes.Length != report.Attempted - report.ReceiptMissing ||
            report.StopReason == "complete" && report.Attempted != report.Scheduled ||
            report.StopReason == "child_unreaped" && report.Cleanup != "cleanup_failed") return false;
        EconomicsAllocation allocation = new(0, 0, 0, 0, 0, 0, 0);
        var startups = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < report.Steps.Length; index++)
        {
            var step = report.Steps[index];
            if (step is null || step.Index != index || step.CaseId != plan.Slots[index].CaseId ||
                step.Allocated != (index < report.Attempted) || step.Reset && plan.Slots[index].ResetChain is null ||
                step.Code is not ("unattempted" or "child_started" or "completed" or "capacity_stop" or "prepared" or "agent_failed" or
                    "session_failed" or "state_failed" or "reset_failed" or "receipt_invalid" or "ready_invalid" or "process_failed" or
                    "credential_invalid" or "deadline" or "caller_cancelled" or "child_unreaped" or "infrastructure_failed")) return false;
            if (!step.Allocated)
            {
                if (step.ReceiptCoverage != "unattempted" || step.Code is not ("unattempted" or "reset_failed") ||
                    step.AttemptSha256 is not null || step.EvaluationStatus is not null || step.Restored || step.Prepared || step.Accepted ||
                    step.Readback || step.Reset || step.ProcessId is not null || step.Startup is not null || step.SessionSha256 is not null ||
                    step.PredecessorSha256 is not null || step.ToolCalls is not null || step.Observation is not null || step.StartedMilliseconds != 0 ||
                    step.FinishedMilliseconds != 0 || step.IntervalMilliseconds is not null) return false;
                continue;
            }
            allocation = EconomicsLedger.Add(allocation, plan.ChildAllocation)!;
            if (step.Reset != (plan.Slots[index].ResetChain is not null)) return false;
            if (index > 0 && report.Steps[index - 1] is { } prior &&
                !(prior.Code == "completed" && prior.Accepted && prior.Readback) &&
                !(prior.Code == "capacity_stop" && prior.Readback && plan.Slots[index].ResetChain == plan.Slots[index - 1].Chain)) return false;
            if (step.Reset && (index == 0 || report.Steps[index - 1].Code != "capacity_stop" ||
                !plan.Slots[index - 1].ExpectedCapacity)) return false;
            if (step.StartedMilliseconds < 0 || step.FinishedMilliseconds < step.StartedMilliseconds ||
                step.IntervalMilliseconds != (index == 0 ? null : step.StartedMilliseconds - report.Steps[index - 1].FinishedMilliseconds) ||
                index > 0 && step.IntervalMilliseconds < plan.Input.SpacingMilliseconds) return false;
            var missing = index == report.Attempted - 1 && report.ReceiptMissing == 1;
            if (missing)
            {
                if (step.ReceiptCoverage != "missing" || step.AttemptSha256 is not null || step.EvaluationStatus is not null ||
                    step.Prepared || step.Accepted || step.Readback || step.ProcessId is not null || step.Startup is not null ||
                    step.SessionSha256 is not null || step.PredecessorSha256 is not null || step.ToolCalls is not null ||
                    step.Observation is not null || step.Restored) return false;
                continue;
            }
            var outcome = report.Outcomes[index];
            if (step.ReceiptCoverage != "complete" || !EvaluationLimits.Hash(step.AttemptSha256) ||
                step.EvaluationStatus is not ("completed" or "failed" or "invalid") || step.ProcessId is null or <= 0 ||
                !Guid.TryParseExact(step.Startup, "N", out _) || !startups.Add(step.Startup!) ||
                step.ToolCalls is null or < 0 or > 64 || step.Restored != (plan.Slots[index].Previous is not null) ||
                step.Restored != (step.PredecessorSha256 is not null) ||
                step.PredecessorSha256 is { } predecessor && !EvaluationLimits.Hash(predecessor) ||
                step.Accepted && (!step.Prepared || !EvaluationLimits.Hash(step.SessionSha256)) ||
                !step.Accepted && step.SessionSha256 is not null || step.Prepared && step.EvaluationStatus != "completed" ||
                step.Readback && !step.Accepted && step.Code != "capacity_stop" ||
                step.Code == "completed" && (!step.Accepted || !step.Readback) || outcome is null ||
                EvaluationJson.ReadOutcome(EvaluationJson.Write(outcome)) != outcome || outcome.AttemptSha256 != step.AttemptSha256 ||
                outcome.CaseId != step.CaseId || outcome.CorpusSha256 != plan.WorkloadSha256 ||
                outcome.ExecutionStatus.ToString().ToLowerInvariant() != step.EvaluationStatus ||
                outcome.SourceCommit != input.Source.Commit || outcome.SourceTree != input.Source.Tree || outcome.SourceClean != input.Source.Clean ||
                outcome.Mode != (report.ExecutionKind == "live" ? "live" : "deterministic")) return false;
            if (!EconomicsJournal.ValidObservation(step.Observation, step) ||
                step.Code is not ("completed" or "capacity_stop") && step.Code != step.Observation!.Code ||
                step.Code == "capacity_stop" && (!plan.Slots[index].ExpectedCapacity || !step.Readback || step.Accepted ||
                    !EconomicsJournal.Capacity(step.Observation!))) return false;
            if (plan.Slots[index].Previous is { } previous &&
                step.PredecessorSha256 != report.Steps[previous].SessionSha256) return false;
            var descriptor = new EvaluationRunInput(report.Campaign + "-" + (index + 1), outcome.Mode,
                input.Source.Commit, input.Source.Tree, input.Source.Clean, input.Provider.ConfigurationSha256);
            if (outcome.ConfigurationSha256 is null || outcome.AttemptSha256 != EvaluationAttempt.Hash("attempt",
                outcome.ConfigurationSha256, AgentCanonical.HashRaw(EvaluationJson.Write(descriptor)))) return false;
        }
        if (report.Allocations != allocation) return false;
        var observed = report.Steps.Take(report.Outcomes.Length).Select(step => step.Observation!).ToArray();
        var reconstructed = EconomicsJournal.CreateObserved(plan, report.Campaign, report.ExecutionKind, observed, report.Outcomes,
            report.ReceiptMissing == 0 ? EconomicsRunner.JournalStop(report.StopReason) : "infrastructure_failed");
        if (reconstructed is null) return false;
        if (report.ReceiptMissing != 0) return !report.UsageComplete && !report.MonetaryComplete &&
            report.C1Handoff == "unavailable_missing_receipt" && report.Journal is null && report.Pricing is null;
        var journal = UsageJournal.Admit(report.Journal, plan.Expectation(report.Campaign, report.ExecutionKind));
        if (journal is null || journal.Document.StopReason != EconomicsRunner.JournalStop(report.StopReason) ||
            journal.Document.Totals.Attempted != report.Attempted ||
            journal.Document.Totals.UsageComplete != report.UsageComplete || report.Pricing is null) return false;
        if (!UsageJournalJson.Write(reconstructed).AsSpan().SequenceEqual(UsageJournalJson.Write(journal))) return false;
        var pricing = PricingJson.Read(JsonSerializer.SerializeToUtf8Bytes(report.Pricing, PricingJsonContext.Default.PricingReportDocument));
        var tariff = AdmittedTariff.Admit(report.Pricing.Tariff, out _);
        if (pricing is null || tariff is null || tariff.Sha256 != plan.Input.TariffSha256 || !pricing.Matches(journal, tariff) ||
            report.MonetaryComplete != pricing.Document.ObservedUsage.TotalComplete || report.C1Handoff != "available") return false;
        for (var index = 0; index < report.Outcomes.Length; index++)
            if (journal.Document.Attempts[index].EvaluationAttemptSha256 != report.Outcomes[index].AttemptSha256 ||
                journal.Document.Attempts[index].Status != report.Steps[index].EvaluationStatus) return false;
        if (report.StopReason == "complete" && (report.Steps[0].ToolCalls < 1 || !report.Steps[1].Restored ||
            report.Steps[^1].Code != "completed")) return false;
        var evidence = new ComparisonEvidence(report.Outcomes, [], [], []);
        var selection = ComparisonJson.Select(report.Pricing, evidence);
        // Validate the exact downstream identities without creating a comparison or baseline.
        return ComparisonAdmission.Valid(new(ComparisonLimits.InputFormat,
            new("none", "not_requested", selection, selection, null, null), report.Pricing, evidence));
    }
}
