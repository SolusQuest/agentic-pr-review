using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Recovery;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.Tests.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Tests.Host.State.Transactions;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
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
