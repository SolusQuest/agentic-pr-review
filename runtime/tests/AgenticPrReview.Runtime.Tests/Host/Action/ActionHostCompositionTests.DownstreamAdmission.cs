using System.Text;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed partial class ActionHostCompositionTests
{
    [Theory]
    [InlineData("ref")]
    [InlineData("permissions")]
    [InlineData("environment")]
    [InlineData("concurrency")]
    [InlineData("policy-source")]
    [InlineData("candidate")]
    [InlineData("reset-input")]
    [InlineData("fork")]
    [InlineData("draft")]
    public async Task DownstreamDenialPrecedesAllLaterOwners(string mutation)
    {
        var test = DownstreamAuthorizationScenario.Create(candidate: mutation == "candidate",
            reset: mutation == "reset-input", eventStateMode: "auto");
        var text = Encoding.UTF8.GetString(test.Scenario.Transport.Source.Bytes);
        var replacement = mutation switch
        {
            "ref" => ("@" + test.Launch.ActionSourceSha, "@main"),
            "permissions" => ("contents: read", "contents: write"),
            "environment" => ("    steps:\n", "    environment: production\n    steps:\n"),
            "concurrency" => ("cancel-in-progress: false", "cancel-in-progress: true"),
            "policy-source" => ("config-path: .github/agentic-pr-review.json", "config-path: policy.json"),
            _ => (string.Empty, string.Empty),
        };
        if (replacement.Item1.Length > 0) test.SetSource(text.Replace(replacement.Item1, replacement.Item2, StringComparison.Ordinal));
        if (mutation == "fork") test.Scenario.Transport.PullRequest = test.Scenario.Transport.PullRequest with {
            HeadRepository = new(99, "attacker/repo") };
        if (mutation == "draft") test.Scenario.Transport.PullRequest = test.Scenario.Transport.PullRequest with { Draft = true };
        var launch = FullLaunch(test.Launch);
        var github = new FullPathGitHubFactory(test.Scenario.Transport.PullRequest);
        var store = FullPathStore(launch);
        var provider = new FullPathProviderFactory();
        var publisher = SuccessfulPublisher(345);
        var stagingCalls = 0;
        var host = new ActionHostComposition(new ActionHostCompositionDependencies(test.Scenario.EventReader,
            test.Scenario.Factory, github, github, new FullPathStateDependencies(store, github), publisher, provider,
            new FrozenLocatorTimeProvider(LocatorTestData.Now), () => {
                stagingCalls++;
                throw new InvalidOperationException("Authorization denial must not stage reviewed content.");
            }));

        var result = await host.RunAsync(launch, default);

        Assert.Equal(mutation switch {
            "fork" => ActionHostStatus.SkippedFork,
            "draft" => ActionHostStatus.SkippedDraft,
            _ => ActionHostStatus.AuthorizationFailed,
        }, result.Status);
        Assert.Equal(mutation is "fork" or "draft" ? ActionHostStateDisposition.NotAccessed :
            ActionHostStateDisposition.NotCommitted, result.Summary.StateDisposition);
        Assert.True(result.Accounting.IsCompleteZero);
        Assert.Equal(ActionHostTerminationReason.NotStarted, result.TerminationReason);
        Assert.Equal(0, stagingCalls);
        Assert.Equal(0, github.CurrentPullRequestCalls);
        Assert.Equal(0, store.ListCalls);
        Assert.Equal(0, store.UploadCalls);
        Assert.Empty(store.Objects);
        Assert.Equal(0, provider.Creates);
        Assert.Empty(publisher.Transport.Bodies);
    }
}
