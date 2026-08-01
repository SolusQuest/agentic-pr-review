using System.Security.Cryptography;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime;

internal enum LiveAgentLineagePublicationOutcome
{
    Ready,
    Unavailable,
    CleanupFailedAfterAtomicPublication,
}

internal interface ILiveAgentAcceptedLineageSink
{
    LiveAgentLineagePublicationOutcome PublishAtomically(
        AcceptedLineage? priorLineage,
        AcceptedLineage acceptedLineage,
        CancellationToken cancellationToken);
}

internal sealed class LiveAgentStatePrepareObservation(
    RestrictedStatePrepareResult outcome,
    int clockReads,
    long? preparedAtUnixSeconds)
{
    internal RestrictedStatePrepareResult Outcome { get; } = outcome;

    internal int ClockReads { get; } = clockReads;

    internal long? PreparedAtUnixSeconds { get; } = preparedAtUnixSeconds;

    public override string ToString() =>
        "live_agent_state_prepare_observation";
}

internal interface ILiveAgentStateTransaction
{
    LiveAgentStatePrepareObservation Prepare(
        AuthorizedStateAccess access,
        RestrictedStatePrepareRequest request,
        CancellationToken cancellationToken);

    StateResult Reconcile(
        AuthorizedStateAccess access,
        AcceptedLineage? lineage,
        PreparedStateReceipt receipt,
        RestrictedStateSessionAdmissionContext sessionContext,
        CancellationToken cancellationToken);

    StateResult Accept(
        AuthorizedStateAccess access,
        AcceptedLineage? lineage,
        PreparedStateReceipt receipt,
        RestrictedStateSessionAdmissionContext sessionContext,
        CancellationToken cancellationToken);
}

internal interface ILiveAgentStateTransactionFactory
{
    ILiveAgentStateTransaction Create(
        string stateRoot,
        IRestrictedStateKeyResolver keyResolver);
}

internal sealed class LiveAgentStateTransactionFactory(
    TimeProvider timeProvider) : ILiveAgentStateTransactionFactory
{
    public ILiveAgentStateTransaction Create(
        string stateRoot,
        IRestrictedStateKeyResolver keyResolver) =>
        new LiveAgentStateTransaction(
            new LocalRestrictedStateStore(stateRoot),
            keyResolver,
            timeProvider);
}

internal sealed class LiveAgentStateTransaction(
    IRestrictedStateStore store,
    IRestrictedStateKeyResolver keyResolver,
    TimeProvider timeProvider) : ILiveAgentStateTransaction
{
    public LiveAgentStatePrepareObservation Prepare(
        AuthorizedStateAccess access,
        RestrictedStatePrepareRequest request,
        CancellationToken cancellationToken)
    {
        var clockReads = 0;
        long? preparedAt = null;
        long ReadPrepareClock()
        {
            clockReads++;
            var value = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            if (clockReads == 1)
            {
                preparedAt = value;
            }

            return value;
        }

        var service = Service(ReadPrepareClock);
        var outcome = service.Prepare(
            access,
            request,
            cancellationToken);
        return new LiveAgentStatePrepareObservation(
            outcome,
            clockReads,
            preparedAt);
    }

    public StateResult Reconcile(
        AuthorizedStateAccess access,
        AcceptedLineage? lineage,
        PreparedStateReceipt receipt,
        RestrictedStateSessionAdmissionContext sessionContext,
        CancellationToken cancellationToken) =>
        Service(ReadCurrentClock).Reconcile(
            access,
            lineage,
            receipt,
            sessionContext,
            cancellationToken);

    public StateResult Accept(
        AuthorizedStateAccess access,
        AcceptedLineage? lineage,
        PreparedStateReceipt receipt,
        RestrictedStateSessionAdmissionContext sessionContext,
        CancellationToken cancellationToken) =>
        Service(ReadCurrentClock).Accept(
            access,
            lineage,
            receipt,
            sessionContext,
            cancellationToken);

    private long ReadCurrentClock() =>
        timeProvider.GetUtcNow().ToUnixTimeSeconds();

    private RestrictedStateService Service(Func<long> clock) =>
        new(
            store,
            keyResolver,
            new AgentSessionRestrictedStateAdmission(),
            clock);
}

