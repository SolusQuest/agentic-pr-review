namespace AgenticPrReview.Runtime.Agent.Core;

internal enum AccountingCompleteness
{
    Complete,
    Partial,
    Unavailable,
}

// Measurements only. Null is unknown, including an invalid observation; it is
// never an estimated zero or a cache partition inferred by subtraction.
internal sealed class ProviderUsageObservation
{
    private ProviderUsageObservation(long? input, long? output, long? hit, long? miss)
    {
        InputTokens = input;
        OutputTokens = output;
        CacheHitTokens = hit;
        CacheMissTokens = miss;
    }

    internal long? InputTokens { get; }
    internal long? OutputTokens { get; }
    internal long? CacheHitTokens { get; }
    internal long? CacheMissTokens { get; }
    internal bool HasAny => InputTokens.HasValue || OutputTokens.HasValue ||
        CacheHitTokens.HasValue || CacheMissTokens.HasValue;
    internal bool IsComplete => InputTokens.HasValue && OutputTokens.HasValue &&
        CacheHitTokens.HasValue && CacheMissTokens.HasValue;
    internal static ProviderUsageObservation Unknown { get; } = Create(null, null);

    internal static ProviderUsageObservation Create(
        long? input, long? output, long? hit = null, long? miss = null, long? total = null)
    {
        input = Nonnegative(input);
        output = Nonnegative(output);
        total = Nonnegative(total);
        if (total is { } sum &&
            (input > sum || output > sum ||
             (input.HasValue && output.HasValue && output.Value != sum - input.Value)))
        {
            input = null;
            output = null;
        }

        hit = input.HasValue && Nonnegative(hit) <= input ? Nonnegative(hit) : null;
        miss = input.HasValue && Nonnegative(miss) <= input ? Nonnegative(miss) : null;
        if (hit.HasValue && miss.HasValue && miss.Value != input!.Value - hit.Value)
        {
            hit = null;
            miss = null;
        }

        return new(input, output, hit, miss);
    }

    private static long? Nonnegative(long? value) => value >= 0 ? value : null;
    public override string ToString() => "provider_usage_observation";
}

// A physical send is distinct from a logical invocation.
// No request/response/exception is retained.
internal sealed record ProviderAttemptObservation(
    int LogicalCallOrdinal,
    int AttemptOrdinal,
    bool DispatchObserved,
    bool Dispatched,
    bool Reconciled,
    bool Succeeded,
    ProviderUsageObservation Usage);

// Parser receives only the ability to publish validated numeric observations,
// not dispatch permission, finalization, or any provider/Host capabilities.
internal interface IProviderUsageObserver
{
    void RecordUsage(ProviderUsageObservation observation);
}

internal sealed class ProviderAttemptCapture(int logicalCallOrdinal, int attemptOrdinal,
    Func<bool>? admitDispatch = null) : IProviderUsageObserver
{
    private readonly object gate = new();
    private bool observed;
    private bool dispatched;
    private bool forcedFailure;
    private bool completed;

    internal bool CanRetry
    {
        get { lock (gate) return frozen is { Dispatched: true, Reconciled: true, Succeeded: false } && completed; }
    }
    private ProviderUsageObservation? usage;
    private ProviderAttemptObservation? frozen;

    internal void ObserveNoDispatch()
    {
        lock (gate)
        {
            if (frozen is null && !dispatched) observed = true;
        }
    }

    internal void ObserveUnavailableDispatch()
    {
        lock (gate)
        {
            if (frozen is null && !dispatched) observed = false;
        }
    }

    // This is the dispatch permission: freeze winning first prohibits HTTP.
    internal bool TryBeginDispatch()
    {
        lock (gate)
        {
            if (frozen is not null || dispatched) return false;
            if (admitDispatch is not null && !admitDispatch()) return false;
            observed = true;
            dispatched = true;
            return true;
        }
    }

    internal void RecordUsage(ProviderUsageObservation observation)
    {
        lock (gate)
        {
            if (frozen is null && usage is null && (!observed || dispatched))
                usage = observation;
        }
    }

    void IProviderUsageObserver.RecordUsage(ProviderUsageObservation observation) => RecordUsage(observation);

    internal void MarkFailed()
    {
        lock (gate)
        {
            if (frozen is null) forcedFailure = true;
        }
    }

    internal ProviderAttemptObservation Freeze(bool operationCompleted, bool succeeded)
    {
        lock (gate)
        {
            if (frozen is null) completed = operationCompleted;
            return frozen ??= new(
                logicalCallOrdinal, attemptOrdinal, observed, dispatched,
                observed && (operationCompleted || !dispatched),
                operationCompleted && succeeded && !forcedFailure,
                usage ?? ProviderUsageObservation.Unknown);
        }
    }

    public override string ToString() => "provider_attempt_capture";
}

