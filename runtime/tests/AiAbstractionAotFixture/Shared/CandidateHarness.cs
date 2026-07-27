using AgenticPrReview.Runtime.Agent.Chat;

namespace AgenticPrReview.Runtime.AiAbstractionFixture;

internal enum FixturePhase
{
    First,
    Resume,
}

internal interface ICandidateHarness
{
    string CandidateName { get; }
    string AdapterId { get; }
    IProjectChatClient ChatClient { get; }
    CandidateProbe Probe { get; }
}

internal sealed class CandidateProbe
{
    private readonly List<NativeRequestObservation> _requests = [];

    internal IReadOnlyList<NativeRequestObservation> Requests => _requests;

    internal void Add(NativeRequestObservation observation) => _requests.Add(observation);
}

internal sealed record NativeRequestObservation(
    ObservedMessage[] Messages,
    string[] ToolNames,
    ObservedContinuation? Continuation);

internal sealed record ObservedMessage(
    string Role,
    ObservedContent[] Contents);

internal sealed record ObservedContent(
    string Kind,
    string? CallId,
    string? Name,
    string? TextSha256,
    string? OpaqueSha256,
    string? Framing,
    string? AssociatedCallId,
    int Position);

internal sealed record ObservedContinuation(
    string ProviderId,
    string ModelId,
    string AdapterId,
    string SessionId,
    string ReadableSha256,
    string OpaqueSha256,
    string Framing,
    string? AssociatedCallId,
    int MessagePosition,
    int ContentPosition);
