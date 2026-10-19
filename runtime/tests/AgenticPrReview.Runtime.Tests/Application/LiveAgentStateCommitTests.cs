using AgenticPrReview.Runtime.Agent.Core;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Tests.Host.State;

namespace AgenticPrReview.Runtime.Tests.Application;

public sealed partial class R3LiveAgentApplicationTests
{

    [Fact]
    public async Task TypedPreparedStateCommitsOnceAndPublishesExactLineage()
    {
        var captured = CaptureTypedPreparedState();
        var transaction = new ScriptedStateTransaction();
        transaction.AcceptOutcomes.Enqueue(
            ScriptedStateOutcome.Accepted);
        var sink = new CapturingLineageSink(
            LiveAgentLineagePublicationOutcome.Ready);

        var result = await CommitAsync(captured, transaction, sink);

        Assert.Equal(R3LiveAgentCodes.Completed, result.Code);
        Assert.True(result.HandoffReady);
        Assert.Equal(0, result.AcceptedGeneration);
        Assert.Equal(transaction.Receipt!.SessionSha256,
            result.AcceptedSessionSha256);
        Assert.Equal(transaction.Receipt.EnvelopeSha256,
            result.AcceptedEnvelopeSha256);
        Assert.Equal(1, transaction.PrepareCalls);
        Assert.Equal(0, transaction.ReconcileCalls);
        Assert.Equal(1, transaction.AcceptCalls);
        Assert.Equal(1, sink.CallCount);
        Assert.Null(sink.PriorLineage);
        Assert.Equal(captured.Access.Scope, sink.Lineage!.Scope);
        Assert.Equal(1_700_000_000, sink.Lineage.AcceptedAtUnixSeconds);
        Assert.Equal(
            1_700_000_000 + RestrictedStateFormat.MaximumRetentionSeconds,
            sink.Lineage.ExpiresAtUnixSeconds);
        Assert.True(sink.Lineage.TransitionAuthorized);
        Assert.All(
            transaction.PreparedPlaintext.ToArray(),
            value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task PrepareOutcomeUnknownReconcilesOnceWithoutReprepare()
    {
        var captured = CaptureTypedPreparedState();
        var transaction = new ScriptedStateTransaction
        {
            PrepareIoFailed = true,
        };
        transaction.ReconcileOutcomes.Enqueue(
            ScriptedStateOutcome.Idempotent);
        transaction.AcceptOutcomes.Enqueue(
            ScriptedStateOutcome.Accepted);

        var result = await CommitAsync(
            captured,
            transaction,
            new CapturingLineageSink(
                LiveAgentLineagePublicationOutcome.Ready));

        Assert.Equal(R3LiveAgentCodes.Completed, result.Code);
        Assert.Equal(1, transaction.PrepareCalls);
        Assert.Equal(1, transaction.ReconcileCalls);
        Assert.Equal(1, transaction.AcceptCalls);
        Assert.True(transaction.ReconcileTokens.Single() ==
            CancellationToken.None);
    }

    [Theory]
    [InlineData(RestrictedStateCodes.Cancelled)]
    [InlineData(RestrictedStateCodes.Conflict)]
    [InlineData(RestrictedStateCodes.CleanupFailed)]
    [InlineData(RestrictedStateCodes.EnumerationInvalid)]
    public async Task ReceiptBearingPrepareFailurePreservesExactCode(
        string expectedCode)
    {
        var captured = CaptureTypedPreparedState();
        var transaction = new ScriptedStateTransaction
        {
            ReceiptBearingPrepareFailureCode = expectedCode,
        };
        var sink = new CapturingLineageSink(
            LiveAgentLineagePublicationOutcome.Ready);

        var result = await CommitAsync(captured, transaction, sink);

        Assert.Equal(expectedCode, result.Code);
        Assert.Null(result.AcceptedGeneration);
        Assert.False(result.HandoffReady);
        Assert.NotNull(transaction.Receipt);
        Assert.Equal(1, transaction.PrepareCalls);
        Assert.Equal(0, transaction.ReconcileCalls);
        Assert.Equal(0, transaction.AcceptCalls);
        Assert.Equal(0, sink.CallCount);
    }

    [Fact]
    public async Task AcceptOutcomeUnknownHasOneReconcileAndOneSameReceiptRetry()
    {
        var captured = CaptureTypedPreparedState();
        var transaction = new ScriptedStateTransaction();
        transaction.AcceptOutcomes.Enqueue(ScriptedStateOutcome.IoFailed);
        transaction.ReconcileOutcomes.Enqueue(
            ScriptedStateOutcome.Idempotent);
        transaction.AcceptOutcomes.Enqueue(
            ScriptedStateOutcome.Idempotent);

        var result = await CommitAsync(
            captured,
            transaction,
            new CapturingLineageSink(
                LiveAgentLineagePublicationOutcome.Ready));

        Assert.Equal(R3LiveAgentCodes.Completed, result.Code);
        Assert.Equal(1, transaction.PrepareCalls);
        Assert.Equal(1, transaction.ReconcileCalls);
        Assert.Equal(2, transaction.AcceptCalls);
        Assert.Same(
            transaction.AcceptReceipts[0],
            transaction.AcceptReceipts[1]);
        Assert.Equal(CancellationToken.None, transaction.AcceptTokens[1]);
    }

    [Fact]
    public async Task SecondAcceptOutcomeUnknownStopsWithoutClaimingCommit()
    {
        var captured = CaptureTypedPreparedState();
        var transaction = new ScriptedStateTransaction();
        transaction.AcceptOutcomes.Enqueue(ScriptedStateOutcome.IoFailed);
        transaction.ReconcileOutcomes.Enqueue(
            ScriptedStateOutcome.Idempotent);
        transaction.AcceptOutcomes.Enqueue(ScriptedStateOutcome.IoFailed);
        var sink = new CapturingLineageSink(
            LiveAgentLineagePublicationOutcome.Ready);

        var result = await CommitAsync(captured, transaction, sink);

        Assert.Equal(RestrictedStateCodes.IoFailed, result.Code);
        Assert.Null(result.AcceptedGeneration);
        Assert.False(result.HandoffReady);
        Assert.Equal(1, transaction.PrepareCalls);
        Assert.Equal(1, transaction.ReconcileCalls);
        Assert.Equal(2, transaction.AcceptCalls);
        Assert.Equal(0, sink.CallCount);
    }

    [Fact]
    public async Task CancellationRaceBeforePrepareClockPreservesCancelledTuple()
    {
        var captured = CaptureTypedPreparedState();
        var transaction = new ScriptedStateTransaction
        {
            EarlyPrepareCancellation = true,
        };
        var sink = new CapturingLineageSink(
            LiveAgentLineagePublicationOutcome.Ready);

        var result = await CommitAsync(captured, transaction, sink);

        Assert.Equal(RestrictedStateCodes.Cancelled, result.Code);
        Assert.Null(result.AcceptedGeneration);
        Assert.False(result.HandoffReady);
        Assert.Equal(1, transaction.PrepareCalls);
        Assert.Equal(0, transaction.AcceptCalls);
        Assert.Equal(0, sink.CallCount);
    }

    [Fact]
    public async Task CancellationAfterPrepareDoesNotInitiateAccept()
    {
        var captured = CaptureTypedPreparedState();
        using var cancellation = new CancellationTokenSource();
        var transaction = new ScriptedStateTransaction
        {
            AfterPrepare = cancellation.Cancel,
        };
        var sink = new CapturingLineageSink(
            LiveAgentLineagePublicationOutcome.Ready);

        var result = await CommitAsync(
            captured,
            transaction,
            sink,
            cancellation.Token);

        Assert.Equal(RestrictedStateCodes.Cancelled, result.Code);
        Assert.Null(result.AcceptedGeneration);
        Assert.Equal(1, transaction.PrepareCalls);
        Assert.Equal(0, transaction.AcceptCalls);
        Assert.Equal(0, sink.CallCount);
    }

    [Fact]
    public async Task CancellationAfterAtomicAcceptPreservesAcceptedTruth()
    {
        var captured = CaptureTypedPreparedState();
        using var cancellation = new CancellationTokenSource();
        var transaction = new ScriptedStateTransaction
        {
            AfterAccept = cancellation.Cancel,
        };
        transaction.AcceptOutcomes.Enqueue(
            ScriptedStateOutcome.Accepted);
        var sink = new CapturingLineageSink(
            LiveAgentLineagePublicationOutcome.Unavailable);

        var result = await CommitAsync(
            captured,
            transaction,
            sink,
            cancellation.Token);

        Assert.Equal(R3LiveAgentCodes.HandoffUnavailable, result.Code);
        Assert.Equal(transaction.Receipt!.Generation,
            result.AcceptedGeneration);
        Assert.False(result.HandoffReady);
        Assert.True(sink.CancellationToken.IsCancellationRequested);
    }

    [Theory]
    [InlineData(0, 1_700_000_000)]
    [InlineData(2, 1_700_000_000)]
    [InlineData(1, -1)]
    public async Task MalformedPrepareClockStopsBeforeAccept(
        int clockReads,
        long preparedAt)
    {
        var captured = CaptureTypedPreparedState();
        var transaction = new ScriptedStateTransaction
        {
            PrepareClockReads = clockReads,
            PrepareTimestamp = preparedAt,
        };
        var sink = new CapturingLineageSink(
            LiveAgentLineagePublicationOutcome.Ready);

        var result = await CommitAsync(captured, transaction, sink);

        Assert.Equal(R3LiveAgentCodes.CompositionFailed, result.Code);
        Assert.Null(result.AcceptedGeneration);
        Assert.Equal(0, transaction.AcceptCalls);
        Assert.Equal(0, sink.CallCount);
    }

    [Theory]
    [InlineData(
        (int)LiveAgentLineagePublicationOutcome.Unavailable,
        "r3_live_handoff_unavailable")]
    [InlineData(
        (int)LiveAgentLineagePublicationOutcome
            .CleanupFailedAfterAtomicPublication,
        "r3_live_handoff_cleanup_failed")]
    public async Task PostCommitSinkOutcomePreservesAcceptedIdentity(
        int publicationValue,
        string expectedCode)
    {
        var publication = (LiveAgentLineagePublicationOutcome)publicationValue;
        var captured = CaptureTypedPreparedState();
        var transaction = new ScriptedStateTransaction();
        transaction.AcceptOutcomes.Enqueue(
            ScriptedStateOutcome.Accepted);

        var result = await CommitAsync(
            captured,
            transaction,
            new CapturingLineageSink(publication));

        Assert.Equal(expectedCode, result.Code);
        Assert.False(result.HandoffReady);
        Assert.Equal(transaction.Receipt!.Generation,
            result.AcceptedGeneration);
        Assert.Equal(transaction.Receipt.SessionSha256,
            result.AcceptedSessionSha256);
        Assert.Equal(transaction.Receipt.EnvelopeSha256,
            result.AcceptedEnvelopeSha256);
    }

    [Fact]
    public async Task PostCommitExceptionCannotFallThroughToPrecommitFailure()
    {
        var captured = CaptureTypedPreparedState();
        var transaction = new ScriptedStateTransaction();
        transaction.AcceptOutcomes.Enqueue(
            ScriptedStateOutcome.Accepted);

        var result = await CommitAsync(
            captured,
            transaction,
            new CapturingLineageSink(
                LiveAgentLineagePublicationOutcome.Ready,
                throwOnPublish: true));

        Assert.Equal(R3LiveAgentCodes.HandoffUnavailable, result.Code);
        Assert.False(result.HandoffReady);
        Assert.Equal(transaction.Receipt!.Generation,
            result.AcceptedGeneration);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RestrictedStateCodes.Conflict)]
    public async Task MalformedReceiptStopsBeforeAcceptAndPublication(
        string? prepareFailureCode)
    {
        var captured = CaptureTypedPreparedState();
        var transaction = new ScriptedStateTransaction
        {
            ForgeSessionHash = true,
            ReceiptBearingPrepareFailureCode = prepareFailureCode,
        };
        var sink = new CapturingLineageSink(
            LiveAgentLineagePublicationOutcome.Ready);

        var result = await CommitAsync(captured, transaction, sink);

        Assert.Equal(R3LiveAgentCodes.CompositionFailed, result.Code);
        Assert.Null(result.AcceptedGeneration);
        Assert.Equal(1, transaction.PrepareCalls);
        Assert.Equal(0, transaction.AcceptCalls);
        Assert.Equal(0, sink.CallCount);
    }

    [Fact]
    public async Task IndependentLineageCannotBeSelfPromotedIntoBootstrapCandidate()
    {
        // A real accepted current source cannot acquire bootstrap authority.
        // The independent Host oracle proves source/generation/claim preconditions
        // and checks zero additional provider, acceptance and publication work.
        var current = new AgenticPrReview.Runtime.Tests.Host.Action.ActionHostCompositionTests();
        await current.SolusSuccessorRequiresExactConsumedSourceAndNewCheckpoint("fresh-preparer");
        await current.SolusSuccessorRequiresExactConsumedSourceAndNewCheckpoint("unclaimed");
    }

    [Fact]
    public async Task PreflightAndFactoryFailuresZeroEveryOwnedPlaintext()
    {
        // Native builder/admitted-session copies are obsolete. Exercise actual
        // current caller and transaction-owned buffers on both failure paths.
        await CurrentPreparedFactoryFailureZerosCallerPlaintext();
        await CurrentPreparedTransactionExceptionZerosBothOwnedPlaintextCopies();
    }

    [Fact]
    public async Task CurrentPreparedTransactionExceptionZerosBothOwnedPlaintextCopies()
    {
        var captured = CaptureTypedPreparedState();
        var transaction = new ScriptedStateTransaction
        {
            AfterPrepare = () => throw new IOException("synthetic_prepare_exception"),
        };
        var sink = new CapturingLineageSink(LiveAgentLineagePublicationOutcome.Ready);
        var result = await CommitAsync(captured, transaction, sink);
        Assert.Equal(R3LiveAgentCodes.CompositionFailed, result.Code);
        Assert.Null(result.AcceptedGeneration);
        Assert.False(result.HandoffReady);
        Assert.Equal(1, transaction.PrepareCalls);
        Assert.True(transaction.PreparedPlaintext.Length > 0);
        Assert.Equal(0, transaction.ReconcileCalls);
        Assert.Equal(0, transaction.AcceptCalls);
        Assert.Equal(0, sink.CallCount);
        // CommitAsync's finally independently checks both the caller buffer
        // and the real production copy observed by the transaction are zero.
    }

    [Fact]
    public async Task CurrentPreparedFactoryFailureZerosCallerPlaintext()
    {
        var captured = CaptureTypedPreparedState();
        var sink = new CapturingLineageSink(LiveAgentLineagePublicationOutcome.Ready);
        var coordinator = new LiveAgentStateCommitCoordinator(
            new ThrowingStateTransactionFactory(), new UnexpectedNativePreflight(), sink);
        Assert.True(R3LiveAgentStateKeyResolver.TryCreate(StateSecret, out var keys));
        using (keys)
        {
            var result = await coordinator.CommitPreparedAsync(captured.Prepared, captured.Access, null,
                ReviewedHeadTransition.SameHead, captured.Context, "state-root", keys!, default);
            Assert.Equal(R3LiveAgentCodes.CompositionFailed, result.Code);
            Assert.Null(result.AcceptedGeneration);
            Assert.False(result.HandoffReady);
        }
        Assert.Equal(0, sink.CallCount);
        Assert.All(captured.Prepared.Plaintext, value => Assert.Equal(0, value));
    }

    private static async Task<LiveAgentStateCommitResult> CommitAsync(
        CapturedTypedPreparedState captured,
        ScriptedStateTransaction transaction,
        CapturingLineageSink sink,
        CancellationToken cancellationToken = default)
    {
        var coordinator = new LiveAgentStateCommitCoordinator(
            new FixedStateTransactionFactory(transaction),
            new UnexpectedNativePreflight(),
            sink);
        Assert.True(R3LiveAgentStateKeyResolver.TryCreate(
            StateSecret,
            out var stateKeys));
        using (stateKeys)
        {
            try
            {
                return await coordinator.CommitPreparedAsync(
                    captured.Prepared,
                    captured.Access,
                    priorLineage: null,
                    ReviewedHeadTransition.SameHead,
                    captured.Context,
                    "state-root",
                    stateKeys!,
                    cancellationToken);
            }
            finally
            {
                Assert.All(captured.Prepared.Plaintext, value => Assert.Equal(0, value));
                Assert.All(transaction.PreparedPlaintext.ToArray(), value => Assert.Equal(0, value));
            }
        }
    }

    // Structural S1 records exercise the unchanged durable engine independently
    // of the retired native builder. This is not SDK import/history evidence.
    private static CapturedTypedPreparedState CaptureTypedPreparedState()
    {
        Assert.True(SolusRestrictedStateRecord.TryCreate(PreparedCompatibility(),
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            "structural-current-state"u8, out var record));
        Assert.True(SolusRestrictedStateSnapshotCodec.TryWrite(record!, out var bytes));
        return new(new(bytes!, SolusRestrictedStateSnapshotCodec.RecordSha256(bytes!), 0),
            RestrictedStateTestData.Access(),
            new(Identity.BaseSha, Identity.HeadSha, 0, null, null!));
    }

    private static SolusRestrictedStateCompatibility PreparedCompatibility() => new(
        SolusRestrictedStateFormat.PackageVersion, SolusRestrictedStateFormat.Profile,
        SolusRestrictedStateFormat.CompletedCandidateMode, SolusRestrictedStateFormat.Implementation,
        SolusRestrictedStateFormat.ContextFormat, SolusRestrictedStateFormat.ContextCompatibility,
        SolusRestrictedStateFormat.PackageClosureSha256, new string('1', 64), new string('2', 64));

    private sealed record CapturedTypedPreparedState(LiveAgentPreparedState Prepared,
        AuthorizedStateAccess Access, RestrictedStateSessionAdmissionContext Context);

    private sealed class UnexpectedNativePreflight : IRestrictedStateSessionAdmission
    {
        public RestrictedStateSessionAdmissionResult Admit(AuthorizedStateAccess access,
            ReadOnlyMemory<byte> plaintext, RestrictedStateSessionAdmissionContext context) =>
            throw new InvalidOperationException("Current prepared engine must not invoke native preflight.");
    }

    private enum ScriptedStateOutcome
    {
        Accepted,
        Idempotent,
        IoFailed,
        Conflict,
    }

    private sealed class FixedStateTransactionFactory(
        ILiveAgentStateTransaction transaction)
        : ILiveAgentStateTransactionFactory
    {
        public ILiveAgentStateTransaction Create(
            string stateRoot,
            IRestrictedStateKeyResolver keyResolver) => transaction;
    }

    private sealed class ThrowingStateTransactionFactory
        : ILiveAgentStateTransactionFactory
    {
        public ILiveAgentStateTransaction Create(
            string stateRoot,
            IRestrictedStateKeyResolver keyResolver) =>
            throw new IOException("synthetic transaction construction failure");
    }

    private sealed class ScriptedStateTransaction : ILiveAgentStateTransaction
    {
        internal Queue<ScriptedStateOutcome> ReconcileOutcomes { get; } = [];

        internal Queue<ScriptedStateOutcome> AcceptOutcomes { get; } = [];

        internal List<CancellationToken> ReconcileTokens { get; } = [];

        internal List<CancellationToken> AcceptTokens { get; } = [];

        internal List<PreparedStateReceipt> AcceptReceipts { get; } = [];

        internal int PrepareCalls { get; private set; }

        internal int ReconcileCalls { get; private set; }

        internal int AcceptCalls { get; private set; }

        internal bool PrepareIoFailed { get; init; }

        internal string? ReceiptBearingPrepareFailureCode { get; init; }

        internal bool EarlyPrepareCancellation { get; init; }

        internal bool ForgeSessionHash { get; init; }

        internal Action? AfterPrepare { get; init; }

        internal Action? AfterAccept { get; init; }

        internal int PrepareClockReads { get; init; } = 1;

        internal long PrepareTimestamp { get; init; } = 1_700_000_000;

        internal PreparedStateReceipt? Receipt { get; private set; }

        internal ReadOnlyMemory<byte> PreparedPlaintext { get; private set; }

        public Task<LiveAgentStatePrepareObservation> PrepareAsync(
            AuthorizedStateAccess access,
            RestrictedStatePrepareRequest request,
            CancellationToken cancellationToken)
        {
            PrepareCalls++;
            PreparedPlaintext = request.Plaintext;
            if (EarlyPrepareCancellation)
            {
                return Task.FromResult(new LiveAgentStatePrepareObservation(
                    new RestrictedStatePrepareResult(
                        StateResult.Create(
                            StateAction.Failed,
                            RestrictedStateCodes.Cancelled),
                        null),
                    clockReads: 0,
                    preparedAtUnixSeconds: null));
            }

            Assert.True(SolusRestrictedStateSnapshotCodec.TryRead(request.Plaintext.Span,
                PreparedCompatibility(), out var record, out var failure), failure.ToString());
            Assert.NotNull(record);
            Receipt = new PreparedStateReceipt(
                request.SessionContext.Generation,
                ForgeSessionHash ? new string('f', 64) : SolusRestrictedStateSnapshotCodec.RecordSha256(request.Plaintext.Span),
                new string('d', 64), new string('e', 64));

            var result = ReceiptBearingPrepareFailureCode is not null
                ? StateResult.Create(
                    StateAction.Failed,
                    ReceiptBearingPrepareFailureCode)
                : PrepareIoFailed
                ? StateResult.Create(
                    StateAction.Failed,
                    RestrictedStateCodes.IoFailed)
                : StateResult.Create(
                    StateAction.Prepared,
                    RestrictedStateCodes.Prepared,
                    Receipt.Generation,
                    Receipt.SessionSha256,
                    Receipt.EnvelopeSha256);
            AfterPrepare?.Invoke();
            return Task.FromResult(new LiveAgentStatePrepareObservation(
                new RestrictedStatePrepareResult(result, Receipt),
                PrepareClockReads,
                PrepareTimestamp));
        }

        public Task<StateResult> ReconcileAsync(
            AuthorizedStateAccess access,
            AcceptedLineage? lineage,
            PreparedStateReceipt receipt,
            RestrictedStateSessionAdmissionContext sessionContext,
            CancellationToken cancellationToken)
        {
            ReconcileCalls++;
            ReconcileTokens.Add(cancellationToken);
            return Task.FromResult(
                Result(ReconcileOutcomes.Dequeue(), receipt));
        }

        public Task<StateResult> AcceptAsync(
            AuthorizedStateAccess access,
            AcceptedLineage? lineage,
            PreparedStateReceipt receipt,
            RestrictedStateSessionAdmissionContext sessionContext,
            CancellationToken cancellationToken)
        {
            AcceptCalls++;
            AcceptTokens.Add(cancellationToken);
            AcceptReceipts.Add(receipt);
            var result = Result(AcceptOutcomes.Dequeue(), receipt);
            AfterAccept?.Invoke();
            return Task.FromResult(result);
        }

        private static StateResult Result(
            ScriptedStateOutcome outcome,
            PreparedStateReceipt receipt) => outcome switch
            {
                ScriptedStateOutcome.Accepted => StateResult.Create(
                    StateAction.Accepted,
                    RestrictedStateCodes.Accepted,
                    receipt.Generation,
                    receipt.SessionSha256,
                    receipt.EnvelopeSha256),
                ScriptedStateOutcome.Idempotent => StateResult.Create(
                    StateAction.Idempotent,
                    RestrictedStateCodes.Idempotent,
                    receipt.Generation,
                    receipt.SessionSha256,
                    receipt.EnvelopeSha256),
                ScriptedStateOutcome.IoFailed => StateResult.Create(
                    StateAction.Failed,
                    RestrictedStateCodes.IoFailed),
                _ => StateResult.Create(
                    StateAction.Failed,
                    RestrictedStateCodes.Conflict),
            };
    }

    private sealed class CapturingLineageSink(
        LiveAgentLineagePublicationOutcome outcome,
        bool throwOnPublish = false) : ILiveAgentAcceptedLineageSink
    {
        internal int CallCount { get; private set; }

        internal AcceptedLineage? PriorLineage { get; private set; }

        internal AcceptedLineage? Lineage { get; private set; }

        internal CancellationToken CancellationToken { get; private set; }

        public LiveAgentLineagePublicationOutcome PublishAtomically(
            AcceptedLineage? priorLineage,
            AcceptedLineage acceptedLineage,
            CancellationToken cancellationToken)
        {
            CallCount++;
            PriorLineage = priorLineage;
            Lineage = acceptedLineage;
            CancellationToken = cancellationToken;
            if (throwOnPublish)
            {
                throw new IOException("synthetic lineage publication failure");
            }

            return outcome;
        }
    }
}
