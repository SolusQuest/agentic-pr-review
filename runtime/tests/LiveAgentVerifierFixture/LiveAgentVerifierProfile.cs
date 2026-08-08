using AgenticPrReview.Runtime.Agent.Quality;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.LiveAgentVerifierFixture;

internal sealed class LiveAgentVerifierProfile(
    VerifierScenario scenario,
    R3QualityCase testCase,
    ReviewedIdentity reviewedIdentity,
    string? expectedHistorySha256) : ILiveAgentFreshProcessProfile
{
    private LiveAgentVerifierExecution? execution;

    internal LiveAgentVerifierExecution? Execution => execution;

    internal int ActivationCount => execution is null ? 0 : 1;

    public ILiveAgentFreshProcessProfileExecution Activate(
        LiveAgentFreshProcessProfileActivation activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        if (execution is not null)
        {
            throw new InvalidOperationException(
                "The verifier profile is single-use.");
        }

        execution = new LiveAgentVerifierExecution(
            scenario,
            testCase,
            reviewedIdentity,
            expectedHistorySha256,
            activation.CurrentPublicInputs);
        return execution;
    }
}

internal sealed class LiveAgentVerifierExecution :
    ILiveAgentFreshProcessProfileExecution
{
    private readonly VerifierTransportFactory factory;
    private readonly VerifierCommitObserver observer;
    private readonly VerifierCombinedProof proof;
    private readonly VerifierScenario scenario;

    internal LiveAgentVerifierExecution(
        VerifierScenario scenario,
        R3QualityCase testCase,
        ReviewedIdentity reviewedIdentity,
        string? expectedHistorySha256,
        IReadOnlyList<byte[]> currentPublicInputs)
    {
        this.scenario = scenario;
        var copiedInputs = currentPublicInputs
            .Select(value => value.ToArray())
            .ToArray();
        factory = new VerifierTransportFactory(
            scenario,
            testCase,
            reviewedIdentity,
            expectedHistorySha256,
            copiedInputs);
        observer = new VerifierCommitObserver(
            scenario,
            testCase,
            CaptureFreshInputs(scenario, copiedInputs));
        proof = new VerifierCombinedProof(scenario, factory, observer);
    }

    public IR3LiveAgentTransportFactory TransportFactory => factory;

    public ILiveAgentFreshProcessProof Proof => proof;

    internal VerifierCommitObserver Observer => observer;

    internal VerifierWireProof WireProof => factory.Proof;

    internal bool PublicResultCanaryInjected =>
        proof.PublicResultCanaryInjected;

    public R3LiveAgentRequest Prepare(R3LiveAgentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return scenario == VerifierScenario.InnerAuthorizationDenied
            ? new R3LiveAgentRequest(
                request.AuthorizedScope with { BuildId = "other-build" },
                request.IsTrustedWorkflow,
                request.IsSameRepository,
                request.IsForkOrigin,
                request.StateLocatorFamily,
                request.StateRestoreIntent,
                request.AcceptedLineage,
                request.StateAdmissionContext,
                request.StateRoot,
                request.SnapshotRoot,
                request.TrackedFiles,
                request.ChangedFiles,
                request.DiffSources)
            : request;
    }

    public ILiveAgentStateCommitCoordinator Observe(
        ILiveAgentStateCommitCoordinator coordinator)
    {
        observer.SetInner(coordinator);
        return observer;
    }

    public void Dispose() => factory.Dispose();

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

internal sealed class VerifierCombinedProof(
    VerifierScenario scenario,
    VerifierTransportFactory factory,
    VerifierCommitObserver observer) : ILiveAgentFreshProcessProof
{
    internal bool PublicResultCanaryInjected =>
        scenario != VerifierScenario.PublicResultCanary ||
        StringComparer.Ordinal.Equals(
            Environment.GetEnvironmentVariable("APR111_PUBLIC_RESULT"),
            VerifierCanaries.PublicResult);

    public int RequestCount => factory.Proof.RequestCount;

    public string? SecondProcessFirstRequestSha256 =>
        factory.Proof.FirstRequestSha256;

    public bool IsSatisfiedBy(string? terminalSha256) =>
        scenario != VerifierScenario.PublicResultCanary &&
        factory.Proof.IsSatisfiedBy(terminalSha256) &&
        observer.DelegationCount == 1 &&
        observer.ProofPassed;
}

internal sealed class VerifierCommitObserver(
    VerifierScenario scenario,
    R3QualityCase testCase,
    R3QualityFreshProcessTwoInputSet freshInputs)
    : ILiveAgentStateCommitCoordinator
{
    private ILiveAgentStateCommitCoordinator? inner;

    internal int DelegationCount { get; private set; }

    internal R3QualityOutcome? Outcome { get; private set; }

    internal LiveAgentStateCommitResult? CommitResult { get; private set; }

    internal bool SeedReceiptValid { get; private set; }

    internal bool ContinuationOnlyReviewValid { get; private set; }

    internal bool ProofPassed => scenario switch
    {
        VerifierScenario.ContinuationSeed =>
            SeedReceiptValid &&
            ContinuationOnlyReviewValid &&
            CommitResult?.HandoffReady == true,
        VerifierScenario.CanaryRouting =>
            SeedReceiptValid && CommitResult?.HandoffReady == true,
        VerifierScenario.ContinuationRestore =>
            QualityPassed() &&
            ContinuationOnlyReviewValid &&
            CommitResult?.HandoffReady == true,
        _ => QualityPassed() && CommitResult?.HandoffReady == true,
    };

    internal void SetInner(ILiveAgentStateCommitCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        if (inner is not null)
        {
            throw new InvalidOperationException(
                "The commit observer is single-use.");
        }

        inner = coordinator;
    }

    public LiveAgentStateCommitResult Commit(
        LiveAgentCandidate candidate,
        AuthorizedStateAccess access,
        AcceptedLineage? priorLineage,
        AgentSessionHeadTransition authorizedTransition,
        string stateRoot,
        IRestrictedStateKeyResolver keyResolver,
        CancellationToken cancellationToken)
    {
        if (inner is null || DelegationCount != 0)
        {
            throw new InvalidOperationException(
                "The commit observer was not activated exactly once.");
        }

        try
        {
            if (scenario is VerifierScenario.ContinuationSeed or
                VerifierScenario.ContinuationRestore)
            {
                ContinuationOnlyReviewValid =
                    IsContinuationOnlyReview(candidate.Outcome, testCase);
            }

            if (scenario == VerifierScenario.ContinuationSeed)
            {
                SeedReceiptValid = candidate.Outcome.CompletedSessionEligible &&
                    candidate.Predecessor is null &&
                    candidate.Transition == AgentSessionHeadTransition.SameHead;
            }
            else if (scenario == VerifierScenario.CanaryRouting)
            {
                SeedReceiptValid = candidate.Outcome.CompletedSessionEligible &&
                    candidate.Predecessor is null &&
                    candidate.Transition == AgentSessionHeadTransition.SameHead;
            }
            else if (scenario == VerifierScenario.QualityFailedAfterCommit)
            {
                var creation = R3QualitySubject.TryCreateCompleted(
                    new AgentSessionBuildInput(
                        candidate.Run,
                        candidate.Outcome,
                        candidate.TrustedRequest,
                        candidate.CurrentReviewContextIndex,
                        candidate.ContinuationCodec,
                        candidate.Predecessor,
                        candidate.Transition),
                    freshInputs);
                var expectation = (R3QualityMustFindExpectation)
                    testCase.Expectation;
                var failedCase = testCase with
                {
                    Expectation = expectation with
                    {
                        RequiredObservationId = "quality_missing_observation",
                    },
                };
                Outcome = R3QualityEvaluator.Evaluate(failedCase, creation);
            }
            else
            {
                var creation = R3QualitySubject.TryCreateCompleted(
                    new AgentSessionBuildInput(
                        candidate.Run,
                        candidate.Outcome,
                        candidate.TrustedRequest,
                        candidate.CurrentReviewContextIndex,
                        candidate.ContinuationCodec,
                        candidate.Predecessor,
                        candidate.Transition),
                    freshInputs);
                Outcome = R3QualityEvaluator.Evaluate(testCase, creation);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not AccessViolationException)
        {
            R3QualitySubject.TryCreateEvaluatorFailure(
                "quality_observer_exception",
                findingCount: 0,
                toolCallCount: 0,
                out var subject);
            Outcome = R3QualityEvaluator.Evaluate(testCase, subject!);
        }

        DelegationCount++;
        CommitResult = inner.Commit(
            candidate,
            access,
            priorLineage,
            authorizedTransition,
            stateRoot,
            keyResolver,
            cancellationToken);
        return CommitResult;
    }

    internal static bool IsContinuationOnlyReview(
        AgentRunOutcome outcome,
        R3QualityCase testCase)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(testCase);
        if (testCase.Expectation is not
                R3QualityContinuationExpectation expectation ||
            !outcome.CompletedSessionEligible ||
            outcome.Review is not { } review ||
            review.Findings.Length != 0 ||
            !review.Summary.Contains(
                expectation.PriorOnlyMarker,
                StringComparison.Ordinal))
        {
            return false;
        }

        var calls = outcome.Events.OfType<AgentToolCallEvent>().ToArray();
        var terminals = outcome.Events.OfType<AgentTerminalEvent>().ToArray();
        return calls is
            [
            {
                Name: AgentToolRegistry.FinishReviewName,
            } terminalCall,
            ] &&
            terminalCall.CanonicalArguments.AsSpan().SequenceEqual(
                review.CanonicalBytes) &&
            StringComparer.Ordinal.Equals(
                terminalCall.ArgumentsSha256,
                review.TerminalSha256) &&
            !outcome.Events.OfType<AgentToolResultEvent>().Any() &&
            terminals is [var terminal] &&
            StringComparer.Ordinal.Equals(
                terminal.TerminalSha256,
                review.TerminalSha256);
    }

    private bool QualityPassed() => Outcome is
    {
        Status: "passed",
        Classification: "quality",
        Code: R3QualityCodes.Passed,
    };
}

internal sealed class VerifierTransportFactory(
    VerifierScenario scenario,
    R3QualityCase testCase,
    ReviewedIdentity reviewedIdentity,
    string? expectedHistorySha256,
    IReadOnlyList<byte[]> currentPublicInputs)
    : IR3LiveAgentTransportFactory,
    IDisposable
{
    private DeepSeekTlsLoopbackServer? server;
    private int createCount;

    internal VerifierWireProof Proof
    {
        get
        {
            if (server is null)
            {
                return VerifierWireProof.Empty with
                {
                    FactoryCreateCount = createCount,
                };
            }

            server.CompleteAsync().GetAwaiter().GetResult();
            return server.Proof with
            {
                FactoryCreateCount = createCount,
            };
        }
    }

    public IDeepSeekTransport Create(DeepSeekCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (Interlocked.Increment(ref createCount) != 1 || server is not null)
        {
            throw new InvalidOperationException(
                "The verifier transport factory is single-use.");
        }

        var authorizationHash = LiveAgentFreshProcessDomain.RawSha256(
            System.Text.Encoding.UTF8.GetBytes(
                string.Concat("Bearer ", credential.Value)));
        server = new DeepSeekTlsLoopbackServer(
            scenario,
            testCase,
            reviewedIdentity,
            expectedHistorySha256,
            currentPublicInputs,
            authorizationHash,
            scenario != VerifierScenario.CanaryRouting ||
                StringComparer.Ordinal.Equals(
                    credential.Value,
                    VerifierCanaries.Provider));
        var handler = DeepSeekTransport.CreateHandler(
            TimeSpan.FromSeconds(5),
            server.ConnectAsync);
        server.PinCertificateFor(handler);
        return DeepSeekTransport.CreateForTesting(
            credential,
            handler,
            TimeSpan.FromSeconds(15));
    }

    public void Dispose() => server?.Dispose();
}

internal sealed record VerifierWireProof(
    bool Succeeded,
    int RequestCount,
    int FactoryCreateCount,
    string? FirstRequestSha256,
    string? ExpectedTerminalSha256,
    string? PriorFactSha256,
    string? HistoricalMessagesSha256,
    bool ExactReplayValidated,
    bool ReplayMutationMatrixValidated,
    string? FailureCode,
    bool CanaryRoutesValidated = false)
{
    internal static VerifierWireProof Empty { get; } = new(
        false,
        0,
        0,
        null,
        null,
        null,
        null,
        false,
        false,
        "wire_not_started");

    internal bool IsSatisfiedBy(string? terminalSha256) =>
        Succeeded &&
        FactoryCreateCount == 1 &&
        LiveAgentFreshProcessDomain.IsSha256(ExpectedTerminalSha256) &&
        StringComparer.Ordinal.Equals(
            ExpectedTerminalSha256,
            terminalSha256);
}
