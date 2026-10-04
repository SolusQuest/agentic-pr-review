using System.Net;
using System.Text;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Serialization;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Agent.Loop;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed partial class ActionHostCompositionTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("retry")]
    [InlineData("failure")]
    [InlineData("unknown_usage")]
    [InlineData("unknown_partition")]
    [InlineData("model_limit")]
    [InlineData("token_limit")]
    [InlineData("deadline")]
    public async Task ActualAgentHttpAccountingReachesHostWire(string scenario)
    {
        var clock = new DeadlineTestClock(LocatorTestData.Now);
        var result = await RunBudgetHost(scenario == "model_limit" ? "{\"maxModelCalls\":1}" :
            scenario == "token_limit" ? "{\"maxOutputTokens\":1}" :
            scenario == "deadline" ? "{\"timeoutSeconds\":1}" : "{}", (ordinal, token) =>
        {
            if (scenario == "deadline") { clock.Advance(TimeSpan.FromSeconds(1)); token.ThrowIfCancellationRequested(); }
            if (scenario == "failure" || (scenario == "retry" && ordinal == 1))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    { Content = new StringContent("unknown usage") });
            var response = BudgetResponse(ordinal, scenario is not ("model_limit" or "token_limit"));
            if (scenario == "unknown_usage" || scenario == "unknown_partition")
            {
                var body = response.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                body = scenario == "unknown_usage" ? body[..body.IndexOf(",\"usage\"", StringComparison.Ordinal)] + "}" :
                    body.Replace(",\"prompt_cache_hit_tokens\":0,\"prompt_cache_miss_tokens\":1", "");
                response.Content.Dispose(); response.Content = new StringContent(body);
            }
            return Task.FromResult(response);
        }, scenario == "deadline" ? clock : null);
        var expected = result.Provider.Outcome!.Accounting!;
        AssertAccounting(expected, result.Completion.Accounting);
        var reason = scenario switch
        {
            "failure" => ActionHostTerminationReason.ProviderFailure,
            "unknown_usage" or "unknown_partition" => ActionHostTerminationReason.InvalidResult,
            "model_limit" => ActionHostTerminationReason.ModelLimit,
            "token_limit" => ActionHostTerminationReason.TokenLimit,
            "deadline" => ActionHostTerminationReason.DeadlineExceeded,
            _ => ActionHostTerminationReason.ReviewCompleted,
        };
        Assert.Equal(reason, result.Completion.TerminationReason);
        Assert.Equal(scenario == "failure" ? 2 : scenario == "retry" ? 1 : 0, expected.ProviderRetries);
        if (scenario == "unknown_usage") Assert.Equal(1, expected.UnknownUsageAttempts);
        if (scenario == "unknown_partition") Assert.Equal(1, expected.UnknownCachePartitionAttempts);
        AssertWire(result.Completion);
    }

    [Theory]
    [InlineData(0)] // factory failure: provably no invocation
    [InlineData(1)] // escaped RunAsync: no finalizer
    [InlineData(2)] // dispose after real Agent outcome: retain finalized facts
    public async Task ProviderExceptionsDistinguishZeroUnavailableAndRetainedOutcome(int mode)
    {
        var inner = new BudgetRecordingFactory((ordinal, _) => Task.FromResult(BudgetResponse(ordinal, true)),
            new FrozenLocatorTimeProvider(LocatorTestData.Now));
        var provider = new AccountingThrowFactory(inner, mode);
        var result = await RunAccountingComposition(provider);
        Assert.Equal(ActionHostStatus.ProviderFailed, result.Status);
        if (mode == 0)
        {
            Assert.True(result.Accounting.IsCompleteZero);
            Assert.Equal(ActionHostTerminationReason.NotStarted, result.TerminationReason);
        }
        else if (mode == 1)
        {
            Assert.Null(result.Accounting.ModelCalls);
            Assert.Null(result.Accounting.ProviderFailedAttempts);
            Assert.Equal(AccountingCompleteness.Unavailable, result.Accounting.AttemptCompleteness);
            Assert.Equal(ActionHostTerminationReason.ProviderFailure, result.TerminationReason);
        }
        else
        {
            AssertAccounting(inner.Outcome!.Accounting!, result.Accounting);
            Assert.Equal(ActionHostTerminationReason.ReviewCompleted, result.TerminationReason);
            Assert.Equal(1, result.Accounting.ProviderAttempts);
        }
        AssertWire(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OuterExceptionReplacementRetainsFinalizedIncompleteObservations(bool cancelled)
    {
        var provider = new FullPathProviderFactory(withFinding: true);
        var result = await RunAccountingComposition(provider, new AccountingThrowInlineHook(cancelled));
        Assert.NotEqual(ActionHostStatus.Reviewed, result.Status);
        AssertAccounting(provider.LastOutcome!.Accounting!, result.Accounting);
        Assert.Equal(ActionHostTerminationReason.ReviewCompleted, result.TerminationReason);
        Assert.Equal(AccountingCompleteness.Unavailable, result.Accounting.AttemptCompleteness);
        Assert.Equal(AccountingCompleteness.Partial, result.Accounting.UsageCompleteness);
        Assert.Equal(2, result.Accounting.ModelCalls);
        AssertWire(result);
    }

    [Fact]
    public async Task ReconciliationDeadlineReplacementRetainsSuccessfulReviewAccounting()
    {
        var clock = new DeadlineTestClock(LocatorTestData.Now);
        var inner = new BudgetRecordingFactory((ordinal, _) => Task.FromResult(BudgetResponse(ordinal, true)), clock);
        var provider = new AccountingThrowFactory(inner, 2, () => clock.Advance(TimeSpan.FromMinutes(28)));
        var result = await RunAccountingComposition(provider, clock: clock);
        Assert.NotEqual(ActionHostStatus.Reviewed, result.Status);
        AssertAccounting(inner.Outcome!.Accounting!, result.Accounting);
        Assert.Equal(ActionHostTerminationReason.ReviewCompleted, result.TerminationReason);
        Assert.Equal(1, result.Accounting.ProviderAttempts);
        AssertWire(result);
    }

    [Fact]
    public async Task InvalidAdapterFinalizesProvenNoProviderRun()
    {
        // Reuse the production factory while substituting only the typed policy at its seam.
        var result = await RunAccountingComposition(new InvalidAccountingAdapterFactory());
        Assert.Equal(ActionHostStatus.AgentResultInvalid, result.Status);
        Assert.True(result.Accounting.IsCompleteZero);
        Assert.Equal(ActionHostTerminationReason.InvalidResult, result.TerminationReason);
        AssertWire(result);
    }

    private static async Task<ActionHostCompletion> RunAccountingComposition(
        IActionHostProviderRunnerFactory provider, IActionHostPostAcceptanceInlineHook? hook = null, TimeProvider? clock = null)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = FullLaunch(scenario.Launch);
        var github = new FullPathGitHubFactory(scenario.Transport.PullRequest, withInlineFile: hook is not null);
        var store = FullPathStore(launch);
        var staging = StagingPath();
        var result = await new ActionHostComposition(new ActionHostCompositionDependencies(
            scenario.EventReader, scenario.Factory, github, github,
            new FullPathStateDependencies(store, github), SuccessfulPublisher(346), provider,
            clock ?? new FrozenLocatorTimeProvider(LocatorTestData.Now), () => staging, inlineHook: hook))
            .RunAsync(launch, default);
        Assert.False(Directory.Exists(staging));
        return result;
    }

    private static void AssertAccounting(ProviderAccounting expected, ActionHostAccounting actual)
    {
        Assert.Equal(expected.ModelCalls, actual.ModelCalls);
        Assert.Equal(expected.ProviderAttempts, actual.ProviderAttempts);
        Assert.Equal(expected.ProviderRetries, actual.ProviderRetries);
        Assert.Equal(expected.ProviderFailedAttempts, actual.ProviderFailedAttempts);
        Assert.Equal(expected.UnknownUsageAttempts, actual.UnknownUsageAttempts);
        Assert.Equal(expected.UnknownCachePartitionAttempts, actual.UnknownCachePartitionAttempts);
        Assert.Equal(expected.InputTokens, actual.InputTokens);
        Assert.Equal(expected.CacheHitTokens, actual.CacheHitTokens);
        Assert.Equal(expected.CacheMissTokens, actual.CacheMissTokens);
        Assert.Equal(expected.OutputTokens, actual.OutputTokens);
        Assert.Equal(expected.AttemptCompleteness, actual.AttemptCompleteness);
        Assert.Equal(expected.UsageCompleteness, actual.UsageCompleteness);
    }

    private static void AssertWire(ActionHostCompletion result)
    {
        Assert.True(ActionHostJsonCodec.TryWriteCompletion(result, out var wire));
        Assert.True(ActionHostJsonCodec.TryReadCompletion(wire, out var parsed, out _));
        Assert.Equal(result.Accounting.ProviderFailedAttempts, parsed!.Accounting.ProviderFailedAttempts);
        Assert.Equal(result.TerminationReason, parsed.TerminationReason);
        Assert.DoesNotContain("PRIVATE_EXCEPTION_CANARY", Encoding.UTF8.GetString(wire));
    }

    private sealed class AccountingThrowFactory(IActionHostProviderRunnerFactory inner, int mode, System.Action? onDispose = null) : IActionHostProviderRunnerFactory
    {
        public IActionHostProviderRunner Create(ActionHostProviderPolicy policy, ActionHostProviderApiKey key,
            ReviewedSnapshot snapshot, TimeProvider timeProvider)
        {
            if (mode == 0) throw new InvalidOperationException("PRIVATE_EXCEPTION_CANARY");
            return new Runner(inner.Create(policy, key, snapshot, timeProvider), mode, onDispose);
        }
        private sealed class Runner(IActionHostProviderRunner inner, int mode, System.Action? onDispose) : IActionHostProviderRunner
        {
            public Task<AgentRunOutcome> RunAsync(AgentRunRequest run, CancellationToken token) => mode == 1
                ? throw new InvalidOperationException("PRIVATE_EXCEPTION_CANARY") : inner.RunAsync(run, token);
            public void Dispose()
            {
                inner.Dispose();
                onDispose?.Invoke();
                if (mode == 2) throw new InvalidOperationException("PRIVATE_EXCEPTION_CANARY");
            }
        }
    }

    private sealed class InvalidAccountingAdapterFactory : IActionHostProviderRunnerFactory
    {
        public IActionHostProviderRunner Create(ActionHostProviderPolicy policy, ActionHostProviderApiKey key,
            ReviewedSnapshot snapshot, TimeProvider timeProvider) => new ActionHostDeepSeekProviderRunnerFactory()
                .Create(policy with { AdapterId = "" }, key, snapshot, timeProvider);
    }

    private sealed class AccountingThrowInlineHook(bool cancelled) : IActionHostPostAcceptanceInlineHook
    {
        public Task<ActionHostInlineHookResult> PublishAsync(ActionHostCoordinator.PostAcceptanceInlineRequest request,
            CancellationToken token) => cancelled ? throw new OperationCanceledException("PRIVATE_EXCEPTION_CANARY") :
                throw new InvalidOperationException("PRIVATE_EXCEPTION_CANARY");
    }
}
