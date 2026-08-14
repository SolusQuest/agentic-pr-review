using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed class ActionHostCompositionTests
{
    [Fact]
    public async Task MissingGitHubCredentialClosesBeforeAnyLaterProducer()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch,
            includeToken: false);
        var stagingCalls = 0;
        var composition = new ActionHostComposition(Dependencies(
            scenario,
            () =>
            {
                stagingCalls++;
                throw new InvalidOperationException("must not stage");
            }));

        var completion = await composition.RunAsync(
            scenario.Launch,
            CancellationToken.None);

        Assert.Equal(ActionHostStatus.CredentialsMissing, completion.Status);
        Assert.Equal(
            ActionHostStateDisposition.NotCommitted,
            completion.Summary.StateDisposition);
        Assert.Equal(0, scenario.Factory.Calls);
        Assert.Equal(0, stagingCalls);
    }

    [Theory]
    [InlineData("fork")]
    [InlineData("draft")]
    [InlineData("closed")]
    public async Task PreservesExactH2SkipAndDoesNotMaterializeLaterLayers(
        string kind)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var pullRequest = scenario.Transport.PullRequest;
        scenario.Transport.PullRequest = kind switch
        {
            "fork" => pullRequest with
            {
                HeadRepository = new(
                    99,
                    "contributor/agentic-pr-review"),
            },
            "draft" => pullRequest with { Draft = true },
            _ => pullRequest with { State = "closed" },
        };
        var stagingCalls = 0;
        var composition = new ActionHostComposition(Dependencies(
            scenario,
            () =>
            {
                stagingCalls++;
                throw new InvalidOperationException("must not stage");
            }));

        var completion = await composition.RunAsync(
            scenario.Launch,
            CancellationToken.None);

        var expected = kind switch
        {
            "fork" => ActionHostStatus.SkippedFork,
            "draft" => ActionHostStatus.SkippedDraft,
            _ => ActionHostStatus.SkippedClosed,
        };
        Assert.Equal(expected, completion.Status);
        Assert.Equal(
            ActionHostStateDisposition.NotAccessed,
            completion.Summary.StateDisposition);
        Assert.Equal(0, stagingCalls);
    }

    private static ActionHostCompositionDependencies Dependencies(
        ActionHostAuthorizationScenario scenario,
        Func<string> stagingParentFactory)
    {
        var github = new ActionHostGitHubAuthorizationTransportFactory();
        return new ActionHostCompositionDependencies(
            scenario.EventReader,
            scenario.Factory,
            github,
            github,
            new AcceptedStateProductionDependencies(),
            new BoundedGitHubPublisherTransportFactory(),
            new ActionHostDeepSeekProviderRunnerFactory(),
            TimeProvider.System,
            stagingParentFactory);
    }
}
