using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using SolusAgent.Api.Usage;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;

namespace AgenticPrReview.Runtime.Execution.Solus;

// Numeric product policy only. This object is also reachable by the provider
// wrapper, so it deliberately holds no Host callback, candidate or credential.
internal sealed class SolusReviewFacts
{
    private readonly object gate = new();
    private readonly ReviewTokenBudget budget;
    private readonly Dictionary<Guid, ProviderAttempt> admitted = [];
    private readonly Dictionary<Guid, RuntimeExposure> exposures = [];
    private readonly Dictionary<Guid, ProviderFinish?> finishes = [];
    private readonly List<SolusAttemptFact> settled = [];
    private readonly List<Guid> attemptOrder = [];
    private static readonly long ProfileInputLimit = Math.Min(Cr1DevelopmentProfile.InputTokenThreshold, Cr1DevelopmentProfile.InputTokenAllowance);
    private long uncached, cached, output, totalInput;
    private bool frozen, missingCore, refused, budgetStopped, hostStopped;
    private ProviderAccounting? projection;
    private int pendingHostFeedback;

    internal SolusReviewFacts(Guid executionId, ReviewTokenBudget budget)
    {
        if (executionId == Guid.Empty || !budget.IsValid ||
            budget.UncachedInputTokens > Cr1DevelopmentProfile.InputTokenAllowance ||
            budget.CachedInputTokens > Cr1DevelopmentProfile.InputTokenAllowance ||
            budget.OutputTokens > Math.Min(Cr1DevelopmentProfile.OutputTokenThreshold, Cr1DevelopmentProfile.OutputTokenAllowance)) throw new ArgumentException("APR_SOLUS_REVIEW_POLICY");
        ExecutionId = executionId;
        this.budget = budget;
    }

    internal Guid ExecutionId { get; }
    internal bool MissingCore { get { lock (gate) return missingCore; } }
    internal bool Refused { get { lock (gate) return refused; } }
    internal bool BudgetStopped { get { lock (gate) return budgetStopped; } }
    internal bool HostStopped { get { lock (gate) return hostStopped; } }
    internal bool Overrun { get { lock (gate) return OverrunLocked; } }
    private bool OverrunLocked => totalInput > ProfileInputLimit || uncached > budget.UncachedInputTokens || cached > budget.CachedInputTokens || output > budget.OutputTokens;
    private bool ExhaustedLocked => totalInput >= ProfileInputLimit || uncached >= budget.UncachedInputTokens || cached >= budget.CachedInputTokens || output >= budget.OutputTokens;

    internal bool Admit(RuntimeExposure exposure)
    {
        lock (gate)
        {
            var attempt = exposure.Attempt;
            if (frozen || attempt.ExecutionId != ExecutionId || attempt.AttemptNumber != 1 ||
                exposure.Scope.Provider != "deepseek" || exposure.Scope.Model != "deepseek-flash" ||
                admitted.Count >= Cr1DevelopmentProfile.PhysicalAttempts ||
                admitted.ContainsKey(attempt.PhysicalAttemptId) ||
                admitted.Values.Any(a => a.LogicalCallId == attempt.LogicalCallId) ||
                admitted.Count != settled.Count)
                throw new InvalidOperationException("APR_SOLUS_ATTEMPT_ASSOCIATION");
            admitted.Add(attempt.PhysicalAttemptId, attempt);
            exposures.Add(attempt.PhysicalAttemptId, exposure);
            attemptOrder.Add(attempt.PhysicalAttemptId);
            if (missingCore || ExhaustedLocked || budget.OutputTokens - output < Cr1DevelopmentProfile.ProviderOutputTokens)
            {
                refused = true;
                return false;
            }
            return true;
        }
    }

    internal void ObserveFinish(UsageAttemptObservation observation, ProviderFinish? finish)
    {
        lock (gate)
        {
            if (frozen) return;
            RequireAdmitted(observation);
            if (!finishes.TryAdd(observation.PhysicalAttemptId, finish))
                throw new InvalidOperationException("APR_SOLUS_ATTEMPT_ASSOCIATION");
        }
    }

