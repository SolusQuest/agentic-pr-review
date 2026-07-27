using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Chat;
using Microsoft.Extensions.AI;

namespace AgenticPrReview.Runtime.AiAbstractionFixture;

internal static class CandidateFactory
{
    internal static ICandidateHarness Create(
        FixturePhase phase,
        string scenario)
    {
        var probe = new CandidateProbe();
        var backend = new FakeMeaiChatClient(phase, scenario, probe);
        return new MicrosoftExtensionsAiCandidateHarness(
            new MicrosoftExtensionsAiCandidateAdapter(backend),
            probe);
    }
}

internal sealed class MicrosoftExtensionsAiCandidateHarness(
    IProjectChatClient chatClient,
    CandidateProbe probe) : ICandidateHarness
{
    public string CandidateName => "MicrosoftExtensionsAI";
    public string AdapterId => "apr-meai-adapter";
    public IProjectChatClient ChatClient => chatClient;
    public CandidateProbe Probe => probe;
    public object MaterializeCandidateRequest(ProjectChatRequest request) =>
        MicrosoftExtensionsAiCandidateAdapter.Materialize(request).Messages;
}

internal sealed class MicrosoftExtensionsAiCandidateAdapter(
    IChatClient client) : IProjectChatClient
{
    public async Task<ProjectChatResponse> GetResponseAsync(
        ProjectChatRequest request,
        CancellationToken cancellationToken)
    {
        var invocation = Materialize(request);
        var response = await client.GetResponseAsync(
            invocation.Messages,
            invocation.Options,
            cancellationToken);
        if (response.Messages.Count != 1)
        {
            throw new FixtureFailure("APR_AI_MAPPING");
        }
        return new ProjectChatResponse(ToProject(response.Messages[0]));
    }

    internal static MeaiInvocation Materialize(ProjectChatRequest request)
    {
        var messages = request.Messages.Select(ToNative).ToList();
        if (request.Continuation is not null)
        {
            InsertContinuations(messages, request.Continuation);
        }
        return new MeaiInvocation(
            messages,
            new ChatOptions
            {
                Reasoning = new ReasoningOptions
                {
                    Effort = ReasoningEffort.High,
                    Output = ReasoningOutput.Full,
                },
                Tools = request.Tools
                    .Select<ProjectToolDefinition, AITool>(tool =>
                        new ClosedToolDeclaration(
                            tool.Name,
                            tool.Description,
                            tool.SchemaJson))
                    .ToList(),
            });
    }

    private static ChatMessage ToNative(ProjectChatMessage message) => new(
        ToRole(message.Role),
        message.Contents.Select(ToNative).ToList());

    private static AIContent ToNative(ProjectChatContent content) => content switch
    {
        ProjectTextContent text => new TextContent(text.Text),
        ProjectReasoningContent reasoning => CreateReasoning(reasoning),
        ProjectToolCallContent call => CreateCall(call),
        ProjectToolResultContent result => new FunctionResultContent(
            result.CallId,
            result.Result),
        _ => throw new FixtureFailure("APR_AI_MAPPING"),
    };

    private static TextReasoningContent CreateReasoning(
        ProjectReasoningContent reasoning)
    {
        var content = new TextReasoningContent(reasoning.Text)
        {
            ProtectedData = reasoning.Opaque,
            AdditionalProperties = Metadata(
                reasoning.Framing,
                reasoning.AssociatedCallId,
                reasoning.MessagePosition,
                reasoning.Position),
        };
        return content;
    }

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

    private static void InsertContinuations(
        List<ChatMessage> messages,
        ProjectContinuation continuation)
    {
        foreach (var item in continuation.Items
            .OrderBy(item => item.MessagePosition))
        {
            if (item.MessagePosition < 0 ||
                item.MessagePosition >= messages.Count)
            {
                throw new FixtureFailure("APR_AI_CONTINUATION");
            }
            var target = messages[item.MessagePosition];
            if (item.ContentPosition < 0 ||
                item.ContentPosition > target.Contents.Count)
            {
                throw new FixtureFailure("APR_AI_CONTINUATION");
            }
            var reasoning = new TextReasoningContent(item.Readable)
            {
                ProtectedData = item.Opaque,
                AdditionalProperties = Metadata(
                    item.Framing,
                    item.AssociatedCallId,
                    item.MessagePosition,
                    item.ContentPosition),
            };
            reasoning.AdditionalProperties["providerId"] = continuation.ProviderId;
            reasoning.AdditionalProperties["modelId"] = continuation.ModelId;
            reasoning.AdditionalProperties["adapterId"] = continuation.AdapterId;
            reasoning.AdditionalProperties["sessionId"] = continuation.SessionId;
            target.Contents.Insert(item.ContentPosition, reasoning);
        }
    }

    private static ProjectChatMessage ToProject(ChatMessage message) => new(
        message.Role == ChatRole.Assistant ? "assistant" :
            throw new FixtureFailure("APR_AI_PROVIDER_ROLE"),
        message.Contents.Select(content => content switch
        {
            TextContent text => (ProjectChatContent)new ProjectTextContent(text.Text),
            TextReasoningContent reasoning => new ProjectReasoningContent(
                reasoning.Text,
                reasoning.ProtectedData ?? string.Empty,
                RequiredString(reasoning, "framing"),
                OptionalString(reasoning, "associatedCallId"),
                RequiredInt(reasoning, "messagePosition"),
                RequiredInt(reasoning, "contentPosition")),
            FunctionCallContent call => new ProjectToolCallContent(
                call.CallId,
                call.Name,
                RequiredString(call, "rawArguments")),
            FunctionResultContent result => new ProjectToolResultContent(
                result.CallId,
                result.Result?.ToString() ?? string.Empty),
            _ => throw new FixtureFailure("APR_AI_MAPPING"),
        }).ToArray());

    private static ChatRole ToRole(string role) => role switch
    {
        "system" => ChatRole.System,
        "user" => ChatRole.User,
        "assistant" => ChatRole.Assistant,
        "tool" => ChatRole.Tool,
        _ => throw new FixtureFailure("APR_AI_PROVIDER_ROLE"),
    };

    internal static AdditionalPropertiesDictionary Metadata(
        string framing,
        string? associatedCallId,
        int messagePosition,
        int contentPosition)
    {
        var metadata = new AdditionalPropertiesDictionary
        {
            ["framing"] = framing,
            ["messagePosition"] = messagePosition,
            ["contentPosition"] = contentPosition,
        };
        if (associatedCallId is not null)
        {
            metadata["associatedCallId"] = associatedCallId;
        }
        return metadata;
    }

    internal static string RequiredString(AIContent content, string name) =>
        content.AdditionalProperties is not null &&
        content.AdditionalProperties.TryGetValue(name, out var value) &&
        value is string text
            ? text
            : throw new FixtureFailure("APR_AI_MAPPING");

    internal static string? OptionalString(AIContent content, string name) =>
        content.AdditionalProperties is not null &&
        content.AdditionalProperties.TryGetValue(name, out var value)
            ? value as string
            : null;

    internal static int RequiredInt(AIContent content, string name) =>
        content.AdditionalProperties is not null &&
        content.AdditionalProperties.TryGetValue(name, out var value) &&
        value is int number
            ? number
            : throw new FixtureFailure("APR_AI_MAPPING");
}

internal sealed record MeaiInvocation(
    List<ChatMessage> Messages,
    ChatOptions Options);

internal sealed class ClosedToolDeclaration : AIFunctionDeclaration
{
    private readonly string _name;
    private readonly string _description;
    private readonly JsonElement _schema;

    internal ClosedToolDeclaration(
        string name,
        string description,
        string schemaJson)
    {
        _name = name;
        _description = description;
        using var document = JsonDocument.Parse(schemaJson);
        _schema = document.RootElement.Clone();
    }

    public override string Name => _name;
    public override string Description => _description;
    public override JsonElement JsonSchema => _schema;
}
