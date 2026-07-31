using System.Collections.Immutable;
using System.Text;

namespace AgenticPrReview.Runtime.Agent.Core;

internal static class AgentFailureCodes
{
    internal const string Cancelled = "agent_cancelled";
    internal const string DeadlineExceeded = "agent_deadline_exceeded";
    internal const string ChatFailed = "agent_chat_failed";
    internal const string ModelLimit = "agent_model_limit";
    internal const string ToolLimit = "agent_tool_limit";
    internal const string TokenLimit = "agent_token_limit";
    internal const string RequestTooLarge = "agent_request_too_large";
    internal const string ResponseTooLarge = "agent_response_too_large";
    internal const string UsageInvalid = "agent_usage_invalid";
    internal const string ResponseInvalid = "agent_response_invalid";
    internal const string UnknownTool = "agent_unknown_tool";
    internal const string ToolArgumentsInvalid = "agent_tool_arguments_invalid";
    internal const string TerminalSequenceInvalid = "agent_terminal_sequence_invalid";
    internal const string TerminalInvalid = "agent_terminal_invalid";
    internal const string ToolPathInvalid = "tool_path_invalid";
    internal const string ToolPathNotTracked = "tool_path_not_tracked";
    internal const string ToolCursorInvalid = "tool_cursor_invalid";
    internal const string ToolPathUnsafe = "tool_path_unsafe";
    internal const string ToolFileTooLarge = "tool_file_too_large";
    internal const string ToolFileBinary = "tool_file_binary";
    internal const string ToolFileInvalidUtf8 = "tool_file_invalid_utf8";
    internal const string ToolFileLoneCr = "tool_file_lone_cr";
    internal const string ToolIoFailed = "tool_io_failed";
    internal const string ToolResultLimit = "tool_result_limit";
}

internal sealed record ReviewedIdentity(
    string RepositoryId,
    long ReviewTarget,
    string BaseSha,
    string HeadSha)
{
    internal bool IsValid()
    {
        return Utf8Bytes(RepositoryId) is >= 1 and <= 128 &&
            ReviewTarget is >= 1 &&
            IsLowerHexSha(BaseSha) &&
            IsLowerHexSha(HeadSha);
    }

    private static int Utf8Bytes(string value)
    {
        try
        {
            return new UTF8Encoding(false, true).GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            return int.MaxValue;
        }
    }

    private static bool IsLowerHexSha(string value)
    {
        if (value.Length != 40)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!(character is >= '0' and <= '9') &&
                !(character is >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed record StableAgentPlan(
    string RepositoryId,
    long ReviewTarget,
    string WorkflowIdentity,
    string PolicySha256,
    string ToolsetSha256,
    string LimitsSha256,
    string BuildId,
    string ProviderId,
    string ModelId,
    string AdapterId,
    string? PriorSessionSha256);

internal static class AgentValueDomains
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool IsIdentifier(string? value)
    {
        if (value is null || value.Length is < 1 or > 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!(character is >= 'A' and <= 'Z') &&
                !(character is >= 'a' and <= 'z') &&
                !(character is >= '0' and <= '9') &&
                character is not '_' and not '-')
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsUtf8(string? value, int minimumBytes, int maximumBytes)
    {
        if (value is null)
        {
            return false;
        }

        try
        {
            var bytes = StrictUtf8.GetByteCount(value);
            return bytes >= minimumBytes && bytes <= maximumBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}

internal sealed record AgentEvidence(
    string ObservationId,
    string Path,
    int StartLine,
    int EndLine);

internal sealed record AgentFinding(
    string Severity,
    string Title,
    string Message,
    ImmutableArray<AgentEvidence> Evidence);

internal sealed record AgentTerminalReview(
    string Summary,
    ImmutableArray<AgentFinding> Findings,
    string TerminalSha256,
    byte[] CanonicalBytes);

internal sealed record AgentDiagnostic(
    string Code,
    int ModelCalls,
    int ToolCalls);

internal sealed record AgentRunOutcome(
    bool Succeeded,
    AgentTerminalReview? Review,
    AgentDiagnostic? Diagnostic,
    ImmutableArray<AgentLogicalEvent> Events,
    AgentContinuationCandidate? Continuation)
{
    internal bool CompletedSessionEligible => Succeeded && Review is not null;

    internal static AgentRunOutcome Success(
        AgentTerminalReview review,
        ImmutableArray<AgentLogicalEvent> events,
        AgentContinuationCandidate? continuation) =>
        new(true, review, null, events, continuation);

    internal static AgentRunOutcome Failure(
        string code,
        int modelCalls,
        int toolCalls,
        ImmutableArray<AgentLogicalEvent> events) =>
        new(false, null, new AgentDiagnostic(code, modelCalls, toolCalls), events, null);
}

internal sealed record AgentContinuationCandidate(
    string ProviderId,
    string ModelId,
    string AdapterId,
    string SessionId,
    ImmutableArray<AgentContinuationCandidateItem> Items)
{
    public override string ToString() => "agent_continuation_candidate";
}

internal sealed record AgentContinuationCandidateItem(
    string Readable,
    string Opaque,
    string Framing,
    string? AssociatedCallId,
    int MessagePosition,
    int ContentPosition)
{
    public override string ToString() => "agent_continuation_candidate_item";
}

internal abstract record AgentLogicalEvent(string Kind);

internal sealed record AgentPlanEvent(string StablePlanSha256)
    : AgentLogicalEvent("stable_plan");

internal sealed record AgentMessageEvent(
    int MessageIndex,
    string Role,
    ImmutableArray<AgentMessagePart> Contents)
    : AgentLogicalEvent("message")
{
    public override string ToString() => "agent_message_event";
}

internal abstract record AgentMessagePart(string Kind);

internal sealed record AgentTextPart(string Text)
    : AgentMessagePart("text")
{
    public override string ToString() => "agent_text_part";
}

internal sealed record AgentReasoningReferencePart(
    string ReadableSha256,
    string OpaqueSha256,
    string FramingSha256,
    string? AssociatedCallId,
    int MessagePosition,
    int ContentPosition)
    : AgentMessagePart("reasoning_reference");

internal sealed record AgentToolCallReferencePart(
    string CallId,
    string Name,
    string ArgumentsSha256)
    : AgentMessagePart("tool_call_reference");

internal sealed record AgentToolResultReferencePart(
    string CallId,
    string ResultSha256)
    : AgentMessagePart("tool_result_reference");

internal sealed record AgentContinuationEvent(
    string ReadableSha256,
    string OpaqueSha256,
    string FramingSha256,
    string? AssociatedCallId,
    int MessagePosition,
    int ContentPosition)
    : AgentLogicalEvent("continuation");

internal sealed record AgentToolCallEvent(
    string CallId,
    string Name,
    string ArgumentsSha256,
    ImmutableArray<byte> CanonicalArguments)
    : AgentLogicalEvent("tool_call");

internal sealed record AgentToolResultEvent(
    string CallId,
    string Name,
    string ObservationId,
    string ResultSha256,
    ImmutableArray<byte> CanonicalResult)
    : AgentLogicalEvent("tool_result");

internal sealed record AgentTerminalEvent(string TerminalSha256)
    : AgentLogicalEvent("terminal");

internal sealed record AgentFailureEvent(string Code)
    : AgentLogicalEvent("failure");
