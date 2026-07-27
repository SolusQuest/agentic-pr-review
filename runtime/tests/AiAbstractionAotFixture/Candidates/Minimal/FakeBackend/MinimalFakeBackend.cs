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

        var scripted = FixtureScript.ResponseFor(phase, _turn++, scenario);
        var contents = scripted.Message.Contents.Select(content => content switch
        {
            AgenticPrReview.Runtime.Agent.Chat.ProjectTextContent text =>
                new MinimalChatContent("text", null, null, text.Text, null, null, null, 0),
            AgenticPrReview.Runtime.Agent.Chat.ProjectReasoningContent reasoning =>
                new MinimalChatContent(
                    "reasoning",
                    null,
                    null,
                    reasoning.Text,
                    reasoning.Opaque,
                    reasoning.Framing,
                    reasoning.AssociatedCallId,
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
                    0),
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
        request.Tools.Select(tool => tool.Name).ToArray(),
        request.Continuation is null
            ? null
            : new ObservedContinuation(
                request.Continuation.ProviderId,
                request.Continuation.ModelId,
                "candidate-adapter",
                request.Continuation.SessionId,
                FixtureHash.Text(request.Continuation.Readable),
                FixtureHash.Text(request.Continuation.Opaque),
                request.Continuation.Framing,
                request.Continuation.AssociatedCallId,
                request.Continuation.MessagePosition,
                request.Continuation.ContentPosition));
    }
}
