using System.Collections.Immutable;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Contracts;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Accounting;

// The scheduler is serial, but cancellation and abandoned transport tasks are
// not. Every mutation uses this gate; callbacks retain handles, never a lookup
// of the next current call. No provider bytes or exception objects are stored.
internal sealed class UsageJournalCollector(UsageJournalExpectation expected)
{
    private readonly UsageJournalExpectation _expected = expected;
    private readonly object _gate = new();
    private readonly Attempt[] _attempts = Enumerable.Range(0, expected.Schedule.Length)
        .Select(index => new Attempt(index)).ToArray();
    private int _nextAttempt;
    private bool _sealed;
    private UsageJournal? _result;

    internal AttemptScope BeginAttempt(int index)
    {
        lock (_gate)
        {
            if (_sealed || index != _nextAttempt || index >= _attempts.Length ||
                index > 0 && !_attempts[index - 1].Ended)
                throw new InvalidOperationException("usage_journal_attempt_invalid");
            _nextAttempt++;
            _attempts[index].Status = "failed";
            return new(this, _attempts[index]);
        }
    }

    internal UsageJournal Seal(string stopReason, UsageJournalReservations reservations)
    {
        lock (_gate)
        {
            if (_result is not null) return _result;
            _sealed = true;
            var attempts = ImmutableArray.CreateBuilder<UsageJournalAttempt>();
            var calls = ImmutableArray.CreateBuilder<UsageJournalCall>();
            foreach (var attempt in _attempts)
            {
                attempt.Ended = true;
                foreach (var call in attempt.Calls)
                {
                    call.Ended = true;
                    calls.Add(call.Snapshot());
                }
                var sent = attempt.Calls.Count(c => c.Dispatched);
                attempts.Add(new(_expected.BindingSha256, _expected.AttemptId(attempt.Index), attempt.Index,
                    _expected.Schedule[attempt.Index], attempt.Status, attempt.AgentStatus, attempt.EvaluationAttemptSha256,
                    attempt.Calls.Count, sent, attempt.Calls.Count - sent));
            }
            var attemptRows = attempts.ToImmutable();
            var callRows = calls.ToImmutable();
            var document = new UsageJournalDocument(_expected.Provenance, _expected.Plan, _expected.BindingSha256, stopReason,
                "not_applicable", reservations, attemptRows, callRows, UsageJournal.Totals(attemptRows, callRows));
            _result = UsageJournal.Admit(document, _expected) ??
                throw new InvalidOperationException("usage_journal_invalid");
            return _result;
        }
    }

    internal sealed class Attempt(int index)
    {
        internal int Index { get; } = index;
        internal string Status = "unattempted";
        internal string AgentStatus = "not_started";
        internal string? EvaluationAttemptSha256;
        internal bool Ended;
        internal List<CallScope> Calls { get; } = [];
    }

    internal sealed class AttemptScope(UsageJournalCollector owner, Attempt state) : ILiveAttemptObserver
    {
        ILiveCallObserver? ILiveAttemptObserver.BeginCall() => BeginCall();
        ILiveCallObserver? ILiveAttemptObserver.CurrentCall => CurrentCall;
        internal void AdmitEvaluation(string? sha256)
        {
            lock (owner._gate)
                if (!owner._sealed && !state.Ended) state.EvaluationAttemptSha256 = sha256;
        }

        internal void AgentFinished(bool succeeded)
        {
            lock (owner._gate)
                if (!owner._sealed && !state.Ended) state.AgentStatus = succeeded ? "succeeded" : "failed";
        }

        internal void AgentStarted()
        {
            lock (owner._gate)
                if (!owner._sealed && !state.Ended) state.AgentStatus = "unknown";
        }

        internal CallScope? BeginCall()
        {
            lock (owner._gate)
            {
                if (owner._sealed || state.Ended) return null;
                if (state.Calls.Count >= UsageJournalLimits.CallsPerAttempt ||
                    state.Calls.LastOrDefault() is { Ended: false })
                    throw new InvalidOperationException("usage_journal_call_invalid");
                var call = new CallScope(owner, state, state.Calls.Count + 1);
                state.Calls.Add(call);
                return call;
            }
        }

        internal CallScope? CurrentCall
        {
            get
            {
                lock (owner._gate)
                    return !owner._sealed && !state.Ended && state.Calls.LastOrDefault() is { Ended: false } call
                        ? call : null;
            }
        }

