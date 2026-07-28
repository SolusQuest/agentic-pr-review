namespace AgenticPrReview.Runtime.Agent.Chat;

internal interface IProjectChatClient
{
    Task<ProjectChatResponse> GetResponseAsync(
        ProjectChatRequest request,
        CancellationToken cancellationToken);
}

internal sealed class ProjectChatNormalizationException : Exception
{
    internal ProjectChatNormalizationException()
        : base("The backend response could not be normalized.")
    {
    }
}

internal sealed record ProjectChatRequest(
    ProjectChatMessage[] Messages,
    ProjectToolDefinition[] Tools,
    ProjectContinuation? Continuation,
    bool ThinkingRequired = false);

internal sealed record ProjectChatMessage(
    string Role,
    ProjectChatContent[] Contents);

internal abstract record ProjectChatContent(string Kind);

internal sealed record ProjectTextContent(string Text)
    : ProjectChatContent("text");

internal sealed record ProjectReasoningContent(
    string Text,
    string Opaque,
    string Framing,
    string? AssociatedCallId,
    int MessagePosition,
    int Position)
    : ProjectChatContent("reasoning");

internal sealed record ProjectToolCallContent(
    string CallId,
    string Name,
    string ArgumentsJson)
    : ProjectChatContent("tool_call");

internal sealed record ProjectToolResultContent(
    string CallId,
    string Result)
    : ProjectChatContent("tool_result");

internal sealed record ProjectChatUsage(
    long InputTokens,
    long OutputTokens);

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
    ProjectContinuationItem[] Items);

internal sealed record ProjectContinuationItem(
    string Readable,
    string Opaque,
    string Framing,
    string? AssociatedCallId,
    int MessagePosition,
    int ContentPosition);
