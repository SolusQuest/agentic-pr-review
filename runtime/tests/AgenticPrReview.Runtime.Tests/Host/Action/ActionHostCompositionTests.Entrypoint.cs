using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Serialization;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed partial class ActionHostCompositionTests
{
    // These invoke the linked library with synthetic outer ports and provider.
    // They do NOT qualify the actual executable's complete successful review.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedEntrypointMapsSuccessfulHostAndPostProviderFailure(bool disposeFailure)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = ActionHostEntrypointTests.WithCurrentBuild(FullLaunch(scenario.Launch));
        var github = new FullPathGitHubFactory(scenario.Transport.PullRequest);
        var store = FullPathStore(launch);
        var staging = StagingPath();
        var clock = new FrozenLocatorTimeProvider(LocatorTestData.Now);
        var inner = new BudgetRecordingFactory((ordinal, _) => Task.FromResult(BudgetResponse(ordinal, true)), clock);
        IActionHostProviderRunnerFactory provider = disposeFailure ? new AccountingThrowFactory(inner, 2) : inner;
        var publisher = SuccessfulPublisher(341);
        var composition = new ActionHostComposition(new ActionHostCompositionDependencies(
            scenario.EventReader, scenario.Factory, github, github,
            new FullPathStateDependencies(store, github), publisher, provider, clock, () => staging));
        Assert.True(ActionHostJsonCodec.TryWriteLaunch(launch, out var bytes));
        using var input = new MemoryStream(ActionHostEntrypointTests.Frame(bytes));
        using var output = new MemoryStream();
        using var stderr = new StringWriter();
        var exit = await RuntimeApplication.RunActionHostAsync(input, output, stderr, default, composition.RunAsync);
        var completion = ActionHostEntrypointTests.ReadCompletion(output);
        Assert.Equal(disposeFailure ? 1 : 0, exit);
        Assert.Equal(exit, completion.ProcessExitCode);
        Assert.Equal(disposeFailure ? ActionHostStatus.ProviderFailed : ActionHostStatus.Reviewed, completion.Status);
        Assert.Equal(disposeFailure ? ActionHostStateDisposition.NotCommitted : ActionHostStateDisposition.Accepted,
            completion.Summary.StateDisposition);
        Assert.Equal(ActionHostTerminationReason.ReviewCompleted, completion.TerminationReason);
        AssertAccounting(inner.Outcome!.Accounting!, completion.Accounting);
        Assert.True(completion.Accounting.ProviderAttempts > 0);
        Assert.Equal(disposeFailure ? 0 : 1, publisher.Transport.Bodies.Count);
        Assert.False(Directory.Exists(staging));
        Assert.Empty(stderr.ToString());
        Assert.DoesNotContain("PRIVATE_EXCEPTION_CANARY", System.Text.Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task LinkedEntrypointFlushFailureCannotReturnSuccessfulHostExit()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = ActionHostEntrypointTests.WithCurrentBuild(FullLaunch(scenario.Launch));
        var github = new FullPathGitHubFactory(scenario.Transport.PullRequest);
        var store = FullPathStore(launch);
        var staging = StagingPath();
        var clock = new FrozenLocatorTimeProvider(LocatorTestData.Now);
        var provider = new BudgetRecordingFactory((ordinal, _) => Task.FromResult(BudgetResponse(ordinal, true)), clock);
        var composition = new ActionHostComposition(new ActionHostCompositionDependencies(
            scenario.EventReader, scenario.Factory, github, github,
            new FullPathStateDependencies(store, github), SuccessfulPublisher(341), provider, clock, () => staging));
        Assert.True(ActionHostJsonCodec.TryWriteLaunch(launch, out var bytes));
        using var input = new MemoryStream(ActionHostEntrypointTests.Frame(bytes));
        using var output = new FlushFailureOutput();
        using var stderr = new StringWriter();
        Assert.Equal(1, await RuntimeApplication.RunActionHostAsync(input, output, stderr, default, composition.RunAsync));
        // The frame was written, but the failed flush must still fail the step.
        Assert.Equal(0, ActionHostEntrypointTests.ReadCompletion(output).ProcessExitCode);
        Assert.Equal(1, output.Flushes);
        Assert.False(Directory.Exists(staging));
        Assert.Equal("APR_ACTION_HOST_INTERNAL" + Environment.NewLine, stderr.ToString());
    }

    private sealed class FlushFailureOutput : MemoryStream
    {
        internal int Flushes { get; private set; }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Flushes++;
            throw new IOException(ActionHostEntrypointTests.PrivateCanary);
        }
    }
}
