using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.Tests.Application;

public sealed partial class R3LiveAgentApplicationTests
{
    [Fact]
    public async Task RealTransactionCommitsTwoEncryptedGenerations()
    {
        using var snapshot = SnapshotDirectory();
        File.WriteAllText(
            Path.Join(snapshot.Path, "a.txt"),
            RepositoryFact + "\n",
            new UTF8Encoding(false));
        var stateRoot = Path.Join(snapshot.Path, "state");
        Directory.CreateDirectory(stateRoot);
        var time = new StaticTimeProvider(DateTimeOffset.UtcNow);
        var firstSink = new CapturingLineageSink(
            LiveAgentLineagePublicationOutcome.Ready);
        var firstCoordinator = new LiveAgentStateCommitCoordinator(
            new LiveAgentStateTransactionFactory(time),
            new AgentSessionRestrictedStateAdmission(),
            firstSink);
        var firstRequest = Request(snapshot.Path, stateRoot);

        var first = await new R3LiveAgentApplication(
            Dependencies(
                new R3LiveAgentStateRestorer(),
                new GroundedReviewHandler(ProviderSecret),
                firstCoordinator))
            .RunAsync(firstRequest, CancellationToken.None);

        Assert.Equal(R3LiveAgentCodes.Completed, first.Result.Code);
        Assert.Equal(0, first.Result.AcceptedGeneration);
        Assert.True(first.Result.HandoffReady);
        var firstLineage = Assert.IsType<AcceptedLineage>(
            firstSink.Lineage);

        var nextContext = new RestrictedStateSessionAdmissionContext(
            Identity.BaseSha,
            Identity.HeadSha,
            1,
            firstLineage.EnvelopeSha256,
            new AgentSessionStateAdmissionContext(
                firstRequest.StateAdmissionContext.SessionContext
                    .TrustedRequest,
                firstRequest.StateAdmissionContext.SessionContext.SessionId,
                Identity,
                new AgenticPrReview.Runtime.Agent.Chat.ProjectChatMessage(
                    "user",
                    [new AgenticPrReview.Runtime.Agent.Chat.ProjectTextContent(
                        "Review the next selected snapshot.")]),
                AgentSessionHeadTransition.SameHead,
                firstRequest.StateAdmissionContext.SessionContext
                    .ContinuationCodec,
                EnvelopeSha256: null));
        var secondRequest = new R3LiveAgentRequest(
            firstRequest.AuthorizedScope,
            isTrustedWorkflow: true,
            isSameRepository: true,
            isForkOrigin: false,
            RestrictedStateLocatorFamily.Current,
            RestrictedStateRestoreIntent.Explicit,
            firstLineage,
            nextContext,
            stateRoot,
            snapshot.Path,
            ["a.txt"],
            [],
            []);
        var secondSink = new CapturingLineageSink(
            LiveAgentLineagePublicationOutcome.Ready);
        var secondCoordinator = new LiveAgentStateCommitCoordinator(
            new LiveAgentStateTransactionFactory(time),
            new AgentSessionRestrictedStateAdmission(),
            secondSink);

        var second = await new R3LiveAgentApplication(
            Dependencies(
                new R3LiveAgentStateRestorer(),
                new GroundedReviewHandler(ProviderSecret, "-next"),
                secondCoordinator))
            .RunAsync(secondRequest, CancellationToken.None);

        Assert.Equal(R3LiveAgentCodes.Completed, second.Result.Code);
        Assert.Equal(1, second.Result.AcceptedGeneration);
        Assert.True(second.Result.HandoffReady);
        Assert.Same(firstLineage, secondSink.PriorLineage);
        Assert.Equal(
            firstLineage.EnvelopeSha256,
            secondSink.Lineage!.ExpectedPredecessorEnvelopeSha256);
        Assert.Equal(1, secondSink.Lineage.Generation);

        var stateBytes = Directory.EnumerateFiles(
                stateRoot,
                "*",
                SearchOption.AllDirectories)
            .SelectMany(File.ReadAllBytes)
            .ToArray();
        var stateText = Encoding.UTF8.GetString(stateBytes);
        Assert.DoesNotContain(
            RepositoryFact,
            stateText,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Inspect the selected file.",
            stateText,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            ProviderSecret,
            stateText,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            StateSecret,
            stateText,
            StringComparison.Ordinal);
        var keyBytes = Convert.FromBase64String(StateSecret);
        try
        {
            Assert.False(ContainsSequence(stateBytes, keyBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    [Fact]
    public async Task GroundedCandidateCommitsOnceAndPublishesExactLineage()
    {
        var captured = await CaptureGroundedCandidate();
        var transaction = new ScriptedStateTransaction();
        transaction.AcceptOutcomes.Enqueue(
            ScriptedStateOutcome.Accepted);
        var sink = new CapturingLineageSink(
            LiveAgentLineagePublicationOutcome.Ready);

        var result = Commit(captured, transaction, sink);

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
        var captured = await CaptureGroundedCandidate();
        var transaction = new ScriptedStateTransaction
        {
            PrepareIoFailed = true,
        };
        transaction.ReconcileOutcomes.Enqueue(
            ScriptedStateOutcome.Idempotent);
        transaction.AcceptOutcomes.Enqueue(
            ScriptedStateOutcome.Accepted);

        var result = Commit(
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
        var captured = await CaptureGroundedCandidate();
        var transaction = new ScriptedStateTransaction
        {
            ReceiptBearingPrepareFailureCode = expectedCode,
        };
        var sink = new CapturingLineageSink(
            LiveAgentLineagePublicationOutcome.Ready);

        var result = Commit(captured, transaction, sink);

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
        var captured = await CaptureGroundedCandidate();
        var transaction = new ScriptedStateTransaction();
        transaction.AcceptOutcomes.Enqueue(ScriptedStateOutcome.IoFailed);
        transaction.ReconcileOutcomes.Enqueue(
            ScriptedStateOutcome.Idempotent);
        transaction.AcceptOutcomes.Enqueue(
            ScriptedStateOutcome.Idempotent);

        var result = Commit(
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
        var captured = await CaptureGroundedCandidate();
        var transaction = new ScriptedStateTransaction();
        transaction.AcceptOutcomes.Enqueue(ScriptedStateOutcome.IoFailed);
        transaction.ReconcileOutcomes.Enqueue(
            ScriptedStateOutcome.Idempotent);
        transaction.AcceptOutcomes.Enqueue(ScriptedStateOutcome.IoFailed);
        var sink = new CapturingLineageSink(
            LiveAgentLineagePublicationOutcome.Ready);

        var result = Commit(captured, transaction, sink);

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
        var captured = await CaptureGroundedCandidate();
        var transaction = new ScriptedStateTransaction
        {
            EarlyPrepareCancellation = true,
        };
        var sink = new CapturingLineageSink(
            LiveAgentLineagePublicationOutcome.Ready);

        var result = Commit(captured, transaction, sink);

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
        var captured = await CaptureGroundedCandidate();
        using var cancellation = new CancellationTokenSource();
        var transaction = new ScriptedStateTransaction
        {
            AfterPrepare = cancellation.Cancel,
        };
        var sink = new CapturingLineageSink(
            LiveAgentLineagePublicationOutcome.Ready);

        var result = Commit(
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
        var captured = await CaptureGroundedCandidate();
        using var cancellation = new CancellationTokenSource();
        var transaction = new ScriptedStateTransaction
        {
            AfterAccept = cancellation.Cancel,
        };
        transaction.AcceptOutcomes.Enqueue(
            ScriptedStateOutcome.Accepted);
        var sink = new CapturingLineageSink(
            LiveAgentLineagePublicationOutcome.Unavailable);

        var result = Commit(
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
        var captured = await CaptureGroundedCandidate();
        var transaction = new ScriptedStateTransaction
        {
            PrepareClockReads = clockReads,
            PrepareTimestamp = preparedAt,
        };
        var sink = new CapturingLineageSink(
            LiveAgentLineagePublicationOutcome.Ready);

        var result = Commit(captured, transaction, sink);

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
        var captured = await CaptureGroundedCandidate();
        var transaction = new ScriptedStateTransaction();
        transaction.AcceptOutcomes.Enqueue(
            ScriptedStateOutcome.Accepted);

        var result = Commit(
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
        var captured = await CaptureGroundedCandidate();
        var transaction = new ScriptedStateTransaction();
        transaction.AcceptOutcomes.Enqueue(
            ScriptedStateOutcome.Accepted);

        var result = Commit(
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
        var captured = await CaptureGroundedCandidate();
        var transaction = new ScriptedStateTransaction
        {
            ForgeSessionHash = true,
            ReceiptBearingPrepareFailureCode = prepareFailureCode,
        };
        var sink = new CapturingLineageSink(
            LiveAgentLineagePublicationOutcome.Ready);

        var result = Commit(captured, transaction, sink);

        Assert.Equal(R3LiveAgentCodes.CompositionFailed, result.Code);
        Assert.Null(result.AcceptedGeneration);
        Assert.Equal(1, transaction.PrepareCalls);
        Assert.Equal(0, transaction.AcceptCalls);
        Assert.Equal(0, sink.CallCount);
    }

    [Fact]
    public async Task IndependentLineageCannotBeSelfPromotedIntoBootstrapCandidate()
    {
        var captured = await CaptureGroundedCandidate();
        var transaction = new ScriptedStateTransaction();
        var sink = new CapturingLineageSink(
            LiveAgentLineagePublicationOutcome.Ready);
        var coordinator = new LiveAgentStateCommitCoordinator(
            new FixedStateTransactionFactory(transaction),
            new AgentSessionRestrictedStateAdmission(),
            sink);
        var forgedPrior = new AcceptedLineage(
            captured.Access.Scope,
            0,
            new string('c', 64),
            new string('d', 64),
            ExpectedPredecessorEnvelopeSha256: null,
            1_700_000_000,
            1_700_000_000 +
                RestrictedStateFormat.MaximumRetentionSeconds,
            TransitionAuthorized: true);
        Assert.True(R3LiveAgentStateKeyResolver.TryCreate(
            StateSecret,
            out var stateKeys));

        using (stateKeys)
        {
            var result = coordinator.Commit(
                captured.Candidate,
                captured.Access,
                forgedPrior,
                AgentSessionHeadTransition.SameHead,
                "state-root",
                stateKeys!,
                CancellationToken.None);
            Assert.Equal(R3LiveAgentCodes.InputInvalid, result.Code);
            Assert.Null(result.AcceptedGeneration);
        }

        Assert.Equal(0, transaction.PrepareCalls);
        Assert.Equal(0, sink.CallCount);
    }

    [Fact]
    public async Task PreflightAndFactoryFailuresZeroEveryOwnedPlaintext()
    {
        var captured = await CaptureGroundedCandidate();
        var preflight = new CapturingAdmission();
        var coordinator = new LiveAgentStateCommitCoordinator(
            new ThrowingStateTransactionFactory(),
            preflight,
            new CapturingLineageSink(
                LiveAgentLineagePublicationOutcome.Ready));
        Assert.True(R3LiveAgentStateKeyResolver.TryCreate(
            StateSecret,
            out var stateKeys));

        using (stateKeys)
        {
            var result = coordinator.Commit(
                captured.Candidate,
                captured.Access,
                priorLineage: null,
                AgentSessionHeadTransition.SameHead,
                "state-root",
                stateKeys!,
                CancellationToken.None);
            Assert.Equal(R3LiveAgentCodes.CompositionFailed, result.Code);
        }

        Assert.All(
            preflight.BuiltPlaintext.ToArray(),
            value => Assert.Equal(0, value));
        Assert.All(
            preflight.AdmittedSession!.Plaintext,
            value => Assert.Equal(0, value));
        Assert.All(
            preflight.AdmittedSession.Value.Artifact.Plaintext,
            value => Assert.Equal(0, value));
    }

    private static LiveAgentStateCommitResult Commit(
        CapturedLiveAgent captured,
        ScriptedStateTransaction transaction,
        CapturingLineageSink sink,
        CancellationToken cancellationToken = default)
    {
        var coordinator = new LiveAgentStateCommitCoordinator(
            new FixedStateTransactionFactory(transaction),
            new AgentSessionRestrictedStateAdmission(),
            sink);
        Assert.True(R3LiveAgentStateKeyResolver.TryCreate(
            StateSecret,
            out var stateKeys));
        using (stateKeys)
        {
            return coordinator.Commit(
                captured.Candidate,
                captured.Access,
                priorLineage: null,
                AgentSessionHeadTransition.SameHead,
                "state-root",
                stateKeys!,
                cancellationToken);
        }
    }

    private static async Task<CapturedLiveAgent> CaptureGroundedCandidate()
    {
        using var snapshot = SnapshotDirectory();
        File.WriteAllText(
            Path.Join(snapshot.Path, "a.txt"),
            RepositoryFact + "\n",
            new UTF8Encoding(false));
        var coordinator = new CapturingStateCommitCoordinator();
        var request = Request(
            snapshot.Path,
            Path.Join(snapshot.Path, "state"));
        var execution = await new R3LiveAgentApplication(
            Dependencies(
                new R3LiveAgentStateRestorer(),
                new GroundedReviewHandler(ProviderSecret),
                coordinator))
            .RunAsync(request, CancellationToken.None);
        Assert.Equal(R3LiveAgentCodes.Completed, execution.Result.Code);
        return new CapturedLiveAgent(
            Assert.IsType<LiveAgentCandidate>(coordinator.Candidate),
            Assert.IsType<AuthorizedStateAccess>(coordinator.Access));
    }

    private sealed record CapturedLiveAgent(
        LiveAgentCandidate Candidate,
        AuthorizedStateAccess Access);

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

        public LiveAgentStatePrepareObservation Prepare(
            AuthorizedStateAccess access,
            RestrictedStatePrepareRequest request,
            CancellationToken cancellationToken)
        {
            PrepareCalls++;
            PreparedPlaintext = request.Plaintext;
            if (EarlyPrepareCancellation)
            {
                return new LiveAgentStatePrepareObservation(
                    new RestrictedStatePrepareResult(
                        StateResult.Create(
                            StateAction.Failed,
                            RestrictedStateCodes.Cancelled),
                        null),
                    clockReads: 0,
                    preparedAtUnixSeconds: null);
            }

            Assert.True(AgentSessionCodec.TryParse(
                request.Plaintext.Span,
                out var artifact,
                out var failure), failure);
            try
            {
                Receipt = new PreparedStateReceipt(
                    request.SessionContext.Generation,
                    ForgeSessionHash
                        ? new string('f', 64)
                        : artifact!.SessionSha256,
                    new string('d', 64),
                    new string('e', 64));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(
                    artifact!.Plaintext);
            }

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
            return new LiveAgentStatePrepareObservation(
                new RestrictedStatePrepareResult(result, Receipt),
                PrepareClockReads,
                PrepareTimestamp);
        }

        public StateResult Reconcile(
            AuthorizedStateAccess access,
            AcceptedLineage? lineage,
            PreparedStateReceipt receipt,
            RestrictedStateSessionAdmissionContext sessionContext,
            CancellationToken cancellationToken)
        {
            ReconcileCalls++;
            ReconcileTokens.Add(cancellationToken);
            return Result(ReconcileOutcomes.Dequeue(), receipt);
        }

        public StateResult Accept(
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
            return result;
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

    private sealed class CapturingAdmission : IRestrictedStateSessionAdmission
    {
        private readonly AgentSessionRestrictedStateAdmission inner = new();

        internal ReadOnlyMemory<byte> BuiltPlaintext { get; private set; }

        internal RestrictedStateAdmittedSession? AdmittedSession {
            get;
            private set;
        }

        public RestrictedStateSessionAdmissionResult Admit(
            AuthorizedStateAccess access,
            ReadOnlyMemory<byte> plaintext,
            RestrictedStateSessionAdmissionContext context)
        {
            BuiltPlaintext = plaintext;
            var result = inner.Admit(access, plaintext, context);
            AdmittedSession = result.Session;
            return result;
        }
    }

    private sealed class StaticTimeProvider(DateTimeOffset value)
        : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private static bool ContainsSequence(
        ReadOnlySpan<byte> haystack,
        ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length)
        {
            return false;
        }

        for (var index = 0;
            index <= haystack.Length - needle.Length;
            index++)
        {
            if (haystack[index..(index + needle.Length)]
                .SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}
