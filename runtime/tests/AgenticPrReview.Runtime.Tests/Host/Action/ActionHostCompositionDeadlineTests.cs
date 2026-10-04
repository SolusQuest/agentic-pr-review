using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Tests.Agent.Loop;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed partial class ActionHostCompositionTests
{
    [Fact]
    public async Task ProductionCompositionCancelsInFlightStateAtPreStickyDeadline()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        var github = new FullPathGitHubFactory(scenario.Transport.PullRequest);
        var clock = new DeadlineTestClock(LocatorTestData.Now);
        var store = new DeadlineBlockingStore();
        var provider = new FullPathProviderFactory();
        var publisher = new FakePublisherTransportFactory();
        var staging = StagingPath();
        var run = new ActionHostComposition(new ActionHostCompositionDependencies(
            scenario.EventReader, scenario.Factory, github, github,
            new FullPathStateDependencies(store, github), publisher, provider, clock, () => staging))
            .RunAsync(FullLaunch(scenario.Launch), default);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromMinutes(24));
        var completion = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(store.Observed.IsCancellationRequested);
        Assert.Equal(ActionHostStatus.Cancelled, completion.Status);
        Assert.Equal(0, provider.Creates);
        Assert.Empty(publisher.Transport.Bodies);
        Assert.False(Directory.Exists(staging));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(23)]
    public async Task ProductionStateDelayConsumesAgentHeadroomAndStillCoversLateAcceptance(int minutes)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = FullLaunch(scenario.Launch);
        var github = new FullPathGitHubFactory(scenario.Transport.PullRequest);
        var clock = new DeadlineTestClock(LocatorTestData.Now);
        var store = FullPathStore(launch);
        store.BeforeList = (_, call) => { if (call == 1) clock.Advance(TimeSpan.FromMinutes(minutes)); };
        var provider = new FullPathProviderFactory();
        var publisher = SuccessfulPublisher(77);
        var staging = StagingPath();
        var completion = await new ActionHostComposition(new ActionHostCompositionDependencies(
            scenario.EventReader, scenario.Factory, github, github,
            new FullPathStateDependencies(store, github), publisher, provider, clock, () => staging))
            .RunAsync(launch, default);
        var request = Assert.Single(provider.Requests);
        Assert.Equal(TimeSpan.FromMinutes(24 - minutes), request.RemainingHostTime);
        Assert.Equal(ActionHostStateDisposition.Accepted, completion.Summary.StateDisposition);
        Assert.False(Directory.Exists(staging));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    public async Task PublicationUsesIndependentWindowAndStopsAcceptanceAtExpiry(int minutes, bool accepted)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = FullLaunch(scenario.Launch);
        var github = new FullPathGitHubFactory(scenario.Transport.PullRequest);
        var clock = new DeadlineTestClock(LocatorTestData.Now);
        var store = FullPathStore(launch);
        store.BeforeList = (_, call) => { if (call == 1) clock.Advance(TimeSpan.FromMinutes(23)); };
        var publisher = SuccessfulPublisher(78);
        var mutate = publisher.Transport.OnMutation!;
        publisher.Transport.OnMutation = () => { mutate(); clock.Advance(TimeSpan.FromMinutes(minutes)); };
        var completion = await new ActionHostComposition(new ActionHostCompositionDependencies(
            scenario.EventReader, scenario.Factory, github, github,
            new FullPathStateDependencies(store, github), publisher, new FullPathProviderFactory(), clock, StagingPath))
            .RunAsync(launch, default);
        Assert.Single(publisher.Transport.Bodies);
        Assert.Equal(accepted, completion.Summary.StateDisposition == ActionHostStateDisposition.Accepted);
        // A known committed sticky write without accepted state preserves the
        // existing cancellation result; it must not become a clean review.
        Assert.Equal(accepted ? ActionHostStatus.Reviewed : ActionHostStatus.InternalFailure, completion.Status);
    }

    [Theory]
    [InlineData(239, true)]
    [InlineData(240, false)]
    public async Task PriorAcceptanceRecoveryHasFourMinutesFromSelection(int seconds, bool accepted)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = FullLaunch(scenario.Launch);
        var github = new FullPathGitHubFactory(scenario.Transport.PullRequest);
        var store = FullPathStore(launch);
        var publisher = SuccessfulPublisher(79);
        var provider = new FullPathProviderFactory();
        var armed = false;
        store.AfterUpload = (_, call) =>
        {
            if (armed || publisher.Transport.Bodies.Count == 0) return;
            armed = true;
            store.FailUploadOnUploadCall = call + 1;
            store.ScheduledUploadFailure = OpaqueStoreFailure.OutcomeUnknown;
            store.ScheduledUploadMutationState = OpaqueStoreMutationState.OutcomeUnknown;
            store.HideFailedUploadForNextLists = 128;
        };
        var first = await new ActionHostComposition(new ActionHostCompositionDependencies(
            scenario.EventReader, scenario.Factory, github, github,
            new FullPathStateDependencies(store, github), publisher, provider,
            new FrozenLocatorTimeProvider(LocatorTestData.Now), StagingPath)).RunAsync(launch, default);
        Assert.True(armed);
        Assert.Equal(ActionHostStatus.OutcomeAmbiguous, first.Status);
        var clock = new DeadlineTestClock(LocatorTestData.Now);
        var advanced = false;
        store.HideNextUploadedObjectForNextLists = 0;
        store.AfterUpload = (_, _) =>
        {
            if (advanced) return;
            advanced = true;
            clock.Advance(TimeSpan.FromSeconds(seconds));
        };
        var resumed = await new ActionHostComposition(new ActionHostCompositionDependencies(
            scenario.EventReader, scenario.Factory, github, github,
            new FullPathStateDependencies(store, github), publisher, provider, clock, StagingPath))
            .RunAsync(WithoutProviderKey(launch), default);
        Assert.True(advanced);
        Assert.Equal(accepted, resumed.Summary.StateDisposition == ActionHostStateDisposition.Accepted);
        Assert.Equal(1, provider.Runs);
        Assert.Single(publisher.Transport.Bodies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingStateClassificationCancellationPreservesConflictWithoutNewWrites(bool callerCancellation)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = FullLaunch(scenario.Launch);
        var github = new FullPathGitHubFactory(scenario.Transport.PullRequest);
        var store = FullPathStore(launch);
        var publisher = SuccessfulPublisher(80);
        var provider = new FullPathProviderFactory();
        var first = await new ActionHostComposition(new ActionHostCompositionDependencies(
            scenario.EventReader, scenario.Factory, github, github,
            new FullPathStateDependencies(store, github), publisher, provider,
            new FrozenLocatorTimeProvider(LocatorTestData.Now), StagingPath)).RunAsync(launch, default);
        Assert.Equal(ActionHostStatus.Reviewed, first.Status);
        var uploads = store.UploadCalls;
        var clock = new DeadlineTestClock(LocatorTestData.Now);
        using var caller = new CancellationTokenSource();
        CancellationToken observed = default;
        publisher.Transport.OnList = () =>
        {
            observed = publisher.Transport.ListCancellationTokens[^1];
            if (callerCancellation) caller.Cancel();
            else clock.Advance(TimeSpan.FromMinutes(24));
        };
        var resumed = await new ActionHostComposition(new ActionHostCompositionDependencies(
            scenario.EventReader, scenario.Factory, github, github,
            new FullPathStateDependencies(store, github), publisher, provider, clock, StagingPath))
            .RunAsync(WithoutProviderKey(launch), caller.Token);
        Assert.True(observed.IsCancellationRequested);
        Assert.Equal(ActionHostStatus.StateConflict, resumed.Status);
        Assert.Equal(uploads, store.UploadCalls);
        Assert.Equal(1, provider.Runs);
        Assert.Single(publisher.Transport.Bodies);
    }

    private sealed class DeadlineBlockingStore : IRestrictedStateStore
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Observed { get; private set; }
        public async Task<OpaqueStoreListResult> ListExactAsync(OpaqueStoreListRequest request, CancellationToken token)
        {
            Observed = token;
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        }
        public Task<OpaqueStoreMetadataResult> ReadMetadataAsync(OpaqueStoreMetadataRequest request, CancellationToken token) => throw new InvalidOperationException();
        public Task<OpaqueStoreDownloadResult> DownloadAsync(OpaqueStoreDownloadRequest request, CancellationToken token) => throw new InvalidOperationException();
        public Task<OpaqueStoreUploadResult> UploadImmutableAsync(OpaqueStoreUploadRequest request, CancellationToken token) => throw new InvalidOperationException();
        public Task<OpaqueStoreReadBackResult> ReadBackExactAsync(OpaqueStoreReadBackRequest request, CancellationToken token) => throw new InvalidOperationException();
        public Task<OpaqueStoreDeleteResult> DeleteExactAsync(OpaqueStoreDeleteRequest request, CancellationToken token) => throw new InvalidOperationException();
    }
}