internal sealed class LiveAgentStateCommitResult(
    string code,
    long? acceptedGeneration,
    string? acceptedSessionSha256,
    string? acceptedEnvelopeSha256,
    bool handoffReady)
{
    internal string Code { get; } = code;

    internal long? AcceptedGeneration { get; } = acceptedGeneration;

    internal string? AcceptedSessionSha256 { get; } =
        acceptedSessionSha256;

    internal string? AcceptedEnvelopeSha256 { get; } =
        acceptedEnvelopeSha256;

    internal bool HandoffReady { get; } = handoffReady;

    public override string ToString() => "live_agent_state_commit_result";

    internal static LiveAgentStateCommitResult Failure(string code) =>
        new(code, null, null, null, handoffReady: false);

    internal static LiveAgentStateCommitResult Committed(
        string code,
        PreparedStateReceipt receipt,
        bool handoffReady) =>
        new(
            code,
            receipt.Generation,
            receipt.SessionSha256,
            receipt.EnvelopeSha256,
            handoffReady);
}

internal interface ILiveAgentStateCommitCoordinator
{
    LiveAgentStateCommitResult Commit(
        LiveAgentCandidate candidate,
        AuthorizedStateAccess access,
        AcceptedLineage? priorLineage,
        AgentSessionHeadTransition authorizedTransition,
        string stateRoot,
        IRestrictedStateKeyResolver keyResolver,
        CancellationToken cancellationToken);
}

