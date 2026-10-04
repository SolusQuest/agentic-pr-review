using System.Net;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.Tests.Agent.Loop;

public sealed partial class AgentLoopTests
{
    [Theory]
    [InlineData(null, true)] [InlineData(10L, true)] [InlineData(11L, false)]
    public async Task RealHttpRetryFreezesUsageAndRequestBeforeSingleLogicalCommit(long? failedOutput, bool success)
    {
        var budget = new ReviewTokenBudget(1000, 1000, 65_546);
        var authority = new AgentLimitAuthority(DeepSeekAdapterContext.Adapter, AgentLimitProfile.Current, budget);
        var run = RetryWireRequest(authority);
        var bodies = new List<byte[]>();
        var handlers = new List<RetryHandler>();
        using var transport = DeepSeekTransport.CreateForTesting(DeepSeekCredential.Create("retry-secret-canary"), () =>
        {
            Assert.All(handlers, handler => Assert.True(handler.Disposed));
            var handler = new RetryHandler(async (request, token) =>
            {
                Assert.Equal(DeepSeekTransportPolicy.Endpoint, request.RequestUri!.AbsoluteUri);
                Assert.Equal("Bearer retry-secret-canary", request.Headers.GetValues("Authorization").Single());
                Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
                bodies.Add(await request.Content.ReadAsByteArrayAsync(token));
                return bodies.Count == 1
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent(failedOutput.HasValue
                        ? "{\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":" + failedOutput + "}}" : "private-error-canary") }
                    : RetryWireResponse();
            });
            handlers.Add(handler);
            return handler;
        }, TimeSpan.FromSeconds(300), TimeProvider.System);
        var client = DeepSeekChatBackend.CreateClient(new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model,
            authority.AdapterId, run.SessionId), transport);
        var executor = new ScriptedToolExecutor();
        var outcome = await new AgentLoop(client, executor, limitAuthority: authority, retryRandom: () => 0).RunAsync(run, default);
        Assert.Equal(success, outcome.Succeeded);
        if (!success) AssertFailure(outcome, AgentFailureCodes.TokenLimit);
        Assert.Equal(success ? 2 : 1, bodies.Count);
        Assert.All(bodies, body =>
        {
            Assert.Equal(bodies[0], body);
            using var json = JsonDocument.Parse(body);
            Assert.Equal(65_536, json.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.DoesNotContain("retry-secret-canary", Encoding.UTF8.GetString(body));
        });
        Assert.All(handlers, handler => Assert.True(handler.Disposed));
        Assert.Equal(1, outcome.Accounting!.ModelCalls);
        Assert.Equal(bodies.Count, outcome.Accounting.ProviderAttempts);
        Assert.Equal(success ? 1 : 0, outcome.Accounting.ProviderRetries);
        Assert.Equal(1, outcome.Accounting.ProviderFailedAttempts);
        Assert.Equal(failedOutput.HasValue ? 0 : 1, outcome.Accounting.UnknownUsageAttempts);
        Assert.Equal((failedOutput ?? 0) + (success ? 1 : 0), outcome.Accounting.OutputTokens);
        Assert.Empty(executor.Order);
        Assert.Equal(success ? 1 : 0, outcome.Events.OfType<AgentToolCallEvent>().Count());
        Assert.Equal(success ? 1 : 0, outcome.Events.OfType<AgentTerminalEvent>().Count());
        Assert.DoesNotContain("private-error-canary", outcome.ToString());
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task PerCallPhysicalBoundAndSuccessHistoryAreIndependent(int failedAttempts)
    {
        var requests = new List<ProjectChatRequest>();
        var client = new CallbackChatClient((request, count) =>
        {
            requests.Add(request);
            Assert.True(request.Accounting!.TryBeginDispatch());
            if (count <= failedAttempts) throw new ProjectChatRetryException();
            return Task.FromResult(Response(TerminalCall("finish", "done"), 1, 1));
        });
        var executor = new ScriptedToolExecutor();
        var outcome = await new AgentLoop(client, executor, retryRandom: () => 0).RunAsync(Request(), default);
        Assert.Equal(failedAttempts < 3, outcome.Succeeded);
        Assert.Equal(Math.Min(failedAttempts + 1, 3), requests.Count);
        Assert.Equal(1, outcome.Accounting!.ModelCalls);
        Assert.Equal(requests.Count - 1, outcome.Accounting.ProviderRetries);
        Assert.Equal(Math.Min(failedAttempts, 3), outcome.Accounting.UnknownUsageAttempts);
        Assert.All(requests, r =>
        {
            Assert.Same(requests[0].Messages, r.Messages);
            Assert.Same(requests[0].Tools, r.Tools);
            Assert.Same(requests[0].Continuation, r.Continuation);
            Assert.Equal(requests[0].MaxOutputTokens, r.MaxOutputTokens);
        });
        Assert.Empty(executor.Order);
        Assert.Equal(failedAttempts < 3 ? 1 : 0, outcome.Events.OfType<AgentToolCallEvent>().Count());
    }

    [Fact]
    public async Task EightGlobalRetriesStillPermitANewFirstAttemptButNoNinthRetry()
    {
        var sends = 0;
        var client = new CallbackChatClient((request, count) =>
        {
            sends++;
            Assert.True(request.Accounting!.TryBeginDispatch());
            if (count % 3 != 0) throw new ProjectChatRetryException();
            return Task.FromResult(Response(new("read" + count, "read_file", "{\"path\":\"a.txt\"}"), 1, 1));
        });
        var executor = new ScriptedToolExecutor();
        var outcome = await new AgentLoop(client, executor, retryRandom: () => 0).RunAsync(Request(), default);
        AssertFailure(outcome, AgentFailureCodes.ChatFailed);
        Assert.Equal(13, sends);
        Assert.Equal(5, outcome.Accounting!.ModelCalls);
        Assert.Equal(8, outcome.Accounting.ProviderRetries);
        Assert.Equal(9, outcome.Accounting.ProviderFailedAttempts);
        Assert.Equal(4, executor.Order.Count);
    }

    [Theory]
    [InlineData((int)AgentLimitProfile.Output8192)] [InlineData((int)AgentLimitProfile.Output65536)]
    public async Task RetainedProfilesRemainSingleAttempt(int profileValue)
    {
        var profile = (AgentLimitProfile)profileValue;
        var run = Request();
        var authority = new AgentLimitAuthority(run.StablePlan.AdapterId, profile);
        run = run with { StablePlan = run.StablePlan with { LimitsSha256 = AgentCanonical.LimitsSha256(profile) } };
        var sends = 0;
        var client = new CallbackChatClient((request, _) =>
        {
            sends++;
            Assert.True(request.Accounting!.TryBeginDispatch());
            throw new ProjectChatRetryException();
        });
        var outcome = await new AgentLoop(client, new ScriptedToolExecutor(), limitAuthority: authority, retryRandom: () => 0).RunAsync(run, default);
        AssertFailure(outcome, AgentFailureCodes.ChatFailed);
        Assert.Equal(1, sends);
        Assert.Equal(0, outcome.Accounting!.ProviderRetries);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task BackoffSharesTheOriginalDeadlineAndCallerCancellation(bool caller)
    {
        var clock = new DeadlineTestClock();
        using var cancel = new CancellationTokenSource();
        var sends = 0;
        var client = new CallbackChatClient((request, _) =>
        {
            sends++;
            Assert.True(request.Accounting!.TryBeginDispatch());
            throw new ProjectChatRetryException(TimeSpan.FromSeconds(40));
        });
        var task = new AgentLoop(client, new ScriptedToolExecutor(), clock, retryRandom: () => 0)
            .RunAsync(Request() with { RemainingHostTime = TimeSpan.FromSeconds(50) }, cancel.Token);
        Assert.False(task.IsCompleted);
        if (caller) cancel.Cancel(); else clock.AdvanceWithLateCallbacks(TimeSpan.FromSeconds(50));
        var outcome = await task.WaitAsync(TimeSpan.FromSeconds(5));
        AssertFailure(outcome, caller ? AgentFailureCodes.Cancelled : AgentFailureCodes.DeadlineExceeded);
        Assert.Equal(1, sends);
        Assert.Equal(0, outcome.Accounting!.ProviderRetries);
    }

    [Theory]
    [InlineData(49, true)] [InlineData(50, false)] [InlineData(51, false)]
    public async Task RetryAfterCanExceedJitterCapButMustFitRemainingTime(int delay, bool retry)
    {
        var clock = new DeadlineTestClock();
        var sends = 0;
        var client = new CallbackChatClient((request, _) =>
        {
            sends++;
            Assert.True(request.Accounting!.TryBeginDispatch());
            if (sends == 1) throw new ProjectChatRetryException(TimeSpan.FromSeconds(delay));
            return Task.FromResult(Response(TerminalCall("finish", "done"), 1, 1));
        });
        var task = new AgentLoop(client, new ScriptedToolExecutor(), clock, retryRandom: () => 0)
            .RunAsync(Request() with { RemainingHostTime = TimeSpan.FromSeconds(50) }, default);
        if (retry) clock.Advance(TimeSpan.FromSeconds(delay));
        var outcome = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(retry, outcome.Succeeded);
        if (!retry) AssertFailure(outcome, AgentFailureCodes.ChatFailed);
        Assert.Equal(retry ? 2 : 1, sends);
    }

    [Fact]
    public void FullJitterUsesExplicitExclusiveWindows()
    {
        Assert.Equal(TimeSpan.Zero, ProviderRetryDelay.Choose(1, 0, null));
        Assert.Equal(TimeSpan.FromSeconds(.5), ProviderRetryDelay.Choose(1, .5, null));
        Assert.Equal(TimeSpan.FromSeconds(1), ProviderRetryDelay.Choose(2, .5, null));
        Assert.Equal(TimeSpan.FromSeconds(15), ProviderRetryDelay.Choose(6, .5, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProviderRetryDelay.Choose(1, 1, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProviderRetryDelay.Choose(1, double.NaN, null));
    }

    [Fact]
    public async Task MinimalChatClientPreservesSanitizedRetrySignal()
    {
        var signal = new ProjectChatRetryException(TimeSpan.FromSeconds(2));
        var client = new MinimalChatClient(new RetryBackend(signal));
        var error = await Assert.ThrowsAsync<ProjectChatRetryException>(() => client.GetResponseAsync(
            new(Request().InitialMessages, [], null, true), default));
        Assert.Same(signal, error);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(999L, 0L, 999L, true)] [InlineData(1000L, 0L, 1000L, false)]
    [InlineData(1000L, 1000L, 0L, false)] [InlineData(1000L, null, null, false)]
    public async Task FailedKnownInputAndCachePartitionsStopBeforeAnotherHttpSend(long input, long? hit, long? miss, bool retry)
    {
        var authority = new AgentLimitAuthority(DeepSeekAdapterContext.Adapter, AgentLimitProfile.Current,
            new ReviewTokenBudget(1000, 1000, 100_000));
        var run = RetryWireRequest(authority);
        var sends = 0;
        using var transport = DeepSeekTransport.CreateForTesting(DeepSeekCredential.Create("synthetic"),
            new RetryHandler((_, _) =>
            {
                sends++;
                if (sends > 1) return Task.FromResult(RetryWireResponse());
                var body = JsonSerializer.Serialize(new { usage = new { prompt_tokens = input, completion_tokens = 0,
                    prompt_cache_hit_tokens = hit, prompt_cache_miss_tokens = miss } });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent(body) });
            }), TimeSpan.FromSeconds(300));
        var client = DeepSeekChatBackend.CreateClient(new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model,
            authority.AdapterId, run.SessionId), transport);
        var outcome = await new AgentLoop(client, new ScriptedToolExecutor(), limitAuthority: authority, retryRandom: () => 0).RunAsync(run, default);
        Assert.Equal(retry, outcome.Succeeded);
        if (!retry) AssertFailure(outcome, AgentFailureCodes.TokenLimit);
        Assert.Equal(retry ? 2 : 1, sends);
        Assert.Equal(input + (retry ? 1 : 0), outcome.Accounting!.InputTokens);
        Assert.Equal(hit.HasValue ? 0 : 1, outcome.Accounting.UnknownCachePartitionAttempts);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PartialConnectionBodyCanRetryButCompleteMalformedResponseCannot(bool partial)
    {
        var authority = DeepSeekAdapterContext.LimitAuthorityFor(DeepSeekRequestProfile.Current);
        var run = RetryWireRequest(authority);
        var bodies = new List<byte[]>();
        using var transport = DeepSeekTransport.CreateForTesting(DeepSeekCredential.Create("synthetic"),
            new RetryHandler(async (request, token) =>
            {
                bodies.Add(await request.Content!.ReadAsByteArrayAsync(token));
                if (bodies.Count > 1) return RetryWireResponse();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = partial
                    ? new StreamContent(new PartialRetryStream()) : new StringContent("{malformed") };
            }), TimeSpan.FromSeconds(300));
        var client = DeepSeekChatBackend.CreateClient(new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model,
            authority.AdapterId, run.SessionId), transport);
        var outcome = await new AgentLoop(client, new ScriptedToolExecutor(), limitAuthority: authority, retryRandom: () => 0).RunAsync(run, default);
        Assert.Equal(partial, outcome.Succeeded);
        if (!partial) AssertFailure(outcome, AgentFailureCodes.ResponseInvalid);
        Assert.Equal(partial ? 2 : 1, bodies.Count);
        Assert.All(bodies, body => Assert.Equal(bodies[0], body));
        Assert.Equal(partial ? 1 : 0, outcome.Events.OfType<AgentToolCallEvent>().Count());
        Assert.Equal(partial ? 1 : 0, outcome.Events.OfType<AgentTerminalEvent>().Count());
        Assert.Equal(1, outcome.Accounting!.UnknownUsageAttempts);
        Assert.Equal(partial ? 1 : 0, outcome.Accounting.ProviderRetries);
    }

    private sealed class PartialRetryStream : MemoryStream
    {
        private bool first = true;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!first) throw new IOException("synthetic reset", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset));
            first = false;
            var prefix = "{\"choices\":[{\"message\":{\"reasoning_content\":\"uncommitted"u8;
            prefix.CopyTo(buffer.Span);
            return ValueTask.FromResult(prefix.Length);
        }
    }

    private sealed class RetryBackend(ProjectChatRetryException signal) : IMinimalChatBackend
    {
        public Task<MinimalChatResponse> GetResponseAsync(MinimalChatRequest request, CancellationToken cancellationToken) => throw signal;
    }

    private static AgentRunRequest RetryWireRequest(AgentLimitAuthority authority)
    {
        var run = Request();
        return run with { StablePlan = run.StablePlan with
        {
            ProviderId = DeepSeekAdapterContext.Provider, ModelId = DeepSeekAdapterContext.Model,
            AdapterId = authority.AdapterId, LimitsSha256 = AgentCanonical.LimitsSha256(authority.Profile, authority.TokenBudget),
        } };
    }

    private static HttpResponseMessage RetryWireResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"model":"deepseek-flash","choices":[{"index":0,"finish_reason":"tool_calls","message":{"role":"assistant","content":null,"reasoning_content":"","tool_calls":[{"id":"finish","type":"function","function":{"name":"finish_review","arguments":"{\"summary\":\"done\",\"findings\":[]}"}}]}}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2,"prompt_cache_hit_tokens":0,"prompt_cache_miss_tokens":1}}"""),
    };

    private sealed class RetryHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