    // Called from the runtime's sealed, associated settlement before candidate
    // feedback. It retains no response or exception and never estimates usage.
    internal bool Settle(RuntimeSettlement settlement)
    {
        lock (gate)
        {
            if (frozen) return false; // Late closure still reaches the Host hook.
            var observation = settlement.Observation;
            RequireAdmitted(observation);
            if (!exposures[observation.PhysicalAttemptId].Matches(settlement.Exposure))
                throw new InvalidOperationException("APR_SOLUS_SETTLEMENT_ASSOCIATION");
            if (settled.Any(f => f.Observation.PhysicalAttemptId == observation.PhysicalAttemptId))
                throw new InvalidOperationException("APR_SOLUS_ATTEMPT_ASSOCIATION");
            var fact = new SolusAttemptFact(observation, settlement.ProviderOutcome, settlement.Stop,
                settlement.ProviderError, settlement.ProviderInvoked, settlement.ProviderLimitKind);
            settled.Add(fact);
            var usage = Measurement(observation.Usage);
            if (observation.Exposure != DispatchExposure.NotDispatched)
            {
                missingCore |= !usage.InputTokens.HasValue || !usage.OutputTokens.HasValue;
                // A missing peer cannot erase independently known cached consumption.
                if (usage.CacheHitTokens is { } measuredHit)
                    cached = Debit(cached, measuredHit, budget.CachedInputTokens);
                if (usage.InputTokens is { } input)
                {
                    totalInput = Debit(totalInput, input, ProfileInputLimit);
                    if (usage.CacheHitTokens is { } hit && usage.CacheMissTokens is { } miss)
                    {
                        uncached = Debit(uncached, miss, budget.UncachedInputTokens);
                    }
                    else uncached = Debit(uncached, input, budget.UncachedInputTokens);
                }
                if (usage.OutputTokens is { } knownOutput) output = Debit(output, knownOutput, budget.OutputTokens);
            }
            var final = settlement.ProviderOutcome == ProviderOutcome.Succeeded &&
                finishes.GetValueOrDefault(observation.PhysicalAttemptId) == ProviderFinish.Final;
            return !final && (missingCore || ExhaustedLocked);
        }
    }

    internal void BeginHostFeedback()
    {
        lock (gate) { if (!frozen) pendingHostFeedback++; }
    }
    internal void EndHostFeedback()
    {
        lock (gate) { if (!frozen) pendingHostFeedback--; }
    }

    internal void RecordBudgetStop()
    {
        lock (gate) { if (!frozen && ExhaustedLocked) budgetStopped = true; }
    }
    internal void RecordHostStop()
    {
        lock (gate) { if (!frozen) hostStopped = true; }
    }

    internal bool CanAdmitTerminal(Guid executionId)
    {
        lock (gate)
        {
            return !frozen && executionId == ExecutionId && !missingCore && !refused && !OverrunLocked &&
                admitted.Count == settled.Count && settled.LastOrDefault() is { Outcome: ProviderOutcome.Succeeded, Stop: RuntimeStop.None } last &&
                finishes.GetValueOrDefault(last.Observation.PhysicalAttemptId) == ProviderFinish.Final;
        }
    }

    internal ProviderAccounting? Freeze(AgentRunUsage? usage)
    {
        lock (gate)
        {
            if (frozen) return projection;
            // A callback still pending when the runtime returns supplied no
            // usable Host receipt, independently of provider exchange success.
            hostStopped |= pendingHostFeedback != 0;
            frozen = true;
            // This leaf admits completed-context NEW work, not unfinished-call
            // recovery. Missing/partial inventory cannot manufacture known zero.
            if (usage is null || usage.ExecutionId != ExecutionId ||
                usage.Coverage != UsageInventoryCoverage.Complete || usage.ContinuedCalls.Count != 0)
                return null;
            foreach (var observation in usage.Attempts)
            {
                if (!admitted.TryGetValue(observation.PhysicalAttemptId, out var attempt) || !Same(attempt, observation)) return null;
                var previous = settled.SingleOrDefault(f => f.Observation.PhysicalAttemptId == observation.PhysicalAttemptId);
                if (previous is not null && !SameMeasurement(previous.Observation, observation)) return null;
            }
            if (usage.Attempts.Count != admitted.Count) return null;
            try { projection = Project(usage, settled); }
            catch (ArgumentException) { projection = null; }
            catch (InvalidOperationException) { projection = null; }
            return projection;
        }
    }

