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
        if (summary.UsageJournal is not { } journal ||
            UsageJournal.Admit(journal) is null ||
            journal.Provenance.PlanSha256 != summary.PlanSha256 ||
            journal.Provenance.CorpusSha256 != summary.CorpusSha256 ||
            journal.Provenance.SourceCommit != summary.SourceCommit ||
            journal.Provenance.SourceTree != summary.SourceTree ||
            journal.Provenance.SourceClean != summary.SourceClean ||
            journal.Provenance.ExecutionKind != summary.ExecutionKind ||
            journal.Plan.Rubric != summary.ProspectiveRubric ||
            !journal.Plan.Schedule.SequenceEqual(R6ProspectiveRubric.Cases))
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
            recoveryReceipts.Length != 5 || recoveryDiagnostics.Length > 1)
            return Block("recovery_count_invalid");

        var groups = new HashSet<(string Scope, string Id)>();
        var expectedCredits = 0;
        var offFocus = 0;
        var repeats = 0;
        var ai = 0;
        var human = 0;
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
                audit.ObservedCount != caseDiagnostics.Length ||
                audit.ObservedCount is < 0 or > 1 ||
                audit.Status != (audit.ObservedCount == 0
                    ? R6ProspectiveRecoveryAudit.None : R6ProspectiveRecoveryAudit.Qualified) ||
                audit.Reason != (audit.ObservedCount == 0 ? "none" : "qualified") ||
                caseDiagnostics.Where((d, position) => !d.IsCanonical() ||
                    d.RejectionIndex != position).Any())
                return Block("recovery_ineligible");
            if (caseReceipt.Origin == R6ProspectiveAssessment.AiOrigin) ai++;
            else if (caseReceipt.Origin == R6ProspectiveAssessment.HumanOrigin) human++;
            else return Block("origin_invalid");

            var localGroups = new HashSet<(string Scope, string Id)>();
            var focal = 0;
            for (var ordinal = 0; ordinal < caseReceipt.Findings.Length; ordinal++)
            {
                var finding = caseReceipt.Findings[ordinal];
                if (finding is null || finding.FindingOrdinal != ordinal ||
                    finding.Verdict is R6ProspectiveAssessment.FalseUnsafe or
                        R6ProspectiveAssessment.Unresolved ||
                    finding.SafeLineRole == R6ProspectiveAssessment.Accusation ||
                    finding.CitationClass == "invalid")
                    return Block("finding_ineligible");
                if (finding.Verdict is not (R6ProspectiveAssessment.Expected or
                    R6ProspectiveAssessment.TrueOffFocus) ||
                    finding.DefectGroupScope is not (R6ProspectiveAssessment.Authored or
                        R6ProspectiveAssessment.Run) ||
                    finding.DefectGroupId is null ||
                    finding.DefectGroupScope == R6ProspectiveAssessment.Authored &&
                        finding.DefectGroupId is not ("cs-null-deref" or "ts-zero-timeout" or
                            "repository-token-log") ||
                    finding.DefectGroupScope == R6ProspectiveAssessment.Run &&
                        (!finding.DefectGroupId.StartsWith("run-", StringComparison.Ordinal) ||
                         finding.DefectGroupId.Length != 68 ||
                         !EvaluationLimits.Hash(finding.DefectGroupId[4..])) ||
                    finding.SafeLineRole is not (R6ProspectiveAssessment.None or
                        R6ProspectiveAssessment.Comparison) ||
                    finding.CitationClass is not ("exact" or "bounded_context" or "reviewed_other") ||
                    finding.Reason != (finding.Verdict == R6ProspectiveAssessment.Expected
                        ? "confirmed_expected" : "confirmed_off_focus"))
                    return Block("finding_shape_invalid");
                var group = (finding.DefectGroupScope, finding.DefectGroupId);
                if (!localGroups.Add(group)) return Block("within_case_duplicate");
                if (!groups.Add(group)) repeats++;
                if (finding.Verdict == R6ProspectiveAssessment.Expected)
                {
                    if (ExpectedGroups[index].Length == 0 ||
                        finding.DefectGroupScope != R6ProspectiveAssessment.Authored ||
                        finding.DefectGroupId != ExpectedGroups[index] ||
                        finding.ExpectedDefectId != "defect" ||
                        finding.CitationClass == "reviewed_other")
                        return Block("expected_binding_invalid");
                    focal++;
                }
                else
                {
                    if (finding.ExpectedDefectId is not null ||
                        finding.DefectGroupScope == R6ProspectiveAssessment.Authored &&
                        finding.DefectGroupId == ExpectedGroups[index])
                        return Block("off_focus_binding_invalid");
                    offFocus++;
                }
            }
            if (focal != (ExpectedGroups[index].Length == 0 ? 0 : 1))
                return Block("expected_finding_missing");
            if (row.Code == EvaluationCode.ExpectedFindingMissing && focal == 0 ||
                row.Code == EvaluationCode.ProhibitedFinding && row.ProhibitedObservations == 0 ||
                row.Code == EvaluationCode.Scored &&
                    (row.ProhibitedObservations != 0 || row.StructurallyMissingDefects != 0))
                return Block("legacy_override_invalid");
            if (row.ProhibitedObservations > 0 &&
                caseReceipt.Findings.Count(f => f.SafeLineRole ==
                    R6ProspectiveAssessment.Comparison) < row.ProhibitedObservations)
                return Block("prohibited_evidence_unexplained");
            expectedCredits += focal;
        }
        if (summary.AiAdjudicatedCases != ai || summary.HumanConfirmedCases != human)
            return Block("origin_count_mismatch");
        return new("candidate_pass", "all_gates_passed", expectedCredits, offFocus,
            repeats, groups.Count, recoveryDiagnostics.Length, ai, human,
            legacy, "passed", "passed",
            repeats > 0 || recoveryDiagnostics.Length > 0 || offFocus > 0
                ? "cost_recorded" : "clean");
    }
}
