using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using SolusAgent.Api.Usage;
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
    private static readonly long ProfileInputLimit = Math.Min(Cr1DevelopmentProfile.InputTokenThreshold, Cr1DevelopmentProfile.InputTokenAllowance);
    private long uncached, cached, output, totalInput;
    private bool frozen, missingCore, refused, budgetStopped, hostStopped;
    private ProviderAccounting? projection;

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
            var fact = new SolusAttemptFact(observation, settlement.ProviderOutcome, settlement.Stop);
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
    public override string ToString() => nameof(SolusReviewFacts);
}

internal sealed record SolusAttemptFact(UsageAttemptObservation Observation, ProviderOutcome? Outcome, RuntimeStop Stop)
{
    public override string ToString() => nameof(SolusAttemptFact);
}