    // Supplemental fully measured retry projection is independently testable;
    // no production request policy enables it in the selected R1 profile.
    internal static ProviderAccounting Project(AgentRunUsage usage, IReadOnlyList<SolusAttemptFact> outcomes)
    {
        if (usage.Coverage != UsageInventoryCoverage.Complete || usage.ContinuedCalls.Count != 0 ||
            outcomes.Any(f => f.Observation.ExecutionId != usage.ExecutionId ||
                !usage.Attempts.Any(a => a.PhysicalAttemptId == f.Observation.PhysicalAttemptId)) ||
            outcomes.Select(f => f.Observation.PhysicalAttemptId).Distinct().Count() != outcomes.Count)
            throw new ArgumentException("APR_SOLUS_USAGE_INVENTORY");
        List<ProviderAttemptObservation> observations = [];
        var logical = 0;
        foreach (var group in usage.Attempts.GroupBy(a => a.LogicalCallId))
        {
            foreach (var current in group.OrderBy(a => a.AttemptNumber))
            {
                var result = outcomes.SingleOrDefault(f => f.Observation.PhysicalAttemptId == current.PhysicalAttemptId);
                if (result is not null && !SameMeasurement(result.Observation, current))
                    throw new ArgumentException("APR_SOLUS_USAGE_ASSOCIATION");
                var known = current.Exposure != DispatchExposure.Unknown;
                var dispatched = current.Exposure == DispatchExposure.Dispatched;
                observations.Add(new(logical, current.AttemptNumber - 1, known, dispatched,
                    known && (result?.Outcome is not null || !dispatched),
                    result is { Outcome: ProviderOutcome.Succeeded }, Measurement(current.Usage)));
            }
            logical++;
        }
        var value = ProviderAccounting.Aggregate(observations);
        // Unknown-first-send + later retry cannot satisfy the existing Action
        // count contract. Refuse unsupported projection; never invent a send.
        _ = ActionHostAccounting.FromProvider(value);
        return value;
    }

    private void RequireAdmitted(UsageAttemptObservation observation)
    {
        if (!admitted.TryGetValue(observation.PhysicalAttemptId, out var attempt) || !Same(attempt, observation))
            throw new InvalidOperationException("APR_SOLUS_ATTEMPT_ASSOCIATION");
    }
    private static bool Same(ProviderAttempt a, UsageAttemptObservation b) => a.ExecutionId == b.ExecutionId &&
        a.LogicalCallId == b.LogicalCallId && a.PhysicalAttemptId == b.PhysicalAttemptId && a.AttemptNumber == b.AttemptNumber;
    private static bool SameMeasurement(UsageAttemptObservation a, UsageAttemptObservation b) =>
        a.ExecutionId == b.ExecutionId && a.LogicalCallId == b.LogicalCallId && a.PhysicalAttemptId == b.PhysicalAttemptId &&
        a.AttemptNumber == b.AttemptNumber && a.Exposure == b.Exposure && a.Usage.InputTokens == b.Usage.InputTokens &&
        a.Usage.OutputTokens == b.Usage.OutputTokens && a.Usage.ProviderCounters.Select(c => (c.Kind, c.Value, c.Relationship)).Order().SequenceEqual(
            b.Usage.ProviderCounters.Select(c => (c.Kind, c.Value, c.Relationship)).Order());
    internal static ProviderUsageObservation Measurement(UsageObservation value) => ProviderUsageObservation.FromIndependentMeasurements(
        value.InputTokens, value.OutputTokens, Detail(value, ProviderTokenCounterKind.CacheRead), Detail(value, ProviderTokenCounterKind.UncachedInput));
    // Independent provider counters are not input partitions.
    private static long? Detail(UsageObservation value, ProviderTokenCounterKind kind) => value.ProviderCounters
        .SingleOrDefault(c => c.Kind == kind && c.Relationship == TokenCounterRelationship.IncludedInInput)?.Value;
    private static long Debit(long current, long value, long ceiling) => current + Math.Min(value, ceiling + 1 - current);
    // Frozen per-invocation evidence only. Admission order is captured before work,
    // never recovered from public counters or persisted historical state.
    internal ActionHostRuntimeFailureDiagnostic? DescribeFailure(CandidateStopReason candidateStop,
        AgentTerminationReason runtimeReason)
    {
        lock (gate)
        {
            if (!frozen || attemptOrder.Count != admitted.Count || attemptOrder.Count > Cr1DevelopmentProfile.PhysicalAttempts ||
                settled.Count > attemptOrder.Count || attemptOrder.Distinct().Count() != attemptOrder.Count) return null;
            List<ActionHostAttemptDiagnostic> attempts = [];
            for (var i = 0; i < attemptOrder.Count; i++)
            {
                var id = attemptOrder[i];
                var fact = settled.SingleOrDefault(item => item.Observation.PhysicalAttemptId == id);
                if (fact is not null && (!admitted.TryGetValue(id, out var attempt) || !Same(attempt, fact.Observation))) return null;
                var value = DescribeAttempt(i + 1, fact);
                if (value is null) return null;
                attempts.Add(value);
            }
            return ActionHostRuntimeFailureDiagnostic.TryCreate(attempts, DiagnosticValue(candidateStop),
                DiagnosticValue(runtimeReason), out var result) ? result : null;
        }
    }

