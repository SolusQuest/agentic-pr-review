using AgenticPrReview.Runtime.Agent.Quality;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.LiveAgentVerifierFixture;

internal sealed class TrustedLiveAgentProfile(
    VerifierScenario scenario,
    R3QualityCase testCase,
    ReviewedIdentity reviewedIdentity) : ILiveAgentFreshProcessProfile
{
    private TrustedLiveAgentExecution? execution;

    internal TrustedLiveAgentExecution? Execution => execution;

    public ILiveAgentFreshProcessProfileExecution Activate(
        LiveAgentFreshProcessProfileActivation activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        if (execution is not null)
        {
            throw new InvalidOperationException(
                "The trusted-live profile is single-use.");
        }

        execution = new TrustedLiveAgentExecution(
            scenario,
            testCase,
            reviewedIdentity,
            activation.CurrentPublicInputs);
        return execution;
    }
}

internal sealed class TrustedLiveAgentExecution :
    ILiveAgentFreshProcessProfileExecution
{
    private readonly R3LiveAgentTransportFactory factory = new();
    private readonly VerifierCommitObserver observer;
    private readonly TrustedLiveAgentProof proof;

    internal TrustedLiveAgentExecution(
        VerifierScenario scenario,
        R3QualityCase testCase,
        ReviewedIdentity reviewedIdentity,
        IReadOnlyList<byte[]> currentPublicInputs)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentNullException.ThrowIfNull(reviewedIdentity);
        ArgumentNullException.ThrowIfNull(currentPublicInputs);
        var copiedInputs = currentPublicInputs
            .Select(value => value.ToArray())
            .ToArray();
        observer = new VerifierCommitObserver(
            scenario,
            testCase,
            CaptureFreshInputs(scenario, copiedInputs));
        proof = new TrustedLiveAgentProof(observer);
    }

    public IR3LiveAgentTransportFactory TransportFactory => factory;

    public ILiveAgentFreshProcessProof Proof => proof;

    internal VerifierCommitObserver Observer => observer;

    public R3LiveAgentRequest Prepare(R3LiveAgentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request;
    }

    public ILiveAgentStateCommitCoordinator Observe(
        ILiveAgentStateCommitCoordinator coordinator)
    {
        observer.SetInner(coordinator);
        return observer;
    }

    public void Dispose()
    {
    }

    private static R3QualityFreshProcessTwoInputSet CaptureFreshInputs(
        VerifierScenario scenario,
        IReadOnlyList<byte[]> inputs) => scenario ==
        VerifierScenario.ContinuationRestore
        ? R3QualityFreshProcessTwoInputSet.Capture(
            [
                (
                    "current-review-context.json",
                    new ReadOnlyMemory<byte>(inputs[1])),
                (
                    "reviewed-snapshot.json",
                    new ReadOnlyMemory<byte>(inputs[2])),
                (
                    "state-locator.json",
                    new ReadOnlyMemory<byte>(inputs[0])),
            ])
        : R3QualityFreshProcessTwoInputSet.Capture([]);
}

internal sealed class TrustedLiveAgentProof(VerifierCommitObserver observer) :
    ILiveAgentFreshProcessProof
{
    public int RequestCount => 0;

    public string? SecondProcessFirstRequestSha256 => null;

    public bool IsSatisfiedBy(string? terminalSha256) =>
        LiveAgentFreshProcessDomain.IsSha256(terminalSha256) &&
        observer.DelegationCount == 1 &&
        observer.ProofPassed;
}
