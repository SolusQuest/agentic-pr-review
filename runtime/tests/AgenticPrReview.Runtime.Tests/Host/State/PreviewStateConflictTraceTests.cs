using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Recovery;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.Tests.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Tests.Host.State.Transactions;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Tests.Host.State.Lineage;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;

namespace AgenticPrReview.Runtime.Tests.Host.State;

public sealed class PreviewStateConflictTraceTests
{
    [Fact]
    public async Task PreviewStateTraceRetainsOnlyTheFirstFixedLocatorRefusal()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = new ScriptedLocatorStore { ListComplete = false };
        var result = await new LocatorRootService(store, keys, new FrozenLocatorTimeProvider(LocatorTestData.Now))
            .ResolveAsync(access, 0, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal(0, store.UploadCalls);
        var trace = new StateConflictTrace();
        trace.RecordReadback(StateConflictStage.Locator, result.Code, result.DiagnosticTerminal);
        trace.RecordCode(StateConflictStage.StateSelection, "unadmitted diagnostic value");
        Assert.Equal((StateConflictStage.Locator, StateConflictReason.Incomplete), trace.Read());
        Assert.Equal((StateConflictStage.Unclassified, StateConflictReason.Unknown), new StateConflictTrace().Read());
    }

    [Fact]
    public async Task LineageObservationKeepsItsIncompleteInventoryClassification()
    {
        using var lease = LineageTestData.Context();
        var store = new ScriptedLocatorStore { ListComplete = false };
        var service = new LineageService(store, lease.Time);
        var result = await service.ObserveReadOnlyAsync(
            lease.Context, LineageTestData.Request(lease.Access), CancellationToken.None);
        Assert.Equal(LineageCodes.Conflict, result.Code);
        Assert.Null(result.Context);
        var trace = new StateConflictTrace();
        trace.RecordLineage(StateConflictStage.LineageInventory, result);
        Assert.Equal((StateConflictStage.LineageInventory, StateConflictReason.Incomplete), trace.Read());
        Assert.Equal(0, store.UploadCalls);
        Assert.Equal(0, store.DeleteCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublicationRecoveryTraceDistinguishesInventoryAndStickyReadback(bool inventoryFails)
    {
        var fixture = await RetainedStateTransactionEndToEndTests.CreateFixtureAsync();
        using var support = fixture.Support;
        using var context = fixture.Context;
        if (inventoryFails)
        {
            fixture.Store.ListComplete = false;
        }
        else
        {
            var run = await RetainedStateTransactionEndToEndTests.CompleteRunAsync(fixture);
            Assert.True(R4PreparedPublication.TryCreate(run.Outcome, fixture.PublicationScope, out var publication));
            var prepared = await SolusTransactionTestSupport.PrepareAsync(context, publication!, CancellationToken.None);
            using var candidate = Assert.IsType<RetainedStatePreparedCandidate>(prepared.Value);
            var persisted = await RestrictedStateService.PersistRetainedCandidateAsync(context, candidate, CancellationToken.None);
            Assert.True(persisted.Succeeded, persisted.Code);
        }

        // The empty transport script refuses the sticky read. It never writes.
        var factory = new FakePublisherTransportFactory();
        var recovery = new PublicationRecoveryService(new StickyCommentPublisher(factory));
        var trace = new StateConflictTrace();
        using var evaluation = await recovery.ClassifyBeforeProviderAsync(fixture.Launch.Inputs.GitHubToken!,
            fixture.Invocation, fixture.PublicationScope, context, CancellationToken.None, trace);
        Assert.Equal(PublicationRecoveryAction.Conflict, evaluation.Decision.Action);
        Assert.Equal(inventoryFails ? StateConflictStage.RecoveryInventory : StateConflictStage.StickyReadback,
            trace.Read().Stage);
        Assert.Equal(0, factory.Transport.Creates);
        Assert.Equal(0, factory.Transport.Updates);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("admission")]
    [InlineData("candidate")]
    [InlineData("anchor-upload")]
    [InlineData("intent-upload")]
    [InlineData("target-visible")]
    [InlineData("target-census")]
    [InlineData("record-visible")]
    public async Task PublicationIntentTracePreservesRealP5OutcomeAndMutationCounts(string scenario)
    {
        async Task<(string Code, int Uploads, int Deletes)> RunAsync(StateConflictTrace? trace)
        {
            var fixture = await RetainedStateTransactionEndToEndTests.CreateFixtureAsync();
            using var support = fixture.Support;
            using var context = fixture.Context;
            var persisted = await PersistSyntheticCandidateAsync(fixture);
            using var candidate = persisted.Prepared;

            var publisher = new FakePublisherTransportFactory();
            publisher.Transport.Enqueue();
            using var evaluation = await new PublicationRecoveryService(new StickyCommentPublisher(publisher))
                .ClassifyBeforeProviderAsync(fixture.Launch.Inputs.GitHubToken!, fixture.Invocation, fixture.PublicationScope,
                    context, CancellationToken.None);
            Assert.Equal(PublicationRecoveryAction.ResumeBeforeIntent, evaluation.Decision.Action);
            Assert.False(evaluation.Decision.AllowsProvider);
            var uploads = fixture.Store.UploadCalls;
            var deletes = fixture.Store.DeleteCalls;

            void RefuseDelete()
            {
                fixture.Store.NextDeleteFailure = OpaqueStoreFailure.OutcomeUnknown;
                fixture.Store.NextDeleteMutationState = OpaqueStoreMutationState.OutcomeUnknown;
                if (scenario == "target-census") fixture.Store.ListComplete = false;
            }

            switch (scenario)
            {
                case "candidate":
                    fixture.Store.ListComplete = false;
                    break;
                case "anchor-upload":
                    fixture.Store.BeforeUpload = () => fixture.Store.NextUploadFailure = OpaqueStoreFailure.Io;
                    break;
                case "intent-upload":
                    fixture.Store.BeforeUpload = () =>
                        fixture.Store.BeforeUpload = () => fixture.Store.NextUploadFailure = OpaqueStoreFailure.Io;
                    break;
                case "target-visible":
                case "target-census":
                    fixture.Store.BeforeDelete = RefuseDelete;
                    break;
                case "record-visible":
                    fixture.Store.AfterDelete = () => fixture.Store.BeforeDelete = RefuseDelete;
                    break;
            }

            var selected = scenario == "admission" ? evaluation with { TargetExpectation = null } : evaluation;
            var result = await PublicationRecoveryPersistence.PersistIntentAndAuthorizeAsync(
                context, selected, CancellationToken.None, trace);
            using var intent = result.Value;
            Assert.Equal(0, publisher.Transport.Creates);
            Assert.Equal(0, publisher.Transport.Updates);
            Assert.Equal(scenario == "success", result.Succeeded);
            if (intent is not null)
            {
                Assert.Equal("success", scenario);
                Assert.Null(intent.StickyWriteAuthorization.PreviousTarget);
            }
            else
            {
                Assert.Equal(RetainedStateTransactionCodes.Conflict, result.Code);
            }
            return (result.Code, fixture.Store.UploadCalls - uploads, fixture.Store.DeleteCalls - deletes);
        }

        var trace = new StateConflictTrace();
        var traced = await RunAsync(trace);
        Assert.Equal(await RunAsync(null), traced);
        var (stage, reason) = trace.Read();
        Assert.Equal(scenario switch
        {
            "success" => StateConflictStage.Unclassified,
            "admission" => StateConflictStage.IntentAdmission,
            "candidate" => StateConflictStage.IntentCandidate,
            "anchor-upload" => StateConflictStage.IntentAnchor,
            "intent-upload" => StateConflictStage.IntentPersistence,
            "record-visible" => StateConflictStage.IntentCleanupRecordUnconfirmed,
            _ => StateConflictStage.IntentCleanupTargetUnconfirmed,
        }, stage);
        if (scenario is "target-visible" or "record-visible") Assert.Equal(StateConflictReason.StillListed, reason);
        if (scenario == "target-census") Assert.Equal(StateConflictReason.CensusIncomplete, reason);
        Assert.Equal(scenario switch { "success" or "record-visible" => 2, "target-visible" or "target-census" => 1, _ => 0 },
            traced.Deletes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnchoredIntentTracePreservesRecoveryAndRevalidation(bool interruptCandidateRead)
    {
        async Task<(string Code, int Uploads, int Deletes)> RunAsync(StateConflictTrace? trace)
        {
            var fixture = await RetainedStateTransactionEndToEndTests.CreateFixtureAsync();
            using var originalSupport = fixture.Support;
            using (var context = fixture.Context)
            {
                var candidate = await PersistSyntheticCandidateAsync(fixture);
                using var prepared = candidate.Prepared;
                var owned = await RestrictedStateService.RenewRetainedStateOwnershipAsync(
                    context, candidate, prior: null, expectedP5Records: [], CancellationToken.None);
                using var ownership = Assert.IsType<RetainedStateOwnership>(owned.Value);
                Assert.True(PublicationRecoveryPersistence.TryCreateIntentWrite(candidate, fixture.Time.UnixSeconds,
                    prepared.Header.LogicalExpiresAtUnixSeconds, out _, out var request));
                var attemptResult = await RestrictedStateService.PrepareRetainedOpaqueWriteAsync(
                    context, ownership, request!, CancellationToken.None);
                using var attempt = Assert.IsType<RetainedStateOpaqueWriteAttempt>(attemptResult.Value);
                var written = await RestrictedStateService.PersistPreparedRetainedOpaqueWriteAsync(
                    context, attempt, CancellationToken.None);
                using var record = Assert.IsType<RetainedStateOpaqueRecord>(written.Value);
            }

            fixture = await RetainedStateTransactionEndToEndTests.RestoreFixtureAsync(fixture);
            using var restoredSupport = fixture.Support;
            using var restored = fixture.Context;
            var publisher = new FakePublisherTransportFactory();
            publisher.Transport.Enqueue();
            using var evaluation = await new PublicationRecoveryService(new StickyCommentPublisher(publisher))
                .ClassifyBeforeProviderAsync(fixture.Launch.Inputs.GitHubToken!, fixture.Invocation,
                    fixture.PublicationScope, restored, CancellationToken.None);
            Assert.Equal(PublicationRecoveryAction.ResumeAnchoredWrite, evaluation.Decision.Action);
            Assert.False(evaluation.Decision.AllowsProvider);
            var uploads = fixture.Store.UploadCalls;
            var deletes = fixture.Store.DeleteCalls;
            if (interruptCandidateRead) fixture.Store.ListComplete = false;

            var resumed = await PublicationRecoveryService.ResumeInterruptedWriteAsync(
                restored, evaluation, CancellationToken.None, trace);
            using var intent = resumed.Value;
            Assert.Equal(!interruptCandidateRead, resumed.Succeeded);
            Assert.Equal(0, publisher.Transport.Creates);
            Assert.Equal(0, publisher.Transport.Updates);
            return (resumed.Code, fixture.Store.UploadCalls - uploads, fixture.Store.DeleteCalls - deletes);
        }

        var trace = new StateConflictTrace();
        var traced = await RunAsync(trace);
        Assert.Equal(await RunAsync(null), traced);
        Assert.Equal(interruptCandidateRead ? StateConflictStage.AnchoredCandidate : StateConflictStage.Unclassified,
            trace.Read().Stage);
        Assert.Equal(interruptCandidateRead ? 0 : 2, traced.Deletes);
    }

    private static async Task<RetainedStatePersistedCandidate> PersistSyntheticCandidateAsync(
        RetainedStateTransactionEndToEndTests.TransactionFixture fixture)
    {
        var run = await RetainedStateTransactionEndToEndTests.CompleteRunAsync(fixture);
        Assert.True(R4PreparedPublication.TryCreate(run.Outcome, fixture.PublicationScope, out var publication));
        var prepared = await SolusTransactionTestSupport.PrepareAsync(fixture.Context, publication!, CancellationToken.None);
        var candidate = Assert.IsType<RetainedStatePreparedCandidate>(prepared.Value);
        try
        {
            var persisted = await RestrictedStateService.PersistRetainedCandidateAsync(fixture.Context, candidate, CancellationToken.None);
            return Assert.IsType<RetainedStatePersistedCandidate>(persisted.Value);
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
    }

    [Theory]
    [InlineData((int)StateConflictStage.PublicationOwnership, RetainedStateTransactionCodes.Conflict, (int)StateConflictReason.Conflict)]
    [InlineData((int)StateConflictStage.CandidatePersistence, RetainedStateTransactionCodes.RetentionFailed, (int)StateConflictReason.RetentionFailed)]
    [InlineData((int)StateConflictStage.AcceptancePersistence, RetainedStateTransactionCodes.CleanupDebt, (int)StateConflictReason.CleanupFailed)]
    public void LateStateTraceKeepsTheFirstTypedTransactionRefusal(
        int stage, string transactionCode, int reason)
    {
        var trace = new StateConflictTrace();
        trace.RecordCode((StateConflictStage)stage, transactionCode);
        trace.Record(StateConflictStage.RecoveryDecision, StateConflictReason.Conflict);
        Assert.Equal(((StateConflictStage)stage, (StateConflictReason)reason), trace.Read());
    }

    [Fact]
    public void SelectionCodesAreMappedWithoutPassingThroughText()
    {
        var incompatible = new StateConflictTrace();
        incompatible.RecordCode(StateConflictStage.StateSelection, AcceptedStateCodes.IncompatibleCurrent);
        Assert.Equal((StateConflictStage.StateSelection, StateConflictReason.Incompatible), incompatible.Read());
        var unknown = new StateConflictTrace();
        unknown.RecordCode(StateConflictStage.RestoredSelection, "unexpected private details");
        Assert.Equal((StateConflictStage.RestoredSelection, StateConflictReason.Unknown), unknown.Read());
    }
}