    internal static ActionHostAttemptDiagnostic? DescribeAttempt(int ordinal, SolusAttemptFact? fact) =>
        ActionHostAttemptDiagnostic.TryCreate(ordinal, fact?.ProviderInvoked, DiagnosticValue(fact?.Outcome),
            DiagnosticValue(fact?.Error), DiagnosticValue(fact?.Observation.Exposure), DiagnosticValue(fact?.Stop),
            fact is null ? null : fact.Observation.Usage.InputTokens.HasValue && fact.Observation.Usage.OutputTokens.HasValue,
            out var result, DiagnosticLimit(fact?.LimitKind)) ? result : null;

    private static ActionHostProviderLimitKind DiagnosticLimit(ProviderLimitKind? value) => value switch
    {
        ProviderLimitKind.Unknown => ActionHostProviderLimitKind.Unknown,
        ProviderLimitKind.ResponseDeclaredBodyBytes => ActionHostProviderLimitKind.ResponseDeclaredBodyBytes,
        ProviderLimitKind.ResponseStreamBodyBytes => ActionHostProviderLimitKind.ResponseStreamBodyBytes,
        ProviderLimitKind.ResponseToolCallCount => ActionHostProviderLimitKind.ResponseToolCallCount,
        ProviderLimitKind.ResponseTextBytes => ActionHostProviderLimitKind.ResponseTextBytes,
        ProviderLimitKind.ResponseIdentifierBytes => ActionHostProviderLimitKind.ResponseIdentifierBytes,
        ProviderLimitKind.ToolNameBytes => ActionHostProviderLimitKind.ToolNameBytes,
        ProviderLimitKind.ToolArgumentBytes => ActionHostProviderLimitKind.ToolArgumentBytes,
        ProviderLimitKind.ReasoningFrameBytes => ActionHostProviderLimitKind.ReasoningFrameBytes,
        ProviderLimitKind.ConstructionToolCallCount => ActionHostProviderLimitKind.ConstructionToolCallCount,
        ProviderLimitKind.ConstructionResponseBytes => ActionHostProviderLimitKind.ConstructionResponseBytes,
        ProviderLimitKind.RequestToolCallCount => ActionHostProviderLimitKind.RequestToolCallCount,
        ProviderLimitKind.RequestResponseBytes => ActionHostProviderLimitKind.RequestResponseBytes,
        ProviderLimitKind.ResponseReplayFrameBytes => ActionHostProviderLimitKind.ResponseReplayFrameBytes,
        _ => ActionHostProviderLimitKind.Unknown,
    };

