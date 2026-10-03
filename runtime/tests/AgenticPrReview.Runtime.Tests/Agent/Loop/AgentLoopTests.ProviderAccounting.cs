using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;

namespace AgenticPrReview.Runtime.Tests.Agent.Loop;

public sealed partial class AgentLoopTests
{
    [Fact]
    public async Task AccountingSurvivesMinimalProjectionFailure()
    {
        var backend = new AccountingBackend();
        var result = await new AgentLoop(new MinimalChatClient(backend), new ScriptedToolExecutor())
            .RunAsync(Request(), default);
        AssertFailure(result, AgentFailureCodes.ResponseInvalid);
        Assert.Equal(1, result.Accounting!.ProviderAttempts);
        Assert.Equal(1, result.Accounting.ProviderFailedAttempts);
        Assert.Equal(10, result.Accounting.InputTokens);
        Assert.Equal(3, result.Accounting.OutputTokens);
        Assert.Equal(AccountingCompleteness.Complete, result.Accounting.UsageCompleteness);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleLogicalCallsAggregateKnownPrefixBeforeFailure(bool observeSecond)
    {
        var client = new AccountingClient(observeSecond);
        var result = await new AgentLoop(client, new ScriptedToolExecutor()).RunAsync(Request(), default);
        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Accounting!.ModelCalls);
        Assert.Equal(observeSecond ? 2 : 1, result.Accounting.ProviderAttempts);
        Assert.Equal(observeSecond ? 1 : 0, result.Accounting.ProviderFailedAttempts);
        Assert.Equal(observeSecond ? 1 : 0, result.Accounting.UnknownUsageAttempts);
        Assert.Equal(10, result.Accounting.InputTokens);
        Assert.Equal(3, result.Accounting.OutputTokens);
        Assert.Equal(observeSecond ? AccountingCompleteness.Complete : AccountingCompleteness.Partial,
            result.Accounting.AttemptCompleteness);
        Assert.Equal(AccountingCompleteness.Partial, result.Accounting.UsageCompleteness);
        Assert.Equal(0, result.Accounting.ProviderRetries);
    }

    [Fact]
    public async Task AccountingMetadataDoesNotChangeCanonicalRequestBytes()
    {
        var request = new ProjectChatRequest(Request().InitialMessages, [], null);
        var original = AgentRequestWriter.Write(request);
        var capture = new ProviderAttemptCapture(0, 0);
        Assert.Equal(original, AgentRequestWriter.Write(request with { Accounting = capture }));
        var client = new ScriptedChatClient([Response(TerminalCall("finish", "clean"), 0, 0)]);
        var outcome = await new AgentLoop(client, new ScriptedToolExecutor()).RunAsync(Request(), default);
        Assert.True(outcome.Succeeded);
        Assert.Equal(AccountingCompleteness.Unavailable, outcome.Accounting!.AttemptCompleteness);
        Assert.Equal(AccountingCompleteness.Partial, outcome.Accounting.UsageCompleteness);
        Assert.Equal(0, outcome.Accounting.InputTokens); // measured zero, incomplete dispatch observation
    }

    [Fact]
    public async Task CancellationBeforeAgentAdmissionRetainsAlreadyValidatedUsage()
    {
        using var cancellation = new CancellationTokenSource();
        var result = await new AgentLoop(new CancelAfterUsageClient(cancellation), new ScriptedToolExecutor())
            .RunAsync(Request(), cancellation.Token);
        AssertFailure(result, AgentFailureCodes.Cancelled);
        Assert.Equal(10, result.Accounting!.InputTokens);
        Assert.Equal(1, result.Accounting.ProviderAttempts);
        Assert.Equal(AccountingCompleteness.Complete, result.Accounting.UsageCompleteness);
    }

    [Fact]
    public async Task FreezeBeforeDelayedTransportPreventsAnyLateSend()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new DelayedDispatchClient();
        var running = new AgentLoop(client, new ScriptedToolExecutor()).RunAsync(Request(), cancellation.Token);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var outcome = await running.WaitAsync(TimeSpan.FromSeconds(5));
        client.Release.SetResult();
        await client.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(client.Dispatched);
        Assert.Equal(0, outcome.Accounting!.ProviderAttempts);
        Assert.Equal(0, outcome.Accounting.ProviderFailedAttempts);
        Assert.Equal(AccountingCompleteness.Complete, outcome.Accounting.AttemptCompleteness);
        Assert.Equal(AccountingCompleteness.Complete, outcome.Accounting.UsageCompleteness);
    }

    private sealed class AccountingBackend : IMinimalChatBackend
    {
        public Task<MinimalChatResponse> GetResponseAsync(MinimalChatRequest request, CancellationToken cancellationToken)
        {
            request.Accounting!.TryBeginDispatch();
            return Task.FromResult(new MinimalChatResponse(
                new MinimalChatMessage("assistant", [new MinimalChatContent("invalid-kind", null, null,
                    "private-source-canary", null, null, null, 1, 0)]),
                new MinimalChatUsage(10, 3, new ProjectProviderUsage("provider", "model", "model", 7, 3)), 1));
        }
    }

    private sealed class AccountingClient(bool observeSecond) : IProjectChatClient
    {
        private int calls;
        public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken cancellationToken)
        {
            if (calls++ == 0)
            {
                request.Accounting!.TryBeginDispatch();
                request.Accounting.RecordUsage(ProviderUsageObservation.Create(10, 3, 7, 3));
                return Task.FromResult(Response(new ProjectToolCallContent("read", "read_file", "{\"path\":\"a.txt\"}"), 10, 3));
            }
            if (observeSecond) request.Accounting!.TryBeginDispatch();
            return Task.FromException<ProjectChatResponse>(new IOException("private-error-canary"));
        }
    }

    private sealed class CancelAfterUsageClient(CancellationTokenSource cancellation) : IProjectChatClient
    {
        public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken cancellationToken)
        {
            request.Accounting!.TryBeginDispatch();
            request.Accounting.RecordUsage(ProviderUsageObservation.Create(10, 3, 7, 3));
            cancellation.Cancel();
            return Task.FromResult(Response(TerminalCall("finish", "clean"), 10, 3));
        }
    }

    private sealed class DelayedDispatchClient : IProjectChatClient
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Dispatched { get; private set; }
        public async Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken cancellationToken)
        {
            request.Accounting!.ObserveNoDispatch();
            Started.SetResult();
            await Release.Task;
            Dispatched = request.Accounting.TryBeginDispatch();
            Completed.SetResult();
            return Response(TerminalCall("finish", "clean"), 10, 3);
        }
    }
}
