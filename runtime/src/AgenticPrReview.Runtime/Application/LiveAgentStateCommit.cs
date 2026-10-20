using AgenticPrReview.Runtime.Agent.Core;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using OpaqueStateStore =
    AgenticPrReview.Runtime.Host.State.OpaqueStore.IRestrictedStateStore;

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
    Task<LiveAgentStatePrepareObservation> PrepareAsync(
        AuthorizedStateAccess access,
        RestrictedStatePrepareRequest request,
        CancellationToken cancellationToken);

    Task<StateResult> ReconcileAsync(
        AuthorizedStateAccess access,
        AcceptedLineage? lineage,
        PreparedStateReceipt receipt,
        RestrictedStateSessionAdmissionContext sessionContext,
        CancellationToken cancellationToken);

    Task<StateResult> AcceptAsync(
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
    TimeProvider timeProvider,
    Func<string, LocalRestrictedStateStore>? storeFactory = null,
    IRestrictedStateSessionAdmission? admission = null) : ILiveAgentStateTransactionFactory
{
    public ILiveAgentStateTransaction Create(
        string stateRoot,
        IRestrictedStateKeyResolver keyResolver) =>
        new LiveAgentStateTransaction(
            storeFactory is null ? new LocalRestrictedStateStore(stateRoot) : storeFactory(stateRoot),
            keyResolver,
            timeProvider,
            admission);

    private IRestrictedStateSessionAdmission SelectedAdmission { get; } =
        admission ?? throw new ArgumentNullException(nameof(admission));
}

