using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;

namespace AgenticPrReview.Runtime.Execution.DeepSeek;

internal sealed class DeepSeekAdapterContext(
    string providerId,
    string modelId,
    string adapterId,
    string sessionId)
{
    internal const string Provider = "deepseek";
    internal const string Model = "deepseek-v4-flash";
    internal const string AdapterDescriptor =
        "{\"schema_version\":1,\"provider\":\"deepseek\",\"model\":" +
        "\"deepseek-v4-flash\",\"endpoint\":" +
        "\"https://api.deepseek.com/chat/completions\",\"stream\":false," +
        "\"thinking\":\"enabled\",\"reasoning_effort\":\"high\"," +
        "\"max_tokens\":4096,\"tool_choice\":\"omitted\"," +
        "\"request_cap_bytes\":1048576,\"response_cap_bytes\":1048576," +
        "\"content_rule\":\"zero-or-one-exact\",\"response_rule\":" +
        "\"reasoning,text-if-nonempty,calls\",\"codec_id\":" +
        "\"deepseek-reasoning-content\",\"codec_discriminator\":" +
        "\"deepseek-v4-flash-thinking-v1\",\"encoding\":\"utf8\"," +
        "\"framing\":\"deepseek.reasoning_content.utf8.v1\"}";
    internal const string Adapter =
        "0c585a37957e31b864e137bde2fbfd7c14005d03c42fd1a6983171d54e8977e0";

    internal string ProviderId { get; } = providerId;
    internal string ModelId { get; } = modelId;
    internal string AdapterId { get; } = adapterId;
    internal string SessionId { get; } = sessionId;

    internal bool IsValid =>
        StringComparer.Ordinal.Equals(ProviderId, Provider) &&
        StringComparer.Ordinal.Equals(ModelId, Model) &&
        StringComparer.Ordinal.Equals(AdapterId, Adapter) &&
        AgentValueDomains.IsIdentifier(SessionId);

    public override string ToString() => "deepseek_adapter_context";
}

