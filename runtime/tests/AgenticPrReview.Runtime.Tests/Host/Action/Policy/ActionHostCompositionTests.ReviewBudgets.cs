using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using AgenticPrReview.Runtime.Agent.Chat;
using System.Net;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.Action.Policy;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed partial class ActionHostCompositionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(65)]
    [InlineData(128)]
    public async Task TrustedConfiguredPolicyReachesFixedAprWorkWithoutNativeExecution(int calls)
    {
        var result = await RunBudgetHost("{\"maxModelCalls\":" + calls + ",\"maxOutputTokens\":1024,\"timeoutSeconds\":60}",
            [BudgetResponse(1, true)]);
        Assert.Single(result.Provider.Bodies);
        Assert.Equal(calls, result.Provider.Policy!.LimitAuthority!.ModelCalls);
        Assert.Equal(1024, result.Provider.Policy.LimitAuthority.TokenBudget!.OutputTokens);
        Assert.Equal(AgentCanonical.LimitsSha256(result.Provider.Policy.LimitAuthority), result.Provider.Run!.StablePlan.LimitsSha256);
        Assert.NotEqual(AgentCanonical.LimitsSha256(), result.Provider.Run.StablePlan.LimitsSha256);
        Assert.True(result.Provider.Outcome!.CompletedSessionEligible);
        Assert.Equal(ActionHostStatus.Reviewed, result.Completion.Status);
        Assert.Equal(ActionHostStateDisposition.Accepted, result.Completion.Summary.StateDisposition);
        Assert.True(result.Published);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"maxModelCalls\":129}")]
    [InlineData("{\"endpoint\":\"https://untrusted.invalid\"}")]
    public async Task InvalidTrustedReviewStopsBeforeStateOrProvider(string review)
    {
        var result = await RunBudgetHost(review, [BudgetResponse(1, true)]);
        Assert.Empty(result.Provider.Bodies);
        Assert.Null(result.Provider.Run);
        Assert.Equal(0, result.StateOperations);
        Assert.Equal(ActionHostStateDisposition.NotCommitted, result.Completion.Summary.StateDisposition);
        Assert.False(result.Published);
    }

    private static async Task<BudgetHostResult> RunBudgetHost(string review,
        BudgetScriptResult[] observations, string? failureCode = null, TimeProvider? clock = null)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = FullLaunch(scenario.Launch);
        var github = new FullPathGitHubFactory(scenario.Transport.PullRequest);
        var config = new BudgetConfigFactory(github, ActionHostTrustedPolicyTests.ReviewConfig(review));
        var store = FullPathStore(launch);
        var publisher = SuccessfulPublisher(334);
        clock ??= new FrozenLocatorTimeProvider(LocatorTestData.Now);
        var provider = new BudgetRecordingFactory(observations, failureCode);
        var staging = StagingPath();
        var completion = await new CurrentHostTestComposition(new ActionHostCompositionDependencies(
            scenario.EventReader, scenario.Factory, config, github,
            new FullPathStateDependencies(store, github), publisher, provider, clock, () => staging))
            .RunAsync(launch, default);
        Assert.False(Directory.Exists(staging));
        foreach (var body in provider.Bodies)
        {
            Assert.DoesNotContain(launch.Inputs.ProviderApiKey!.ExportForPrivateLaunch(), body);
            Assert.DoesNotContain(launch.Inputs.GitHubToken!.ExportForPrivateLaunch(), body);
            Assert.DoesNotContain(launch.Inputs.StateKey!.ExportForPrivateLaunch(), body);
        }
        return new(completion, provider, publisher.Transport.Bodies.Count > 0,
            store.ListCalls + store.UploadCalls + store.DeleteCalls);
    }

    private sealed record BudgetScriptResult(bool Terminal, int Miss, int Hit, int Output, bool Failed = false, int? FailedOutput = null, bool UnknownUsage = false, bool UnknownPartition = false);
    private static BudgetScriptResult BudgetResponse(int ordinal, bool terminal, int miss = 1, int hit = 0, int output = 1) =>
        new(terminal, miss, hit, output);

    private sealed record BudgetHostResult(ActionHostCompletion Completion, BudgetRecordingFactory Provider, bool Published, int StateOperations);

    // Finalized accounting is authored input to the actual Host wire and state
    // boundary. This fixture performs no provider dispatch, retry or budget loop.
    private sealed class BudgetRecordingFactory(BudgetScriptResult[] observations, string? failureCode = null) : IActionHostProviderRunnerFactory
    {
        internal List<string> Bodies { get; } = [];
        internal AgentRunRequest? Run { get; private set; }
        internal AgentRunOutcome? Outcome { get; private set; }
        internal ActionHostProviderPolicy? Policy { get; private set; }
        public IActionHostProviderRunner Create(ActionHostProviderPolicy policy, ActionHostProviderApiKey key,
            ReviewedSnapshot snapshot, TimeProvider timeProvider)
        {
            Policy = policy;
            return new Runner(this, observations, failureCode);
        }
        private sealed class Runner(BudgetRecordingFactory owner, BudgetScriptResult[] observations, string? failureCode) : IActionHostProviderRunner
        {
            public async Task<AgentRunOutcome> RunAsync(AgentRunRequest run, CancellationToken token)
            {
                owner.Run = run;
                owner.Bodies.Add(Encoding.UTF8.GetString(AgentRequestWriter.Write(new(run.InitialMessages, [], null))));
                var facts = new List<ProviderAttemptObservation>();
                for (var index = 0; index < observations.Length; index++)
                {
                    var value = observations[index];
                    var capture = new ProviderAttemptCapture(1, index);
                    // A synthetic finalized dispatch fact, not an observed network send.
                    Assert.True(capture.TryBeginDispatch());
                    if (value.Failed) capture.RecordUsage(ProviderUsageObservation.Create(null, value.FailedOutput));
                    else if (!value.UnknownUsage) capture.RecordUsage(value.UnknownPartition
                        ? ProviderUsageObservation.Create(value.Miss + value.Hit, value.Output)
                        : ProviderUsageObservation.Create(value.Miss + value.Hit, value.Output,
                            value.Hit, value.Miss, value.Miss + value.Hit + value.Output));
                    facts.Add(capture.Freeze(operationCompleted: true, succeeded: !value.Failed));
                }
                var accounting = ProviderAccounting.Aggregate(facts);
                if (failureCode is not null)
                    return owner.Outcome = AgentRunOutcome.Failure(failureCode, 0, 0, []) with { Accounting = accounting };
                var outcome = await AuthoredReviewWork.ExecuteAsync(run, new NeverBudgetTools(), [],
                    _ => AgentToolArguments.WriteFinishReview("done", []), null, null, token, recordAuthoredAccounting: false);
                return owner.Outcome = outcome with { Accounting = accounting };
            }
            public void Dispose() { }
        }
    }
    private sealed class NeverBudgetTools : IAgentToolExecutor
    {
        public string? Preflight(PreparedAgentToolCall call) => throw new InvalidOperationException("no_budget_fixture_tool");
        public ValueTask<AgentToolExecution> ExecuteAsync(PreparedAgentToolCall call, CancellationToken token) => throw new InvalidOperationException("no_budget_fixture_tool");
    }

    private sealed class BudgetConfigFactory(IActionHostGitObjectTransportFactory inner, byte[] config) : IActionHostGitObjectTransportFactory
    {
        public IActionHostGitObjectTransport CreateExactObjectTransport(ActionHostGitHubToken token) => new Transport(inner.CreateExactObjectTransport(token), config);

        private sealed class Transport(IActionHostGitObjectTransport inner, byte[] config) : IActionHostGitObjectTransport
        {
            public Task<ActionHostGitObjectResult<ActionHostGitCommitObject>> GetCommitObjectAsync(string repository, string sha, CancellationToken token) => inner.GetCommitObjectAsync(repository, sha, token);
            public Task<ActionHostGitObjectResult<ActionHostGitTreeObject>> GetTreeObjectAsync(string repository, string sha, CancellationToken token) => inner.GetTreeObjectAsync(repository, sha, token);
            public Task<ActionHostGitObjectResult<ActionHostGitArchiveReader>> GetHeadArchiveAsync(string repository, string sha, CancellationToken token) => inner.GetHeadArchiveAsync(repository, sha, token);
            public Task<ActionHostGitObjectResult<ActionHostGitBlobObject>> GetBlobObjectAsync(string repository, string sha, ActionHostGitBlobReadBudget budget, CancellationToken token) =>
                budget == ActionHostGitBlobReadBudget.TrustedConfig
                    ? Task.FromResult(ActionHostGitObjectResult<ActionHostGitBlobObject>.Success(new(sha, config), config.Length))
                    : inner.GetBlobObjectAsync(repository, sha, budget, token);
            public void Dispose() => inner.Dispose();
        }
    }
}
