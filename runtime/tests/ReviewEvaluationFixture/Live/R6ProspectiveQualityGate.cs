using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Contracts;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

internal sealed record R6ProspectiveGateResult(
    [property: JsonRequired] string Status,
    [property: JsonRequired] string Reason,
    [property: JsonRequired] int ExpectedCredits,
    [property: JsonRequired] int TrueOffFocusFindings,
    [property: JsonRequired] int CrossCaseRepeats,
    [property: JsonRequired] int UniqueDefectGroups,
    [property: JsonRequired] int QualifiedRecoveries,
    [property: JsonRequired] int AiAdjudicatedCases,
    [property: JsonRequired] int HumanConfirmedCases,
    [property: JsonRequired] string LegacyStructuralStatus,
    [property: JsonRequired] string ProspectiveSemanticStatus,
    [property: JsonRequired] string SafetyStatus,
    [property: JsonRequired] string UsabilityStatus);

// Recomputable candidate from public-safe, bounded rows. It verifies row
// consistency and accounting, but semantic truth still depends on the actual
// independent reviewer of the authenticated in-process subject.
internal static class R6ProspectiveQualityGate
{
    private static readonly string[] ExpectedGroups =
        ["cs-null-deref", "", "ts-zero-timeout", "", "repository-token-log"];

    // Public rows are untrusted persisted evidence. Validate their complete
    // grammar before a blocking finding can short-circuit the gate decision.
    internal static bool ValidFindingShape(R6ProspectiveFindingReceipt? finding,
        int caseIndex, int ordinal)
    {
        if (finding is null || finding.FindingOrdinal != ordinal ||
            finding.SafeLineRole is not (R6ProspectiveAssessment.None or
                R6ProspectiveAssessment.Comparison or R6ProspectiveAssessment.Accusation))
            return false;
        var isTrue = finding.Verdict is R6ProspectiveAssessment.Expected or
            R6ProspectiveAssessment.TrueOffFocus;
        if (!isTrue && finding.Verdict is not (R6ProspectiveAssessment.FalseUnsafe or
            R6ProspectiveAssessment.Unresolved)) return false;
        if (isTrue)
        {
            if (finding.DefectGroupScope is not (R6ProspectiveAssessment.Authored or
                    R6ProspectiveAssessment.Run) ||
                finding.DefectGroupId is null ||
                finding.DefectGroupScope == R6ProspectiveAssessment.Authored &&
                    finding.DefectGroupId is not ("cs-null-deref" or "ts-zero-timeout" or
                        "repository-token-log") ||
                finding.DefectGroupScope == R6ProspectiveAssessment.Run &&
                    (!finding.DefectGroupId.StartsWith("run-", StringComparison.Ordinal) ||
                     finding.DefectGroupId.Length != 68 ||
                     !EvaluationLimits.Hash(finding.DefectGroupId[4..])) ||
                finding.CitationClass is not ("exact" or "bounded_context" or
                    "reviewed_other" or "invalid") ||
                finding.DefectGroupScope == R6ProspectiveAssessment.Authored &&
                    finding.CitationClass == "reviewed_other" ||
                finding.DefectGroupScope == R6ProspectiveAssessment.Run &&
                    finding.CitationClass is "exact" or "bounded_context")
                return false;
            if (finding.Verdict == R6ProspectiveAssessment.Expected)
            {
                if (ExpectedGroups[caseIndex].Length == 0 ||
                    finding.DefectGroupScope != R6ProspectiveAssessment.Authored ||
                    finding.DefectGroupId != ExpectedGroups[caseIndex] ||
                    finding.ExpectedDefectId != "defect") return false;
            }
            else if (finding.ExpectedDefectId is not null ||
                finding.DefectGroupScope == R6ProspectiveAssessment.Authored &&
                    finding.DefectGroupId == ExpectedGroups[caseIndex]) return false;
        }
        else if (finding.ExpectedDefectId is not null ||
            finding.DefectGroupScope is not null || finding.DefectGroupId is not null ||
            finding.CitationClass != "invalid") return false;
        var reason = finding.Verdict switch
        {
            R6ProspectiveAssessment.FalseUnsafe => "review_rejected",
            R6ProspectiveAssessment.Unresolved => "review_pending",
            _ when finding.SafeLineRole == R6ProspectiveAssessment.Accusation =>
                "safe_line_accusation",
            _ when finding.CitationClass == "invalid" => "citation_invalid",
            R6ProspectiveAssessment.Expected => "confirmed_expected",
            _ => "confirmed_off_focus",
        };
        return finding.Reason == reason;
    }