        internal void Finish(string status, bool cancelled = false)
        {
            lock (owner._gate)
            {
                if (owner._sealed || state.Ended) return;
                if (status is not ("completed" or "failed" or "invalid"))
                    throw new InvalidOperationException("usage_journal_status_invalid");
                // WaitAsync may release the Agent before the transport token's
                // callback runs. Close any still-pending call at the scheduler's
                // cancellation boundary, without replacing admitted usage.
                if (cancelled)
                    foreach (var call in state.Calls) call.Cancel();
                state.Status = status;
                state.Ended = true;
                foreach (var call in state.Calls) call.Ended = true;
            }
        }
    }

    internal sealed class CallScope(UsageJournalCollector owner, Attempt attempt, int ordinal) : ILiveCallObserver
    {
        bool ILiveCallObserver.Dispatch() => Dispatch();
        void ILiveCallObserver.Refuse(string reason) => Refuse(reason);
        void ILiveCallObserver.TransportFinished(DeepSeekTransportResult? result) => TransportFinished(result);
        void ILiveCallObserver.Returned(ProjectChatUsage? usage) => Returned(usage);
        void ILiveCallObserver.Threw() => Threw();
        void ILiveCallObserver.Cancel() => Cancel();
        internal bool Ended;
        internal bool Dispatched;
        private bool _transportObserved;
        private string _transport = "not_dispatched";
        private string _chat = "not_observed";
        private UsageJournalUsage? _usage;
        private bool Mutable => !owner._sealed && !attempt.Ended && !Ended;

        internal bool Dispatch()
        {
            lock (owner._gate)
            {
                if (!Mutable || Dispatched || _transportObserved) return false;
                Dispatched = true;
                _transport = "incomplete";
                return true;
            }
        }

        internal void Refuse(string reason)
        {
            lock (owner._gate)
            {
                if (!Mutable || Dispatched || _transportObserved) return;
                if (reason is not ("budget_refused" or "violation_refused"))
                    throw new InvalidOperationException("usage_journal_refusal_invalid");
                _transport = reason;
                _transportObserved = true;
            }
        }

        internal void TransportFinished(DeepSeekTransportResult? result)
        {
            lock (owner._gate)
            {
                if (!Mutable || !Dispatched || _transportObserved) return;
                _transportObserved = true;
                _transport = result?.Outcome switch
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
                if (_transport == "request_rejected") Dispatched = false;
            }
        }

        internal void Returned(ProjectChatUsage? usage)
        {
            lock (owner._gate)
            {
                if (!Mutable) return;
                _chat = "returned";
                if (Dispatched && _transport == "success") _usage = Project(usage);
                Ended = true;
            }
        }

        internal void Threw()
        {
            lock (owner._gate)
            {
                if (!Mutable) return;
                _chat = "threw";
                if (_transport == "incomplete") _transport = "transport_failure";
                Ended = true;
            }
        }

        internal void Cancel()
        {
            lock (owner._gate)
            {
                if (!Mutable) return;
                _chat = "cancelled";
                _transport = "cancelled";
                Ended = true;
            }
        }

        internal UsageJournalCall Snapshot() => new(owner._expected.BindingSha256,
            owner._expected.CallId(attempt.Index, ordinal), owner._expected.AttemptId(attempt.Index), ordinal,
            Dispatched, _transport, _chat, !Dispatched ? "not_sent" : _usage is null ? "unknown" : "known", _usage);

        private static UsageJournalUsage? Project(ProjectChatUsage? usage)
        {
            if (usage is null || usage.InputTokens < 0 || usage.OutputTokens < 0 ||
                usage.InputTokens > long.MaxValue - usage.OutputTokens) return null;
            UsageJournalCache? cache = null;
            if (usage.ProviderUsage is { } observed && observed.ProviderId == DeepSeekAdapterContext.Provider &&
                observed.RequestedModel == DeepSeekRequestWriter.Model &&
                observed.ResponseModel is DeepSeekRequestWriter.Model or "deepseek-flash" &&
                observed.CacheReadInputTokens >= 0 && observed.CacheReadInputTokens <= usage.InputTokens &&
                observed.UncachedInputTokens == usage.InputTokens - observed.CacheReadInputTokens)
                cache = new(observed.ResponseModel, observed.CacheReadInputTokens, observed.UncachedInputTokens);
            return new(usage.InputTokens, usage.OutputTokens, usage.InputTokens + usage.OutputTokens, cache);
        }
    }
}
