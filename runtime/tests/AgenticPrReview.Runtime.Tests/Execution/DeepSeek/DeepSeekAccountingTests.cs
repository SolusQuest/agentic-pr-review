using System.Net;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.Tests.Execution.DeepSeek;

public sealed class DeepSeekAccountingTests
{
    private const string FullUsage = """{"prompt_tokens":10,"completion_tokens":3,"total_tokens":13,"prompt_cache_hit_tokens":7,"prompt_cache_miss_tokens":3}""";
    private static readonly ReviewedIdentity Identity = new("repo", 1, new string('0', 40), new string('1', 40));

    [Fact]
    public async Task MeasuredZeroIsCompleteRatherThanUnavailable()
    {
        var zero = FullUsage.Replace(":10", ":0", StringComparison.Ordinal)
            .Replace(":13", ":0", StringComparison.Ordinal)
            .Replace(":7", ":0", StringComparison.Ordinal)
            .Replace(":3", ":0", StringComparison.Ordinal);
        using var handler = new Handler((_, _) => Task.FromResult(Http(Body(zero))));
        using var transport = Transport(handler);
        var outcome = await Loop(transport).RunAsync(Run(), default);
        Assert.True(outcome.Succeeded);
        Assert.Equal(1, outcome.Accounting!.ProviderAttempts);
        Assert.Equal(0, outcome.Accounting.InputTokens);
        Assert.Equal(0, outcome.Accounting.UnknownUsageAttempts);
        Assert.Equal(AccountingCompleteness.Complete, outcome.Accounting.UsageCompleteness);
    }

