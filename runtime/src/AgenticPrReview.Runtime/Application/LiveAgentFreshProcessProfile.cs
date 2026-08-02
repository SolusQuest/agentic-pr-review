namespace AgenticPrReview.Runtime;

internal sealed record LiveAgentFreshProcessProfileActivation(
    string Phase,
    IReadOnlyList<byte[]> CurrentPublicInputs)
{
    public override string ToString() =>
        "live_agent_fresh_process_profile_activation";
}

internal interface ILiveAgentFreshProcessProof
{
    int RequestCount { get; }

    string? SecondProcessFirstRequestSha256 { get; }

    bool IsSatisfiedBy(string? terminalSha256);
}

internal interface ILiveAgentFreshProcessProfile
{
    ILiveAgentFreshProcessProfileExecution Activate(
        LiveAgentFreshProcessProfileActivation activation);
}

internal interface ILiveAgentFreshProcessProfileExecution : IDisposable
{
    IR3LiveAgentTransportFactory TransportFactory { get; }

    ILiveAgentStateCommitCoordinator Observe(
        ILiveAgentStateCommitCoordinator coordinator);

    ILiveAgentFreshProcessProof Proof { get; }
}

internal sealed class LiveAgentFreshProcessDeterministicProfile :
    ILiveAgentFreshProcessProfile
{
    internal static LiveAgentFreshProcessDeterministicProfile Instance {
        get;
    } = new();

    private LiveAgentFreshProcessDeterministicProfile()
    {
    }

    public ILiveAgentFreshProcessProfileExecution Activate(
        LiveAgentFreshProcessProfileActivation activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        return new LiveAgentFreshProcessDeterministicProfileExecution(
            activation.Phase,
            activation.CurrentPublicInputs);
    }
}

internal sealed class LiveAgentFreshProcessDeterministicProfileExecution :
    ILiveAgentFreshProcessProfileExecution
{
    private readonly LiveAgentFreshProcessDeterministicTransportFactory factory;

    internal LiveAgentFreshProcessDeterministicProfileExecution(
        string phase,
        IEnumerable<byte[]> currentPublicInputs)
    {
        factory = new LiveAgentFreshProcessDeterministicTransportFactory(
            phase,
            currentPublicInputs);
    }

    public IR3LiveAgentTransportFactory TransportFactory => factory;

    public ILiveAgentFreshProcessProof Proof => factory.Proof;

    public ILiveAgentStateCommitCoordinator Observe(
        ILiveAgentStateCommitCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        return coordinator;
    }

    public void Dispose() => factory.Dispose();
}
