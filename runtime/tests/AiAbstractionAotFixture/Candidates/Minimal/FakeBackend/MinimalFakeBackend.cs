using AgenticPrReview.Runtime.Agent.Chat;

namespace AgenticPrReview.Runtime.AiAbstractionFixture;

internal sealed class MinimalFakeBackend(
    FixturePhase phase,
    string scenario,
    CandidateProbe probe) : IMinimalChatBackend
{
    private int _turn;

    public async Task<MinimalChatResponse> GetResponseAsync(
        MinimalChatRequest request,
        CancellationToken cancellationToken)
    {
        probe.Add(Observe(request));
        if (scenario == "cancellation")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        var scripted = FixtureScript.ResponseFor(
            phase,
            _turn++,
            scenario,
            request.Messages.Length);
        var contents = scripted.Message.Contents.Select((content, position) => content switch
        {
            AgenticPrReview.Runtime.Agent.Chat.ProjectTextContent text =>
                new MinimalChatContent(
                    "text", null, null, text.Text, null, null, null,
                    request.Messages.Length, position),
            AgenticPrReview.Runtime.Agent.Chat.ProjectReasoningContent reasoning =>
                new MinimalChatContent(
                    "reasoning",
                    null,
                    null,
                    reasoning.Text,
                    reasoning.Opaque,
                    reasoning.Framing,
                    reasoning.AssociatedCallId,
                    reasoning.MessagePosition,
                    reasoning.Position),
            AgenticPrReview.Runtime.Agent.Chat.ProjectToolCallContent call =>
                new MinimalChatContent(
                    "tool_call",
                    call.CallId,
                    call.Name,
                    call.ArgumentsJson,
                    null,
                    null,
                    null,
                    request.Messages.Length,
                    position),
            _ => throw new FixtureFailure("APR_AI_MAPPING"),
        }).ToArray();
        return new MinimalChatResponse(
            new MinimalChatMessage(scripted.Message.Role, contents));
    }

    private static NativeRequestObservation Observe(MinimalChatRequest request)
    {
        if (request.Continuation is not null &&
            string.IsNullOrWhiteSpace(request.Continuation.AdapterId))
        {
            throw new FixtureFailure("APR_AI_MAPPING");
        }
        var continuations = new List<ObservedContinuation>();
        foreach (var (message, messagePosition) in request.Messages.Select(
            (message, messagePosition) => (message, messagePosition)))
        {
            foreach (var (content, contentPosition) in message.Contents.Select(
                (content, contentPosition) => (content, contentPosition)))
            {
                if (content.Kind != "reasoning")
                {
                    continue;
                }
                var item = request.Continuation?.Items.SingleOrDefault(candidate =>
                    candidate.MessagePosition == messagePosition &&
                    candidate.ContentPosition == contentPosition);
                if (item is null ||
                    content.MessagePosition != messagePosition ||
                    content.Position != contentPosition ||
                    item.AssociatedCallId is null ||
                    !message.Contents.Any(candidate =>
                        candidate.Kind == "tool_call" &&
                        candidate.CallId == item.AssociatedCallId))
                {
                    throw new FixtureFailure("APR_AI_CONTINUATION");
                }
                continuations.Add(new ObservedContinuation(
                    request.Continuation!.ProviderId,
                    request.Continuation.ModelId,
                    "candidate-adapter",
                    request.Continuation.SessionId,
                    FixtureHash.Text(content.Text!),
                    FixtureHash.Text(content.Opaque!),
                    content.Framing!,
                    content.AssociatedCallId,
                    messagePosition,
                    contentPosition));
            }
        }
        return new NativeRequestObservation(
        request.Messages.Select((message, messageIndex) => new ObservedMessage(
            message.Role,
            message.Contents.Select(content => new ObservedContent(
                content.Kind,
                content.CallId,
                content.Name,
                content.Text is null ? null : FixtureHash.Text(content.Text),
                content.Opaque is null ? null : FixtureHash.Text(content.Opaque),
                content.Framing,
                content.AssociatedCallId,
                content.Position)).ToArray())).ToArray(),
        request.Tools.Select(tool => NativeObservation.Tool(
            tool.Name,
            tool.Description,
            tool.SchemaJson)).ToArray(),
        continuations.ToArray());
    }
}
