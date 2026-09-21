using System.Collections.Immutable;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Contracts;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;

internal sealed class EconomicsCalls : ILiveAttemptObserver
{
    private readonly object gate = new();
    private readonly List<Call> calls = [];
    private bool sealedCalls;
    private ImmutableArray<EconomicsCall> snapshot;
    public ILiveCallObserver? BeginCall()
    {
        lock (gate)
        {
            if (sealedCalls) return null;
            if (calls.Count >= 8 || calls.LastOrDefault() is { Ended: false })
                throw new InvalidOperationException("r6_economics_call_invalid");
            var call = new Call(this, calls.Count + 1); calls.Add(call); return call;
        }
    }
    public ILiveCallObserver? CurrentCall
    { get { lock (gate) return !sealedCalls && calls.LastOrDefault() is { Ended: false } current ? current : null; } }
    internal ImmutableArray<EconomicsCall> Seal(bool cancelled)
    {
        lock (gate)
        {
            if (sealedCalls) return snapshot;
            if (cancelled) foreach (var call in calls) call.Cancel();
            sealedCalls = true;
            return snapshot = calls.Select(call => call.Snapshot()).ToImmutableArray();
        }
    }
    private sealed class Call(EconomicsCalls owner, int ordinal) : ILiveCallObserver
    {
        internal bool Ended;
        private bool dispatched, transportObserved;
        private string transport = "not_dispatched", chat = "not_observed";
        private UsageJournalUsage? usage;
        private bool Mutable => !owner.sealedCalls && !Ended;
        public bool Dispatch()
        {
            lock (owner.gate)
            {
                if (!Mutable || dispatched || transportObserved) return false;
                dispatched = true; transport = "incomplete"; return true;
            }
        }
        public void Refuse(string reason)
        {
            lock (owner.gate)
            {
                if (!Mutable || dispatched || transportObserved) return;
                if (reason is not ("budget_refused" or "violation_refused")) throw new InvalidOperationException();
                transport = reason; transportObserved = true;
            }
        }
        public void TransportFinished(DeepSeekTransportResult? result)
        {
            lock (owner.gate)
            {
                if (!Mutable || !dispatched || transportObserved) return;
                transportObserved = true;
                transport = result?.Outcome switch
                {
                    DeepSeekTransportOutcome.Success => "success",
                    DeepSeekTransportOutcome.RequestRejected => "request_rejected",
                    DeepSeekTransportOutcome.ResponseTooLarge => "response_too_large",
                    DeepSeekTransportOutcome.ConnectTimeout => "connect_timeout",
                    DeepSeekTransportOutcome.ProviderTimeout => "provider_timeout",
                    DeepSeekTransportOutcome.HttpFailure when result.StatusClass == DeepSeekHttpStatusClass.TooManyRequests => "http429",
                    DeepSeekTransportOutcome.HttpFailure when result.StatusClass == DeepSeekHttpStatusClass.Other5xx => "http5xx",
                    DeepSeekTransportOutcome.HttpFailure => "http4xx",
                    _ => "transport_failure",
                };
                if (transport == "request_rejected") dispatched = false;
            }
        }
        public void Returned(ProjectChatUsage? observed)
        {
            lock (owner.gate)
            {
                if (!Mutable) return;
                chat = "returned";
                if (dispatched && transport == "success" && observed is { InputTokens: >= 0, OutputTokens: >= 0 } &&
                    observed.InputTokens <= long.MaxValue - observed.OutputTokens)
                {
                    UsageJournalCache? cache = null;
                    if (observed.ProviderUsage is { } provider && provider.ProviderId == DeepSeekAdapterContext.Provider &&
                        provider.RequestedModel == DeepSeekRequestWriter.Model &&
                        provider.ResponseModel is DeepSeekRequestWriter.Model or "deepseek-flash" &&
                        provider.CacheReadInputTokens >= 0 && provider.CacheReadInputTokens <= observed.InputTokens &&
                        provider.UncachedInputTokens == observed.InputTokens - provider.CacheReadInputTokens)
                        cache = new(provider.ResponseModel, provider.CacheReadInputTokens, provider.UncachedInputTokens);
                    usage = new(observed.InputTokens, observed.OutputTokens, observed.InputTokens + observed.OutputTokens, cache);
                }
                Ended = true;
            }
        }
        public void Threw()
        {
            lock (owner.gate)
            {
                if (!Mutable) return;
                chat = "threw"; if (transport == "incomplete") transport = "transport_failure"; Ended = true;
            }
        }
        public void Cancel()
        { lock (owner.gate) { if (!Mutable) return; chat = "cancelled"; transport = "cancelled"; Ended = true; } }
        internal EconomicsCall Snapshot() => new(ordinal, dispatched, transport, chat,
            !dispatched ? "not_sent" : usage is null ? "unknown" : "known", usage);
    }
}