    // The journal's own admission proves its internal graph. Public readback
    // must also bind that graph to these outcome rows and summary counters;
    // the finite plan alone is shared by distinct live executions.
    internal static bool ValidJournalBinding(LiveRunSummary summary,
        ImmutableArray<EvaluationOutcome> outcomes)
    {
        if (summary.ProspectiveRubric is not { } rubric ||
            rubric.Id != R6ProspectiveRubric.Id ||
            rubric.Sha256 != R6ProspectiveRubric.Sha256 ||
            summary.UsageJournal is not { } journal ||
            UsageJournal.Admit(journal) is null ||
            journal.Plan.Rubric != rubric ||
            !journal.Plan.Schedule.SequenceEqual(R6ProspectiveRubric.Cases) ||
            journal.Provenance.PlanSha256 != summary.PlanSha256 ||
            journal.Provenance.CorpusSha256 != summary.CorpusSha256 ||
            journal.Provenance.SourceCommit != summary.SourceCommit ||
            journal.Provenance.SourceTree != summary.SourceTree ||
            journal.Provenance.SourceClean != summary.SourceClean ||
            journal.Provenance.ExecutionKind != summary.ExecutionKind ||
            journal.StopReason != summary.StopReason ||
            journal.Plan.Bounds.SpendCeilingMicroUsd != summary.SpendCeilingMicroUsd ||
            journal.Attempts.Length != outcomes.Length)
            return false;
        for (var index = 0; index < outcomes.Length; index++)
        {
            var row = outcomes[index];
            var attempt = journal.Attempts[index];
            if (attempt.CaseId != row.CaseId ||
                attempt.EvaluationAttemptSha256 != row.AttemptSha256 ||
                attempt.Status != (row.ExecutionStatus switch
                {
                    EvaluationStatus.Completed => "completed",
                    EvaluationStatus.Invalid => "invalid",
                    _ => "failed",
                })) return false;
        }
        var totals = journal.Totals;
        var reservations = journal.Reservations;
        return totals.Scheduled == summary.Scheduled &&
            totals.Attempted == summary.Attempted &&
            totals.Completed == summary.Completed &&
            totals.Failed == summary.Failed &&
            totals.Invalid == summary.Invalid &&
            totals.Unattempted == summary.Unattempted &&
            reservations.Calls == (long)summary.ActualProviderCalls +
                summary.SimulatedAdapterCalls &&
            (decimal)summary.KnownInputTokens == totals.KnownInputTokens &&
            (decimal)summary.KnownOutputTokens == totals.KnownOutputTokens &&
            (decimal)summary.KnownCombinedTokens == totals.KnownCombinedTokens &&
            summary.UsageUnknownCalls == totals.UnknownUsageSends &&
            reservations.InputTokens == summary.ReservedInputTokens &&
            reservations.OutputTokens == summary.ReservedOutputTokens &&
            reservations.CombinedTokens == summary.ReservedCombinedTokens &&
            reservations.SpendMicroUsd == summary.ReservedSpendMicroUsd;
    }

