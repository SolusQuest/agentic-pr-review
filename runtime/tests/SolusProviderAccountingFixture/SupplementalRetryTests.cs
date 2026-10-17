using System.Net;
using AgenticPrReview.Runtime.Execution.Solus;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Startup;
using SolusAgent.Runtime.Api.Providers;

// Explicit test-only policies: never selectable by SolusReviewBridge/R1.
static class SupplementalRetryTests
{
    internal static async Task Run()
    {
        using (var timeoutHandler = new ControlledHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new ProofFailure("timeout_returned");
        }))
        using (var provider = F.Provider(timeoutHandler, timeout: TimeSpan.FromMilliseconds(100)))
        {
            var result = await provider.ExchangeAsync(F.Request(Guid.NewGuid()));
            F.Check(result.Outcome == ProviderOutcome.Failed && result.Retry?.Kind == ProviderRetryKind.Transient &&
                result.Observation.Exposure == DispatchExposure.Unknown && timeoutHandler.Sends == 1, "attempt_timeout_not_caller_cancel");
        }
        foreach (var cancel in new[] { false, true })
        {
            var backoff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var clock = new ObservedClock(due => { if (due == TimeSpan.FromSeconds(5)) backoff.TrySetResult(); });
            using var cancellation = new CancellationTokenSource();
            using var handler = new ControlledHandler((_, _) =>
            {
                var response = F.Http(F.Response(F.Usage()), HttpStatusCode.ServiceUnavailable);
                response.Headers.TryAddWithoutValidation("Retry-After", "5");
                return Task.FromResult(response);
            });
            using var provider = F.Provider(handler);
            var agent = RuntimeAgentFactory.Create(new(provider, [], new ProofHooks(), SolusReviewBridge.Bounds()),
                new RuntimeOptions(clock, requireContinuation: true));
            var request = new AgentRequest(Guid.NewGuid(), "trusted policy", [new(AgentInputSource.Repository, "synthetic data")],
                new(6, TimeSpan.FromSeconds(2)), AgentCapability.DurationLimit | AgentCapability.Cancellation | AgentCapability.DispatchLimits | AgentCapability.UsageReporting,
                new(maximumLogicalCalls: 1, maximumPhysicalDispatches: 2, retryPolicy: new(2, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), true)));
            var pending = agent.ExecuteAsync(request, cancellationToken: cancellation.Token).AsTask();
            await F.Wait(backoff.Task);
            if (cancel) cancellation.Cancel();
            var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            F.Check(outcome.Reason == (cancel ? AgentTerminationReason.Cancelled : AgentTerminationReason.ResourceLimit) &&
                handler.Sends == 1 && outcome.Usage!.Attempts.Count == 1, "original_deadline_backoff");
        }
        var execution = Guid.NewGuid(); var logical = Guid.NewGuid(); var count = 0;
        using var ambiguousHandler = new ControlledHandler((_, _) =>
        {
            if (++count == 1) throw new HttpRequestException(HttpRequestError.ConnectionError, "SYNTHETIC_EXCEPTION_CANARY");
            return Task.FromResult(F.Http(F.Response(F.Usage())));
        });
        using var ambiguous = F.Provider(ambiguousHandler);
        var a = await ambiguous.ExchangeAsync(F.Request(execution, logical));
        var b = await ambiguous.ExchangeAsync(F.Request(execution, logical, 2));
        var usage = new AgentRunUsage(execution, UsageInventoryCoverage.Complete, [a.Observation, b.Observation]);
        F.Throws<InvalidOperationException>(() => SolusReviewFacts.Project(usage,
            [new(a.Observation, a.Outcome, SolusAgent.Runtime.Api.Exposure.RuntimeStop.None),
                new(b.Observation, b.Outcome, SolusAgent.Runtime.Api.Exposure.RuntimeStop.None)]), "unsupported_unknown_retry_projection");
    }
    private sealed class ObservedClock(Action<TimeSpan> timerCreated) : TimeProvider
    {
        public override long GetTimestamp() => TimeProvider.System.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = TimeProvider.System.CreateTimer(callback, state, dueTime, period);
            timerCreated(dueTime);
            return timer;
        }
    }
}