internal sealed class LiveAgentStateCommitCoordinator(
    ILiveAgentStateTransactionFactory transactionFactory,
    IRestrictedStateSessionAdmission preflightAdmission,
    ILiveAgentAcceptedLineageSink lineageSink)
    : ILiveAgentStateCommitCoordinator
{
    public LiveAgentStateCommitResult Commit(
        LiveAgentCandidate candidate,
        AuthorizedStateAccess access,
        AcceptedLineage? priorLineage,
        AgentSessionHeadTransition authorizedTransition,
        string stateRoot,
        IRestrictedStateKeyResolver keyResolver,
        CancellationToken cancellationToken)
    {
        PreparedStateReceipt? committedReceipt = null;
        AgentSessionArtifact? builtArtifact = null;
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return LiveAgentStateCommitResult.Failure(
                    RestrictedStateCodes.Cancelled);
            }

            if (!ValidInput(
                    candidate,
                    access,
                    priorLineage,
                    authorizedTransition,
                    stateRoot,
                    keyResolver))
            {
                return LiveAgentStateCommitResult.Failure(
                    R3LiveAgentCodes.InputInvalid);
            }

            AgentSessionBuildResult built;
            try
            {
                built = AgentSessionBuilder.Build(
                    new AgentSessionBuildInput(
                        candidate.Run,
                        candidate.Outcome,
                        candidate.TrustedRequest,
                        candidate.CurrentReviewContextIndex,
                        candidate.ContinuationCodec,
                        candidate.Predecessor,
                        candidate.Transition));
            }
            finally
            {
                Zero(candidate.Predecessor?.Plaintext);
            }

            if (!built.Succeeded || built.Artifact is null)
            {
                return LiveAgentStateCommitResult.Failure(
                    built.FailureCode ?? R3LiveAgentCodes.CompositionFailed);
            }

            builtArtifact = built.Artifact;
            try
            {
                if (!Preflight(
                        access,
                        builtArtifact,
                        candidate.StateAdmissionContext))
                {
                    return LiveAgentStateCommitResult.Failure(
                        RestrictedStateCodes.EnvelopeInvalid);
                }

                var transaction = transactionFactory.Create(
                    stateRoot,
                    keyResolver);
                var prepare = transaction.Prepare(
                    access,
                    new RestrictedStatePrepareRequest(
                        priorLineage,
                        builtArtifact.Plaintext,
                        candidate.StateAdmissionContext),
                    cancellationToken);
                Zero(builtArtifact.Plaintext);

                if (IsEarlyCancellation(prepare))
                {
                    return LiveAgentStateCommitResult.Failure(
                        RestrictedStateCodes.Cancelled);
                }

                if (!HasValidPrepareClock(prepare))
                {
                    return LiveAgentStateCommitResult.Failure(
                        R3LiveAgentCodes.CompositionFailed);
                }

                if (!TryPreparedReceipt(
                        prepare,
                        builtArtifact,
                        candidate.StateAdmissionContext,
                        out var receipt,
                        out var preparedAt))
                {
                    return LiveAgentStateCommitResult.Failure(
                        PrepareFailureCode(prepare));
                }

                if (IsIoFailure(prepare.Outcome.Result))
                {
                    var reconciled = transaction.Reconcile(
                        access,
                        priorLineage,
                        receipt!,
                        candidate.StateAdmissionContext,
                        CancellationToken.None);
                    if (!IsExactIdentity(
                            reconciled,
                            StateAction.Idempotent,
                            RestrictedStateCodes.Idempotent,
                            receipt!))
                    {
                        return LiveAgentStateCommitResult.Failure(
                            BoundedFailureCode(reconciled));
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return LiveAgentStateCommitResult.Failure(
                        RestrictedStateCodes.Cancelled);
                }

                var accepted = transaction.Accept(
                    access,
                    priorLineage,
                    receipt!,
                    candidate.StateAdmissionContext,
                    cancellationToken);
                if (!IsCommitted(accepted, receipt!))
                {
                    if (!IsIoFailure(accepted))
                    {
                        return LiveAgentStateCommitResult.Failure(
                            BoundedFailureCode(accepted));
                    }

                    var reconciled = transaction.Reconcile(
                        access,
                        priorLineage,
                        receipt!,
                        candidate.StateAdmissionContext,
                        CancellationToken.None);
                    if (!IsExactIdentity(
                            reconciled,
                            StateAction.Idempotent,
                            RestrictedStateCodes.Idempotent,
                            receipt!))
                    {
                        return LiveAgentStateCommitResult.Failure(
                            BoundedFailureCode(reconciled));
                    }

                    accepted = transaction.Accept(
                        access,
                        priorLineage,
                        receipt!,
                        candidate.StateAdmissionContext,
                        CancellationToken.None);
                    if (!IsCommitted(accepted, receipt!))
                    {
                        return LiveAgentStateCommitResult.Failure(
                            BoundedFailureCode(accepted));
                    }
                }

                committedReceipt = receipt;
                return PublishCommitted(
                    access,
                    priorLineage,
                    authorizedTransition,
                    receipt!,
                    preparedAt,
                    cancellationToken);
            }
            finally
            {
                Zero(builtArtifact.Plaintext);
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return committedReceipt is null
                ? LiveAgentStateCommitResult.Failure(
                    R3LiveAgentCodes.CompositionFailed)
                : LiveAgentStateCommitResult.Committed(
                    R3LiveAgentCodes.HandoffUnavailable,
                    committedReceipt,
                    handoffReady: false);
        }
        finally
        {
            Zero(candidate?.Predecessor?.Plaintext);
            Zero(builtArtifact?.Plaintext);
        }
    }

    private bool Preflight(
        AuthorizedStateAccess access,
        AgentSessionArtifact built,
        RestrictedStateSessionAdmissionContext context)
    {
        var admitted = preflightAdmission.Admit(
            access,
            built.Plaintext,
            context);

        try
        {
            var session = admitted.Session;
            return admitted.Succeeded &&
                session is not null &&
                session.Value?.Artifact is not null &&
                StringComparer.Ordinal.Equals(
                    session.SessionSha256,
                    built.SessionSha256) &&
                StringComparer.Ordinal.Equals(
                    session.Value.Artifact.SessionSha256,
                    built.SessionSha256) &&
                session.Generation == context.Generation &&
                session.Value.Artifact.Document.Generation ==
                    context.Generation &&
                StringComparer.Ordinal.Equals(
                    session.ProducerBaseSha,
                    context.ProducerBaseSha) &&
                StringComparer.Ordinal.Equals(
                    session.ProducerHeadSha,
                    context.ProducerHeadSha) &&
                StringComparer.Ordinal.Equals(
                    session.PredecessorEnvelopeSha256,
                    context.PredecessorEnvelopeSha256);
        }
        finally
        {
            ZeroAdmitted(admitted.Session);
        }
    }

    private LiveAgentStateCommitResult PublishCommitted(
        AuthorizedStateAccess access,
        AcceptedLineage? priorLineage,
        AgentSessionHeadTransition authorizedTransition,
        PreparedStateReceipt receipt,
        long preparedAt,
        CancellationToken cancellationToken)
    {
        var predecessor = receipt.Generation == 0
            ? null
            : priorLineage!.EnvelopeSha256;
        var lineage = new AcceptedLineage(
            access.Scope,
            receipt.Generation,
            receipt.SessionSha256,
            receipt.EnvelopeSha256,
            predecessor,
            preparedAt,
            checked(
                preparedAt +
                    RestrictedStateFormat.MaximumRetentionSeconds),
            TransitionAuthorized: authorizedTransition is
                AgentSessionHeadTransition.SameHead or
                AgentSessionHeadTransition.VerifiedAhead);
        if (!RestrictedStateValidation.IsValidLineage(lineage))
        {
            return LiveAgentStateCommitResult.Committed(
                R3LiveAgentCodes.HandoffUnavailable,
                receipt,
                handoffReady: false);
        }

        var publication = lineageSink.PublishAtomically(
            priorLineage,
            lineage,
            cancellationToken);
        return publication switch
        {
            LiveAgentLineagePublicationOutcome.Ready =>
                LiveAgentStateCommitResult.Committed(
                    R3LiveAgentCodes.Completed,
                    receipt,
                    handoffReady: true),
            LiveAgentLineagePublicationOutcome
                .CleanupFailedAfterAtomicPublication =>
                LiveAgentStateCommitResult.Committed(
                    R3LiveAgentCodes.HandoffCleanupFailed,
                    receipt,
                    handoffReady: false),
            _ => LiveAgentStateCommitResult.Committed(
                R3LiveAgentCodes.HandoffUnavailable,
                receipt,
                handoffReady: false),
        };
    }

    private static bool ValidInput(
        LiveAgentCandidate candidate,
        AuthorizedStateAccess access,
        AcceptedLineage? priorLineage,
        AgentSessionHeadTransition authorizedTransition,
        string stateRoot,
        IRestrictedStateKeyResolver keyResolver)
    {
        if (candidate is null ||
            access is null ||
            keyResolver is null ||
            string.IsNullOrWhiteSpace(stateRoot) ||
            candidate.Run is null ||
            candidate.Outcome is null ||
            !candidate.Outcome.CompletedSessionEligible ||
            candidate.TrustedRequest is null ||
            candidate.ContinuationCodec is null ||
            candidate.StateAdmissionContext is null ||
            candidate.StateAdmissionContext.SessionContext is null ||
            !RestrictedStateValidation.IsValidScope(access.Scope) ||
            authorizedTransition is not (
                AgentSessionHeadTransition.SameHead or
                AgentSessionHeadTransition.VerifiedAhead) ||
            candidate.Transition != authorizedTransition ||
            candidate.StateAdmissionContext.SessionContext.Transition !=
                authorizedTransition ||
            !MatchesScope(candidate, access.Scope))
        {
            return false;
        }

        var context = candidate.StateAdmissionContext;
        if (priorLineage is null)
        {
            return context.Generation == 0 &&
                context.PredecessorEnvelopeSha256 is null &&
                candidate.Predecessor is null;
        }

        if (!RestrictedStateValidation.IsValidLineage(priorLineage) ||
            priorLineage.Scope != access.Scope ||
            !priorLineage.TransitionAuthorized ||
            candidate.Predecessor is null ||
            priorLineage.Generation == long.MaxValue)
        {
            return false;
        }

        var predecessor = candidate.Predecessor;
        return context.Generation == priorLineage.Generation + 1 &&
            predecessor.Generation == priorLineage.Generation &&
            StringComparer.Ordinal.Equals(
                predecessor.SessionSha256,
                priorLineage.SessionSha256) &&
            StringComparer.Ordinal.Equals(
                predecessor.EnvelopeSha256,
                priorLineage.EnvelopeSha256) &&
            StringComparer.Ordinal.Equals(
                predecessor.PredecessorStateSha256,
                priorLineage.ExpectedPredecessorEnvelopeSha256) &&
            StringComparer.Ordinal.Equals(
                context.PredecessorEnvelopeSha256,
                priorLineage.EnvelopeSha256);
    }

    private static bool MatchesScope(
        LiveAgentCandidate candidate,
        RestrictedStateScope scope)
    {
        var stable = candidate.Run.StablePlan;
        var trusted = candidate.TrustedRequest;
        var session = candidate.StateAdmissionContext.SessionContext;
        return StringComparer.Ordinal.Equals(
                stable.RepositoryId,
                scope.RepositoryId) &&
            StringComparer.Ordinal.Equals(
                stable.WorkflowIdentity,
                scope.WorkflowIdentity) &&
            stable.ReviewTarget == scope.ReviewTarget &&
            StringComparer.Ordinal.Equals(
                candidate.Run.SessionId,
                scope.SessionId) &&
            StringComparer.Ordinal.Equals(
                stable.ProviderId,
                scope.ProviderId) &&
            StringComparer.Ordinal.Equals(stable.ModelId, scope.ModelId) &&
            StringComparer.Ordinal.Equals(
                stable.AdapterId,
                scope.AdapterId) &&
            StringComparer.Ordinal.Equals(
                stable.PolicySha256,
                scope.PolicySha256) &&
            StringComparer.Ordinal.Equals(
                stable.LimitsSha256,
                scope.LimitsSha256) &&
            StringComparer.Ordinal.Equals(
                stable.ToolsetSha256,
                scope.ToolsetSha256) &&
            StringComparer.Ordinal.Equals(stable.BuildId, scope.BuildId) &&
            StringComparer.Ordinal.Equals(
                trusted.RepositoryId,
                scope.RepositoryId) &&
            trusted.ReviewTarget == scope.ReviewTarget &&
            StringComparer.Ordinal.Equals(
                trusted.WorkflowIdentity,
                scope.WorkflowIdentity) &&
            StringComparer.Ordinal.Equals(session.SessionId, scope.SessionId);
    }

    private static bool IsEarlyCancellation(
        LiveAgentStatePrepareObservation prepare) =>
        prepare.ClockReads == 0 &&
        prepare.PreparedAtUnixSeconds is null &&
        prepare.Outcome.Receipt is null &&
        prepare.Outcome.Result.Action == StateAction.Failed &&
        StringComparer.Ordinal.Equals(
            prepare.Outcome.Result.Code,
            RestrictedStateCodes.Cancelled);

    private static bool TryPreparedReceipt(
        LiveAgentStatePrepareObservation prepare,
        AgentSessionArtifact built,
        RestrictedStateSessionAdmissionContext context,
        out PreparedStateReceipt? receipt,
        out long preparedAt)
    {
        receipt = null;
        preparedAt = 0;
        if (prepare.ClockReads != 1 ||
            prepare.PreparedAtUnixSeconds is not { } observed ||
            observed is < 0 or >
                RestrictedStateFormat.MaximumUnixSeconds -
                    RestrictedStateFormat.MaximumRetentionSeconds)
        {
            return false;
        }

        var result = prepare.Outcome.Result;
        var candidateReceipt = prepare.Outcome.Receipt;
        var prepared = IsExactActionCode(
            result,
            StateAction.Prepared,
            RestrictedStateCodes.Prepared);
        var ioFailure = IsIoFailure(result) &&
            result.Generation is null &&
            result.SessionSha256 is null &&
            result.EnvelopeSha256 is null;
        var eligible = prepared || ioFailure;
        if (!eligible ||
            candidateReceipt is null ||
            !RestrictedStateValidation.IsValidReceipt(candidateReceipt) ||
            (prepared && !MatchesIdentity(result, candidateReceipt)) ||
            candidateReceipt.Generation != context.Generation ||
            candidateReceipt.Generation != built.Document.Generation ||
            !StringComparer.Ordinal.Equals(
                candidateReceipt.SessionSha256,
                built.SessionSha256))
        {
            return false;
        }

        receipt = candidateReceipt;
        preparedAt = observed;
        return true;
    }

    private static bool HasValidPrepareClock(
        LiveAgentStatePrepareObservation prepare) =>
        prepare.ClockReads == 1 &&
        prepare.PreparedAtUnixSeconds is { } observed &&
        observed is >= 0 and <=
            RestrictedStateFormat.MaximumUnixSeconds -
                RestrictedStateFormat.MaximumRetentionSeconds;

    private static string PrepareFailureCode(
        LiveAgentStatePrepareObservation prepare)
    {
        var result = prepare.Outcome.Result;
        if (prepare.ClockReads == 1 &&
            prepare.PreparedAtUnixSeconds is not null &&
            prepare.Outcome.Receipt is null &&
            result.Action == StateAction.Failed &&
            result.Generation is null &&
            result.SessionSha256 is null &&
            result.EnvelopeSha256 is null &&
            RestrictedStateCodes.All.Any(code =>
                StringComparer.Ordinal.Equals(code, result.Code)))
        {
            return result.Code;
        }

        return R3LiveAgentCodes.CompositionFailed;
    }

    private static string BoundedFailureCode(StateResult result) =>
        result.Action == StateAction.Failed &&
        result.Generation is null &&
        result.SessionSha256 is null &&
        result.EnvelopeSha256 is null &&
        RestrictedStateCodes.All.Any(code =>
            StringComparer.Ordinal.Equals(code, result.Code))
            ? result.Code
            : R3LiveAgentCodes.CompositionFailed;

    private static bool IsCommitted(
        StateResult result,
        PreparedStateReceipt receipt) =>
        IsExactIdentity(
            result,
            StateAction.Accepted,
            RestrictedStateCodes.Accepted,
            receipt) ||
        IsExactIdentity(
            result,
            StateAction.Idempotent,
            RestrictedStateCodes.Idempotent,
            receipt);

    private static bool IsExactIdentity(
        StateResult result,
        StateAction action,
        string code,
        PreparedStateReceipt receipt) =>
        IsExactActionCode(result, action, code) &&
        MatchesIdentity(result, receipt);

    private static bool MatchesIdentity(
        StateResult result,
        PreparedStateReceipt receipt) =>
        result.Generation == receipt.Generation &&
        StringComparer.Ordinal.Equals(
            result.SessionSha256,
            receipt.SessionSha256) &&
        StringComparer.Ordinal.Equals(
            result.EnvelopeSha256,
            receipt.EnvelopeSha256);

    private static bool IsIoFailure(StateResult result) =>
        IsExactActionCode(
            result,
            StateAction.Failed,
            RestrictedStateCodes.IoFailed);

    private static bool IsExactActionCode(
        StateResult result,
        StateAction action,
        string code) =>
        result.Action == action &&
        StringComparer.Ordinal.Equals(result.Code, code);

    private static void ZeroAdmitted(RestrictedStateAdmittedSession? session)
    {
        if (session is null)
        {
            return;
        }

        Zero(session.Plaintext);
        Zero(session.Value?.Artifact?.Plaintext);
    }

    private static void Zero(byte[]? bytes)
    {
        if (bytes is not null)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is AccessViolationException or
            AppDomainUnloadedException or
            BadImageFormatException or
            CannotUnloadAppDomainException or
            OutOfMemoryException or
            StackOverflowException;
}
