using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

// Bounded APR rejection metadata and historical fixed-feedback records only.
// Rejected argument bytes and successful evidence never enter this signal.
internal sealed record LiveRecoveryDiagnostic(
    int ScheduleIndex,
    int RejectionIndex,
    string Tool,
    string Category,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? PathField = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? PathRule = null)
{
    internal const string ArgumentsInvalid = "arguments_invalid";
    internal const string ListFilesPathInvalid = "list_files_path_invalid";
    internal const string PathNotTracked = "path_not_tracked";
    internal const string CursorInvalid = "cursor_invalid";

    // The historical field name is retained by report readers. This value
    // records a bounded APR rejection, never evidence that correction ran.
    internal static LiveRecoveryDiagnostic? FromAuthoredRejection(int index, LiveToolRejectionProjection? rejected)
    {
        if (rejected is null) return null;
        var category = rejected.Category == ListFilesPathInvalid ? ListFilesPathInvalid : rejected.FailureCode switch
        {
            AgentFailureCodes.ToolArgumentsInvalid => ArgumentsInvalid,
            AgentFailureCodes.ToolPathNotTracked => PathNotTracked,
            AgentFailureCodes.ToolCursorInvalid => CursorInvalid,
            _ => null,
        };
        if (category is null) return null;
        var value = new LiveRecoveryDiagnostic(index, 0, rejected.Tool, category,
            category == ListFilesPathInvalid ? rejected.PathField : null,
            category == ListFilesPathInvalid ? rejected.PathRule : null);
        return value.IsCanonical() ? value : null;
    }

    internal static ImmutableArray<LiveRecoveryDiagnostic> Capture(
        int scheduleIndex, AgentRunOutcome outcome)
    {
        var found = ImmutableArray.CreateBuilder<LiveRecoveryDiagnostic>();
        var events = outcome.Events;
        for (var index = 0; index < events.Length; index++)
        {
            if (events[index] is not AgentRecoveryToolCallEvent call ||
                !call.Rejected)
                continue;
            if (index + 1 >= events.Length ||
                events[index + 1] is not AgentToolErrorEvent error ||
                error.CallId != call.CallId || error.Name != call.Name)
                throw new InvalidOperationException("recovery_event_pair_invalid");

            var fixedError = Encoding.UTF8.GetString(error.CanonicalResult.AsSpan());
            LiveRecoveryDiagnostic diagnostic;
            if (fixedError == AgentRecoveryFeedback.ArgumentsInvalid)
            {
                diagnostic = new(scheduleIndex, found.Count, call.Name,
                    ArgumentsInvalid);
            }
            else if (call.Name == AgentToolRegistry.ListFilesName &&
                AgentRecoveryFeedback.IsCanonicalPathError(fixedError))
            {
                using var document = JsonDocument.Parse(fixedError);
                var root = document.RootElement;
                diagnostic = new(scheduleIndex, found.Count, call.Name,
                    ListFilesPathInvalid,
                    root.GetProperty("path_field").GetString(),
                    root.GetProperty("path_rule").GetString());
            }
            else if (AgentRecoveryFeedback.IsRejectedError(call.Name, fixedError) &&
                fixedError is AgentRecoveryFeedback.TrackedPathMissing or AgentRecoveryFeedback.ChangedPathMissing or
                    AgentRecoveryFeedback.CursorInvalid)
            {
                diagnostic = new(scheduleIndex, found.Count, call.Name,
                    fixedError == AgentRecoveryFeedback.CursorInvalid ? CursorInvalid : PathNotTracked);
            }
            else
            {
                throw new InvalidOperationException("recovery_error_invalid");
            }

            if (!diagnostic.IsCanonical())
                throw new InvalidOperationException("recovery_diagnostic_invalid");
            found.Add(diagnostic);
        }

        return found.ToImmutable();
    }

    internal bool IsCanonical() =>
        ScheduleIndex >= 0 && RejectionIndex >= 0 &&
        Tool is AgentToolRegistry.ListFilesName or
            AgentToolRegistry.ListChangedFilesName or
            AgentToolRegistry.ReadDiffName or
            AgentToolRegistry.ReadFileName or
            AgentToolRegistry.SearchTextName &&
        (Category == ArgumentsInvalid && PathField is null && PathRule is null ||
         Category == ListFilesPathInvalid &&
             Tool == AgentToolRegistry.ListFilesName &&
             PathField is not null && PathRule is not null &&
             LiveToolRejectionProjector.ValidPathDetail(PathField, PathRule) ||
         Category == PathNotTracked && PathField is null && PathRule is null &&
             Tool is AgentToolRegistry.ReadFileName or AgentToolRegistry.SearchTextName or AgentToolRegistry.ReadDiffName ||
         Category == CursorInvalid && PathField is null && PathRule is null &&
             Tool is AgentToolRegistry.ListFilesName or AgentToolRegistry.ListChangedFilesName);
}
