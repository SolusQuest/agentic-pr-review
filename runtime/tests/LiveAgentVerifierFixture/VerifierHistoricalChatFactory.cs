using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Quality;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.LiveAgentVerifierFixture;

internal sealed class VerifierChatFactory(
    VerifierScenario scenario,
    R3QualityCase testCase,
    ReviewedIdentity reviewedIdentity,
    string? expectedHistorySha256,
    IReadOnlyList<byte[]> currentPublicInputs, VerifierTypedObservationOwner observations)
    : IHistoricalResponseFactory,
    IDisposable
{
    private VerifierHistoricalChat? server;
    private int createCount;

    internal VerifierTypedProof Proof
    {
        get
        {
            if (server is null)
            {
                return VerifierTypedProof.Empty with
                {
                    FactoryCreateCount = createCount,
                };
            }

            return server.Proof with
            {
                FactoryCreateCount = createCount,
            };
        }
    }

    public IAuthoredResponseSource Create(DeepSeekAdapterContext context)
    {
        if (Interlocked.Increment(ref createCount) != 1 || server is not null)
            throw new InvalidOperationException("The historical typed factory is single-use.");
        server = new VerifierHistoricalChat(scenario, testCase, reviewedIdentity, expectedHistorySha256, currentPublicInputs, context, observations);
        return server;
    }

    public void Dispose() => server?.Dispose();
}

internal sealed record VerifierTypedProof(
    bool Succeeded,
    int SyntheticCalls,
    int FactoryCreateCount,
    string? FirstTypedRequestSha256,
    string? ExpectedTerminalSha256,
    string? PriorFactSha256,
    string? HistoricalMessagesSha256,
    bool ExactReplayValidated,
    bool ReplayMutationMatrixValidated,
    string? FailureCode,
    bool CanaryRoutesValidated = false, IReadOnlyList<VerifierTypedObservation>? TypedObservations = null)
{
    internal static VerifierTypedProof Empty { get; } = new(
        false,
        0,
        0,
        null,
        null,
        null,
        null,
        false,
        false,
        "typed_not_started");

    internal bool IsSatisfiedBy(string? terminalSha256) =>
        Succeeded &&
        FactoryCreateCount == 1 && TypedObservations is not null && TypedObservations.Count == SyntheticCalls &&
        TypedObservations.Select((value, index) => value.Ordinal == index + 1 && value.Outcome == "succeeded" &&
            LiveAgentFreshProcessDomain.IsSha256(value.RequestSha256)).All(valid => valid) &&
        LiveAgentFreshProcessDomain.IsSha256(ExpectedTerminalSha256) &&
        StringComparer.Ordinal.Equals(
            ExpectedTerminalSha256,
            terminalSha256);
}

internal sealed class VerifierTypedObservationOwner
{
    private readonly List<VerifierTypedObservation> observations = [];
    internal IReadOnlyList<VerifierTypedObservation> Snapshot => observations.ToArray();
    internal int Begin(ProjectChatRequest request)
    {
        if (observations.Count >= 4) throw new VerifierScriptException("typed_observation_bound");
        var index = observations.Count;
        observations.Add(new(index + 1, LiveAgentFreshProcessDomain.RawSha256(
            AgenticPrReview.Runtime.Agent.Loop.AgentRequestWriter.Write(request)), "entered"));
        return index;
    }
    internal void Complete(int index, string outcome)
    {
        if (index < 0 || index >= observations.Count || observations[index].Outcome != "entered" ||
            outcome is not ("succeeded" or "failed" or "cancelled")) throw new VerifierScriptException("typed_observation_invalid");
        observations[index] = observations[index] with { Outcome = outcome };
    }
}
