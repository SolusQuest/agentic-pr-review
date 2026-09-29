using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

internal sealed record R6ProspectiveRecoveryReceipt(
    [property: JsonRequired] string RubricId,
    [property: JsonRequired] string RubricSha256,
    [property: JsonRequired] int ScheduleIndex,
    [property: JsonRequired] string CaseSha256,
    [property: JsonRequired] string? ConfigurationSha256,
    [property: JsonRequired] string? ExecutionSha256,
    [property: JsonRequired] int ObservedCount,
    [property: JsonRequired] string Status,
    [property: JsonRequired] string Reason);

internal sealed record R6ProspectiveRecoveryCapture(
    R6ProspectiveRecoveryReceipt Receipt,
    ImmutableArray<LiveRecoveryDiagnostic> CanonicalDiagnostics);

// Inspects the committed in-process event stream before it is discarded.
// Public receipts contain no call ID, arguments, path, or model text.
internal static class R6ProspectiveRecoveryAudit
{
    internal const string Qualified = "qualified";
    internal const string None = "none";
    internal const string Blocked = "blocked";

    internal static R6ProspectiveRecoveryCapture Capture(int scheduleIndex,
        EvaluationCase testCase, AgentRunOutcome outcome, EvaluationSubject? subject,
        string? attemptedConfigurationSha256 = null,
        LivePlanRubric? selectedRubric = null)
    {
        var rubric = selectedRubric ?? new LivePlanRubric(R6ProspectiveRubric.Id,
            R6ProspectiveRubric.Sha256);
        var events = outcome.Events;
        var reason = None;
        var found = 0;
        var canonicalPairs = true;
        for (var index = 0; index < events.Length; index++)
        {
            if (events[index] is not AgentRecoveryToolCallEvent { Rejected: true } call)
                continue;
            found++;
            if (reason == None) reason = "qualified";
            if (index + 1 >= events.Length ||
                events[index + 1] is not AgentToolErrorEvent error ||
                error.CallId != call.CallId || error.Name != call.Name)
            {
                reason = "error_pair_invalid";
                canonicalPairs = false;
            }
            else if (events.Any(e => e is AgentToolCallEvent ordinary && ordinary.CallId == call.CallId ||
                      e is AgentToolResultEvent result && result.CallId == call.CallId))
                reason = "rejected_dispatched";
            else if (subject is not null && !ErrorExcludedFromEvidence(events, subject, error))
                reason = "recovery_error_as_evidence";
            else if (!LaterCompletedTool(events, index + 2))
                reason = "followup_missing";
            else if (!outcome.Succeeded || outcome.Review is null || subject is null)
                reason = "completion_missing";
        }
        ImmutableArray<LiveRecoveryDiagnostic> diagnostics = [];
        if (canonicalPairs)
        {
            try { diagnostics = LiveRecoveryDiagnostic.Capture(scheduleIndex, outcome); }
            catch (InvalidOperationException)
            {
                reason = "error_pair_invalid";
                canonicalPairs = false;
            }
        }
        if (canonicalPairs && found != diagnostics.Length) reason = "count_mismatch";
        if (found > 1 && reason != "error_pair_invalid") reason = "multiple_recoveries";
        var receipt = new R6ProspectiveRecoveryReceipt(rubric.Id,
            rubric.Sha256, scheduleIndex,
            testCase.Sha256, subject?.ConfigurationSha256 ?? attemptedConfigurationSha256,
            subject?.ExecutionSha256,
            found, reason == None ? None : reason == "qualified" ? Qualified : Blocked, reason);
        return new(receipt, diagnostics);
    }

    internal static bool ValidPublicBinding(R6ProspectiveRecoveryReceipt receipt,
        int canonicalCount)
    {
        if (receipt.ObservedCount is < 0 or > AgentLimits.ToolCalls ||
            receipt.Status is not (None or Qualified or Blocked)) return false;
        if (receipt.ObservedCount == 0)
            return canonicalCount == 0 &&
                (receipt.Status == None && receipt.Reason == "none" ||
                 receipt.Status == Blocked && receipt.Reason == "capture_missing");
        if (receipt.ObservedCount > 1)
            return receipt.Status == Blocked &&
                (receipt.Reason == "multiple_recoveries" &&
                    canonicalCount == receipt.ObservedCount ||
                 receipt.Reason == "error_pair_invalid" && canonicalCount == 0);
        if (receipt.Status == Qualified)
            return canonicalCount == 1 && receipt.Reason == "qualified";
        if (receipt.Status != Blocked) return false;
        if (receipt.Reason == "error_pair_invalid")
            return canonicalCount == 0;
        return canonicalCount == 1 && receipt.Reason is
            ("rejected_dispatched" or "recovery_error_as_evidence" or
             "followup_missing" or "completion_missing");
    }

    private static bool LaterCompletedTool(ImmutableArray<AgentLogicalEvent> events, int start)
    {
        var calls = new HashSet<string>(StringComparer.Ordinal);
        for (var index = start; index < events.Length; index++)
        {
            if (events[index] is AgentToolCallEvent call) calls.Add(call.CallId);
            else if (events[index] is AgentToolResultEvent result && calls.Contains(result.CallId))
                return true;
        }
        return false;
    }

    internal static bool ErrorExcludedFromEvidence(ImmutableArray<AgentLogicalEvent> events,
        EvaluationSubject subject, AgentToolErrorEvent error)
        => ErrorExcludedFromEvidence(events,
            subject.Observations.Select(item => item.ObservationId),
            subject.GroundedObservations.Select(item => item.Observation.ObservationId),
            subject.Findings.SelectMany(item => item.Evidence).Select(item => item.ObservationId),
            error);

    internal static bool ErrorExcludedFromEvidence(ImmutableArray<AgentLogicalEvent> events,
        IEnumerable<string?> observations, IEnumerable<string?> grounded,
        IEnumerable<string?> findingEvidence, AgentToolErrorEvent error)
    {
        var ordinary = events.OfType<AgentToolResultEvent>().ToArray();
        var admittedIds = ordinary.Select(item => item.ObservationId)
            .ToHashSet(StringComparer.Ordinal);
        return ordinary.All(item => item.CallId != error.CallId &&
                item.ResultSha256 != error.ResultSha256) &&
            observations.All(item => item is not null && admittedIds.Contains(item)) &&
            grounded.All(item => item is not null && admittedIds.Contains(item)) &&
            findingEvidence.All(item => item is not null && admittedIds.Contains(item));
    }
}
