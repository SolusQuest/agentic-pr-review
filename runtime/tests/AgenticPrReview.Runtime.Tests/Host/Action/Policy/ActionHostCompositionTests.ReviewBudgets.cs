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
    public async Task TrustedConfiguredCallsReachAuthoredTypedScriptAndBothSessionConsumers(int calls, bool finish)
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
            Assert.Equal(1024 - i, result.Provider.OutputCaps[i]);
            Assert.Equal("deepseek-flash", result.Provider.Context!.ModelId);
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
    public async Task TrustedConfiguredPartitionsStopAuthoredTypedScriptAfterObservedUsage(string field, int miss, int hit, int output)
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
    public async Task TrustedConfiguredOutputPreservesGenericRetryAndUnknownUsage(int? failedOutput, int expectedSends)
    {
        var result = await RunBudgetHost("{\"maxOutputTokens\":10}", (ordinal, _) => Task.FromResult(ordinal == 1
            ? new BudgetScriptResult(false, 0, 0, 0, Failed: true, FailedOutput: failedOutput)
            : BudgetResponse(ordinal, true, 0, 0, 1)));
        Assert.Equal(expectedSends, result.Provider.Bodies.Count);
        Assert.Equal(1, result.Provider.Outcome!.Accounting!.ModelCalls);
        Assert.Equal(expectedSends, result.Provider.Outcome.Accounting.ProviderAttempts);
        Assert.Equal(expectedSends - 1, result.Provider.Outcome.Accounting.ProviderRetries);
        Assert.Equal(1, result.Provider.Outcome.Accounting.UnknownUsageAttempts);
        Assert.Equal(10, result.Provider.OutputCaps[0]);
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
    public async Task TrustedConfiguredTimeoutCancelsActualTypedAttempt()
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
        Func<int, CancellationToken, Task<BudgetScriptResult>> respond, TimeProvider? clock = null)
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

    private sealed class BudgetRecordingFactory(Func<int, CancellationToken, Task<BudgetScriptResult>> respond,
        TimeProvider clock) : IActionHostProviderRunnerFactory
    {
        internal List<string> Bodies { get; } = [];
        internal List<int?> OutputCaps { get; } = [];
        internal DeepSeekAdapterContext? Context { get; private set; }
        internal AgentRunRequest? Run { get; private set; }
        internal AgentRunOutcome? Outcome { get; private set; }
        internal ActionHostProviderPolicy? Policy { get; private set; }

        public IActionHostProviderRunner Create(ActionHostProviderPolicy policy, ActionHostProviderApiKey key,
            ReviewedSnapshot snapshot, TimeProvider timeProvider)
        {
            Policy = policy;
            var factory = new ActionHostHistoricalChatRunnerFactory(context =>
            {
                Context = context;
                return new BudgetChat(this, context, respond);
            });
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

    private sealed class BudgetChat(BudgetRecordingFactory owner, DeepSeekAdapterContext context,
        Func<int, CancellationToken, Task<BudgetScriptResult>> respond) : IProjectChatClient
    {
        public async Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.Accounting is { } capture && !capture.TryBeginDispatch()) throw new IOException();
            owner.Bodies.Add(Encoding.UTF8.GetString(AgentRequestWriter.Write(request)));
            owner.OutputCaps.Add(request.MaxOutputTokens);
            var ordinal = owner.Bodies.Count;
            var result = await respond(ordinal, token);
            if (result.Failed)
            {
                // Authored partial usage and generic retry signal; no HTTP response is simulated.
                request.Accounting?.RecordUsage(ProviderUsageObservation.Create(null, result.FailedOutput));
                request.Accounting?.MarkFailed();
                throw new ProjectChatRetryException();
            }
            if (!result.UnknownUsage)
                request.Accounting?.RecordUsage(result.UnknownPartition
                    ? ProviderUsageObservation.Create(result.Miss + result.Hit, result.Output)
                    : ProviderUsageObservation.Create(result.Miss + result.Hit, result.Output,
                        result.Hit, result.Miss, result.Miss + result.Hit + result.Output));
            // These fixed negative scripts preserve the Host's invalid-result accounting path.
            if (result.UnknownUsage || result.UnknownPartition) throw new ProjectChatNormalizationException();
            var position = request.Messages.Length;
            return new(new("assistant", [new ProjectReasoningContent("", "", DeepSeekAdapterContext.ReasoningFraming, null, position, 0),
                new ProjectToolCallContent("budget" + ordinal, result.Terminal ? "finish_review" : "list_files",
                    result.Terminal ? "{\"summary\":\"done\",\"findings\":[]}" : "{}")]),
                result.UnknownUsage ? null : new(result.Miss + result.Hit, result.Output, result.UnknownPartition ? null :
                    new(context.ProviderId, context.ModelId, context.ModelId, result.Hit, result.Miss)),
                CapturedResponseBodyBytes: 0, Continuation: new(context.ProviderId, context.ModelId, context.AdapterId, context.SessionId,
                    [new("", "", DeepSeekAdapterContext.ReasoningFraming, null, position, 0)]));
        }
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
