using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.Tests.Agent.Loop;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed class ActionHostDeadlineTests
{
    [Fact]
    public void FinalizationOwnsItsWindowAfterPreStickyAdmission()
    {
        var clock = new DeadlineTestClock();
        using var budget = new ActionHostTimeBudget(clock, default);
        using var journal = new ActionHostTransactionJournal(ActionHostCancellationState.Active, default, budget);
        clock.Advance(TimeSpan.FromMinutes(23));
        Assert.True(journal.TryBeginBusinessOperation(ActionHostOperationKind.StickyPublication, default, out var scope));
        scope!.Dispose();
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(budget.BusinessToken.IsCancellationRequested);
        Assert.False(budget.ReconciliationToken.IsCancellationRequested);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(budget.ReconciliationToken.IsCancellationRequested);
        Assert.False(journal.TryBeginBusinessOperation(ActionHostOperationKind.Acceptance, default, out _, allowReconciliation: true));
    }

    [Fact]
    public async Task InFlightPreStickyWorkIsCancelledAtTheOriginalBoundary()
    {
        var clock = new DeadlineTestClock();
        using var budget = new ActionHostTimeBudget(clock, default);
        using var journal = new ActionHostTransactionJournal(ActionHostCancellationState.Active, default, budget);
        clock.Advance(TimeSpan.FromMinutes(23));
        Assert.Equal(TimeSpan.FromMinutes(1), journal.RemainingReviewTime);
        var operation = Task.Delay(Timeout.InfiniteTimeSpan, budget.BusinessToken);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.False(journal.TryBeginBusinessOperation(ActionHostOperationKind.Provider, default, out _));
        Assert.False(journal.TryBeginBusinessOperation(ActionHostOperationKind.StickyPublication, default, out _));
        Assert.False(budget.ReconciliationToken.IsCancellationRequested);
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.True(budget.ReconciliationToken.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VisibilityDelayCannotCrossHardHorizonEvenThroughFrozenClock(bool frozen)
    {
        var clock = new DeadlineTestClock();
        using var budget = new ActionHostTimeBudget(clock, default);
        clock.Advance(TimeSpan.FromSeconds(28 * 60 - 2));
        TimeProvider time = frozen ? new FrozenTimeProvider(clock.GetUtcNow().ToUnixTimeSeconds(), budget) : budget;
        var window = new PostUploadVisibilityWindow(time, null, StateReconciliationOwner.Acceptance,
            StateReconciliationOutcome.OutcomeUnknown, StateReconciliationExactReadBack.NotAvailable);
        window.RecordObservation();
        var delay = window.WaitForNextObservationAsync().AsTask();
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(await delay.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, window.Observations);
        Assert.Equal(1, window.ScheduleIndex);
    }

    [Fact]
    public async Task AlreadyDispatchedMutationGetsOneIndependentFourMinuteWindow()
    {
        var clock = new DeadlineTestClock();
        using var caller = new CancellationTokenSource();
        using var budget = new ActionHostTimeBudget(clock, caller.Token);
        using var journal = new ActionHostTransactionJournal(ActionHostCancellationState.Active, caller.Token, budget);
        Assert.True(journal.TryBeginBusinessOperation(ActionHostOperationKind.StateSetup, default, out var scope));
        using (scope!) journal.BeforeMutationDispatch(default);
        caller.Cancel();
        Assert.True(budget.BusinessToken.IsCancellationRequested);
        Assert.False(budget.ReconciliationToken.IsCancellationRequested);
        Assert.Equal(ActionHostStatus.OutcomeAmbiguous, journal.CancellationStatus);
        clock.Advance(TimeSpan.FromSeconds(238));
        budget.BeginFinalization(); // repeated entry cannot restart the window
        var window = new PostUploadVisibilityWindow(budget, null, StateReconciliationOwner.LocatorRoot,
            StateReconciliationOutcome.OutcomeUnknown, StateReconciliationExactReadBack.NotAvailable);
        var waiting = window.WaitForNextObservationAsync().AsTask();
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(journal.TryBeginBusinessOperation(ActionHostOperationKind.Acceptance, default, out _, allowReconciliation: true));
        Assert.Equal(ActionHostStatus.OutcomeAmbiguous, journal.CancellationStatus);
    }

    [Fact]
    public void RetentionHorizonDoesNotRestartAfterSnapshotOrStateWork()
    {
        var clock = new DeadlineTestClock();
        var start = clock.GetUtcNow().ToUnixTimeSeconds();
        using var budget = new ActionHostTimeBudget(clock, default);
        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(start + 28 * 60, budget.LatestAcceptanceUnixSeconds);
        Assert.Equal(TimeSpan.FromMinutes(4), budget.RemainingPreSticky);
    }
}
