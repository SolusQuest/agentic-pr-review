using AgenticPrReview.Runtime.Agent.Chat;
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
    string? expectedHistorySha256, VerifierTypedObservationOwner? observations = null) : ILiveAgentFreshProcessProfile
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
            activation.CurrentPublicInputs, observations ?? new());
        return execution;
    }
}

internal sealed class LiveAgentVerifierExecution :
    ILiveAgentFreshProcessProfileExecution
{
    private readonly VerifierChatFactory factory;
    private readonly VerifierCommitObserver observer;
    private readonly VerifierCombinedProof proof;
    private readonly VerifierScenario scenario;

    internal LiveAgentVerifierExecution(
        VerifierScenario scenario,
        R3QualityCase testCase,
        ReviewedIdentity reviewedIdentity,
        string? expectedHistorySha256,
        IReadOnlyList<byte[]> currentPublicInputs, VerifierTypedObservationOwner observations)
    {
        this.scenario = scenario;
        var copiedInputs = currentPublicInputs
            .Select(value => value.ToArray())
            .ToArray();
        factory = new VerifierChatFactory(
            scenario,
            testCase,
            reviewedIdentity,
            expectedHistorySha256,
            copiedInputs, observations);
        observer = new VerifierCommitObserver(
            scenario,
            testCase,
            CaptureFreshInputs(scenario, copiedInputs));
        proof = new VerifierCombinedProof(scenario, factory, observer);
    }

    public IR3LiveAgentChatFactory ChatFactory => factory;

    public ILiveAgentFreshProcessProof Proof => proof;

    internal VerifierCommitObserver Observer => observer;

    internal VerifierTypedProof TypedProof => factory.Proof;

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
    VerifierChatFactory factory,
    VerifierCommitObserver observer) : ILiveAgentFreshProcessProof
{
    internal bool PublicResultCanaryInjected =>
        scenario != VerifierScenario.PublicResultCanary ||
        StringComparer.Ordinal.Equals(
            Environment.GetEnvironmentVariable("APR111_PUBLIC_RESULT"),
            VerifierCanaries.PublicResult);

    public int RequestCount => factory.Proof.SyntheticCalls;

    public string? SecondProcessFirstRequestSha256 => null;

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

    public async Task<LiveAgentStateCommitResult> CommitAsync(
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
        CommitResult = await inner.CommitAsync(
                candidate,
                access,
                priorLineage,
                authorizedTransition,
                stateRoot,
                keyResolver,
                cancellationToken)
            .ConfigureAwait(false);
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

internal sealed class VerifierChatFactory(
    VerifierScenario scenario,
    R3QualityCase testCase,
    ReviewedIdentity reviewedIdentity,
    string? expectedHistorySha256,
    IReadOnlyList<byte[]> currentPublicInputs, VerifierTypedObservationOwner observations)
    : IR3LiveAgentChatFactory,
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

    public IProjectChatClient Create(DeepSeekAdapterContext context)
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