internal sealed class DeepSeekChatBackend(
    DeepSeekAdapterContext context,
    IDeepSeekTransport transport) : IMinimalChatBackend
{
    private const string ResponseTooLargeText =
        "provider response omitted: byte cap exceeded";

    internal static IProjectChatClient CreateClient(
        DeepSeekAdapterContext context,
        IDeepSeekTransport transport) =>
        new MinimalChatClient(new DeepSeekChatBackend(context, transport));

    public async Task<MinimalChatResponse> GetResponseAsync(
        MinimalChatRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context is null ||
            transport is null ||
            !context.IsValid ||
            !ValidReplay(request))
        {
            throw new ProjectChatNormalizationException();
        }

        var projection = DeepSeekRequestWriter.Write(request);
        if (projection.Outcome != DeepSeekRequestWriteOutcome.Success ||
            !projection.HasBody)
        {
            throw new ProjectChatNormalizationException();
        }

        var transportResult = await transport.SendAsync(
            projection.Body.ToArray(),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (transportResult is null)
        {
            throw new ProjectChatNormalizationException();
        }

        return transportResult.Outcome switch
        {
            DeepSeekTransportOutcome.RequestRejected =>
                throw new ProjectChatNormalizationException(),
            DeepSeekTransportOutcome.Success => Parse(transportResult, request),
            DeepSeekTransportOutcome.ResponseTooLarge =>
                ResponseTooLarge(request.Messages.Length),
            DeepSeekTransportOutcome.HttpFailure or
            DeepSeekTransportOutcome.ConnectTimeout or
            DeepSeekTransportOutcome.ProviderTimeout or
            DeepSeekTransportOutcome.TransportFailure =>
                throw new DeepSeekChatBackendException(),
            _ => throw new ProjectChatNormalizationException(),
        };
    }

    private MinimalChatResponse Parse(
        DeepSeekTransportResult transportResult,
        MinimalChatRequest request)
    {
        var parsed = DeepSeekResponseParser.Parse(transportResult);
        if (parsed.Outcome != DeepSeekResponseParseOutcome.Success ||
            parsed.Response is not { } response)
        {
            throw new ProjectChatNormalizationException();
        }

        var messagePosition = request.Messages.Length;
        var contents = new List<MinimalChatContent>(response.Calls.Length + 2)
        {
            new(
                "reasoning",
                null,
                null,
                response.Reasoning,
                string.Empty,
                DeepSeekReasoningContinuationCodec.FramingName,
                null,
                messagePosition,
                0),
        };
        if (response.Content.Length > 0)
        {
            contents.Add(new MinimalChatContent(
                "text",
                null,
                null,
                response.Content,
                null,
                null,
                null,
                messagePosition,
                contents.Count));
        }

        foreach (var call in response.Calls)
        {
            contents.Add(new MinimalChatContent(
                "tool_call",
                call.Id,
                call.Name,
                call.Arguments,
                null,
                null,
                null,
                messagePosition,
                contents.Count));
        }

        var continuationItem = new MinimalChatContinuationItem(
            response.Reasoning,
            string.Empty,
            DeepSeekReasoningContinuationCodec.FramingName,
            null,
            messagePosition,
            0);
        return new MinimalChatResponse(
            new MinimalChatMessage("assistant", contents.ToArray()),
            new MinimalChatUsage(
                response.Usage.InputTokens,
                response.Usage.OutputTokens),
            response.CapturedBytes,
            new MinimalChatContinuation(
                context.ProviderId,
                context.ModelId,
                context.AdapterId,
                context.SessionId,
                [continuationItem]));
    }

    private static MinimalChatResponse ResponseTooLarge(int messagePosition) =>
        new(
            new MinimalChatMessage(
                "assistant",
                [
                    new MinimalChatContent(
                        "text",
                        null,
                        null,
                        ResponseTooLargeText,
                        null,
                        null,
                        null,
                        messagePosition,
                        0),
                ]),
            new MinimalChatUsage(0, 0),
            DeepSeekTransportPolicy.ResponseTooLargeCount,
            Continuation: null);

    private bool ValidReplay(MinimalChatRequest request)
    {
        if (request is null ||
            request.Messages is null ||
            request.Tools is null)
        {
            return false;
        }

        var assistants = request.Messages
            .Select((message, position) => (message, position))
            .Where(entry => entry.message is not null &&
                StringComparer.Ordinal.Equals(
                    entry.message.Role,
                    "assistant"))
            .ToArray();
        if (assistants.Length == 0)
        {
            return request.Continuation is null;
        }

        var continuation = request.Continuation;
        if (continuation is null ||
            continuation.Items is null ||
            continuation.Items.Length != assistants.Length ||
            !StringComparer.Ordinal.Equals(
                continuation.ProviderId,
                context.ProviderId) ||
            !StringComparer.Ordinal.Equals(
                continuation.ModelId,
                context.ModelId) ||
            !StringComparer.Ordinal.Equals(
                continuation.AdapterId,
                context.AdapterId) ||
            !StringComparer.Ordinal.Equals(
                continuation.SessionId,
                context.SessionId))
        {
            return false;
        }

        for (var index = 0; index < assistants.Length; index++)
        {
            var (message, messagePosition) = assistants[index];
            var item = continuation.Items[index];
            if (message.Contents is null ||
                item is null ||
                message.Contents.Length < 2 ||
                message.Contents.Count(content => content is not null &&
                    StringComparer.Ordinal.Equals(
                        content.Kind,
                        "reasoning")) != 1 ||
                message.Contents[0] is not { } reasoning ||
                !StringComparer.Ordinal.Equals(reasoning.Kind, "reasoning") ||
                message.Contents.Count(content => content is not null &&
                    StringComparer.Ordinal.Equals(
                        content.Kind,
                        "tool_call")) is < 1 or >
                    AgentLimits.ToolCallsPerResponse ||
                item.MessagePosition != messagePosition ||
                item.ContentPosition != 0 ||
                item.AssociatedCallId is not null ||
                item.Opaque is not { Length: 0 } ||
                !StringComparer.Ordinal.Equals(
                    item.Framing,
                    DeepSeekReasoningContinuationCodec.FramingName) ||
                !StringComparer.Ordinal.Equals(item.Readable, reasoning.Text) ||
                !StringComparer.Ordinal.Equals(item.Opaque, reasoning.Opaque) ||
                !StringComparer.Ordinal.Equals(item.Framing, reasoning.Framing) ||
                reasoning.AssociatedCallId is not null ||
                reasoning.MessagePosition != messagePosition ||
                reasoning.Position != 0 ||
                !AgentValueDomains.IsUtf8(
                    item.Readable,
                    1,
                    AgentLimits.ContinuationItemBytes))
            {
                return false;
            }
        }

        return true;
    }

    public override string ToString() => "deepseek_chat_backend";
}

internal sealed class DeepSeekChatBackendException : Exception
{
    internal DeepSeekChatBackendException()
        : base("The DeepSeek backend request failed.")
    {
    }

    public override string ToString() => "deepseek_chat_backend_exception";
}