    [Fact]
    public async Task ActualTransportLoopReturnsAccountingOnSuccessAndKeepsRunsSeparate()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Http(Body())));
        using var transport = Transport(handler);
        var loop = Loop(transport);
        for (var index = 0; index < 2; index++)
        {
            var outcome = await loop.RunAsync(Run(), default);
            Assert.True(outcome.Succeeded, outcome.Diagnostic?.ToString());
            Assert.True(outcome.CompletedSessionEligible);
            var accounting = Assert.IsType<ProviderAccounting>(outcome.Accounting);
            Assert.Equal(1, accounting.ModelCalls);
            Assert.Equal(1, accounting.ProviderAttempts);
            Assert.Equal(0, accounting.ProviderFailedAttempts);
            Assert.Equal(0, accounting.ProviderRetries);
            Assert.Equal(10, accounting.InputTokens);
            Assert.Equal(3, accounting.OutputTokens);
            Assert.Equal(7, accounting.CacheHitTokens);
            Assert.Equal(3, accounting.CacheMissTokens);
            Assert.Equal(AccountingCompleteness.Complete, accounting.UsageCompleteness);
            Assert.Equal(AccountingCompleteness.Complete, accounting.AttemptCompleteness);
        }
        Assert.Equal(2, handler.Sends);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("finish")]
    [InlineData("choice")]
    [InlineData("message")]
    [InlineData("missing-tool")]
    [InlineData("terminal")]
    public async Task LaterValidationFailureRetainsKnownUsageExactlyOnce(string rejection)
    {
        var body = Body();
        body = rejection switch
        {
            "model" => body.Replace("deepseek-flash", "wrong-model", StringComparison.Ordinal),
            "finish" => body.Replace("\"finish_reason\":\"tool_calls\"", "\"finish_reason\":\"length\"", StringComparison.Ordinal),
            "choice" => body.Replace("\"index\":0", "\"index\":2", StringComparison.Ordinal),
            "message" => body.Replace("\"role\":\"assistant\"", "\"role\":\"user\"", StringComparison.Ordinal),
            "missing-tool" => Body(calls: "[]"),
            "terminal" => Body(arguments: """{"summary":"clean","findings":[{}]}"""),
            _ => body,
        };
        using var handler = new Handler((_, _) => Task.FromResult(Http(body)));
        using var transport = Transport(handler);
        var outcome = await Loop(transport).RunAsync(Run(), default);
        Assert.False(outcome.Succeeded);
        Assert.False(outcome.CompletedSessionEligible);
        Assert.Null(outcome.Continuation);
        var accounting = outcome.Accounting!;
        Assert.Equal(1, handler.Sends);
        Assert.Equal(1, accounting.ProviderAttempts);
        Assert.Equal(rejection == "terminal" ? 0 : 1, accounting.ProviderFailedAttempts);
        Assert.Equal(10, accounting.InputTokens);
        Assert.Equal(3, accounting.OutputTokens);
        Assert.Equal(0, accounting.UnknownUsageAttempts);
        Assert.Equal(AccountingCompleteness.Complete, accounting.UsageCompleteness);
    }

    [Theory]
    [InlineData("null", 0, 0, 0, 0, 1, 0, AccountingCompleteness.Unavailable)]
    [InlineData("{}", 0, 0, 0, 0, 1, 0, AccountingCompleteness.Unavailable)]
    [InlineData("{\"prompt_tokens\":10,\"completion_tokens\":3}", 10, 3, 0, 0, 0, 1, AccountingCompleteness.Partial)]
    [InlineData("{\"prompt_tokens\":10,\"completion_tokens\":3,\"prompt_cache_hit_tokens\":7}", 10, 3, 7, 0, 0, 1, AccountingCompleteness.Partial)]
    [InlineData("{\"prompt_tokens\":10,\"completion_tokens\":3,\"prompt_cache_hit_tokens\":8,\"prompt_cache_miss_tokens\":8}", 10, 3, 0, 0, 0, 1, AccountingCompleteness.Partial)]
    [InlineData("{\"prompt_tokens\":10}", 10, 0, 0, 0, 1, 1, AccountingCompleteness.Partial)]
    [InlineData("{\"prompt_tokens\":-1,\"completion_tokens\":3}", 0, 3, 0, 0, 1, 0, AccountingCompleteness.Partial)]
    [InlineData("{\"prompt_tokens\":1.5,\"completion_tokens\":3}", 0, 3, 0, 0, 1, 0, AccountingCompleteness.Partial)]
    [InlineData("{\"prompt_tokens\":9223372036854775808,\"completion_tokens\":3}", 0, 3, 0, 0, 1, 0, AccountingCompleteness.Partial)]
    [InlineData("{\"prompt_tokens\":10,\"completion_tokens\":3,\"total_tokens\":12}", 0, 0, 0, 0, 1, 0, AccountingCompleteness.Unavailable)]
    public async Task PartialUsageIsExplicitEvenWhenCurrentResponseAdmissionRejectsIt(
        string usage, long input, long output, long hit, long miss,
        int unknown, int unknownCache, object completeness)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Http(Body(usage))));
        using var transport = Transport(handler);
        var outcome = await Loop(transport).RunAsync(Run(), default);
        Assert.False(outcome.Succeeded);
        var result = outcome.Accounting!;
        Assert.Equal(1, result.ProviderAttempts);
        Assert.Equal(1, result.ProviderFailedAttempts);
        Assert.Equal(input, result.InputTokens);
        Assert.Equal(output, result.OutputTokens);
        Assert.Equal(hit, result.CacheHitTokens);
        Assert.Equal(miss, result.CacheMissTokens);
        Assert.Equal(unknown, result.UnknownUsageAttempts);
        Assert.Equal(unknownCache, result.UnknownCachePartitionAttempts);
        Assert.Equal((AccountingCompleteness)completeness, result.UsageCompleteness);
        Assert.Equal(AccountingCompleteness.Complete, result.AttemptCompleteness);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("exception")]
    [InlineData("timeout")]
    [InlineData("oversize")]
    [InlineData("json")]
    [InlineData("duplicate")]
    public async Task FailedSendAndInvalidBodyNeverBecomeKnownZero(string failure)
    {
        using var handler = new Handler(async (_, cancellation) =>
        {
            if (failure == "exception") throw new HttpRequestException("secret-error-canary");
            if (failure == "timeout") await Task.Delay(Timeout.Infinite, cancellation);
            return failure switch
            {
                "http" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                "oversize" => Http(new string('x', AgentLimits.ResponseBytes + 1)),
                "json" => Http("{"),
                "duplicate" => Http(Body().Replace("\"prompt_tokens\":10", "\"prompt_tokens\":10,\"prompt_tokens\":10", StringComparison.Ordinal)),
                _ => Http(Body()),
            };
        });
        using var transport = Transport(handler, failure == "timeout" ? TimeSpan.FromMilliseconds(20) : null);
        var outcome = await Loop(transport).RunAsync(Run(), default);
        Assert.False(outcome.Succeeded);
        Assert.Equal(1, handler.Sends);
        Assert.Equal(1, outcome.Accounting!.ProviderAttempts);
        Assert.Equal(1, outcome.Accounting.ProviderFailedAttempts);
        Assert.Equal(1, outcome.Accounting.UnknownUsageAttempts);
        Assert.Equal(AccountingCompleteness.Unavailable, outcome.Accounting.UsageCompleteness);
        Assert.DoesNotContain("secret-error-canary", outcome.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransportLocalRejectionAndFrozenPermissionNeverDispatch()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Http(Body())));
        using var transport = Transport(handler);
        var tooLarge = new ProviderAttemptCapture(0, 0);
        await transport.SendAsync(new byte[AgentLimits.RequestBytes + 1], default, tooLarge);
        Assert.False(tooLarge.Freeze(true, false).Dispatched);
        var cancelled = new ProviderAttemptCapture(0, 0);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.SendAsync(
            "{}"u8.ToArray(), new CancellationToken(true), cancelled));
        Assert.False(cancelled.Freeze(true, false).Dispatched);
        var frozen = new ProviderAttemptCapture(0, 0);
        frozen.ObserveNoDispatch();
        frozen.Freeze(false, false);
        await transport.SendAsync("{}"u8.ToArray(), default, frozen);
        transport.Dispose();
        var disposed = new ProviderAttemptCapture(0, 0);
        await transport.SendAsync("{}"u8.ToArray(), default, disposed);
        Assert.False(disposed.Freeze(true, false).Dispatched);
        Assert.Equal(0, handler.Sends);
    }

    [Fact]
    public async Task LocalAdapterDenialIsOneLogicalCallAndNoProviderFailure()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Http(Body())));
        using var transport = Transport(handler);
        var badContext = new DeepSeekAdapterContext("wrong", DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Adapter, "session");
        var loop = new AgentLoop(DeepSeekChatBackend.CreateClient(badContext, transport), new NoTools());
        var outcome = await loop.RunAsync(Run(), default);
        Assert.False(outcome.Succeeded);
        Assert.Equal(1, outcome.Accounting!.ModelCalls);
        Assert.Equal(0, outcome.Accounting.ProviderAttempts);
        Assert.Equal(0, outcome.Accounting.ProviderFailedAttempts);
        Assert.Equal(AccountingCompleteness.Complete, outcome.Accounting.UsageCompleteness);
        Assert.Equal(0, handler.Sends);

        var cancelled = await Loop(transport).RunAsync(Run(), new CancellationToken(true));
        Assert.Equal(0, cancelled.Accounting!.ModelCalls);
        Assert.Equal(AccountingCompleteness.Complete, cancelled.Accounting.AttemptCompleteness);
        Assert.Equal(AccountingCompleteness.Complete, cancelled.Accounting.UsageCompleteness);
    }

    [Fact]
    public async Task PendingDispatchCancellationReturnsImmutablePartialAccounting()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler(async (_, _) =>
        {
            started.SetResult();
            await release.Task;
            return Http(Body());
        });
        using var transport = Transport(handler);
        var running = Loop(transport).RunAsync(Run(), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var outcome = await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(outcome.Succeeded);
        var snapshot = outcome.Accounting!;
        Assert.Equal(1, snapshot.ProviderAttempts);
        Assert.Equal(1, snapshot.ProviderFailedAttempts);
        Assert.Equal(AccountingCompleteness.Partial, snapshot.AttemptCompleteness);
        Assert.Equal(AccountingCompleteness.Unavailable, snapshot.UsageCompleteness);
        release.SetResult();
        await handler.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, snapshot.InputTokens);
        Assert.Equal(1, handler.Sends);
    }

    [Fact]
    public async Task UninstrumentedTransportIsUnavailableNotCompleteZeroButRetainsUsage()
    {
        using var transport = new UninstrumentedTransport();
        var outcome = await Loop(transport).RunAsync(Run(), default);
        Assert.True(outcome.Succeeded);
        Assert.Equal(0, outcome.Accounting!.ProviderAttempts);
        Assert.Equal(10, outcome.Accounting.InputTokens);
        Assert.Equal(AccountingCompleteness.Unavailable, outcome.Accounting.AttemptCompleteness);
        Assert.Equal(AccountingCompleteness.Partial, outcome.Accounting.UsageCompleteness);
    }

    private static AgentLoop Loop(IDeepSeekTransport transport) => new(
        DeepSeekChatBackend.CreateClient(new(DeepSeekAdapterContext.Provider,
            DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Adapter, "session"), transport), new NoTools());

    private static AgentRunRequest Run() => new(Identity,
        new StableAgentPlan("repo", 1, "workflow", new string('2', 64),
            AgentCanonical.ToolsetSha256(AgentToolRegistry.Definitions), AgentCanonical.LimitsSha256(),
            "build", DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Adapter, null),
        "session", [new("user", [new ProjectTextContent("review")])]);

    private static DeepSeekTransport Transport(HttpMessageHandler handler, TimeSpan? timeout = null) =>
        DeepSeekTransport.CreateForTesting(DeepSeekCredential.Create("secret-accounting-canary"), handler, timeout ?? TimeSpan.FromSeconds(5));

    private static HttpResponseMessage Http(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string Body(string usage = FullUsage, string? calls = null, string? arguments = null)
    {
        arguments ??= """{"summary":"clean","findings":[]}""";
        calls ??= """[{"id":"finish","type":"function","function":{"name":"finish_review","arguments":ARGUMENTS}}]"""
            .Replace("ARGUMENTS", JsonSerializer.Serialize(arguments), StringComparison.Ordinal);
        return """{"model":"deepseek-flash","usage":USAGE,"choices":[{"index":0,"finish_reason":"tool_calls","message":{"role":"assistant","content":"","reasoning_content":"reasoning","tool_calls":CALLS}}]}"""
            .Replace("USAGE", usage, StringComparison.Ordinal).Replace("CALLS", calls, StringComparison.Ordinal);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Sends { get; private set; }
        internal TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            try { return await send(request, cancellationToken); }
            finally { Completed.TrySetResult(); }
        }
    }

    private sealed class NoTools : IAgentToolExecutor
    {
        public string? Preflight(PreparedAgentToolCall call) => null;
        public ValueTask<AgentToolExecution> ExecuteAsync(PreparedAgentToolCall call, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No ordinary tools expected.");
    }

    private sealed class UninstrumentedTransport : IDeepSeekTransport
    {
        public Task<DeepSeekTransportResult> SendAsync(ReadOnlyMemory<byte> requestBody, CancellationToken cancellationToken) =>
            Task.FromResult(DeepSeekTransportResult.Success(Encoding.UTF8.GetBytes(Body())));
        public void Dispose() { }
    }
}
