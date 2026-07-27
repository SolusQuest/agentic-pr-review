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
            new MinimalChatClient(backend),
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
    public object MaterializeCandidateRequest(ProjectChatRequest request) =>
        MinimalChatClient.Materialize(request);
}
