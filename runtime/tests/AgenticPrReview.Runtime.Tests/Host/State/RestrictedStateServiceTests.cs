using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.Tests.Host.State;

public sealed class RestrictedStateServiceTests
{
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void UntrustedPrincipalsAreDeniedWithoutIssuingCapability(
        bool trustedWorkflow,
        bool sameRepository,
        bool forkOrigin)
    {
        var scope = RestrictedStateTestData.Scope();

        var result = AuthorizedStateAccess.Authorize(
            new RestrictedStateAccessRequest(
                scope,
                scope,
                trustedWorkflow,
                sameRepository,
                forkOrigin),
            out var access);

        Assert.Equal(StateAction.Denied, result.Action);
        Assert.Equal(RestrictedStateCodes.AccessDenied, result.Code);
        Assert.Null(access);
    }

    [Fact]
    public void WrongScopeIsDeniedBeforeAnyStoreOrKeyBoundaryExists()
    {
        var requested = RestrictedStateTestData.Scope();
        var authorized = requested with
        {
            SessionId = "another_session",
        };

        var result = AuthorizedStateAccess.Authorize(
            new RestrictedStateAccessRequest(
                requested,
                authorized,
                IsTrustedWorkflow: true,
                IsSameRepository: true,
                IsForkOrigin: false),
            out var access);

        Assert.Equal(
            StateResult.Create(
                StateAction.Denied,
                RestrictedStateCodes.AccessDenied),
            result);
        Assert.Null(access);
    }

    [Fact]
    public void PrepareAcceptRestoreAndHandoffUseIndependentLineage()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var sessions = new TestSessionAdmission();
        var service = Service(store, keys, sessions);
        var access = RestrictedStateTestData.Access();
        var plaintext = new byte[] { 1, 2, 3 };

        var prepared = service.Prepare(
            access,
            new RestrictedStatePrepareRequest(
                Lineage: null,
                plaintext,
                RestrictedStateTestData.SessionContext()),
            CancellationToken.None);

        Assert.Equal(StateAction.Prepared, prepared.Result.Action);
        Assert.Equal(RestrictedStateCodes.Prepared, prepared.Result.Code);
        Assert.NotNull(prepared.Receipt);
        Assert.Empty(store.Snapshot.Accepted);
        Assert.NotNull(store.Snapshot.Staging);

        var accepted = service.Accept(
            access,
            lineage: null,
            prepared.Receipt!,
            RestrictedStateTestData.SessionContext(),
            CancellationToken.None);

        Assert.Equal(StateAction.Accepted, accepted.Action);
        Assert.Equal(RestrictedStateCodes.Accepted, accepted.Code);
        Assert.Single(store.Snapshot.Accepted);
        Assert.Null(store.Snapshot.Staging);
        var lineage = Lineage(store.Snapshot.Accepted[0]);

        var restored = service.Restore(
            access,
            new RestrictedStateRestoreRequest(
                RestrictedStateLocatorFamily.Current,
                RestrictedStateRestoreIntent.Automatic,
                lineage,
                RestrictedStateTestData.SessionContext()),
            CancellationToken.None);

        Assert.Equal(StateAction.Restored, restored.Result.Action);
        Assert.Equal(RestrictedStateCodes.Restored, restored.Result.Code);
        Assert.Equal(plaintext, restored.Session!.Plaintext);

