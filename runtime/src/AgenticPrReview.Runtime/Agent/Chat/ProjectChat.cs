using AgenticPrReview.Runtime.Agent.Core;

namespace AgenticPrReview.Runtime.Agent.Chat;

internal interface IProjectChatClient
{
    Task<ProjectChatResponse> GetResponseAsync(
        ProjectChatRequest request,
        CancellationToken cancellationToken);
}

// Fixed stage reasons for local diagnostics. A provider adapter maps its
// parser-specific categories into this provider-neutral domain.
internal enum ProjectChatNormalizationReason
{
    None,
    RequestProjection,
    TransportContract,
    ProviderJson,
    ProviderRoot,
    ProviderUsage,
    ProviderChoice,
    ProviderChoiceShape,
    ProviderChoiceIndex,
    ProviderChoiceLogprobs,
    ProviderChoiceMessageMissing,
    ProviderFinishReasonLength,
    ProviderFinishReasonContentFilter,
    ProviderFinishReasonResource,
    ProviderFinishReasonAborted,
    ProviderFinishReasonOther,
    ProviderMessage,
    ProviderInternal,
    ResponseProjection,
}

internal sealed class ProjectChatNormalizationException : Exception
{
    internal ProjectChatNormalizationException()
        : this(AgentFailureCodes.ResponseInvalid)
    {
    }

    internal ProjectChatNormalizationException(string diagnosticCode)
        : this(diagnosticCode, ProjectChatNormalizationReason.None)
    {
    }

    internal ProjectChatNormalizationException(string diagnosticCode,
        ProjectChatNormalizationReason reason)
        : base("The backend response could not be normalized.")
    {
        if (diagnosticCode is not (
            AgentFailureCodes.ResponseInvalid or
            AgentFailureCodes.MissingTool))
        {
            throw new ArgumentOutOfRangeException(nameof(diagnosticCode));
        }

        DiagnosticCode = diagnosticCode;
        Reason = reason;
    }

    internal string DiagnosticCode { get; }
    internal ProjectChatNormalizationReason Reason { get; }
}

internal sealed record ProjectChatRequest(
    ProjectChatMessage[] Messages,
    ProjectToolDefinition[] Tools,
    ProjectContinuation? Continuation,
    bool ThinkingRequired = false);

internal sealed record ProjectChatMessage(
    string Role,
    ProjectChatContent[] Contents)
{
    public override string ToString() => "project_chat_message";
}

internal abstract record ProjectChatContent(string Kind);

internal sealed record ProjectTextContent(string Text)
    : ProjectChatContent("text")
{
    public override string ToString() => "project_text_content";
}

internal sealed record ProjectReasoningContent(
    string Text,
    string Opaque,
    string Framing,
    string? AssociatedCallId,
    int MessagePosition,
    int Position)
    : ProjectChatContent("reasoning")
{
    public override string ToString() => "project_reasoning_content";
}

internal sealed record ProjectToolCallContent(
    string CallId,
    string Name,
    string ArgumentsJson)
    : ProjectChatContent("tool_call")
{
    public override string ToString() => "project_tool_call_content";
}

internal sealed record ProjectToolResultContent(
    string CallId,
    string Result)
    : ProjectChatContent("tool_result")
{
    public override string ToString() => "project_tool_result_content";
}

internal sealed record ProjectChatUsage(
    long InputTokens,
    long OutputTokens,
    ProjectProviderUsage? ProviderUsage = null);

// Optional observation only; never part of Agent admission or SESSION.
internal sealed record ProjectProviderUsage(
    string ProviderId,
    string RequestedModel,
    string ResponseModel,
    long CacheReadInputTokens,
    long UncachedInputTokens)
{
    public override string ToString() => "project_provider_usage";
}

internal sealed record ProjectChatResponse(
    ProjectChatMessage Message,
    ProjectChatUsage? Usage = null,
    long? CapturedResponseBodyBytes = null,
    ProjectContinuation? Continuation = null);

internal sealed record ProjectToolDefinition(
    string Name,
    string Description,
    string SchemaJson);

internal sealed record ProjectContinuation(
    string ProviderId,
    string ModelId,
    string AdapterId,
    string SessionId,
    ProjectContinuationItem[] Items)
{
    public override string ToString() => "project_continuation";
}

internal sealed record ProjectContinuationItem(
    string Readable,
    string Opaque,
    string Framing,
    string? AssociatedCallId,
    int MessagePosition,
    int ContentPosition)
{
    public override string ToString() => "project_continuation_item";
}