internal static class ProviderRetryLimits
{
    internal const int PerCall = 2;
    internal const int PerReview = 8;
}

internal sealed class ProviderLogicalCall(int ordinal, Func<bool> admitRetry)
{
    private readonly List<ProviderAttemptCapture> attempts = [];

    internal ProviderAttemptCapture BeginAttempt()
    {
        if (attempts.Count > ProviderRetryLimits.PerCall ||
            (attempts.Count > 0 && !attempts[^1].CanRetry))
            throw new InvalidOperationException("Provider attempt is not admissible.");
        var attempt = new ProviderAttemptCapture(ordinal, attempts.Count,
            attempts.Count == 0 ? null : admitRetry);
        attempts.Add(attempt);
        return attempt;
    }

    internal IEnumerable<ProviderAttemptObservation> Finish()
    {
        if (attempts.Count == 0) BeginAttempt();
        return attempts.Select(attempt => attempt.Freeze(false, false));
    }
}

internal sealed class ReviewAccounting
{
    private readonly int modelCallLimit;
    private readonly List<ProviderLogicalCall> calls = [];
    private readonly object dispatchGate = new();
    private int retries;

    internal ReviewAccounting(int modelCallLimit = AgentLimits.ModelCalls)
    {
        if (modelCallLimit is < 1 or > AgentLimits.ModelCallsCeiling)
            throw new ArgumentOutOfRangeException(nameof(modelCallLimit));
        this.modelCallLimit = modelCallLimit;
    }

    internal bool CanRetry { get { lock (dispatchGate) return retries < ProviderRetryLimits.PerReview; } }

    private bool AdmitRetry()
    {
        lock (dispatchGate)
        {
            if (retries >= ProviderRetryLimits.PerReview) return false;
            retries++;
            return true;
        }
    }

    internal ProviderLogicalCall BeginCall()
    {
        if (calls.Count >= modelCallLimit)
            throw new InvalidOperationException("Logical call accounting limit exceeded.");
        var call = new ProviderLogicalCall(calls.Count, AdmitRetry);
        calls.Add(call);
        return call;
    }

    internal ProviderAccounting Finish() => ProviderAccounting.Aggregate(
        calls.SelectMany(call => call.Finish()).ToArray());
}

// Only this validated, immutable projection crosses the existing outcome seam.
// Counts/sums are observed lower bounds unless their completeness is Complete.
// Null token sums denote overflow; null outcome.Accounting denotes no finalizer.
internal sealed class ProviderAccounting
{
    private ProviderAccounting() { }

    internal int ModelCalls { get; private init; }
    internal int ProviderAttempts { get; private init; }
    internal int ProviderRetries { get; private init; }
    internal int ProviderFailedAttempts { get; private init; }
    internal int UnknownUsageAttempts { get; private init; }
    internal int UnknownCachePartitionAttempts { get; private init; }
    internal long? InputTokens { get; private init; }
    internal long? CacheHitTokens { get; private init; }
    internal long? CacheMissTokens { get; private init; }
    internal long? OutputTokens { get; private init; }
    internal AccountingCompleteness AttemptCompleteness { get; private init; }
    internal AccountingCompleteness UsageCompleteness { get; private init; }

