using System.Net;
using System.Net.Sockets;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Chat;
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
        var run = HistoricalRetryRequest(authority);
        var sends = 0;
        var client = new CallbackChatClient((_, _) =>
        {
            sends++;
            // An eligible authored signal without a dispatch observation cannot authorize retry.
            throw new ProjectChatRetryException(connectTimeout ? TimeSpan.Zero : null);
        });
        var outcome = await new AgentLoop(client, new ScriptedToolExecutor(), limitAuthority: authority, retryRandom: () => 0)
            .RunAsync(run, default);
        AssertFailure(outcome, AgentFailureCodes.ChatFailed);
        Assert.Equal(1, sends);
        Assert.Equal(1, outcome.Accounting!.ModelCalls);
        Assert.Equal(0, outcome.Accounting.ProviderAttempts);
        Assert.Equal(0, outcome.Accounting.ProviderRetries);
        Assert.Equal(AccountingCompleteness.Unavailable, outcome.Accounting.AttemptCompleteness);
        Assert.Empty(outcome.Events.OfType<AgentTerminalEvent>());
    }

    [Theory]
    [InlineData(4)] [InlineData(12)]
    public async Task AuthoredRetryDelayUsesRemainingTimeAfterScriptWork(int elapsedSeconds)
    {
        var clock = new DeadlineTestClock();
        var authority = DeepSeekAdapterContext.LimitAuthorityFor(DeepSeekRequestProfile.Current);
        var run = HistoricalRetryRequest(authority) with { RemainingHostTime = TimeSpan.FromSeconds(17) };
        var started = clock.GetTimestamp();
        var sends = 0;
        TimeSpan? retriedAt = null;
        var client = new CallbackChatClient((request, _) =>
        {
            Assert.True(request.Accounting!.TryBeginDispatch());
            if (++sends > 1)
            {
                retriedAt = clock.GetElapsedTime(started);
                request.Accounting.RecordUsage(ProviderUsageObservation.Create(1, 1, 0, 1, 2));
                return Task.FromResult(Response(TerminalCall("finish", "done"), 1, 1));
            }
            clock.Advance(TimeSpan.FromSeconds(elapsedSeconds));
            request.Accounting.MarkFailed();
            throw new ProjectChatRetryException(TimeSpan.FromSeconds(Math.Max(0, 10 - elapsedSeconds)));
        });
        var task = new AgentLoop(client, new ScriptedToolExecutor(), clock, limitAuthority: authority, retryRandom: () => 0)
            .RunAsync(run, default);
        if (elapsedSeconds < 10)
        {
            Assert.False(task.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(10 - elapsedSeconds - 1));
            Assert.Equal(1, sends);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        var outcome = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(outcome.Succeeded);
        Assert.Equal(2, sends);
        Assert.Equal(TimeSpan.FromSeconds(Math.Max(10, elapsedSeconds)), retriedAt);
        Assert.Equal(1, outcome.Accounting!.ProviderRetries);
        Assert.Equal(2, outcome.Accounting.ProviderAttempts);
        Assert.Single(outcome.Events.OfType<AgentTerminalEvent>());
    }
}
