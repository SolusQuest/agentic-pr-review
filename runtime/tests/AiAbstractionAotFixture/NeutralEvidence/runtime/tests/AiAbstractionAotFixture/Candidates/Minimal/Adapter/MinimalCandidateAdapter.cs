using AgenticPrReview.Runtime.Agent.Chat;

namespace AgenticPrReview.Runtime.AiAbstractionFixture;

internal static class CandidateFactory
{
    internal static ICandidateHarness Create(
        FixturePhase phase,
        string scenario)
    {
        var probe = new CandidateProbe();
        var backend = new MinimalFakeBackend(phase, scenario, probe);
        return new MinimalCandidateHarness(
            new MinimalCandidateAdapter(backend),
            probe);
    }
}

internal sealed class MinimalCandidateHarness(
    IProjectChatClient chatClient,
    CandidateProbe probe) : ICandidateHarness
{
    public string CandidateName => "Minimal";
    public string AdapterId => "apr-minimal-adapter";
    public IProjectChatClient ChatClient => chatClient;
    public CandidateProbe Probe => probe;
}

internal sealed class MinimalCandidateAdapter(
    MinimalFakeBackend backend) : IProjectChatClient
{
    public async Task<ProjectChatResponse> GetResponseAsync(
        ProjectChatRequest request,
        CancellationToken cancellationToken)
    {
        var native = new MinimalRequest(
            request.Messages.Select(ToNative).ToArray(),
            request.Tools.Select(tool => new MinimalTool(
                tool.Name,
                tool.Description,
                tool.SchemaJson)).ToArray(),
            request.Continuation is null
                ? null
                : new MinimalContinuation(
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

    private static MinimalMessage ToNative(ProjectChatMessage message) => new(
        message.Role,
        message.Contents.Select(content => content switch
        {
            ProjectTextContent text => new MinimalContent(
                "text", null, null, text.Text, null, null, null, 0),
            ProjectReasoningContent reasoning => new MinimalContent(
                "reasoning",
                null,
                null,
                reasoning.Text,
                reasoning.Opaque,
                reasoning.Framing,
                reasoning.AssociatedCallId,
                reasoning.Position),
            ProjectToolCallContent call => new MinimalContent(
                "tool_call",
                call.CallId,
                call.Name,
                call.ArgumentsJson,
                null,
                null,
                null,
                0),
            ProjectToolResultContent result => new MinimalContent(
                "tool_result",
                result.CallId,
                null,
                result.Result,
                null,
                null,
                null,
                0),
            _ => throw new FixtureFailure("APR_AI_MAPPING"),
        }).ToArray());

    private static ProjectChatMessage ToProject(MinimalMessage message) => new(
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
            _ => throw new FixtureFailure("APR_AI_MAPPING"),
        }).ToArray());
}

internal sealed record MinimalRequest(
    MinimalMessage[] Messages,
    MinimalTool[] Tools,
    MinimalContinuation? Continuation);

internal sealed record MinimalMessage(
    string Role,
    MinimalContent[] Contents);

internal sealed record MinimalContent(
    string Kind,
    string? CallId,
    string? Name,
    string? Text,
    string? Opaque,
    string? Framing,
    string? AssociatedCallId,
    int Position);

internal sealed record MinimalTool(
    string Name,
    string Description,
    string SchemaJson);

internal sealed record MinimalContinuation(
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