internal sealed class LiveAgentStateTransaction(
    OpaqueStateStore store,
    IRestrictedStateKeyResolver keyResolver,
    TimeProvider timeProvider,
    IRestrictedStateSessionAdmission? admission = null) : ILiveAgentStateTransaction
{
    public async Task<LiveAgentStatePrepareObservation> PrepareAsync(
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
        var outcome = await service.PrepareAsync(
                access,
                request,
                cancellationToken)
            .ConfigureAwait(false);
        return new LiveAgentStatePrepareObservation(
            outcome,
            clockReads,
            preparedAt);
    }

    public Task<StateResult> ReconcileAsync(
        AuthorizedStateAccess access,
        AcceptedLineage? lineage,
        PreparedStateReceipt receipt,
        RestrictedStateSessionAdmissionContext sessionContext,
        CancellationToken cancellationToken) =>
        Service(ReadCurrentClock).ReconcileAsync(
            access,
            lineage,
            receipt,
            sessionContext,
            cancellationToken);

    public Task<StateResult> AcceptAsync(
        AuthorizedStateAccess access,
        AcceptedLineage? lineage,
        PreparedStateReceipt receipt,
        RestrictedStateSessionAdmissionContext sessionContext,
        CancellationToken cancellationToken) =>
        Service(ReadCurrentClock).AcceptAsync(
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
            SelectedAdmission,
            clock);

    private IRestrictedStateSessionAdmission SelectedAdmission { get; } =
        admission ?? throw new ArgumentNullException(nameof(admission));
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

internal sealed record LiveAgentPreparedState(byte[] Plaintext, string SessionSha256, long Generation);

internal sealed class LiveAgentStateCommitCoordinator(
    ILiveAgentStateTransactionFactory transactionFactory,
    ILiveAgentAcceptedLineageSink lineageSink)

{

    // Typed candidate preflight precedes this shared durable protocol. The
    // plaintext identity is data; current access/store/key capabilities and the
    // selected admission adapter still authorize every prepare/accept operation.
    internal async Task<LiveAgentStateCommitResult> CommitPreparedAsync(
        LiveAgentPreparedState prepared, AuthorizedStateAccess access,
        AcceptedLineage? priorLineage, ReviewedHeadTransition authorizedTransition,
        RestrictedStateSessionAdmissionContext context, string stateRoot,
        IRestrictedStateKeyResolver keyResolver, CancellationToken cancellationToken)
    {
        PreparedStateReceipt? committedReceipt = null;
        var callerPlaintext = prepared.Plaintext;
        prepared = prepared with { Plaintext = prepared.Plaintext.ToArray() };
        Zero(callerPlaintext);
        try
        {
            if (prepared.Generation != context.Generation ||
                !RestrictedStateValidation.IsLowerHex(prepared.SessionSha256, 64) ||
                authorizedTransition is not (ReviewedHeadTransition.SameHead or ReviewedHeadTransition.VerifiedAhead))
                return LiveAgentStateCommitResult.Failure(RestrictedStateCodes.LineageMismatch);
            var transaction = transactionFactory.Create(
                stateRoot,
                keyResolver);
            var prepare = await transaction.PrepareAsync(
                    access,
                    new RestrictedStatePrepareRequest(
                        priorLineage,
                        prepared.Plaintext,
                        context),
                    cancellationToken)
                .ConfigureAwait(false);
            Zero(prepared.Plaintext);

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

            if (TryReceiptBearingPrepareFailure(
                    prepare,
                    prepared,
                    context,
                    out var prepareFailureCode))
            {
                return LiveAgentStateCommitResult.Failure(
                    prepareFailureCode!);
            }

            if (!TryPreparedReceipt(
                    prepare,
                    prepared,
                    context,
                    out var receipt,
                    out var preparedAt))
            {
                return LiveAgentStateCommitResult.Failure(
                    PrepareFailureCode(prepare));
            }

            if (IsIoFailure(prepare.Outcome.Result))
            {
                var reconciled = await transaction.ReconcileAsync(
                        access,
                        priorLineage,
                        receipt!,
                        context,
                        CancellationToken.None)
                    .ConfigureAwait(false);
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

            var accepted = await transaction.AcceptAsync(
                    access,
                    priorLineage,
                    receipt!,
                    context,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!IsCommitted(accepted, receipt!))
            {
                if (!IsIoFailure(accepted))
                {
                    return LiveAgentStateCommitResult.Failure(
                        BoundedFailureCode(accepted));
                }

                var reconciled = await transaction.ReconcileAsync(
                        access,
                        priorLineage,
                        receipt!,
                        context,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (!IsExactIdentity(
                        reconciled,
                        StateAction.Idempotent,
                        RestrictedStateCodes.Idempotent,
                        receipt!))
                {
                    return LiveAgentStateCommitResult.Failure(
                        BoundedFailureCode(reconciled));
                }

                accepted = await transaction.AcceptAsync(
                        access,
                        priorLineage,
                        receipt!,
                        context,
                        CancellationToken.None)
                    .ConfigureAwait(false);
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
        finally { Zero(prepared.Plaintext); }
    }

    private LiveAgentStateCommitResult PublishCommitted(
        AuthorizedStateAccess access,
        AcceptedLineage? priorLineage,
        ReviewedHeadTransition authorizedTransition,
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
                ReviewedHeadTransition.SameHead or
                ReviewedHeadTransition.VerifiedAhead);
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
        LiveAgentPreparedState built,
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
            !MatchesPreparedReceipt(candidateReceipt, built, context) ||
            (prepared && !MatchesIdentity(result, candidateReceipt)))
        {
            return false;
        }

        receipt = candidateReceipt;
        preparedAt = observed;
        return true;
    }

    private static bool TryReceiptBearingPrepareFailure(
        LiveAgentStatePrepareObservation prepare,
        LiveAgentPreparedState built,
        RestrictedStateSessionAdmissionContext context,
        out string? code)
    {
        code = null;
        var result = prepare.Outcome.Result;
        var receipt = prepare.Outcome.Receipt;
        if (result.Action != StateAction.Failed ||
            result.Generation is not null ||
            result.SessionSha256 is not null ||
            result.EnvelopeSha256 is not null ||
            receipt is null ||
            !IsReceiptBearingWriteFailure(result.Code) ||
            !MatchesPreparedReceipt(receipt, built, context))
        {
            return false;
        }

        code = result.Code;
        return true;
    }

    private static bool IsReceiptBearingWriteFailure(string code) =>
        StringComparer.Ordinal.Equals(code, RestrictedStateCodes.Cancelled) ||
        StringComparer.Ordinal.Equals(code, RestrictedStateCodes.Conflict) ||
        StringComparer.Ordinal.Equals(
            code,
            RestrictedStateCodes.CleanupFailed) ||
        StringComparer.Ordinal.Equals(
            code,
            RestrictedStateCodes.EnumerationInvalid);

    private static bool MatchesPreparedReceipt(
        PreparedStateReceipt receipt,
        LiveAgentPreparedState built,
        RestrictedStateSessionAdmissionContext context) =>
        RestrictedStateValidation.IsValidReceipt(receipt) &&
        receipt.Generation == context.Generation &&
        receipt.Generation == built.Generation &&
        StringComparer.Ordinal.Equals(
            receipt.SessionSha256,
            built.SessionSha256);

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
