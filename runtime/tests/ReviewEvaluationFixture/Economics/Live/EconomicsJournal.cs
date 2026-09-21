using System.Collections.Immutable;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Contracts;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;

internal static class EconomicsJournal
{
    internal static bool ValidReceipt(EconomicsChildInput input, EconomicsChildReady? ready,
        EconomicsReceipt receipt, EconomicsPlan plan, AdmittedReplayRun run)
    {
        if (ready is null || !EconomicsProcess.ValidReady(input, ready, receipt.ProcessId) ||
            receipt.Operation != input.Operation || receipt.PlanSha256 != input.PlanSha256 ||
            receipt.WorkloadSha256 != input.WorkloadSha256 || receipt.Index != input.Slot.Index ||
            receipt.LeaseId != input.Lease.Id || receipt.Startup != ready.Startup || receipt.Transport != input.Transport ||
            receipt.Session != input.Session || receipt.PredecessorSha256 != input.Predecessor?.SessionSha256 ||
            receipt.Restored != ready.Restored || receipt.Calls.IsDefault || receipt.Calls.Length > 8 ||
            receipt.Accounting is not { Outcomes: not null, CacheUsage: not null } accounting ||
            receipt.ToolCalls is < 0 or > 64 || receipt.Measurement is not { } counts ||
            counts.Calls != receipt.Calls.Length || counts.Calls is < 1 or > 8 ||
            counts.LastProjectRequestBytes is < 1 or > AgentLimits.RequestBytes || counts.LastMessages is < 1 or > AgentLimits.Messages ||
            counts.LastResponseMessages < counts.LastMessages || counts.LastResponseMessages > counts.LastMessages + 1 + AgentLimits.ToolCallsPerResponse ||
            counts.LastContinuationBeforeBytes is < 0 or > AgentLimits.ContinuationTotalBytes ||
            counts.LastContinuationAfterBytes < counts.LastContinuationBeforeBytes ||
            counts.LastContinuationAfterBytes > 2L * AgentLimits.ContinuationTotalBytes || !EvaluationLimits.Hash(receipt.InitialPrefixSha256) ||
            receipt.Evaluation is not { } evaluation || EvaluationJson.ReadOutcome(EvaluationJson.Write(evaluation)) != evaluation)
            return false;
        var descriptor = new EvaluationRunInput(input.Campaign + "-" + (input.Slot.Index + 1),
            input.Transport == "live" ? "live" : "deterministic", input.Plan.Source.Commit, input.Plan.Source.Tree,
            input.Plan.Source.Clean, input.Plan.Provider.ConfigurationSha256);
        var attempt = EvaluationAttempt.Admit(run.CreateTrustedRequest(ReplayState.Build), descriptor);
        if (attempt is null || evaluation.CaseId != run.Input.CaseId || evaluation.CorpusSha256 != plan.WorkloadSha256 ||
            evaluation.CaseSha256 != run.Expected.Sha256 || evaluation.ConfigurationSha256 != attempt.ConfigurationSha256 ||
            evaluation.AttemptSha256 != attempt.AttemptSha256 || evaluation.Mode != descriptor.Mode ||
            evaluation.SourceCommit != descriptor.SourceCommit || evaluation.SourceTree != descriptor.SourceTree ||
            evaluation.SourceClean != descriptor.SourceClean || evaluation.ExpectedDefects != run.Expected.Input.Defects.Length)
            return false;
        var lifecycle = receipt.Code switch
        {
            "agent_failed" => receipt.Stage == "agent" && GrowthProfiles.AgentCode(receipt.Diagnostic) &&
                receipt.AgentStatus == "failed" && evaluation.ExecutionStatus == EvaluationStatus.Failed &&
                receipt.Prepared is null && receipt.CompletedSessionSha256 is null,
            "session_failed" => receipt.Stage == "build" && GrowthProfiles.SessionCode(receipt.Diagnostic) &&
                receipt.AgentStatus == "succeeded" && evaluation.ExecutionStatus == EvaluationStatus.Failed &&
                receipt.Prepared is null && receipt.CompletedSessionSha256 is null,
            "prepared" or "state_failed" => receipt.Stage == "prepare" && GrowthProfiles.StateCode(receipt.Diagnostic) &&
                receipt.AgentStatus == "succeeded" && evaluation.ExecutionStatus == EvaluationStatus.Completed &&
                EvaluationLimits.Hash(receipt.CompletedSessionSha256) && (receipt.Code == "prepared"
                    ? receipt.Prepared is { } prepared && prepared.Generation == input.Slot.Phase &&
                        prepared.SessionSha256 == receipt.CompletedSessionSha256 && EvaluationLimits.Hash(prepared.EnvelopeSha256)
                    : receipt.Prepared is null),
            _ => false,
        };
        if (!lifecycle || accounting.Sends is < 0 or > 8 || accounting.ReservedInputTokens < 0 ||
            accounting.ReservedOutputTokens < 0 || accounting.ReservedCombinedTokens < 0 || accounting.ReservedSpendMicroUsd < 0 ||
            accounting.ReservedInputTokens != accounting.Sends * plan.Input.Bounds.PerCall.MaxInputTokens ||
            accounting.ReservedOutputTokens != accounting.Sends * plan.Input.Bounds.PerCall.MaxOutputTokens ||
            accounting.ReservedCombinedTokens != accounting.ReservedInputTokens + accounting.ReservedOutputTokens ||
            accounting.ReservedSpendMicroUsd != accounting.Sends * plan.Input.Bounds.PerCall.MaxChargeMicroUsd ||
            accounting.ReservedSpendMicroUsd > input.Lease.Allocation.SpendMicroUsd) return false;
        return true;
    }

