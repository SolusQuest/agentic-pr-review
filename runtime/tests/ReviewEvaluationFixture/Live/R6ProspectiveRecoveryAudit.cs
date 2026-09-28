using System.Collections.Immutable;
using System.Text.Json.Serialization;
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

// Inspects the committed in-process event stream before it is discarded.
// Public receipts contain no call ID, arguments, path, or model text.
internal static class R6ProspectiveRecoveryAudit
{
    internal const string Qualified = "qualified";
    internal const string None = "none";
    internal const string Blocked = "blocked";

    internal static R6ProspectiveRecoveryReceipt Capture(int scheduleIndex,
        EvaluationCase testCase, AgentRunOutcome outcome, EvaluationSubject? subject)
    {
        var diagnostics = LiveRecoveryDiagnostic.Capture(scheduleIndex, outcome);
        var events = outcome.Events;
        var reason = diagnostics.Length == 0 ? None : "qualified";
        var found = 0;
        for (var index = 0; index < events.Length; index++)
        {
            if (events[index] is not AgentRecoveryToolCallEvent { Rejected: true } call)
                continue;
            found++;
            if (index + 1 >= events.Length ||
                events[index + 1] is not AgentToolErrorEvent error ||
                error.CallId != call.CallId || error.Name != call.Name)
                reason = "error_pair_invalid";
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
        if (found != diagnostics.Length) reason = "count_mismatch";
        if (found > 1) reason = "multiple_recoveries";
        return new(R6ProspectiveRubric.Id, R6ProspectiveRubric.Sha256, scheduleIndex,
            testCase.Sha256, subject?.ConfigurationSha256, subject?.ExecutionSha256,
            found, reason == None ? None : reason == "qualified" ? Qualified : Blocked, reason);
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
    {
        var ordinary = events.OfType<AgentToolResultEvent>().ToArray();
        var admittedIds = ordinary.Select(item => item.ObservationId)
            .ToHashSet(StringComparer.Ordinal);
        return ordinary.All(item => item.CallId != error.CallId &&
                item.ResultSha256 != error.ResultSha256) &&
            subject.Observations.All(item => item.ObservationId is not null &&
                admittedIds.Contains(item.ObservationId)) &&
            subject.GroundedObservations.All(item =>
                admittedIds.Contains(item.Observation.ObservationId)) &&
            subject.Findings.SelectMany(item => item.Evidence).All(item =>
                admittedIds.Contains(item.ObservationId));
    }
}
