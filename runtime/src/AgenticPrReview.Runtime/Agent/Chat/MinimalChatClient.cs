namespace AgenticPrReview.Runtime.Agent.Chat;

internal sealed class MinimalChatClient(
    IMinimalChatBackend backend) : IProjectChatClient
{
    public async Task<ProjectChatResponse> GetResponseAsync(
        ProjectChatRequest request,
        CancellationToken cancellationToken)
    {
        var native = Materialize(request);
        var response = await backend.GetResponseAsync(native, cancellationToken);
        return new ProjectChatResponse(ToProject(response.Message));
    }

    internal static MinimalChatRequest Materialize(ProjectChatRequest request)
    {
        var messages = request.Messages
            .Select((message, messagePosition) => ToNative(message, messagePosition))
            .ToArray();
        var continuation = request.Continuation is null
            ? null
            : new MinimalChatContinuation(
                request.Continuation.ProviderId,
                request.Continuation.ModelId,
                request.Continuation.AdapterId,
                request.Continuation.SessionId,
                request.Continuation.Items.Select(item =>
                    new MinimalChatContinuationItem(
                        item.Readable,
                        item.Opaque,
                        item.Framing,
                        item.AssociatedCallId,
                        item.MessagePosition,
                        item.ContentPosition)).ToArray());
        if (continuation is not null)
        {
            InsertContinuations(messages, continuation);
        }
        return new MinimalChatRequest(
            messages,
            request.Tools.Select(tool => new MinimalChatTool(
                tool.Name,
                tool.Description,
                tool.SchemaJson)).ToArray(),
            continuation);
    }

    private static MinimalChatMessage ToNative(
        ProjectChatMessage message,
        int messagePosition) => new(
        message.Role,
        message.Contents.Select((content, contentPosition) => content switch
        {
            ProjectTextContent text => new MinimalChatContent(
                "text", null, null, text.Text, null, null, null,
                messagePosition, contentPosition),
            ProjectReasoningContent reasoning => new MinimalChatContent(
                "reasoning",
                null,
                null,
                reasoning.Text,
                reasoning.Opaque,
                reasoning.Framing,
                reasoning.AssociatedCallId,
                reasoning.MessagePosition,
                reasoning.Position),
            ProjectToolCallContent call => new MinimalChatContent(
                "tool_call",
                call.CallId,
                call.Name,
                call.ArgumentsJson,
                null,
                null,
                null,
                messagePosition,
                contentPosition),
            ProjectToolResultContent result => new MinimalChatContent(
                "tool_result",
                result.CallId,
                null,
                result.Result,
                null,
                null,
                null,
                messagePosition,
                contentPosition),
            _ => throw new InvalidOperationException("Unsupported project chat content."),
        }).ToArray());

    private static void InsertContinuations(
        MinimalChatMessage[] messages,
        MinimalChatContinuation continuation)
    {
        foreach (var item in continuation.Items.OrderBy(item => item.MessagePosition))
        {
            if (item.MessagePosition < 0 ||
                item.MessagePosition >= messages.Length)
            {
                throw new InvalidOperationException("Invalid continuation message position.");
            }
            var target = messages[item.MessagePosition];
            if (item.ContentPosition < 0 ||
                item.ContentPosition > target.Contents.Length)
            {
                throw new InvalidOperationException("Invalid continuation content position.");
            }
            var contents = target.Contents.ToList();
            contents.Insert(item.ContentPosition, new MinimalChatContent(
                "reasoning",
                null,
                null,
                item.Readable,
                item.Opaque,
                item.Framing,
                item.AssociatedCallId,
                item.MessagePosition,
                item.ContentPosition));
            messages[item.MessagePosition] = target with
            {
                Contents = contents.Select((content, position) =>
                    content with { Position = position }).ToArray(),
            };
        }
    }

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
                content.MessagePosition,
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
    int MessagePosition,
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
    MinimalChatContinuationItem[] Items);

internal sealed record MinimalChatContinuationItem(
    string Readable,
    string Opaque,
    string Framing,
    string? AssociatedCallId,
    int MessagePosition,
    int ContentPosition);

internal sealed record MinimalChatResponse(MinimalChatMessage Message);
