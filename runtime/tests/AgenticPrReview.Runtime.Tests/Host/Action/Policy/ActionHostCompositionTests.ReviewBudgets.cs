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
using AgenticPrReview.Runtime.Tests.Agent.Loop;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.Action.Policy;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed partial class ActionHostCompositionTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(1, false)]
    [InlineData(65, true)]
    [InlineData(128, true)]
    [InlineData(128, false)]
    public async Task TrustedConfiguredCallsReachRealProviderAndBothSessionConsumers(int calls, bool finish)
    {
        var result = await RunBudgetHost("{\"maxModelCalls\":" + calls + ",\"maxOutputTokens\":1024,\"timeoutSeconds\":60}",
            (ordinal, _) => Task.FromResult(BudgetResponse(ordinal, finish && ordinal == calls)));
        Assert.Equal(calls, result.Provider.Bodies.Count);
        Assert.Equal(calls, result.Provider.Outcome!.Accounting!.ModelCalls);
        Assert.Equal(calls, result.Provider.Outcome.Accounting.ProviderAttempts);
        Assert.Equal(finish, result.Provider.Outcome.Succeeded);
        Assert.Equal(result.Provider.Policy!.LimitAuthority!.ModelCalls, calls);
        Assert.Equal(AgentCanonical.LimitsSha256(result.Provider.Policy.LimitAuthority), result.Provider.Run!.StablePlan.LimitsSha256);
        Assert.NotEqual(AgentCanonical.LimitsSha256(), result.Provider.Run.StablePlan.LimitsSha256);
        for (var i = 0; i < calls; i++)
        {
            using var json = JsonDocument.Parse(result.Provider.Bodies[i]);
            Assert.Equal(1024 - i, json.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.Equal("deepseek-flash", json.RootElement.GetProperty("model").GetString());
            Assert.DoesNotContain("provider-key", result.Provider.Bodies[i], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("github-token", result.Provider.Bodies[i], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("state-key", result.Provider.Bodies[i], StringComparison.OrdinalIgnoreCase);
        }
        if (finish)
        {
            Assert.Equal(ActionHostStatus.Reviewed, result.Completion.Status);
            Assert.Equal(ActionHostStateDisposition.Accepted, result.Completion.Summary.StateDisposition);
            Assert.True(result.Published);
        }
        else
        {
            Assert.Equal(AgentFailureCodes.ModelLimit, result.Provider.Outcome.Diagnostic!.Code);
            Assert.NotEqual(ActionHostStateDisposition.Accepted, result.Completion.Summary.StateDisposition);
            Assert.False(result.Published);
        }
    }

    [Theory]
    [InlineData("maxUncachedInputTokens", 2, 0, 0)]
    [InlineData("maxCachedInputTokens", 0, 2, 0)]
    [InlineData("maxOutputTokens", 0, 0, 2)]
    public async Task TrustedConfiguredPartitionsStopRealProviderAfterObservedUsage(string field, int miss, int hit, int output)
    {
        var result = await RunBudgetHost("{\"" + field + "\":2}",
            (ordinal, _) => Task.FromResult(BudgetResponse(ordinal, false, miss, hit, output)));
        Assert.Single(result.Provider.Bodies);
        Assert.Equal(AgentFailureCodes.TokenLimit, result.Provider.Outcome!.Diagnostic!.Code);
        Assert.False(result.Published);
        Assert.NotEqual(ActionHostStateDisposition.Accepted, result.Completion.Summary.StateDisposition);
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData(0, 2)]
    [InlineData(1, 1)]
    public async Task TrustedConfiguredOutputPreservesFrozenRetryAndUnknownUsage(int? failedOutput, int expectedSends)
    {
        var result = await RunBudgetHost("{\"maxOutputTokens\":10}", (ordinal, _) => Task.FromResult(ordinal == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(failedOutput.HasValue
                    ? "{\"usage\":{\"completion_tokens\":" + failedOutput.Value + "}}" : "unknown"),
            }
            : BudgetResponse(ordinal, true, 0, 0, 1)));
        Assert.Equal(expectedSends, result.Provider.Bodies.Count);
        Assert.Equal(1, result.Provider.Outcome!.Accounting!.ModelCalls);
        Assert.Equal(expectedSends, result.Provider.Outcome.Accounting.ProviderAttempts);
        Assert.Equal(expectedSends - 1, result.Provider.Outcome.Accounting.ProviderRetries);
        Assert.Equal(1, result.Provider.Outcome.Accounting.UnknownUsageAttempts);
        using var body = JsonDocument.Parse(result.Provider.Bodies[0]);
        Assert.Equal(10, body.RootElement.GetProperty("max_tokens").GetInt32());
        if (expectedSends == 2)
        {
            Assert.Equal(result.Provider.Bodies[0], result.Provider.Bodies[1]);
            Assert.True(result.Provider.Outcome.Succeeded);
            Assert.Equal(ActionHostStateDisposition.Accepted, result.Completion.Summary.StateDisposition);
        }
        else
        {
            Assert.False(result.Provider.Outcome.Succeeded);
            Assert.False(result.Published);
        }
    }

    [Fact]
    public async Task TrustedConfiguredTimeoutCancelsRealPhysicalAttempt()
    {
        var clock = new DeadlineTestClock(LocatorTestData.Now);
        var result = await RunBudgetHost("{\"timeoutSeconds\":1}", (ordinal, token) =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            token.ThrowIfCancellationRequested();
            return Task.FromResult(BudgetResponse(ordinal, true));
        }, clock);
        Assert.Single(result.Provider.Bodies);
        Assert.Equal(AgentFailureCodes.DeadlineExceeded, result.Provider.Outcome!.Diagnostic!.Code);
        Assert.False(result.Published);
        Assert.NotEqual(ActionHostStateDisposition.Accepted, result.Completion.Summary.StateDisposition);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"maxModelCalls\":129}")]
    [InlineData("{\"endpoint\":\"https://untrusted.invalid\"}")]
    public async Task InvalidTrustedReviewStopsBeforeStateOrProvider(string review)
    {
        var result = await RunBudgetHost(review, (ordinal, _) => Task.FromResult(BudgetResponse(ordinal, true)));
        Assert.Empty(result.Provider.Bodies);
        Assert.Null(result.Provider.Run);
        Assert.Equal(0, result.StateOperations);
        Assert.Equal(ActionHostStateDisposition.NotCommitted, result.Completion.Summary.StateDisposition);
        Assert.False(result.Published);
    }

    private static async Task<BudgetHostResult> RunBudgetHost(string review,
        Func<int, CancellationToken, Task<HttpResponseMessage>> respond, TimeProvider? clock = null)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = FullLaunch(scenario.Launch);
        var github = new FullPathGitHubFactory(scenario.Transport.PullRequest);
        var config = new BudgetConfigFactory(github, ActionHostTrustedPolicyTests.ReviewConfig(review));
        var store = FullPathStore(launch);
        var publisher = SuccessfulPublisher(334);
        clock ??= new FrozenLocatorTimeProvider(LocatorTestData.Now);
        var provider = new BudgetRecordingFactory(respond, clock);
        var staging = StagingPath();
        var completion = await new ActionHostComposition(new ActionHostCompositionDependencies(
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

    private static HttpResponseMessage BudgetResponse(int ordinal, bool terminal, int miss = 1, int hit = 0, int output = 1) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = "deepseek-flash",
                choices = new[] { new { index = 0, finish_reason = "tool_calls", message = new
                {
                    role = "assistant", content = (string?)null, reasoning_content = "",
                    tool_calls = new[] { new { id = "budget" + ordinal, type = "function", function = new
                    {
                        name = terminal ? "finish_review" : "list_files",
                        arguments = terminal ? "{\"summary\":\"done\",\"findings\":[]}" : "{}",
                    } } },
                } } },
                usage = new { prompt_tokens = miss + hit, completion_tokens = output, total_tokens = miss + hit + output,
                    prompt_cache_hit_tokens = hit, prompt_cache_miss_tokens = miss },
            })),
        };

    private sealed record BudgetHostResult(ActionHostCompletion Completion, BudgetRecordingFactory Provider, bool Published, int StateOperations);

    private sealed class BudgetRecordingFactory(Func<int, CancellationToken, Task<HttpResponseMessage>> respond,
        TimeProvider clock) : IActionHostProviderRunnerFactory
    {
        internal List<string> Bodies { get; } = [];
        internal AgentRunRequest? Run { get; private set; }
        internal AgentRunOutcome? Outcome { get; private set; }
        internal ActionHostProviderPolicy? Policy { get; private set; }

        public IActionHostProviderRunner Create(ActionHostProviderPolicy policy, ActionHostProviderApiKey key,
            ReviewedSnapshot snapshot, TimeProvider timeProvider)
        {
            Policy = policy;
            var factory = new ActionHostDeepSeekProviderRunnerFactory(credential =>
                DeepSeekTransport.CreateForTesting(credential, () => new BudgetHandler(async (request, token) =>
                {
                    Assert.Equal(DeepSeekTransportPolicy.Endpoint, request.RequestUri!.AbsoluteUri);
                    Bodies.Add(await request.Content!.ReadAsStringAsync(token));
                    return await respond(Bodies.Count, token);
                }), DeepSeekTransportPolicy.ProviderTimeout, clock));
            return new RecordingRunner(this, factory.Create(policy, key, snapshot, timeProvider));
        }

        private sealed class RecordingRunner(BudgetRecordingFactory owner, IActionHostProviderRunner inner) : IActionHostProviderRunner
        {
            public async Task<AgentRunOutcome> RunAsync(AgentRunRequest run, CancellationToken token)
            {
                owner.Run = run;
                return owner.Outcome = await inner.RunAsync(run, token);
            }
            public void Dispose() => inner.Dispose();
        }
    }

    private sealed class BudgetHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
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