    private static ActionHostRuntimeDiagnosticValue DiagnosticValue(ProviderError? value) => value switch
    {
        ProviderError.None => ActionHostRuntimeDiagnosticValue.None,
        ProviderError.InvalidInput => ActionHostRuntimeDiagnosticValue.InvalidInput,
        ProviderError.InvalidAssociation => ActionHostRuntimeDiagnosticValue.InvalidAssociation,
        ProviderError.UnsupportedCapability => ActionHostRuntimeDiagnosticValue.UnsupportedCapability,
        ProviderError.InvalidEncoding => ActionHostRuntimeDiagnosticValue.InvalidEncoding,
        ProviderError.LimitExceeded => ActionHostRuntimeDiagnosticValue.LimitExceeded,
        ProviderError.InvalidResponse => ActionHostRuntimeDiagnosticValue.InvalidResponse,
        ProviderError.ContinuationMismatch => ActionHostRuntimeDiagnosticValue.ContinuationMismatch,
        ProviderError.Cancelled => ActionHostRuntimeDiagnosticValue.Cancelled,
        ProviderError.ProviderFailed => ActionHostRuntimeDiagnosticValue.ProviderFailed,
        ProviderError.ObservationClosed => ActionHostRuntimeDiagnosticValue.ObservationClosed,
        ProviderError.ObservationConflict => ActionHostRuntimeDiagnosticValue.ObservationConflict,
        _ => ActionHostRuntimeDiagnosticValue.Unknown,
    };

    private static ActionHostRuntimeDiagnosticValue DiagnosticValue(ProviderOutcome? value) => value switch
    {
        ProviderOutcome.Succeeded => ActionHostRuntimeDiagnosticValue.Succeeded,
        ProviderOutcome.Rejected => ActionHostRuntimeDiagnosticValue.Rejected,
        ProviderOutcome.Cancelled => ActionHostRuntimeDiagnosticValue.Cancelled,
        ProviderOutcome.Failed => ActionHostRuntimeDiagnosticValue.Failed,
        _ => ActionHostRuntimeDiagnosticValue.Unknown,
    };

    private static ActionHostRuntimeDiagnosticValue DiagnosticValue(DispatchExposure? value) => value switch
    {
        DispatchExposure.NotDispatched => ActionHostRuntimeDiagnosticValue.NotDispatched,
        DispatchExposure.Dispatched => ActionHostRuntimeDiagnosticValue.Dispatched,
        DispatchExposure.Unknown => ActionHostRuntimeDiagnosticValue.Unknown,
        _ => ActionHostRuntimeDiagnosticValue.Unknown,
    };

    private static ActionHostRuntimeDiagnosticValue DiagnosticValue(RuntimeStop? value) => value switch
    {
        RuntimeStop.None => ActionHostRuntimeDiagnosticValue.None,
        RuntimeStop.UnsupportedCapability => ActionHostRuntimeDiagnosticValue.UnsupportedCapability,
        RuntimeStop.MissingHooks => ActionHostRuntimeDiagnosticValue.MissingHooks,
        RuntimeStop.InvalidAssociation => ActionHostRuntimeDiagnosticValue.InvalidAssociation,
        RuntimeStop.ResourceLimit => ActionHostRuntimeDiagnosticValue.ResourceLimit,
        RuntimeStop.Cancelled => ActionHostRuntimeDiagnosticValue.Cancelled,
        RuntimeStop.ExposureDenied => ActionHostRuntimeDiagnosticValue.ExposureDenied,
        RuntimeStop.ExposureMissing => ActionHostRuntimeDiagnosticValue.ExposureMissing,
        RuntimeStop.ExposureFailed => ActionHostRuntimeDiagnosticValue.ExposureFailed,
        RuntimeStop.ExposureUnknown => ActionHostRuntimeDiagnosticValue.ExposureUnknown,
        RuntimeStop.ExposureMismatch => ActionHostRuntimeDiagnosticValue.ExposureMismatch,
        RuntimeStop.DurableAcknowledgementRequired => ActionHostRuntimeDiagnosticValue.DurableAcknowledgementRequired,
        RuntimeStop.SettlementMissing => ActionHostRuntimeDiagnosticValue.SettlementMissing,
        RuntimeStop.SettlementFailed => ActionHostRuntimeDiagnosticValue.SettlementFailed,
        RuntimeStop.SettlementUnknown => ActionHostRuntimeDiagnosticValue.SettlementUnknown,
        RuntimeStop.SettlementMismatch => ActionHostRuntimeDiagnosticValue.SettlementMismatch,
        RuntimeStop.HostStopped => ActionHostRuntimeDiagnosticValue.HostStopped,
        RuntimeStop.DurationLimit => ActionHostRuntimeDiagnosticValue.DurationLimit,
        _ => ActionHostRuntimeDiagnosticValue.Unknown,
    };

