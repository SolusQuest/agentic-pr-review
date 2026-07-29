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
                RestrictedStateTestData.SessionContext(),
                RestrictedStateTestData.Now,
                RestrictedStateTestData.Expires),
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
                    first.EnvelopeSha256),
                RestrictedStateTestData.Now + 1,
                RestrictedStateTestData.Expires),
            CancellationToken.None);
        Assert.Equal(StateAction.Prepared, prepared.Result.Action);

        var accepted = service.Accept(
            access,
            firstLineage,
            prepared.Receipt!,
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
        store.Snapshot = new RestrictedStateSnapshot(
            [current],
            current);
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
                CancellationToken.None).Code);

        var conflictReceipt = new PreparedStateReceipt(
            1,
            new string('1', 64),
            new string('2', 64),
            new string('3', 64));
        Assert.Equal(
            RestrictedStateCodes.Conflict,
            service.Accept(
                access,
                lineage,
                conflictReceipt,
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
                RestrictedStateTestData.SessionContext(),
                RestrictedStateTestData.Now,
                RestrictedStateTestData.Expires),
            CancellationToken.None);
        var writes = keys.WriteCalls;

        var reconciled = service.Reconcile(
            access,
            prepared.Receipt!,
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
                RestrictedStateTestData.SessionContext(),
                RestrictedStateTestData.Now,
                RestrictedStateTestData.Expires),
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
                    predecessor),
                RestrictedStateTestData.Now,
                RestrictedStateTestData.Expires),
            CancellationToken.None);
        Assert.Equal(StateAction.Prepared, prepared.Result.Action);
        var accepted = service.Accept(
            access,
            lineage,
            prepared.Receipt!,
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
}
