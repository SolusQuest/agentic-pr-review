using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.Tests.Host.State;

public sealed class RestrictedStateServiceTests
{
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task UntrustedPrincipalsAreDeniedWithoutIssuingCapability(
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
    public async Task WrongScopeIsDeniedBeforeAnyStoreOrKeyBoundaryExists()
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
    public async Task MalformedTypedAuthorizationAndRestoreEnumsReturnStableCodes()
    {
        var malformedScope = RestrictedStateTestData.Scope() with
        {
            SessionId = null!,
        };
        var authorization = AuthorizedStateAccess.Authorize(
            new RestrictedStateAccessRequest(
                malformedScope,
                malformedScope,
                IsTrustedWorkflow: true,
                IsSameRepository: true,
                IsForkOrigin: false),
            out var malformedAccess);

        Assert.Equal(StateAction.Denied, authorization.Action);
        Assert.Equal(
            RestrictedStateCodes.AccessDenied,
            authorization.Code);
        Assert.Null(malformedAccess);

        var access = RestrictedStateTestData.Access();
        var service = Service(
            new MemoryRestrictedStateStore(),
            new TestKeyResolver(),
            new TestSessionAdmission());
        foreach (var restore in await Task.WhenAll(new[]
        {
            new RestrictedStateRestoreRequest(
                (RestrictedStateLocatorFamily)int.MaxValue,
                RestrictedStateRestoreIntent.Automatic,
                null,
                RestrictedStateTestData.SessionContext()),
            new RestrictedStateRestoreRequest(
                RestrictedStateLocatorFamily.Current,
                (RestrictedStateRestoreIntent)int.MaxValue,
                null,
                RestrictedStateTestData.SessionContext()),
        }.Select(request => service.RestoreAsync(
            access,
            request,
            CancellationToken.None))))
        {
            Assert.Equal(StateAction.Failed, restore.Result.Action);
            Assert.Equal(
                RestrictedStateCodes.EnvelopeInvalid,
                restore.Result.Code);
        }
    }

    [Fact]
    public async Task PrepareAcceptRestoreAndHandoffUseIndependentLineage()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var sessions = new TestSessionAdmission();
        var service = Service(store, keys, sessions);
        var access = RestrictedStateTestData.Access();
        var plaintext = new byte[] { 1, 2, 3 };

        var prepared = await service.PrepareAsync(
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

        var accepted = await service.AcceptAsync(
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

        var restored = await service.RestoreAsync(
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

        var handoff = await service.PrepareHandoffAsync(
            access,
            lineage,
            RestrictedStateTestData.SessionContext(),
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
    public async Task NextGenerationRetainsOnlyCurrentAndImmediatePredecessor()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var sessions = new TestSessionAdmission();
        var service = Service(store, keys, sessions);
        var access = RestrictedStateTestData.Access();
        var first = await PrepareAndAccept(
            service,
            store,
            access,
            lineage: null,
            generation: 0,
            predecessor: null);
        var firstLineage = Lineage(first);

        var prepared = await service.PrepareAsync(
            access,
            new RestrictedStatePrepareRequest(
                firstLineage,
                new byte[] { 2 },
                RestrictedStateTestData.SessionContext(
                    1,
                    first.EnvelopeSha256)),
            CancellationToken.None);
        Assert.Equal(StateAction.Prepared, prepared.Result.Action);

        var accepted = await service.AcceptAsync(
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
    public async Task StagingNeverRestoresAndMissingCurrentNeverFallsBack()
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

        var absent = await service.RestoreAsync(
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
        var missing = await service.RestoreAsync(
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
    public async Task ExpiryWinsBeforeKeyLookupAndRemovesTheWholeScope()
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

        var result = await service.RestoreAsync(
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
    public async Task AuthenticatedInvalidPlaintextFailsBeforeAdmission()
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

        var result = await service.RestoreAsync(
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
    public async Task ReplayLineageAndSameGenerationConflictAreDistinct()
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
            (await service.AcceptAsync(
                access,
                lineage,
                replayReceipt,
                RestrictedStateTestData.SessionContext(
                    1,
                    new string('a', 64)),
                CancellationToken.None)).Code);

        var conflictReceipt = new PreparedStateReceipt(
            2,
            new string('1', 64),
            new string('2', 64),
            new string('3', 64));
        Assert.Equal(
            RestrictedStateCodes.Conflict,
            (await service.AcceptAsync(
                access,
                lineage,
                conflictReceipt,
                RestrictedStateTestData.SessionContext(
                    1,
                    new string('a', 64)),
                CancellationToken.None)).Code);
    }

    [Fact]
    public async Task ExactReceiptReconciliationDoesNotReencrypt()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var sessions = new TestSessionAdmission();
        var service = Service(store, keys, sessions);
        var access = RestrictedStateTestData.Access();
        var prepared = await service.PrepareAsync(
            access,
            new RestrictedStatePrepareRequest(
                null,
                new byte[] { 1 },
                RestrictedStateTestData.SessionContext()),
            CancellationToken.None);
        var writes = keys.WriteCalls;

        var reconciled = await service.ReconcileAsync(
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
    public async Task CancellationAndCleanupFailuresDoNotAdvanceState()
    {
        var store = new MemoryRestrictedStateStore();
        var service = Service(
            store,
            new TestKeyResolver(),
            new TestSessionAdmission());
        var access = RestrictedStateTestData.Access();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var result = await service.PrepareAsync(
            access,
            new RestrictedStatePrepareRequest(
                null,
                new byte[] { 1 },
                RestrictedStateTestData.SessionContext()),
            cancelled.Token);
        Assert.Equal(RestrictedStateCodes.Cancelled, result.Result.Code);
        Assert.Equal(0, store.ReadCalls);

        store.WriteFailure = RestrictedStateStoreFailure.Cleanup;
        var reset = await service.ResetAsync(access, CancellationToken.None);
        Assert.Equal(
            RestrictedStateCodes.CleanupFailed,
            reset.Code);
        Assert.Empty(store.Snapshot.Accepted);
    }

    [Fact]
    public async Task PrepareUsesOneTrustedClockReadAndExactMaximumRetention()
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

        var result = await service.PrepareAsync(
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
    public async Task AcceptAuthenticatesStagingBeforeTransition()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var sessions = new TestSessionAdmission();
        var service = Service(store, keys, sessions);
        var access = RestrictedStateTestData.Access();
        var prepared = await service.PrepareAsync(
            access,
            new RestrictedStatePrepareRequest(
                null,
                new byte[] { 1 },
                RestrictedStateTestData.SessionContext()),
            CancellationToken.None);
        sessions.Reject = true;

        var result = await service.AcceptAsync(
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
    public async Task PreparePreservesReceiptAcrossOutcomeUnknownAndHonorsCommitted()
    {
        var access = RestrictedStateTestData.Access();
        var unknownStore = new MemoryRestrictedStateStore
        {
            WriteFailure = RestrictedStateStoreFailure.Io,
        };
        var unknown = await Service(
            unknownStore,
            new TestKeyResolver(),
            new TestSessionAdmission()).PrepareAsync(
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
        var committed = await Service(
            committedStore,
            new TestKeyResolver(),
            new TestSessionAdmission()).PrepareAsync(
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
    public async Task ForgedExpiredLineageCannotDeleteLiveState()
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

        var result = await service.CleanupExpiredAsync(
            access,
            forged,
            CancellationToken.None);

        Assert.Equal(
            RestrictedStateCodes.LineageMismatch,
            result.Code);
        Assert.Single(store.Snapshot.Accepted);
    }

    [Fact]
    public async Task ExpiredPredecessorCleanupRetainsLiveCurrent()
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

        var result = await service.CleanupExpiredAsync(
            access,
            Lineage(predecessor),
            CancellationToken.None);

        Assert.Equal(RestrictedStateCodes.Expired, result.Code);
        Assert.Equal(current, Assert.Single(store.Snapshot.Accepted));
    }

    [Fact]
    public async Task ExpiredLineageIsRejectedBeforePrepareAdmissionAndHandoff()
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

        var prepared = await service.PrepareAsync(
            access,
            new RestrictedStatePrepareRequest(
                expired,
                new byte[] { 2 },
                RestrictedStateTestData.SessionContext(
                    1,
                    current.EnvelopeSha256)),
            CancellationToken.None);
        var handoff = await service.PrepareHandoffAsync(
            access,
            expired,
            RestrictedStateTestData.SessionContext(),
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
    public async Task MissingCurrentWinsOverMalformedOlderEnvelope()
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

        var result = await service.RestoreAsync(
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
    public async Task OlderReplayIsRejectedWhileNewerCurrentExists()
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

        var result = await service.RestoreAsync(
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
    public async Task CandidateSelfDeclaredLineageCannotReplaceHostCurrent()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var sessions = new TestSessionAdmission
        {
            Reject = true,
        };
        var access = RestrictedStateTestData.Access();
        var current = RestrictedStateTestData.Candidate(access, keys);
        store.Snapshot = new RestrictedStateSnapshot([current], null);
        var service = Service(store, keys, sessions);
        var forged = Lineage(current) with
        {
            EnvelopeSha256 = new string('a', 64),
        };

        var result = await service.PrepareAsync(
            access,
            new RestrictedStatePrepareRequest(
                forged,
                new byte[] { 2 },
                RestrictedStateTestData.SessionContext(
                    1,
                    forged.EnvelopeSha256)),
            CancellationToken.None);

        Assert.Equal(
            RestrictedStateCodes.EnvelopeInvalid,
            result.Result.Code);
        Assert.Equal(1, sessions.Calls);
        Assert.Null(store.Snapshot.Staging);

        sessions.Reject = false;
        var lineageResult = await service.PrepareAsync(
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
            lineageResult.Result.Code);
        Assert.Equal(3, sessions.Calls);
        Assert.Null(store.Snapshot.Staging);
    }

    [Fact]
    public async Task OutcomeUnknownReconcileUsesExactPersistedEnvelope()
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
        var prepared = await service.PrepareAsync(
            access,
            new RestrictedStatePrepareRequest(
                null,
                new byte[] { 1 },
                RestrictedStateTestData.SessionContext()),
            CancellationToken.None);
        var envelope = store.Snapshot.Staging!.Envelope.ToArray();
        var writeCalls = keys.WriteCalls;
        store.WriteFailure = RestrictedStateStoreFailure.None;

        var reconciled = await service.ReconcileAsync(
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
    public async Task SameSemanticDifferentEnvelopeReceiptConflicts()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var sessions = new TestSessionAdmission();
        var service = Service(store, keys, sessions);
        var access = RestrictedStateTestData.Access();
        var prepared = await service.PrepareAsync(
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

        var result = await service.AcceptAsync(
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
    public async Task AcceptExpiryBoundaryIsExact()
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
        var prepared = await service.PrepareAsync(
            access,
            new RestrictedStatePrepareRequest(
                null,
                new byte[] { 1 },
                RestrictedStateTestData.SessionContext()),
            CancellationToken.None);
        now = RestrictedStateTestData.Expires;

        var exact = await service.AcceptAsync(
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
    public async Task EnumerationStoreFailuresHaveExactStableCodes(
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

        var result = await service.EnumerateAsync(
            RestrictedStateTestData.Access(),
            CancellationToken.None);

        Assert.Equal(StateAction.Failed, result.Result.Action);
        Assert.Equal(expectedCode, result.Result.Code);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task EnumerationValidatesStagingFramingBeforeReturningAccepted()
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

        var valid = await service.EnumerateAsync(
            access,
            CancellationToken.None);

        Assert.Equal(StateAction.Enumerated, valid.Result.Action);
        Assert.Equal(
            RestrictedStateCodes.Enumerated,
            valid.Result.Code);
        Assert.Equal(current, Assert.Single(valid.Candidates));

        var malformedEnvelope = new byte[] { 0 };
        var envelopeSha =
            RestrictedStateEnvelope.EnvelopeSha256(
                malformedEnvelope);
        var malformedStaging = staging with
        {
            Envelope = malformedEnvelope,
            EnvelopeSha256 = envelopeSha,
            ObjectIdentity = RestrictedStateEnvelope.ObjectIdentity(
                staging.Binding,
                staging.SessionSha256,
                envelopeSha),
        };
        store.Snapshot = new RestrictedStateSnapshot(
            [current],
            malformedStaging);
        Assert.True(
            RestrictedStateValidation.IsValidSnapshot(
                store.Snapshot));

        var malformed = await service.EnumerateAsync(
            access,
            CancellationToken.None);

        Assert.Equal(StateAction.Failed, malformed.Result.Action);
        Assert.Equal(
            RestrictedStateCodes.EnumerationInvalid,
            malformed.Result.Code);
        Assert.Empty(malformed.Candidates);
        Assert.Equal(malformedStaging, store.Snapshot.Staging);
        Assert.Equal(0, store.WriteCalls);
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
    public async Task PrepareWriteFailuresPreserveExactReceiptAndCode(
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

        var result = await service.PrepareAsync(
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
    public async Task ResetDeleteFailuresHaveExactStableCodes(
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

        var result = await service.ResetAsync(
            RestrictedStateTestData.Access(),
            CancellationToken.None);

        Assert.Equal(StateAction.Failed, result.Action);
        Assert.Equal(expectedCode, result.Code);
    }

    [Fact]
    public async Task KeyAndSessionBoundaryExceptionsBecomeStableFailures()
    {
        var access = RestrictedStateTestData.Access();
        var sessionFailure = new TestSessionAdmission
        {
            Throw = true,
        };
        var sessionResult = await Service(
            new MemoryRestrictedStateStore(),
            new TestKeyResolver(),
            sessionFailure).PrepareAsync(
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
        var keyResult = await Service(
            new MemoryRestrictedStateStore(),
            writeKeys,
            new TestSessionAdmission()).PrepareAsync(
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
    public async Task HandoffExpiryBoundaryIsExact(
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

        var result = await service.PrepareHandoffAsync(
            access,
            Lineage(current),
            RestrictedStateTestData.SessionContext(),
            CancellationToken.None);

        Assert.Equal(expectedAction, result.Result.Action);
        Assert.Equal(
            expectedAction == StateAction.HandoffReady
                ? RestrictedStateCodes.HandoffReady
                : RestrictedStateCodes.LineageMismatch,
            result.Result.Code);
    }

    [Fact]
    public async Task RestoreIgnoresLegalNextStagingAndReturnsCurrent()
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

        var result = await service.RestoreAsync(
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
    public async Task AcceptedNextGenerationRetryStillRequiresExactPredecessor()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var sessions = new TestSessionAdmission();
        var service = Service(
            store,
            keys,
            sessions);
        var access = RestrictedStateTestData.Access();
        var first = await PrepareAndAccept(
            service,
            store,
            access,
            null,
            0,
            null);
        var lineage = Lineage(first);
        var prepared = await service.PrepareAsync(
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
            (await service.AcceptAsync(
                access,
                lineage,
                prepared.Receipt!,
                RestrictedStateTestData.SessionContext(
                    1,
                    first.EnvelopeSha256),
                CancellationToken.None)).Action);

        var acceptRetry = await service.AcceptAsync(
            access,
            lineage,
            prepared.Receipt!,
            RestrictedStateTestData.SessionContext(
                1,
                first.EnvelopeSha256),
            CancellationToken.None);
        var reconcileRetry = await service.ReconcileAsync(
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
        sessions.Reject = true;
        var callsBeforeForged = sessions.Calls;
        var forged = await service.AcceptAsync(
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
            RestrictedStateCodes.EnvelopeInvalid,
            forged.Code);
        Assert.Equal(callsBeforeForged + 1, sessions.Calls);
    }

    [Fact]
    public async Task PresentCandidatesAuthenticateBeforeReceiptAndLineageDefects()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var sessions = new TestSessionAdmission();
        var service = Service(store, keys, sessions);
        var access = RestrictedStateTestData.Access();
        var first = await PrepareAndAccept(
            service,
            store,
            access,
            null,
            0,
            null);
        var lineage = Lineage(first);
        var prepared = await service.PrepareAsync(
            access,
            new RestrictedStatePrepareRequest(
                lineage,
                new byte[] { 2 },
                RestrictedStateTestData.SessionContext(
                    1,
                    first.EnvelopeSha256)),
            CancellationToken.None);
        sessions.Reject = true;
        var malformedReceipt = prepared.Receipt! with
        {
            Generation = 0,
        };
        var callsBeforeReceipt = sessions.Calls;

        var receiptResult = await service.AcceptAsync(
            access,
            lineage,
            malformedReceipt,
            RestrictedStateTestData.SessionContext(
                1,
                first.EnvelopeSha256),
            CancellationToken.None);

        Assert.Equal(
            RestrictedStateCodes.EnvelopeInvalid,
            receiptResult.Code);
        Assert.Equal(callsBeforeReceipt + 1, sessions.Calls);
        Assert.Single(store.Snapshot.Accepted);
        Assert.NotNull(store.Snapshot.Staging);

        sessions.Reject = false;
        var malformedEnvelope = new byte[] { 0 };
        var staging = store.Snapshot.Staging!;
        var envelopeSha =
            RestrictedStateEnvelope.EnvelopeSha256(malformedEnvelope);
        var malformed = staging with
        {
            Envelope = malformedEnvelope,
            EnvelopeSha256 = envelopeSha,
            ObjectIdentity = RestrictedStateEnvelope.ObjectIdentity(
                staging.Binding,
                staging.SessionSha256,
                envelopeSha),
        };
        store.Snapshot = store.Snapshot with
        {
            Staging = malformed,
        };
        var exactMalformedReceipt = new PreparedStateReceipt(
            malformed.Binding.Generation,
            malformed.SessionSha256,
            malformed.EnvelopeSha256,
            malformed.ObjectIdentity);
        var wrongLineage = lineage with
        {
            SessionSha256 = new string('a', 64),
        };

        var aeadBeforeLineage = await service.AcceptAsync(
            access,
            wrongLineage,
            exactMalformedReceipt,
            RestrictedStateTestData.SessionContext(
                1,
                first.EnvelopeSha256),
            CancellationToken.None);
        var reconcileBeforeLineage = await service.ReconcileAsync(
            access,
            wrongLineage,
            exactMalformedReceipt,
            RestrictedStateTestData.SessionContext(
                1,
                first.EnvelopeSha256),
            CancellationToken.None);

        Assert.Equal(
            RestrictedStateCodes.EnvelopeInvalid,
            aeadBeforeLineage.Code);
        Assert.Equal(
            RestrictedStateCodes.EnvelopeInvalid,
            reconcileBeforeLineage.Code);
        Assert.Single(store.Snapshot.Accepted);
        Assert.NotNull(store.Snapshot.Staging);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProducerBindingTamperCannotInfluenceLineageSelectedOperations(
        bool mutateHead)
    {
        var access = RestrictedStateTestData.Access();
        var keys = new TestKeyResolver();
        var sessions = new TestSessionAdmission();
        var current = RestrictedStateTestData.Candidate(access, keys);
        var binding = mutateHead
            ? current.Binding with
            {
                ProducerHeadSha = new string('7', 40),
            }
            : current.Binding with
            {
                ProducerBaseSha = new string('6', 40),
            };
        var tampered = Rebind(current, binding);
        var lineage = Lineage(current);

        var prepareStore = new MemoryRestrictedStateStore
        {
            Snapshot = new RestrictedStateSnapshot([tampered], null),
        };
        var prepare = await Service(prepareStore, keys, sessions).PrepareAsync(
            access,
            new RestrictedStatePrepareRequest(
                lineage,
                new byte[] { 2 },
                RestrictedStateTestData.SessionContext(
                    1,
                    current.EnvelopeSha256)),
            CancellationToken.None);

        var staging = RestrictedStateTestData.Candidate(
            access,
            keys,
            1,
            current.EnvelopeSha256,
            plaintext: [2]);
        var acceptStore = new MemoryRestrictedStateStore
        {
            Snapshot = new RestrictedStateSnapshot(
                [tampered],
                staging),
        };
        var accept = await Service(acceptStore, keys, sessions).AcceptAsync(
            access,
            lineage,
            Receipt(staging),
            RestrictedStateTestData.SessionContext(
                1,
                current.EnvelopeSha256),
            CancellationToken.None);

        var handoffStore = new MemoryRestrictedStateStore
        {
            Snapshot = new RestrictedStateSnapshot([tampered], null),
        };
        var handoff = await Service(handoffStore, keys, sessions).PrepareHandoffAsync(
            access,
            lineage,
            RestrictedStateTestData.SessionContext(),
            CancellationToken.None);

        Assert.Equal(
            RestrictedStateCodes.AuthenticationFailed,
            prepare.Result.Code);
        Assert.Null(prepareStore.Snapshot.Staging);
        Assert.Equal(
            RestrictedStateCodes.AuthenticationFailed,
            accept.Code);
        Assert.Equal(tampered, Assert.Single(acceptStore.Snapshot.Accepted));
        Assert.Equal(staging, acceptStore.Snapshot.Staging);
        Assert.Equal(
            RestrictedStateCodes.AuthenticationFailed,
            handoff.Result.Code);
        Assert.Null(handoff.Receipt);
        Assert.Equal(4, keys.ReadCalls);
        Assert.Equal(2, sessions.Calls);
        Assert.Equal(0, prepareStore.WriteCalls);
        Assert.Equal(0, acceptStore.WriteCalls);
        Assert.Equal(0, handoffStore.WriteCalls);
    }

    [Fact]
    public async Task LineageSelectedCandidatesRequireSessionReadmission()
    {
        var access = RestrictedStateTestData.Access();
        var current = RestrictedStateTestData.Candidate(
            access,
            new TestKeyResolver());
        var lineage = Lineage(current);

        var prepareStore = new MemoryRestrictedStateStore
        {
            Snapshot = new RestrictedStateSnapshot([current], null),
        };
        var prepareSessions = new TestSessionAdmission
        {
            RejectOnCall = 2,
        };
        var prepare = await Service(
            prepareStore,
            new TestKeyResolver(),
            prepareSessions).PrepareAsync(
                access,
                new RestrictedStatePrepareRequest(
                    lineage,
                    new byte[] { 2 },
                    RestrictedStateTestData.SessionContext(
                        1,
                        current.EnvelopeSha256)),
                CancellationToken.None);

        var acceptKeys = new TestKeyResolver();
        var acceptedCurrent = RestrictedStateTestData.Candidate(
            access,
            acceptKeys);
        var staging = RestrictedStateTestData.Candidate(
            access,
            acceptKeys,
            1,
            acceptedCurrent.EnvelopeSha256,
            plaintext: [2]);
        var acceptStore = new MemoryRestrictedStateStore
        {
            Snapshot = new RestrictedStateSnapshot(
                [acceptedCurrent],
                staging),
        };
        var acceptSessions = new TestSessionAdmission
        {
            RejectOnCall = 2,
        };
        var accept = await Service(
            acceptStore,
            acceptKeys,
            acceptSessions).AcceptAsync(
                access,
                Lineage(acceptedCurrent),
                Receipt(staging),
                RestrictedStateTestData.SessionContext(
                    1,
                    acceptedCurrent.EnvelopeSha256),
                CancellationToken.None);

        var handoffKeys = new TestKeyResolver();
        var handoffCurrent = RestrictedStateTestData.Candidate(
            access,
            handoffKeys);
        var handoffStore = new MemoryRestrictedStateStore
        {
            Snapshot = new RestrictedStateSnapshot(
                [handoffCurrent],
                null),
        };
        var handoffSessions = new TestSessionAdmission
        {
            RejectOnCall = 1,
        };
        var handoff = await Service(
            handoffStore,
            handoffKeys,
            handoffSessions).PrepareHandoffAsync(
                access,
                Lineage(handoffCurrent),
                RestrictedStateTestData.SessionContext(),
                CancellationToken.None);

        Assert.Equal(
            RestrictedStateCodes.EnvelopeInvalid,
            prepare.Result.Code);
        Assert.Equal(2, prepareSessions.Calls);
        Assert.Null(prepareStore.Snapshot.Staging);
        Assert.Equal(
            RestrictedStateCodes.EnvelopeInvalid,
            accept.Code);
        Assert.Equal(2, acceptSessions.Calls);
        Assert.Equal(
            acceptedCurrent,
            Assert.Single(acceptStore.Snapshot.Accepted));
        Assert.Equal(staging, acceptStore.Snapshot.Staging);
        Assert.Equal(
            RestrictedStateCodes.EnvelopeInvalid,
            handoff.Result.Code);
        Assert.Equal(1, handoffSessions.Calls);
        Assert.Null(handoff.Receipt);
    }

    [Fact]
    public async Task RetryAndReconcileAuthenticateLineagePredecessor()
    {
        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var sessions = new TestSessionAdmission();
        var service = Service(store, keys, sessions);
        var access = RestrictedStateTestData.Access();
        var first = await PrepareAndAccept(
            service,
            store,
            access,
            null,
            0,
            null);
        var lineage = Lineage(first);
        var prepared = await service.PrepareAsync(
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
            (await service.AcceptAsync(
                access,
                lineage,
                prepared.Receipt!,
                RestrictedStateTestData.SessionContext(
                    1,
                    first.EnvelopeSha256),
                CancellationToken.None)).Action);

        var current = store.Snapshot.Accepted[0];
        var tamperedPredecessor = Rebind(
            store.Snapshot.Accepted[1],
            store.Snapshot.Accepted[1].Binding with
            {
                ProducerHeadSha = new string('7', 40),
            });
        store.Snapshot = new RestrictedStateSnapshot(
            [current, tamperedPredecessor],
            null);

        var accept = await service.AcceptAsync(
            access,
            lineage,
            prepared.Receipt!,
            RestrictedStateTestData.SessionContext(
                1,
                first.EnvelopeSha256),
            CancellationToken.None);
        var reconcile = await service.ReconcileAsync(
            access,
            lineage,
            prepared.Receipt!,
            RestrictedStateTestData.SessionContext(
                1,
                first.EnvelopeSha256),
            CancellationToken.None);

        Assert.Equal(
            RestrictedStateCodes.AuthenticationFailed,
            accept.Code);
        Assert.Equal(
            RestrictedStateCodes.AuthenticationFailed,
            reconcile.Code);
        Assert.Equal(
            tamperedPredecessor,
            store.Snapshot.Accepted[1]);
        Assert.Null(store.Snapshot.Staging);
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

    private static async Task<RestrictedStateCandidate> PrepareAndAccept(
        RestrictedStateService service,
        MemoryRestrictedStateStore store,
        AuthorizedStateAccess access,
        AcceptedLineage? lineage,
        long generation,
        string? predecessor)
    {
        var prepared = await service.PrepareAsync(
            access,
            new RestrictedStatePrepareRequest(
                lineage,
                new[] { checked((byte)(generation + 1)) },
                RestrictedStateTestData.SessionContext(
                    generation,
                    predecessor)),
            CancellationToken.None);
        Assert.Equal(StateAction.Prepared, prepared.Result.Action);
        var accepted = await service.AcceptAsync(
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

    private static PreparedStateReceipt Receipt(
        RestrictedStateCandidate candidate) =>
        new(
            candidate.Binding.Generation,
            candidate.SessionSha256,
            candidate.EnvelopeSha256,
            candidate.ObjectIdentity);

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
