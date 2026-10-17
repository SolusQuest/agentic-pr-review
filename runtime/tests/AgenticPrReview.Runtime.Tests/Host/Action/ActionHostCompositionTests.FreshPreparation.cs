using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed partial class ActionHostCompositionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitFreshContinuationOwnsLivePreparationWithoutNativeState(bool failCallback)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        var github = new FullPathGitHubFactory(scenario.Transport.PullRequest, withInlineFile: true);
        var state = new FreshDeniedState();
        var provider = new FullPathProviderFactory();
        var publisher = EmptyPublisher();
        var staging = StagingPath();
        SolusFreshHostContext? retainedAuthority = null;
        var called = 0;
        var host = new ActionHostComposition(new ActionHostCompositionDependencies(
            scenario.EventReader, scenario.Factory, github, github, state, publisher, provider,
            new FrozenLocatorTimeProvider(LocatorTestData.Now), () => staging));
        var result = await host.RunPreparedFreshAsync(FullLaunch(scenario.Launch), prepared =>
        {
            called++;
            retainedAuthority = prepared.FreshContext;
            Assert.NotNull(retainedAuthority);
            Assert.True(retainedAuthority.IsLive);
            Assert.True(Directory.Exists(prepared.Snapshot.Snapshot.AbsoluteRoot));
            if (failCallback) throw new InvalidOperationException("FRESH_PRIVATE_EXCEPTION_CANARY");
            return Task.FromResult("completed");
        }, CancellationToken.None);
        Assert.Equal(1, called);
        Assert.Equal(0, state.Calls);
        Assert.Equal(0, provider.Creates);
        Assert.Empty(publisher.Transport.Bodies);
        Assert.False(Directory.Exists(staging));
        Assert.False(retainedAuthority!.IsLive);
        if (failCallback)
        {
            Assert.Null(result.Value);
            Assert.Equal(ActionHostStatus.InternalFailure, result.Failure!.Status);
        }
        else
        {
            Assert.Equal("completed", result.Value);
            Assert.Null(result.Failure);
        }
        Assert.DoesNotContain("CANARY", result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshPreparationCannotBypassAuthorizationOrPreObservedCancellation(bool cancelled)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        if (!cancelled) scenario.Transport.Permission = new("read");
        var github = new FullPathGitHubFactory(scenario.Transport.PullRequest);
        var state = new FreshDeniedState();
        var provider = new FullPathProviderFactory();
        var staging = StagingPath();
        var called = false;
        var host = new ActionHostComposition(new ActionHostCompositionDependencies(
            scenario.EventReader, scenario.Factory, github, github, state, EmptyPublisher(), provider,
            new FrozenLocatorTimeProvider(LocatorTestData.Now), () => staging));
        var result = await host.RunPreparedFreshAsync(FullLaunch(scenario.Launch), _ =>
        { called = true; return Task.FromResult("forbidden"); }, new CancellationToken(cancelled));
        Assert.False(called);
        Assert.Null(result.Value);
        Assert.Equal(cancelled ? ActionHostStatus.Cancelled : ActionHostStatus.AuthorizationFailed, result.Failure!.Status);
        Assert.Equal(0, state.Calls);
        Assert.Equal(0, provider.Creates);
        Assert.False(Directory.Exists(staging));
    }

    private sealed class FreshDeniedState : IAcceptedStateProductionDependencies
    {
        internal int Calls;
        public IRestrictedStateStore CreateArtifactStore(ActionHostLaunchContract launch)
        { Calls++; throw new InvalidOperationException("fresh must not restore"); }
        public IActionHostGitObjectTransport CreateAncestryTransport(ActionHostGitHubToken token)
        { Calls++; throw new InvalidOperationException("fresh must not restore"); }
    }
}
