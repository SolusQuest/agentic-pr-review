using System.Runtime.CompilerServices;
using AgenticPrReview.Runtime.Agent.Chat;
using Microsoft.Extensions.AI;

namespace AgenticPrReview.Runtime.AiAbstractionFixture;

internal sealed class FakeMeaiChatClient(
    FixturePhase phase,
    string scenario,
    CandidateProbe probe) : IChatClient
{
    private int _turn;

    public void Dispose()
    {
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageArray = messages.ToArray();
        ValidateOptions(options);
        probe.Add(Observe(messageArray, options!));
        if (scenario == "cancellation")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        var scripted = FixtureScript.ResponseFor(phase, _turn++, scenario);
        var contents = scripted.Message.Contents.Select(ToNative).ToList();
        return new ChatResponse(new ChatMessage(
            scripted.Message.Role == "assistant"
                ? ChatRole.Assistant
                : ChatRole.User,
            contents));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        throw new FixtureFailure("APR_AI_STREAMING_NOT_ALLOWED");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static void ValidateOptions(ChatOptions? options)
    {
        if (options?.Reasoning is null ||
            options.Reasoning.Effort != ReasoningEffort.High ||
            options.Reasoning.Output != ReasoningOutput.Full)
        {
            throw new FixtureFailure("APR_AI_REASONING_DISABLED");
        }
        var toolNames = options.Tools?
            .OfType<AIFunctionDeclaration>()
            .Select(tool => tool.Name)
            .ToArray();
        if (toolNames is null ||
            !toolNames.SequenceEqual(["read_file", "search_text", "finish_review"]))
        {
            throw new FixtureFailure("APR_AI_TOOL_ORDER");
        }
    }

    private static AIContent ToNative(ProjectChatContent content) => content switch
    {
        ProjectTextContent text => new TextContent(text.Text),
        ProjectReasoningContent reasoning => new TextReasoningContent(reasoning.Text)
        {
            ProtectedData = reasoning.Opaque,
            AdditionalProperties = MicrosoftExtensionsAiCandidateAdapter.Metadata(
                reasoning.Framing,
                reasoning.AssociatedCallId,
                reasoning.MessagePosition,
                reasoning.Position),
        },
        ProjectToolCallContent call => CreateCall(call),
        _ => throw new FixtureFailure("APR_AI_MAPPING"),
    };

    private static FunctionCallContent CreateCall(ProjectToolCallContent call)
    {
        var content = new FunctionCallContent(
            call.CallId,
            call.Name,
            new Dictionary<string, object?>());
        content.AdditionalProperties = new AdditionalPropertiesDictionary
        {
            ["rawArguments"] = call.ArgumentsJson,
        };
        return content;
    }

    private static NativeRequestObservation Observe(
        ChatMessage[] messages,
        ChatOptions options)
    {
        ObservedContinuation? continuation = null;
        var observedMessages = messages.Select(message =>
        {
            var contents = new List<ObservedContent>();
            foreach (var (content, position) in message.Contents.Select(
                (content, position) => (content, position)))
            {
                if (content is TextReasoningContent reasoning &&
                    reasoning.AdditionalProperties?.ContainsKey("providerId") == true)
                {
                    _ = MicrosoftExtensionsAiCandidateAdapter.RequiredString(
                        reasoning,
                        "adapterId");
                    continuation = new ObservedContinuation(
                        MicrosoftExtensionsAiCandidateAdapter.RequiredString(
                            reasoning,
                            "providerId"),
                        MicrosoftExtensionsAiCandidateAdapter.RequiredString(
                            reasoning,
                            "modelId"),
                        "candidate-adapter",
                        MicrosoftExtensionsAiCandidateAdapter.RequiredString(
                            reasoning,
                            "sessionId"),
                        FixtureHash.Text(reasoning.Text),
                        FixtureHash.Text(reasoning.ProtectedData ?? string.Empty),
                        MicrosoftExtensionsAiCandidateAdapter.RequiredString(
                            reasoning,
                            "framing"),
                        MicrosoftExtensionsAiCandidateAdapter.OptionalString(
                            reasoning,
                            "associatedCallId"),
                        MicrosoftExtensionsAiCandidateAdapter.RequiredInt(
                            reasoning,
                            "messagePosition"),
                        MicrosoftExtensionsAiCandidateAdapter.RequiredInt(
                            reasoning,
                            "contentPosition"));
                    continue;
                }
                contents.Add(ObserveContent(content, position));
            }
            return new ObservedMessage(RoleName(message.Role), contents.ToArray());
        }).ToArray();
        return new NativeRequestObservation(
            observedMessages,
            options.Tools!
                .OfType<AIFunctionDeclaration>()
                .Select(tool => tool.Name)
                .ToArray(),
            continuation);
    }

    private static ObservedContent ObserveContent(AIContent content, int position) =>
        content switch
        {
            TextContent text => new ObservedContent(
                "text",
                null,
                null,
                FixtureHash.Text(text.Text),
                null,
                null,
                null,
                position),
            TextReasoningContent reasoning => new ObservedContent(
                "reasoning",
                null,
                null,
                FixtureHash.Text(reasoning.Text),
                FixtureHash.Text(reasoning.ProtectedData ?? string.Empty),
                MicrosoftExtensionsAiCandidateAdapter.RequiredString(
                    reasoning,
                    "framing"),
                MicrosoftExtensionsAiCandidateAdapter.OptionalString(
                    reasoning,
                    "associatedCallId"),
                position),
            FunctionCallContent call => new ObservedContent(
                "tool_call",
                call.CallId,
                call.Name,
                FixtureHash.Text(
                    MicrosoftExtensionsAiCandidateAdapter.RequiredString(
                        call,
                        "rawArguments")),
                null,
                null,
                null,
                position),
            FunctionResultContent result => new ObservedContent(
                "tool_result",
                result.CallId,
                null,
                FixtureHash.Text(result.Result?.ToString() ?? string.Empty),
                null,
                null,
                null,
                position),
            _ => throw new FixtureFailure("APR_AI_MAPPING"),
        };

    private static string RoleName(ChatRole role) =>
        role == ChatRole.System ? "system" :
        role == ChatRole.User ? "user" :
        role == ChatRole.Assistant ? "assistant" :
        role == ChatRole.Tool ? "tool" :
        throw new FixtureFailure("APR_AI_PROVIDER_ROLE");
}