    internal static R6ProspectiveGateResult? Evaluate(LiveRunSummary summary,
        ImmutableArray<EvaluationOutcome> outcomes)
    {
        if (summary.ProspectiveRubric is null) return null;
        var legacy = outcomes.Length != 5 ? "incomplete" :
            outcomes.Any(row => row.ScenarioStatus != AssertionStatus.Passed) ? "failed" : "passed";
        R6ProspectiveGateResult Block(string reason)
        {
            var semantic = reason is "finding_ineligible" or "finding_shape_invalid" or
                "within_case_duplicate" or "expected_finding_missing" or
                "expected_binding_invalid" or "off_focus_binding_invalid"
                ? "failed" : "not_evaluable";
            var safety = reason is "finding_ineligible" or "finding_shape_invalid"
                ? "failed" : semantic == "failed" ? "passed" : "not_evaluable";
            return new("blocked", reason, 0, 0, 0, 0, 0, 0, 0,
                legacy, semantic, safety, "blocked");
        }
        if (summary.ProspectiveRubric.Id != R6ProspectiveRubric.Id ||
            summary.ProspectiveRubric.Sha256 != R6ProspectiveRubric.Sha256 ||
            summary.CorpusSha256 != R6ProspectiveRubric.CorpusSha256)
            return Block("rubric_identity_invalid");
        if (summary.ExecutionKind != "live")
            return new("not_evaluable", "keyless_run", 0, 0, 0, 0, 0, 0, 0,
                legacy, "not_evaluable", "not_evaluable", "not_evaluable");
        if (!ValidJournalBinding(summary, outcomes))
            return Block("plan_binding_invalid");
        if (summary.Scheduled != 5 || summary.Attempted != 5 || summary.Completed != 5 ||
            summary.Failed != 0 || summary.Invalid != 0 || summary.Unattempted != 0 ||
            summary.StopReason != "complete" || summary.Cleanup != "cleaned" ||
            !summary.SourceClean || summary.AccountingViolation ||
            summary.SimulatedAdapterCalls != 0 || summary.ActualProviderCalls <= 0 ||
            summary.UsageUnknownCalls != 0 || !summary.AgentDiagnostics.IsDefaultOrEmpty ||
            summary.AdjudicationStatus != "adjudicated" || outcomes.Length != 5)
            return Block("population_ineligible");
        if (summary.ProspectiveCaseReceipts is not { } receipts ||
            receipts.IsDefault || receipts.Length != 5)
            return Block("case_receipts_missing");
        var recoveryDiagnostics = summary.RecoveryDiagnostics ?? [];
        var recoveryReceipts = summary.ProspectiveRecoveryReceipts ?? [];
        if (recoveryDiagnostics.IsDefault || recoveryReceipts.IsDefault ||
            recoveryReceipts.Length != 5 ||
            recoveryDiagnostics.Any(item => item.ScheduleIndex is < 0 or >= 5))
            return Block("recovery_count_invalid");

        // Phase one validates every bound row. A malformed later row cannot be
        // hidden behind an earlier unsafe finding in the retained public report.
        for (var index = 0; index < 5; index++)
        {
            var row = outcomes[index];
            var caseReceipt = receipts[index];
            if (row.CaseId != R6ProspectiveRubric.Cases[index] ||
                row.CorpusSha256 != R6ProspectiveRubric.CorpusSha256 ||
                row.Mode != "live" || row.ExecutionStatus != EvaluationStatus.Completed ||
                row.EvidenceStatus != AssertionStatus.Passed ||
                row.ScenarioStatus != (row.Code == EvaluationCode.Scored
                    ? AssertionStatus.Passed : AssertionStatus.Failed) ||
                row.FailureSource != EvaluationFailureSource.None ||
                row.FailureKind != EvaluationFailureKind.None ||
                row.DuplicateObservations != 0 ||
                row.SourceCommit != summary.SourceCommit ||
                row.SourceTree != summary.SourceTree ||
                row.SourceClean != summary.SourceClean ||
                row.Code is not (EvaluationCode.Scored or EvaluationCode.ExpectedFindingMissing or
                    EvaluationCode.ProhibitedFinding) ||
                caseReceipt is null || caseReceipt.ScheduleIndex != index ||
                caseReceipt.CaseId != row.CaseId ||
                caseReceipt.RubricId != R6ProspectiveRubric.Id ||
                caseReceipt.RubricSha256 != R6ProspectiveRubric.Sha256 ||
                caseReceipt.CorpusSha256 != row.CorpusSha256 ||
                caseReceipt.CaseSha256 != row.CaseSha256 ||
                caseReceipt.ConfigurationSha256 != row.ConfigurationSha256 ||
                caseReceipt.ExecutionSha256 != row.ExecutionSha256 ||
                caseReceipt.Status != "assessed" ||
                caseReceipt.Findings.IsDefault ||
                caseReceipt.FindingRowCount != row.FindingCount ||
                caseReceipt.Findings.Length != row.FindingCount)
                return Block("case_binding_invalid");
            var audit = recoveryReceipts[index];
            var caseDiagnostics = recoveryDiagnostics.Where(d => d.ScheduleIndex == index).ToArray();
            if (audit is null || audit.RubricId != R6ProspectiveRubric.Id ||
                audit.RubricSha256 != R6ProspectiveRubric.Sha256 ||
                audit.ScheduleIndex != index || audit.CaseSha256 != row.CaseSha256 ||
                audit.ConfigurationSha256 != row.ConfigurationSha256 ||
                audit.ExecutionSha256 != row.ExecutionSha256 ||
                !R6ProspectiveRecoveryAudit.ValidPublicBinding(audit,
                    caseDiagnostics.Length) ||
                caseDiagnostics.Where((d, position) => !d.IsCanonical() ||
                    d.RejectionIndex != position).Any())
                return Block("recovery_ineligible");
            if (caseReceipt.Origin is not (R6ProspectiveAssessment.AiOrigin or
                R6ProspectiveAssessment.HumanOrigin)) return Block("origin_invalid");
            for (var ordinal = 0; ordinal < caseReceipt.Findings.Length; ordinal++)
                if (!ValidFindingShape(caseReceipt.Findings[ordinal], index, ordinal))
                    return Block("finding_shape_invalid");
        }

        // Phase two derives facts for all five cases before deciding whether any
        // semantic or safety failure blocks this population.
        var groups = new HashSet<(string Scope, string Id)>();
        var expectedCredits = 0;
        var offFocus = 0;
        var repeats = 0;
        var ai = 0;
        var human = 0;
        var qualifiedRecoveries = 0;
        string? blockedReason = null;
        var safetyFailed = false;
        var semanticFailed = false;
        var withinCaseDuplicate = false;
        for (var index = 0; index < 5; index++)
        {
            var row = outcomes[index];
            var caseReceipt = receipts[index];
            var audit = recoveryReceipts[index];
            if (caseReceipt.Origin == R6ProspectiveAssessment.AiOrigin) ai++;
            else human++;
            if (audit.Status == R6ProspectiveRecoveryAudit.Qualified)
                qualifiedRecoveries++;
            else if (audit.Status == R6ProspectiveRecoveryAudit.Blocked)
                blockedReason ??= "recovery_ineligible";
            var localGroups = new HashSet<(string Scope, string Id)>();
            var focal = 0;
            foreach (var finding in caseReceipt.Findings)
            {
                if (finding.Verdict is R6ProspectiveAssessment.FalseUnsafe or
                    R6ProspectiveAssessment.Unresolved ||
                    finding.SafeLineRole == R6ProspectiveAssessment.Accusation ||
                    finding.CitationClass == "invalid")
                {
                    blockedReason ??= "finding_ineligible";
                    semanticFailed = true;
                    safetyFailed = true;
                }
                if (finding.Verdict is not (R6ProspectiveAssessment.Expected or
                    R6ProspectiveAssessment.TrueOffFocus)) continue;
                var group = (finding.DefectGroupScope!, finding.DefectGroupId!);
                if (!localGroups.Add(group))
                {
                    blockedReason ??= "within_case_duplicate";
                    withinCaseDuplicate = true;
                    semanticFailed = true;
                    continue;
                }
                if (finding.Verdict == R6ProspectiveAssessment.Expected) focal++;
                else offFocus++;
            }
            foreach (var group in localGroups)
                if (!groups.Add(group)) repeats++;
            if (focal != (ExpectedGroups[index].Length == 0 ? 0 : 1))
            {
                blockedReason ??= "expected_finding_missing";
                semanticFailed = true;
            }
            if (row.Code == EvaluationCode.ExpectedFindingMissing && focal == 0 ||
                row.Code == EvaluationCode.ProhibitedFinding && row.ProhibitedObservations == 0 ||
                row.Code == EvaluationCode.Scored &&
                    (row.ProhibitedObservations != 0 || row.StructurallyMissingDefects != 0))
                blockedReason ??= "legacy_override_invalid";
            if (row.ProhibitedObservations > 0 &&
                caseReceipt.Findings.Count(f => f.SafeLineRole ==
                    R6ProspectiveAssessment.Comparison) < row.ProhibitedObservations)
                blockedReason ??= "prohibited_evidence_unexplained";
            if (focal > 0) expectedCredits++;
        }
        if (summary.AiAdjudicatedCases != ai || summary.HumanConfirmedCases != human)
            return Block("origin_count_mismatch");
        if (recoveryReceipts.Sum(receipt => receipt.ObservedCount) > 1 ||
            recoveryDiagnostics.Length > 1)
            blockedReason ??= "recovery_count_invalid";
        var usability = withinCaseDuplicate ? "blocked" :
            repeats > 0 || qualifiedRecoveries > 0 || offFocus > 0
                ? "cost_recorded" : "clean";
        if (blockedReason is not null)
            return new("blocked", blockedReason, expectedCredits, offFocus,
                repeats, groups.Count, qualifiedRecoveries, ai, human, legacy,
                semanticFailed ? "failed" : "passed",
                safetyFailed ? "failed" : "passed", usability);
        return new("candidate_pass", "all_gates_passed", expectedCredits, offFocus,
            repeats, groups.Count, qualifiedRecoveries, ai, human,
            legacy, "passed", "passed", usability);
    }
}