    internal static ProviderAccounting Aggregate(IReadOnlyList<ProviderAttemptObservation> attempts)
    {
        if (!ValidSequence(attempts) ||
            attempts.Any(attempt =>
                (attempt.Dispatched && !attempt.DispatchObserved) ||
                (attempt.Reconciled && !attempt.DispatchObserved) ||
                (attempt.DispatchObserved && !attempt.Dispatched && attempt.Usage.HasAny) ||
                (attempt.Dispatched && attempt.Succeeded && !attempt.Reconciled)))
            throw new ArgumentException("Invalid provider accounting observations.");

        var sends = attempts.Where(attempt => attempt.Dispatched).ToArray();
        var completeness = attempts.All(attempt => attempt.Reconciled)
            ? AccountingCompleteness.Complete
            : attempts.Any(attempt => attempt.DispatchObserved)
                ? AccountingCompleteness.Partial : AccountingCompleteness.Unavailable;
        var input = Sum(attempts, usage => usage.InputTokens);
        var output = Sum(attempts, usage => usage.OutputTokens);
        var hit = Sum(attempts, usage => usage.CacheHitTokens);
        var miss = Sum(attempts, usage => usage.CacheMissTokens);
        var usageComplete = completeness == AccountingCompleteness.Complete &&
            sends.All(attempt => attempt.Usage.IsComplete) &&
            input.HasValue && output.HasValue && hit.HasValue && miss.HasValue;
        return new()
        {
            ModelCalls = attempts.Count(attempt => attempt.AttemptOrdinal == 0),
            ProviderRetries = sends.Count(attempt => attempt.AttemptOrdinal > 0),
            ProviderAttempts = sends.Length,
            ProviderFailedAttempts = sends.Count(attempt => !attempt.Succeeded),
            UnknownUsageAttempts = sends.Count(attempt =>
                !attempt.Usage.InputTokens.HasValue || !attempt.Usage.OutputTokens.HasValue),
            UnknownCachePartitionAttempts = sends.Count(attempt =>
                attempt.Usage.InputTokens.HasValue &&
                (!attempt.Usage.CacheHitTokens.HasValue || !attempt.Usage.CacheMissTokens.HasValue)),
            InputTokens = input,
            OutputTokens = output,
            CacheHitTokens = hit,
            CacheMissTokens = miss,
            AttemptCompleteness = completeness,
            UsageCompleteness = usageComplete ? AccountingCompleteness.Complete
                : attempts.Any(attempt => attempt.Usage.HasAny)
                    ? AccountingCompleteness.Partial : AccountingCompleteness.Unavailable,
        };
    }

    private static bool ValidSequence(IReadOnlyList<ProviderAttemptObservation> attempts)
    {
        var logical = -1;
        var ordinal = -1;
        var retries = 0;
        ProviderAttemptObservation? previous = null;
        foreach (var attempt in attempts)
        {
            if (attempt.AttemptOrdinal == 0)
            {
                logical++;
                ordinal = 0;
            }
            else
            {
                ordinal++;
                if (previous is null || previous.Succeeded) return false;
            }
            if (attempt.LogicalCallOrdinal != logical || attempt.AttemptOrdinal != ordinal ||
                ordinal > ProviderRetryLimits.PerCall || logical >= AgentLimits.ModelCallsCeiling)
                return false;
            if (ordinal > 0 && attempt.Dispatched && ++retries > ProviderRetryLimits.PerReview)
                return false;
            previous = attempt;
        }
        return true;
    }

    private static long? Sum(IReadOnlyList<ProviderAttemptObservation> attempts,
        Func<ProviderUsageObservation, long?> select)
    {
        long sum = 0;
        foreach (var attempt in attempts)
        {
            if (select(attempt.Usage) is not { } value) continue;
            try { sum = checked(sum + value); }
            catch (OverflowException) { return null; }
        }
        return sum;
    }

    public override string ToString() => "provider_accounting";
}
