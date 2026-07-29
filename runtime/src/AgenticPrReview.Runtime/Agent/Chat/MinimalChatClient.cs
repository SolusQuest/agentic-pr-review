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
        try
        {
            return new ProjectChatResponse(
                ToProject(response.Message),
                response.Usage is null
                    ? null
                    : new ProjectChatUsage(
                        response.Usage.InputTokens,
                        response.Usage.OutputTokens),
                response.CapturedResponseBodyBytes,
                response.Continuation is null
                    ? null
                    : ToProject(response.Continuation));
        }
        catch
        {
            throw new ProjectChatNormalizationException();
        }
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
            continuation,
            request.ThinkingRequired);
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
        foreach (var group in continuation.Items
            .GroupBy(item => item.MessagePosition)
            .OrderBy(group => group.Key))
        {
            if (group.Key < 0 || group.Key >= messages.Length)
            {
                throw new InvalidOperationException("Invalid continuation message position.");
            }

            var target = messages[group.Key];
            if (!StringComparer.Ordinal.Equals(target.Role, "assistant"))
            {
                throw new InvalidOperationException("Invalid continuation message role.");
            }

            var contents = target.Contents.ToList();
            var ordered = group.OrderBy(item => item.ContentPosition).ToArray();
            if (ordered.Select(item => item.ContentPosition).Distinct().Count() !=
                ordered.Length)
            {
                throw new InvalidOperationException("Duplicate continuation content position.");
            }

            foreach (var item in ordered)
            {
                if (item.ContentPosition < 0 ||
                    item.ContentPosition >= target.Contents.Length + ordered.Length ||
                    (item.AssociatedCallId is not null &&
                        !target.Contents.OfType<MinimalChatContent>().Any(content =>
                            content.Kind == "tool_call" &&
                            StringComparer.Ordinal.Equals(
                                content.CallId,
                                item.AssociatedCallId))))
                {
                    throw new InvalidOperationException(
                        "Invalid continuation content position or association.");
                }

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
            }

            messages[group.Key] = target with
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

    private static ProjectContinuation ToProject(
        MinimalChatContinuation continuation) => new(
        continuation.ProviderId,
        continuation.ModelId,
        continuation.AdapterId,
        continuation.SessionId,
        continuation.Items.Select(item => new ProjectContinuationItem(
            item.Readable,
            item.Opaque,
            item.Framing,
            item.AssociatedCallId,
            item.MessagePosition,
            item.ContentPosition)).ToArray());
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
    MinimalChatContinuation? Continuation,
    bool ThinkingRequired = false);

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

internal sealed record MinimalChatUsage(
    long InputTokens,
    long OutputTokens);

internal sealed record MinimalChatResponse(
    MinimalChatMessage Message,
    MinimalChatUsage? Usage = null,
    long? CapturedResponseBodyBytes = null,
    MinimalChatContinuation? Continuation = null);
