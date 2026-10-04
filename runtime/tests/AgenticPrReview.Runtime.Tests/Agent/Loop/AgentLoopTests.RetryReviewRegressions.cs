using System.Net;
using System.Net.Sockets;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.Tests.Agent.Loop;

public sealed partial class AgentLoopTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task UninstrumentedEligibleFailureCannotAuthorizeAnotherSend(bool connectTimeout)
    {
        var authority = DeepSeekAdapterContext.LimitAuthorityFor(DeepSeekRequestProfile.Current);
        var run = RetryWireRequest(authority);
        using var transport = new UninstrumentedRetryTransport(connectTimeout);
        var client = DeepSeekChatBackend.CreateClient(new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model,
            authority.AdapterId, run.SessionId), transport);
        var outcome = await new AgentLoop(client, new ScriptedToolExecutor(), limitAuthority: authority, retryRandom: () => 0)
            .RunAsync(run, default);
        AssertFailure(outcome, AgentFailureCodes.ChatFailed);
        Assert.Equal(1, transport.Sends);
        Assert.Equal(1, outcome.Accounting!.ModelCalls);
        Assert.Equal(0, outcome.Accounting.ProviderAttempts);
        Assert.Equal(0, outcome.Accounting.ProviderRetries);
        Assert.Equal(AccountingCompleteness.Unavailable, outcome.Accounting.AttemptCompleteness);
        Assert.Empty(outcome.Events.OfType<AgentTerminalEvent>());
    }

    [Theory]
    [InlineData(true, false, 4)] [InlineData(true, false, 12)]
    [InlineData(true, true, 4)] [InlineData(true, true, 12)]
    [InlineData(false, false, 4)] [InlineData(false, false, 12)]
    [InlineData(false, true, 4)] [InlineData(false, true, 12)]
    public async Task RetryAfterConsumesBodyTimeBeforeActualLoopBackoff(bool httpDate, bool bodyFails, int bodySeconds)
    {
        var clock = new DeadlineTestClock();
        var authority = DeepSeekAdapterContext.LimitAuthorityFor(DeepSeekRequestProfile.Current);
        var run = RetryWireRequest(authority) with { RemainingHostTime = TimeSpan.FromSeconds(17) };
        var started = clock.GetTimestamp();
        var notBefore = clock.GetUtcNow().AddSeconds(10);
        var sends = 0;
        TimeSpan? retriedAt = null;
        using var transport = DeepSeekTransport.CreateForTesting(DeepSeekCredential.Create("synthetic"),
            new RetryHandler((_, _) =>
            {
                if (++sends > 1)
                {
                    retriedAt = clock.GetElapsedTime(started);
                    return Task.FromResult(RetryWireResponse());
                }
                var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StreamContent(new AdvancingRetryBody(clock, bodySeconds, bodyFails)),
                };
                response.Headers.TryAddWithoutValidation("Retry-After", httpDate ? notBefore.ToString("R") : "10");
                return Task.FromResult(response);
            }), TimeSpan.FromSeconds(300), clock);
        var client = DeepSeekChatBackend.CreateClient(new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model,
            authority.AdapterId, run.SessionId), transport);
        var task = new AgentLoop(client, new ScriptedToolExecutor(), clock, limitAuthority: authority, retryRandom: () => 0)
            .RunAsync(run, default);
        if (bodySeconds < 10)
        {
            Assert.False(task.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(10 - bodySeconds - 1));
            Assert.Equal(1, sends);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        var outcome = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(outcome.Succeeded);
        Assert.Equal(2, sends);
        Assert.Equal(TimeSpan.FromSeconds(Math.Max(10, bodySeconds)), retriedAt);
        Assert.Equal(1, outcome.Accounting!.ProviderRetries);
        Assert.Equal(2, outcome.Accounting.ProviderAttempts);
        Assert.Single(outcome.Events.OfType<AgentTerminalEvent>());
    }

    private sealed class UninstrumentedRetryTransport(bool connectTimeout) : IDeepSeekTransport
    {
        internal int Sends { get; private set; }
        public Task<DeepSeekTransportResult> SendAsync(ReadOnlyMemory<byte> body, CancellationToken token)
        {
            Sends++;
            return Task.FromResult(connectTimeout ? DeepSeekTransportResult.ConnectTimeout()
                : DeepSeekTransportResult.HttpFailure(DeepSeekHttpStatusClass.Other5xx, 0, 503, null));
        }
        public void Dispose() { }
    }

    private sealed class AdvancingRetryBody(DeadlineTestClock clock, int seconds, bool fails) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            if (fails) throw new IOException("synthetic body reset", new SocketException((int)SocketError.ConnectionReset));
            return ValueTask.FromResult(0);
        }
    }
}
