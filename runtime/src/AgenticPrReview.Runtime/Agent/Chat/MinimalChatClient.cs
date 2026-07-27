namespace AgenticPrReview.Runtime.Agent.Chat;

internal sealed class MinimalChatClient(
    IMinimalChatBackend backend) : IProjectChatClient
{
    public async Task<ProjectChatResponse> GetResponseAsync(
        ProjectChatRequest request,
        CancellationToken cancellationToken)
    {
        var native = new MinimalChatRequest(
            request.Messages.Select(ToNative).ToArray(),
            request.Tools.Select(tool => new MinimalChatTool(
                tool.Name,
                tool.Description,
                tool.SchemaJson)).ToArray(),
            request.Continuation is null
                ? null
                : new MinimalChatContinuation(
                    request.Continuation.ProviderId,
                    request.Continuation.ModelId,
                    request.Continuation.AdapterId,
                    request.Continuation.SessionId,
                    request.Continuation.Readable,
                    request.Continuation.Opaque,
                    request.Continuation.Framing,
                    request.Continuation.AssociatedCallId,
                    request.Continuation.MessagePosition,
                    request.Continuation.ContentPosition));
        var response = await backend.GetResponseAsync(native, cancellationToken);
        return new ProjectChatResponse(ToProject(response.Message));
    }

    private static MinimalChatMessage ToNative(ProjectChatMessage message) => new(
        message.Role,
        message.Contents.Select(content => content switch
        {
            ProjectTextContent text => new MinimalChatContent(
                "text", null, null, text.Text, null, null, null, 0),
            ProjectReasoningContent reasoning => new MinimalChatContent(
                "reasoning",
                null,
                null,
                reasoning.Text,
                reasoning.Opaque,
                reasoning.Framing,
                reasoning.AssociatedCallId,
                reasoning.Position),
            ProjectToolCallContent call => new MinimalChatContent(
                "tool_call",
                call.CallId,
                call.Name,
                call.ArgumentsJson,
                null,
                null,
                null,
                0),
            ProjectToolResultContent result => new MinimalChatContent(
                "tool_result",
                result.CallId,
                null,
                result.Result,
                null,
                null,
                null,
                0),
            _ => throw new InvalidOperationException("Unsupported project chat content."),
        }).ToArray());

    private static ProjectChatMessage ToProject(MinimalChatMessage message) => new(
        message.Role,
        message.Contents.Select(content => content.Kind switch
        {
            "text" => (ProjectChatContent)new ProjectTextContent(content.Text!),
            "reasoning" => new ProjectReasoningContent(
                content.Text!,
                content.Opaque!,
                content.Framing!,
                content.AssociatedCallId,
                2,
                content.Position),
            "tool_call" => new ProjectToolCallContent(
                content.CallId!,
                content.Name!,
                content.Text!),
            "tool_result" => new ProjectToolResultContent(
                content.CallId!,
                content.Text!),
            _ => throw new InvalidOperationException("Unsupported backend chat content."),
        }).ToArray());
}

internal interface IMinimalChatBackend
{
    Task<MinimalChatResponse> GetResponseAsync(
        MinimalChatRequest request,
        CancellationToken cancellationToken);
}

internal sealed record MinimalChatRequest(
    MinimalChatMessage[] Messages,
    MinimalChatTool[] Tools,
    MinimalChatContinuation? Continuation);

internal sealed record MinimalChatMessage(
    string Role,
    MinimalChatContent[] Contents);

internal sealed record MinimalChatContent(
    string Kind,
    string? CallId,
    string? Name,
    string? Text,
    string? Opaque,
    string? Framing,
    string? AssociatedCallId,
    int Position);

internal sealed record MinimalChatTool(
    string Name,
    string Description,
    string SchemaJson);

internal sealed record MinimalChatContinuation(
    string ProviderId,
    string ModelId,
    string AdapterId,
    string SessionId,
    string Readable,
    string Opaque,
    string Framing,
    string? AssociatedCallId,
    int MessagePosition,
    int ContentPosition);

internal sealed record MinimalChatResponse(MinimalChatMessage Message);