    private static ActionHostRuntimeDiagnosticValue DiagnosticValue(CandidateStopReason? value) => value switch
    {
        CandidateStopReason.Completed => ActionHostRuntimeDiagnosticValue.Completed,
        CandidateStopReason.HostEnded => ActionHostRuntimeDiagnosticValue.HostEnded,
        CandidateStopReason.ProductionExhausted => ActionHostRuntimeDiagnosticValue.ProductionExhausted,
        CandidateStopReason.SubmissionLimit => ActionHostRuntimeDiagnosticValue.SubmissionLimit,
        CandidateStopReason.WorkUnitLimit => ActionHostRuntimeDiagnosticValue.WorkUnitLimit,
        CandidateStopReason.RepairLimit => ActionHostRuntimeDiagnosticValue.RepairLimit,
        CandidateStopReason.ContinuationLimit => ActionHostRuntimeDiagnosticValue.ContinuationLimit,
        CandidateStopReason.MissingAcknowledgement => ActionHostRuntimeDiagnosticValue.MissingAcknowledgement,
        CandidateStopReason.FailedAcknowledgement => ActionHostRuntimeDiagnosticValue.FailedAcknowledgement,
        CandidateStopReason.UnknownAcknowledgement => ActionHostRuntimeDiagnosticValue.UnknownAcknowledgement,
        CandidateStopReason.MismatchedFeedback => ActionHostRuntimeDiagnosticValue.MismatchedFeedback,
        CandidateStopReason.DuplicateFeedback => ActionHostRuntimeDiagnosticValue.DuplicateFeedback,
        CandidateStopReason.Cancelled => ActionHostRuntimeDiagnosticValue.Cancelled,
        CandidateStopReason.ProductionFailed => ActionHostRuntimeDiagnosticValue.ProductionFailed,
        CandidateStopReason.ProgressObserverFailed => ActionHostRuntimeDiagnosticValue.ProgressObserverFailed,
        CandidateStopReason.UnsupportedCapability => ActionHostRuntimeDiagnosticValue.UnsupportedCapability,
        CandidateStopReason.DurationLimit => ActionHostRuntimeDiagnosticValue.DurationLimit,
        CandidateStopReason.RuntimeLimit => ActionHostRuntimeDiagnosticValue.RuntimeLimit,
        CandidateStopReason.ProductionStopped => ActionHostRuntimeDiagnosticValue.ProductionStopped,
        CandidateStopReason.UsageAccountingUnavailable => ActionHostRuntimeDiagnosticValue.UsageAccountingUnavailable,
        _ => ActionHostRuntimeDiagnosticValue.Unknown,
    };

    private static ActionHostRuntimeDiagnosticValue DiagnosticValue(AgentTerminationReason? value) => value switch
    {
        AgentTerminationReason.Completed => ActionHostRuntimeDiagnosticValue.Completed,
        AgentTerminationReason.Partial => ActionHostRuntimeDiagnosticValue.Partial,
        AgentTerminationReason.ResourceLimit => ActionHostRuntimeDiagnosticValue.ResourceLimit,
        AgentTerminationReason.Cancelled => ActionHostRuntimeDiagnosticValue.Cancelled,
        AgentTerminationReason.Failed => ActionHostRuntimeDiagnosticValue.Failed,
        AgentTerminationReason.UnsupportedCapability => ActionHostRuntimeDiagnosticValue.UnsupportedCapability,
        _ => ActionHostRuntimeDiagnosticValue.Unknown,
    };

    public override string ToString() => nameof(SolusReviewFacts);
}

internal sealed record SolusAttemptFact(UsageAttemptObservation Observation, ProviderOutcome? Outcome, RuntimeStop Stop,
    ProviderError? Error = null, bool? ProviderInvoked = null, ProviderLimitKind LimitKind = ProviderLimitKind.Unknown)
{
    public override string ToString() => nameof(SolusAttemptFact);
}