        var handoff = service.PrepareHandoff(
            access,
            lineage,
            CancellationToken.None);
        Assert.Equal(StateAction.HandoffReady, handoff.Result.Action);
        Assert.Equal(
            RestrictedStateCodes.HandoffReady,
            handoff.Result.Code);
        Assert.NotNull(handoff.Receipt);
        Assert.DoesNotContain(
            Path.DirectorySeparatorChar,
            handoff.Receipt!.ScopeIdentity);
    }

    [Fact]
    public void NextGenerationRetainsOnlyCurrentAndImmediatePredecessor()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var sessions = new TestSessionAdmission();
        var service = Service(store, keys, sessions);
        var access = RestrictedStateTestData.Access();
        var first = PrepareAndAccept(
            service,
            store,
            access,
            lineage: null,
            generation: 0,
            predecessor: null);
        var firstLineage = Lineage(first);

        var prepared = service.Prepare(
            access,
            new RestrictedStatePrepareRequest(
                firstLineage,
                new byte[] { 2 },
                RestrictedStateTestData.SessionContext(
                    1,
                    first.EnvelopeSha256)),
            CancellationToken.None);
        Assert.Equal(StateAction.Prepared, prepared.Result.Action);

        var accepted = service.Accept(
            access,
            firstLineage,
            prepared.Receipt!,
            RestrictedStateTestData.SessionContext(
                1,
                first.EnvelopeSha256),
            CancellationToken.None);

        Assert.Equal(StateAction.Accepted, accepted.Action);
        Assert.Equal(2, store.Snapshot.Accepted.Length);
        Assert.Equal(
            new long[] { 1, 0 },
            store.Snapshot.Accepted
                .Select(candidate => candidate.Binding.Generation)
                .ToArray());
    }

    [Fact]
    public void StagingNeverRestoresAndMissingCurrentNeverFallsBack()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var sessions = new TestSessionAdmission();
        var service = Service(store, keys, sessions);
        var access = RestrictedStateTestData.Access();
        var staged = RestrictedStateTestData.Candidate(
            access,
            keys);
        store.Snapshot = new RestrictedStateSnapshot([], staged);

        var absent = service.Restore(
            access,
            new RestrictedStateRestoreRequest(
                RestrictedStateLocatorFamily.Current,
                RestrictedStateRestoreIntent.Automatic,
                Lineage: null,
                RestrictedStateTestData.SessionContext()),
            CancellationToken.None);
        Assert.Equal(StateAction.Bootstrap, absent.Result.Action);
        Assert.Equal(RestrictedStateCodes.Absent, absent.Result.Code);
        Assert.Equal(0, keys.ReadCalls);

        var hidden = Lineage(staged) with
        {
            EnvelopeSha256 = new string('a', 64),
        };
        var missing = service.Restore(
            access,
            new RestrictedStateRestoreRequest(
                RestrictedStateLocatorFamily.Current,
                RestrictedStateRestoreIntent.Automatic,
                hidden,
                RestrictedStateTestData.SessionContext()),
            CancellationToken.None);
        Assert.Equal(StateAction.Bootstrap, missing.Result.Action);
        Assert.Equal(
            RestrictedStateCodes.CurrentMissing,
            missing.Result.Code);
        Assert.Equal(0, keys.ReadCalls);
    }

    [Fact]
    public void ExpiryWinsBeforeKeyLookupAndRemovesTheWholeScope()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var sessions = new TestSessionAdmission();
        var service = new RestrictedStateService(
            store,
            keys,
            sessions,
            () => RestrictedStateTestData.Expires);
        var access = RestrictedStateTestData.Access();
        var current = RestrictedStateTestData.Candidate(access, keys);
        store.Snapshot = new RestrictedStateSnapshot([current], null);

        var result = service.Restore(
            access,
            new RestrictedStateRestoreRequest(
                RestrictedStateLocatorFamily.Current,
                RestrictedStateRestoreIntent.Automatic,
                Lineage(current),
                RestrictedStateTestData.SessionContext()),
            CancellationToken.None);

        Assert.Equal(StateAction.Bootstrap, result.Result.Action);
        Assert.Equal(RestrictedStateCodes.Expired, result.Result.Code);
        Assert.Equal(0, keys.ReadCalls);
        Assert.Empty(store.Snapshot.Accepted);
        Assert.Null(store.Snapshot.Staging);
    }

    [Fact]
    public void AuthenticatedInvalidPlaintextFailsBeforeAdmission()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var sessions = new TestSessionAdmission
        {
            Reject = true,
        };
        var service = Service(store, keys, sessions);
        var access = RestrictedStateTestData.Access();
        var current = RestrictedStateTestData.Candidate(access, keys);
        store.Snapshot = new RestrictedStateSnapshot([current], null);

        var result = service.Restore(
            access,
            new RestrictedStateRestoreRequest(
                RestrictedStateLocatorFamily.Current,
                RestrictedStateRestoreIntent.Automatic,
                Lineage(current),
                RestrictedStateTestData.SessionContext()),
            CancellationToken.None);

        Assert.Equal(StateAction.Failed, result.Result.Action);
        Assert.Equal(
            RestrictedStateCodes.EnvelopeInvalid,
            result.Result.Code);
        Assert.Equal(1, sessions.Calls);
    }

    [Fact]
    public void ReplayLineageAndSameGenerationConflictAreDistinct()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var sessions = new TestSessionAdmission();
        var service = Service(store, keys, sessions);
        var access = RestrictedStateTestData.Access();
        var current = RestrictedStateTestData.Candidate(
            access,
            keys,
            generation: 1,
            predecessor: new string('a', 64));
        var staging = RestrictedStateTestData.Candidate(
            access,
            keys,
            generation: 2,
            predecessor: current.EnvelopeSha256,
            plaintext: [2]);
        store.Snapshot = new RestrictedStateSnapshot(
            [current],
            staging);
        var lineage = Lineage(current);

        var replayReceipt = new PreparedStateReceipt(
            0,
            new string('1', 64),
            new string('2', 64),
            new string('3', 64));
        Assert.Equal(
            RestrictedStateCodes.ReplayRejected,
            service.Accept(
                access,
                lineage,
                replayReceipt,
                RestrictedStateTestData.SessionContext(
                    1,
                    new string('a', 64)),
                CancellationToken.None).Code);

        var conflictReceipt = new PreparedStateReceipt(
            2,
            new string('1', 64),
            new string('2', 64),
            new string('3', 64));
        Assert.Equal(
            RestrictedStateCodes.Conflict,
            service.Accept(
                access,
                lineage,
                conflictReceipt,
                RestrictedStateTestData.SessionContext(
                    1,
                    new string('a', 64)),
                CancellationToken.None).Code);
    }

    [Fact]
    public void ExactReceiptReconciliationDoesNotReencrypt()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var sessions = new TestSessionAdmission();
        var service = Service(store, keys, sessions);
        var access = RestrictedStateTestData.Access();
        var prepared = service.Prepare(
            access,
            new RestrictedStatePrepareRequest(
                null,
                new byte[] { 1 },
                RestrictedStateTestData.SessionContext()),
            CancellationToken.None);
        var writes = keys.WriteCalls;

        var reconciled = service.Reconcile(
            access,
            lineage: null,
            prepared.Receipt!,
            RestrictedStateTestData.SessionContext(),
            CancellationToken.None);

        Assert.Equal(StateAction.Idempotent, reconciled.Action);
        Assert.Equal(
            RestrictedStateCodes.Idempotent,
            reconciled.Code);
        Assert.Equal(writes, keys.WriteCalls);
    }

    [Fact]
    public void CancellationAndCleanupFailuresDoNotAdvanceState()
    {
        var store = new MemoryRestrictedStateStore();
        var service = Service(
            store,
            new TestKeyResolver(),
            new TestSessionAdmission());
        var access = RestrictedStateTestData.Access();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var result = service.Prepare(
            access,
            new RestrictedStatePrepareRequest(
                null,
                new byte[] { 1 },
                RestrictedStateTestData.SessionContext()),
            cancelled.Token);
        Assert.Equal(RestrictedStateCodes.Cancelled, result.Result.Code);
        Assert.Equal(0, store.ReadCalls);

        store.WriteFailure = RestrictedStateStoreFailure.Cleanup;
        var reset = service.Reset(access, CancellationToken.None);
        Assert.Equal(
            RestrictedStateCodes.CleanupFailed,
            reset.Code);
        Assert.Empty(store.Snapshot.Accepted);
    }

    [Fact]
    public void PrepareUsesOneTrustedClockReadAndExactMaximumRetention()
    {
        var clockReads = 0;
        var now = RestrictedStateTestData.Now + 123;
        var store = new MemoryRestrictedStateStore();
        var service = new RestrictedStateService(
            store,
            new TestKeyResolver(),
            new TestSessionAdmission(),
            () =>
            {
                clockReads++;
                return now;
            });

        var result = service.Prepare(
            RestrictedStateTestData.Access(),
            new RestrictedStatePrepareRequest(
                null,
                new byte[] { 1 },
                RestrictedStateTestData.SessionContext()),
            CancellationToken.None);

        Assert.Equal(StateAction.Prepared, result.Result.Action);
        Assert.Equal(1, clockReads);
        Assert.Equal(
            now,
            store.Snapshot.Staging!.Binding.AcceptedAtUnixSeconds);
        Assert.Equal(
            now + RestrictedStateFormat.MaximumRetentionSeconds,
            store.Snapshot.Staging.Binding.ExpiresAtUnixSeconds);
    }

    [Fact]
    public void AcceptAuthenticatesStagingBeforeTransition()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var sessions = new TestSessionAdmission();
        var service = Service(store, keys, sessions);
        var access = RestrictedStateTestData.Access();
        var prepared = service.Prepare(
            access,
            new RestrictedStatePrepareRequest(
                null,
                new byte[] { 1 },
                RestrictedStateTestData.SessionContext()),
            CancellationToken.None);
        sessions.Reject = true;

        var result = service.Accept(
            access,
            null,
            prepared.Receipt!,
            RestrictedStateTestData.SessionContext(),
            CancellationToken.None);

        Assert.Equal(RestrictedStateCodes.EnvelopeInvalid, result.Code);
        Assert.Empty(store.Snapshot.Accepted);
        Assert.NotNull(store.Snapshot.Staging);
        Assert.Equal(1, keys.ReadCalls);
    }

    [Fact]
    public void PreparePreservesReceiptAcrossOutcomeUnknownAndHonorsCommitted()
    {
        var access = RestrictedStateTestData.Access();
        var unknownStore = new MemoryRestrictedStateStore
        {
            WriteFailure = RestrictedStateStoreFailure.Io,
        };
        var unknown = Service(
            unknownStore,
            new TestKeyResolver(),
            new TestSessionAdmission()).Prepare(
                access,
                new RestrictedStatePrepareRequest(
                    null,
                    new byte[] { 1 },
                    RestrictedStateTestData.SessionContext()),
                CancellationToken.None);
        Assert.Equal(RestrictedStateCodes.IoFailed, unknown.Result.Code);
        Assert.NotNull(unknown.Receipt);

        var committedStore = new MemoryRestrictedStateStore
        {
            WriteFailure = RestrictedStateStoreFailure.Io,
            CommitOnWriteFailure = true,
        };
        var committed = Service(
            committedStore,
            new TestKeyResolver(),
            new TestSessionAdmission()).Prepare(
                access,
                new RestrictedStatePrepareRequest(
                    null,
                    new byte[] { 1 },
                    RestrictedStateTestData.SessionContext()),
                CancellationToken.None);
        Assert.Equal(StateAction.Prepared, committed.Result.Action);
        Assert.NotNull(committed.Receipt);
        Assert.NotNull(committedStore.Snapshot.Staging);
    }

    [Fact]
    public void ForgedExpiredLineageCannotDeleteLiveState()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var access = RestrictedStateTestData.Access();
        var current = RestrictedStateTestData.Candidate(access, keys);
        store.Snapshot = new RestrictedStateSnapshot([current], null);
        var service = new RestrictedStateService(
            store,
            keys,
            new TestSessionAdmission(),
            () => RestrictedStateTestData.Expires);
        var forged = Lineage(current) with
        {
            SessionSha256 = new string('a', 64),
        };

        var result = service.CleanupExpired(
            access,
            forged,
            CancellationToken.None);

        Assert.Equal(
            RestrictedStateCodes.LineageMismatch,
            result.Code);
        Assert.Single(store.Snapshot.Accepted);
    }

    [Fact]
    public void ExpiredPredecessorCleanupRetainsLiveCurrent()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var access = RestrictedStateTestData.Access();
        var predecessor = RestrictedStateTestData.Candidate(access, keys);
        var expiredBinding = predecessor.Binding with
        {
            AcceptedAtUnixSeconds = RestrictedStateTestData.Now - 1,
            ExpiresAtUnixSeconds = RestrictedStateTestData.Now,
        };
        predecessor = Rebind(predecessor, expiredBinding);
        var current = RestrictedStateTestData.Candidate(
            access,
            keys,
            1,
            predecessor.EnvelopeSha256,
            plaintext: [2]);
        store.Snapshot = new RestrictedStateSnapshot(
            [current, predecessor],
            null);
        var service = Service(
            store,
            keys,
            new TestSessionAdmission());

        var result = service.CleanupExpired(
            access,
            Lineage(predecessor),
            CancellationToken.None);

        Assert.Equal(RestrictedStateCodes.Expired, result.Code);
        Assert.Equal(current, Assert.Single(store.Snapshot.Accepted));
    }

    [Fact]
    public void ExpiredLineageIsRejectedBeforePrepareAdmissionAndHandoff()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var sessions = new TestSessionAdmission();
        var access = RestrictedStateTestData.Access();
        var current = RestrictedStateTestData.Candidate(access, keys);
        store.Snapshot = new RestrictedStateSnapshot([current], null);
        var service = new RestrictedStateService(
            store,
            keys,
            sessions,
            () => RestrictedStateTestData.Expires);
        var expired = Lineage(current);

        var prepared = service.Prepare(
            access,
            new RestrictedStatePrepareRequest(
                expired,
                new byte[] { 2 },
                RestrictedStateTestData.SessionContext(
                    1,
                    current.EnvelopeSha256)),
            CancellationToken.None);
        var handoff = service.PrepareHandoff(
            access,
            expired,
            CancellationToken.None);

        Assert.Equal(
            RestrictedStateCodes.LineageMismatch,
            prepared.Result.Code);
        Assert.Equal(0, sessions.Calls);
        Assert.Equal(
            RestrictedStateCodes.LineageMismatch,
            handoff.Result.Code);
    }

    [Fact]
    public void MissingCurrentWinsOverMalformedOlderEnvelope()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var access = RestrictedStateTestData.Access();
        var older = RestrictedStateTestData.Candidate(access, keys);
        var malformedEnvelope = new byte[] { 0 };
        var malformedBinding = older.Binding;
        var envelopeSha =
            RestrictedStateEnvelope.EnvelopeSha256(malformedEnvelope);
        older = new RestrictedStateCandidate(
            malformedBinding,
            older.SessionSha256,
            envelopeSha,
            RestrictedStateEnvelope.ObjectIdentity(
                malformedBinding,
                older.SessionSha256,
                envelopeSha),
            malformedEnvelope);
        store.Snapshot = new RestrictedStateSnapshot([older], null);
        var service = Service(
            store,
            keys,
            new TestSessionAdmission());
        var requested = new AcceptedLineage(
            access.Scope,
            1,
            new string('a', 64),
            new string('b', 64),
            older.EnvelopeSha256,
            RestrictedStateTestData.Now,
            RestrictedStateTestData.Expires,
            TransitionAuthorized: true);

        var result = service.Restore(
            access,
            new RestrictedStateRestoreRequest(
                RestrictedStateLocatorFamily.Current,
                RestrictedStateRestoreIntent.Automatic,
                requested,
                RestrictedStateTestData.SessionContext(
                    1,
                    older.EnvelopeSha256)),
            CancellationToken.None);

        Assert.Equal(
            RestrictedStateCodes.CurrentMissing,
            result.Result.Code);
        Assert.Equal(0, keys.ReadCalls);
    }

    [Fact]
    public void OlderReplayIsRejectedWhileNewerCurrentExists()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var access = RestrictedStateTestData.Access();
        var older = RestrictedStateTestData.Candidate(access, keys);
        var newer = RestrictedStateTestData.Candidate(
            access,
            keys,
            1,
            older.EnvelopeSha256,
            plaintext: [2]);
        store.Snapshot = new RestrictedStateSnapshot(
            [newer, older],
            null);
        var service = Service(
            store,
            keys,
            new TestSessionAdmission());

        var result = service.Restore(
            access,
            new RestrictedStateRestoreRequest(
                RestrictedStateLocatorFamily.Current,
                RestrictedStateRestoreIntent.Automatic,
                Lineage(older),
                RestrictedStateTestData.SessionContext()),
            CancellationToken.None);

        Assert.Equal(
            RestrictedStateCodes.ReplayRejected,
            result.Result.Code);
        Assert.Equal(1, keys.ReadCalls);
    }

    [Fact]
    public void CandidateSelfDeclaredLineageCannotReplaceHostCurrent()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var sessions = new TestSessionAdmission();
        var access = RestrictedStateTestData.Access();
        var current = RestrictedStateTestData.Candidate(access, keys);
        store.Snapshot = new RestrictedStateSnapshot([current], null);
        var service = Service(store, keys, sessions);
        var forged = Lineage(current) with
        {
            EnvelopeSha256 = new string('a', 64),
        };

        var result = service.Prepare(
            access,
            new RestrictedStatePrepareRequest(
                forged,
                new byte[] { 2 },
                RestrictedStateTestData.SessionContext(
                    1,
                    forged.EnvelopeSha256)),
            CancellationToken.None);

        Assert.Equal(
            RestrictedStateCodes.LineageMismatch,
            result.Result.Code);
        Assert.Equal(0, sessions.Calls);
        Assert.Null(store.Snapshot.Staging);
    }

    [Fact]
    public void OutcomeUnknownReconcileUsesExactPersistedEnvelope()
    {
        var store = new MemoryRestrictedStateStore
        {
            WriteFailure = RestrictedStateStoreFailure.Io,
            PersistOnWriteFailure = true,
        };
        var keys = new TestKeyResolver();
        var service = Service(
            store,
            keys,
            new TestSessionAdmission());
        var access = RestrictedStateTestData.Access();
        var prepared = service.Prepare(
            access,
            new RestrictedStatePrepareRequest(
                null,
                new byte[] { 1 },
                RestrictedStateTestData.SessionContext()),
            CancellationToken.None);
        var envelope = store.Snapshot.Staging!.Envelope.ToArray();
        var writeCalls = keys.WriteCalls;
        store.WriteFailure = RestrictedStateStoreFailure.None;

        var reconciled = service.Reconcile(
            access,
            null,
            prepared.Receipt!,
            RestrictedStateTestData.SessionContext(),
            CancellationToken.None);

        Assert.Equal(StateAction.Idempotent, reconciled.Action);
        Assert.Equal(writeCalls, keys.WriteCalls);
        Assert.Equal(envelope, store.Snapshot.Staging!.Envelope);
    }

    [Fact]
    public void SameSemanticDifferentEnvelopeReceiptConflicts()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var sessions = new TestSessionAdmission();
        var service = Service(store, keys, sessions);
        var access = RestrictedStateTestData.Access();
        var prepared = service.Prepare(
            access,
            new RestrictedStatePrepareRequest(
                null,
                new byte[] { 1, 2, 3 },
                RestrictedStateTestData.SessionContext()),
            CancellationToken.None);
        var other = RestrictedStateTestData.Candidate(
            access,
            keys,
            plaintext: [1, 2, 3]);
        var otherReceipt = new PreparedStateReceipt(
            other.Binding.Generation,
            other.SessionSha256,
            other.EnvelopeSha256,
            other.ObjectIdentity);

        var result = service.Accept(
            access,
            null,
            otherReceipt,
            RestrictedStateTestData.SessionContext(),
            CancellationToken.None);

        Assert.NotEqual(
            prepared.Receipt!.EnvelopeSha256,
            otherReceipt.EnvelopeSha256);
        Assert.Equal(RestrictedStateCodes.Conflict, result.Code);
        Assert.NotNull(store.Snapshot.Staging);
    }

    [Fact]
    public void AcceptExpiryBoundaryIsExact()
    {
        var now = RestrictedStateTestData.Now;
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var service = new RestrictedStateService(
            store,
            keys,
            new TestSessionAdmission(),
            () => now);
        var access = RestrictedStateTestData.Access();
        var prepared = service.Prepare(
            access,
            new RestrictedStatePrepareRequest(
                null,
                new byte[] { 1 },
                RestrictedStateTestData.SessionContext()),
            CancellationToken.None);
        now = RestrictedStateTestData.Expires;

        var exact = service.Accept(
            access,
            null,
            prepared.Receipt!,
            RestrictedStateTestData.SessionContext(),
            CancellationToken.None);

        Assert.Equal(RestrictedStateCodes.Expired, exact.Code);
        Assert.Empty(store.Snapshot.Accepted);
        Assert.NotNull(store.Snapshot.Staging);
    }

    [Theory]
    [InlineData(
        (int)RestrictedStateStoreFailure.Cancelled,
        RestrictedStateCodes.Cancelled)]
    [InlineData(
        (int)RestrictedStateStoreFailure.Invalid,
        RestrictedStateCodes.EnumerationInvalid)]
    [InlineData(
        (int)RestrictedStateStoreFailure.Cleanup,
        RestrictedStateCodes.CleanupFailed)]
    [InlineData(
        (int)RestrictedStateStoreFailure.Io,
        RestrictedStateCodes.IoFailed)]
    public void EnumerationStoreFailuresHaveExactStableCodes(
        int failureValue,
        string expectedCode)
    {
        var failure = (RestrictedStateStoreFailure)failureValue;
        var store = new MemoryRestrictedStateStore
        {
            ReadFailure = failure,
        };
        var service = Service(
            store,
            new TestKeyResolver(),
            new TestSessionAdmission());

        var result = service.Enumerate(
            RestrictedStateTestData.Access(),
            CancellationToken.None);

        Assert.Equal(StateAction.Failed, result.Result.Action);
        Assert.Equal(expectedCode, result.Result.Code);
        Assert.Empty(result.Candidates);
    }

    [Theory]
    [InlineData(
        (int)RestrictedStateStoreFailure.Cancelled,
        RestrictedStateCodes.Cancelled)]
    [InlineData(
        (int)RestrictedStateStoreFailure.Invalid,
        RestrictedStateCodes.EnumerationInvalid)]
    [InlineData(
        (int)RestrictedStateStoreFailure.Conflict,
        RestrictedStateCodes.Conflict)]
    [InlineData(
        (int)RestrictedStateStoreFailure.Cleanup,
        RestrictedStateCodes.CleanupFailed)]
    [InlineData(
        (int)RestrictedStateStoreFailure.Io,
        RestrictedStateCodes.IoFailed)]
    public void PrepareWriteFailuresPreserveExactReceiptAndCode(
        int failureValue,
        string expectedCode)
    {
        var failure = (RestrictedStateStoreFailure)failureValue;
        var store = new MemoryRestrictedStateStore
        {
            WriteFailure = failure,
        };
        var service = Service(
            store,
            new TestKeyResolver(),
            new TestSessionAdmission());

        var result = service.Prepare(
            RestrictedStateTestData.Access(),
            new RestrictedStatePrepareRequest(
                null,
                new byte[] { 1 },
                RestrictedStateTestData.SessionContext()),
            CancellationToken.None);

        Assert.Equal(StateAction.Failed, result.Result.Action);
        Assert.Equal(expectedCode, result.Result.Code);
        Assert.NotNull(result.Receipt);
        Assert.Empty(store.Snapshot.Accepted);
        Assert.Null(store.Snapshot.Staging);
    }

    [Theory]
    [InlineData(
        (int)RestrictedStateStoreFailure.Cancelled,
        RestrictedStateCodes.Cancelled)]
    [InlineData(
        (int)RestrictedStateStoreFailure.Invalid,
        RestrictedStateCodes.EnumerationInvalid)]
    [InlineData(
        (int)RestrictedStateStoreFailure.Conflict,
        RestrictedStateCodes.Conflict)]
    [InlineData(
        (int)RestrictedStateStoreFailure.Cleanup,
        RestrictedStateCodes.CleanupFailed)]
    [InlineData(
        (int)RestrictedStateStoreFailure.Io,
        RestrictedStateCodes.IoFailed)]
    public void ResetDeleteFailuresHaveExactStableCodes(
        int failureValue,
        string expectedCode)
    {
        var failure = (RestrictedStateStoreFailure)failureValue;
        var store = new MemoryRestrictedStateStore
        {
            WriteFailure = failure,
        };
        var service = Service(
            store,
            new TestKeyResolver(),
            new TestSessionAdmission());

        var result = service.Reset(
            RestrictedStateTestData.Access(),
            CancellationToken.None);

        Assert.Equal(StateAction.Failed, result.Action);
        Assert.Equal(expectedCode, result.Code);
    }

    [Fact]
    public void KeyAndSessionBoundaryExceptionsBecomeStableFailures()
    {
        var access = RestrictedStateTestData.Access();
        var sessionFailure = new TestSessionAdmission
        {
            Throw = true,
        };
        var sessionResult = Service(
            new MemoryRestrictedStateStore(),
            new TestKeyResolver(),
            sessionFailure).Prepare(
                access,
                new RestrictedStatePrepareRequest(
                    null,
                    new byte[] { 1 },
                    RestrictedStateTestData.SessionContext()),
                CancellationToken.None);
        Assert.Equal(
            RestrictedStateCodes.EnvelopeInvalid,
            sessionResult.Result.Code);

        var writeKeys = new TestKeyResolver
        {
            ThrowOnWrite = true,
        };
        var keyResult = Service(
            new MemoryRestrictedStateStore(),
            writeKeys,
            new TestSessionAdmission()).Prepare(
                access,
                new RestrictedStatePrepareRequest(
                    null,
                    new byte[] { 1 },
                    RestrictedStateTestData.SessionContext()),
                CancellationToken.None);
        Assert.Equal(
            RestrictedStateCodes.KeyUnavailable,
            keyResult.Result.Code);
    }

    [Theory]
    [InlineData(-1, (int)StateAction.HandoffReady)]
    [InlineData(0, (int)StateAction.Failed)]
    [InlineData(1, (int)StateAction.Failed)]
    public void HandoffExpiryBoundaryIsExact(
        int offset,
        int expectedActionValue)
    {
        var expectedAction = (StateAction)expectedActionValue;
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var access = RestrictedStateTestData.Access();
        var current = RestrictedStateTestData.Candidate(access, keys);
        store.Snapshot = new RestrictedStateSnapshot([current], null);
        var service = new RestrictedStateService(
            store,
            keys,
            new TestSessionAdmission(),
            () => RestrictedStateTestData.Expires + offset);

        var result = service.PrepareHandoff(
            access,
            Lineage(current),
            CancellationToken.None);

        Assert.Equal(expectedAction, result.Result.Action);
        Assert.Equal(
            expectedAction == StateAction.HandoffReady
                ? RestrictedStateCodes.HandoffReady
                : RestrictedStateCodes.LineageMismatch,
            result.Result.Code);
    }

    [Fact]
    public void RestoreIgnoresLegalNextStagingAndReturnsCurrent()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var access = RestrictedStateTestData.Access();
        var current = RestrictedStateTestData.Candidate(access, keys);
        var staging = RestrictedStateTestData.Candidate(
            access,
            keys,
            1,
            current.EnvelopeSha256,
            plaintext: [2]);
        store.Snapshot = new RestrictedStateSnapshot(
            [current],
            staging);
        var service = Service(
            store,
            keys,
            new TestSessionAdmission());

        var result = service.Restore(
            access,
            new RestrictedStateRestoreRequest(
                RestrictedStateLocatorFamily.Current,
                RestrictedStateRestoreIntent.Automatic,
                Lineage(current),
                RestrictedStateTestData.SessionContext()),
            CancellationToken.None);

        Assert.Equal(StateAction.Restored, result.Result.Action);
        Assert.Equal(
            current.EnvelopeSha256,
            result.Result.EnvelopeSha256);
        Assert.Equal(
            staging.EnvelopeSha256,
            store.Snapshot.Staging!.EnvelopeSha256);
    }

    [Fact]
    public void AcceptedNextGenerationRetryStillRequiresExactPredecessor()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var service = Service(
            store,
            keys,
            new TestSessionAdmission());
        var access = RestrictedStateTestData.Access();
        var first = PrepareAndAccept(
            service,
            store,
            access,
            null,
            0,
            null);
        var lineage = Lineage(first);
        var prepared = service.Prepare(
            access,
            new RestrictedStatePrepareRequest(
                lineage,
                new byte[] { 2 },
                RestrictedStateTestData.SessionContext(
                    1,
                    first.EnvelopeSha256)),
            CancellationToken.None);
        Assert.Equal(
            StateAction.Accepted,
            service.Accept(
                access,
                lineage,
                prepared.Receipt!,
                RestrictedStateTestData.SessionContext(
                    1,
                    first.EnvelopeSha256),
                CancellationToken.None).Action);

        var acceptRetry = service.Accept(
            access,
            lineage,
            prepared.Receipt!,
            RestrictedStateTestData.SessionContext(
                1,
                first.EnvelopeSha256),
            CancellationToken.None);
        var reconcileRetry = service.Reconcile(
            access,
            lineage,
            prepared.Receipt!,
            RestrictedStateTestData.SessionContext(
                1,
                first.EnvelopeSha256),
            CancellationToken.None);
        var forgedLineage = lineage with
        {
            SessionSha256 = new string('a', 64),
        };
        var forged = service.Accept(
            access,
            forgedLineage,
            prepared.Receipt!,
            RestrictedStateTestData.SessionContext(
                1,
                first.EnvelopeSha256),
            CancellationToken.None);

        Assert.Equal(StateAction.Idempotent, acceptRetry.Action);
        Assert.Equal(StateAction.Idempotent, reconcileRetry.Action);
        Assert.Equal(
            RestrictedStateCodes.LineageMismatch,
            forged.Code);
    }

    private static RestrictedStateService Service(
        MemoryRestrictedStateStore store,
        TestKeyResolver keys,
        TestSessionAdmission sessions) =>
        new(
            store,
            keys,
            sessions,
            () => RestrictedStateTestData.Now);

    private static RestrictedStateCandidate PrepareAndAccept(
        RestrictedStateService service,
        MemoryRestrictedStateStore store,
        AuthorizedStateAccess access,
        AcceptedLineage? lineage,
        long generation,
        string? predecessor)
    {
        var prepared = service.Prepare(
            access,
            new RestrictedStatePrepareRequest(
                lineage,
                new[] { checked((byte)(generation + 1)) },
                RestrictedStateTestData.SessionContext(
                    generation,
                    predecessor)),
            CancellationToken.None);
        Assert.Equal(StateAction.Prepared, prepared.Result.Action);
        var accepted = service.Accept(
            access,
            lineage,
            prepared.Receipt!,
            RestrictedStateTestData.SessionContext(
                generation,
                predecessor),
            CancellationToken.None);
        Assert.Equal(StateAction.Accepted, accepted.Action);
        return store.Snapshot.Accepted[0];
    }

    private static AcceptedLineage Lineage(
        RestrictedStateCandidate candidate) =>
        new(
            candidate.Binding.Scope,
            candidate.Binding.Generation,
            candidate.SessionSha256,
            candidate.EnvelopeSha256,
            candidate.Binding.PredecessorEnvelopeSha256,
            candidate.Binding.AcceptedAtUnixSeconds,
            candidate.Binding.ExpiresAtUnixSeconds,
            TransitionAuthorized: true);

    private static RestrictedStateCandidate Rebind(
        RestrictedStateCandidate candidate,
        RestrictedStateBinding binding) =>
        candidate with
        {
            Binding = binding,
            ObjectIdentity = RestrictedStateEnvelope.ObjectIdentity(
                binding,
                candidate.SessionSha256,
                candidate.EnvelopeSha256),
        };
}