    internal static UsageJournal? Create(EconomicsPlan plan, string campaign, string transport,
        IReadOnlyList<EconomicsReceipt> receipts, string stopReason)
    {
        var expected = plan.Expectation(campaign, transport);
        var attempts = ImmutableArray.CreateBuilder<UsageJournalAttempt>();
        var calls = ImmutableArray.CreateBuilder<UsageJournalCall>();
        long reserved = 0;
        for (var index = 0; index < plan.Slots.Length; index++)
        {
            if (index >= receipts.Count)
            {
                attempts.Add(new(expected.BindingSha256, expected.AttemptId(index), index, plan.Slots[index].CaseId,
                    "unattempted", "not_started", null, 0, 0, 0));
                continue;
            }
            var receipt = receipts[index];
            if (receipt.Index != index) return null;
            var sends = receipt.Calls.Count(call => call.Dispatched);
            attempts.Add(new(expected.BindingSha256, expected.AttemptId(index), index, plan.Slots[index].CaseId,
                receipt.Evaluation!.ExecutionStatus.ToString().ToLowerInvariant(), receipt.AgentStatus,
                receipt.Evaluation.AttemptSha256, receipt.Calls.Length, sends, receipt.Calls.Length - sends));
            foreach (var call in receipt.Calls)
                calls.Add(new(expected.BindingSha256, expected.CallId(index, call.Ordinal), expected.AttemptId(index),
                    call.Ordinal, call.Dispatched, call.TransportOutcome, call.ChatOutcome, call.UsageStatus, call.Usage));
            reserved += receipt.Accounting.Sends;
        }
        var per = plan.Input.Bounds.PerCall;
        var attemptRows = attempts.ToImmutable(); var callRows = calls.ToImmutable();
        return UsageJournal.Admit(new(expected.Provenance, expected.Plan, expected.BindingSha256, stopReason,
            "not_applicable", new(reserved, reserved * per.MaxInputTokens, reserved * per.MaxOutputTokens,
                reserved * (per.MaxInputTokens + per.MaxOutputTokens), reserved * per.MaxChargeMicroUsd),
            attemptRows, callRows, UsageJournal.Totals(attemptRows, callRows)), expected);
    }

    internal static string? Stop(EconomicsReceipt receipt, EconomicsPlan plan)
    {
        if (receipt.Accounting.AccountingViolation || receipt.Calls.Any(call => call.Usage is { } usage &&
                (usage.InputTokens > plan.Input.Bounds.PerCall.MaxInputTokens || usage.OutputTokens > plan.Input.Bounds.PerCall.MaxOutputTokens)))
            return "accounting_violation";
        if (receipt.Calls.Any(call => call.TransportOutcome == "http429")) return "rate_limited";
        if (receipt.Calls.Any(call => call.ChatOutcome == "cancelled") || receipt.Diagnostic == "agent_cancelled") return "caller_cancelled";
        if (receipt.Diagnostic == "agent_deadline_exceeded") return "deadline";
        return null;
    }
    internal static bool Capacity(EconomicsReceipt receipt) => receipt.Code == "session_failed" &&
        receipt.Diagnostic == AgentSessionCodes.ConstructionLimit || receipt.Code == "agent_failed" &&
        receipt.Diagnostic == "agent_response_invalid" && (receipt.Measurement.LastResponseMessages > AgentLimits.Messages ||
            receipt.Measurement.LastContinuationAfterBytes > AgentLimits.ContinuationTotalBytes);
}
