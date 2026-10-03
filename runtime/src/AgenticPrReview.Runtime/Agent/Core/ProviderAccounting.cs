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

// A transport operation is distinct from a logical invocation, even while R2
// permits only attempt ordinal zero. No request/response/exception is retained.
internal sealed record ProviderAttemptObservation(
    int LogicalCallOrdinal,
    int AttemptOrdinal,
    bool DispatchObserved,
    bool Dispatched,
    bool Reconciled,
    bool Succeeded,
    ProviderUsageObservation Usage);

internal sealed class ProviderAttemptCapture(int logicalCallOrdinal, int attemptOrdinal)
{
    private readonly object gate = new();
    private bool observed;
    private bool dispatched;
    private bool forcedFailure;
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
            return frozen ??= new(
                logicalCallOrdinal, attemptOrdinal, observed, dispatched,
                observed && (operationCompleted || !dispatched),
                operationCompleted && succeeded && !forcedFailure,
                usage ?? ProviderUsageObservation.Unknown);
        }
    }

    public override string ToString() => "provider_attempt_capture";
}

internal sealed class ProviderLogicalCall(int ordinal)
{
    private ProviderAttemptCapture? attempt;

    internal ProviderAttemptCapture BeginAttempt()
    {
        if (attempt is not null)
            throw new InvalidOperationException("Provider retries are disabled.");
        return attempt = new ProviderAttemptCapture(ordinal, 0);
    }

    internal ProviderAttemptObservation Finish() =>
        (attempt ??= new ProviderAttemptCapture(ordinal, 0)).Freeze(false, false);
}

internal sealed class ReviewAccounting
{
    private readonly List<ProviderLogicalCall> calls = [];

    internal ProviderLogicalCall BeginCall()
    {
        if (calls.Count >= AgentLimits.ModelCalls)
            throw new InvalidOperationException("Logical call accounting limit exceeded.");
        var call = new ProviderLogicalCall(calls.Count);
        calls.Add(call);
        return call;
    }

    internal ProviderAccounting Finish() => ProviderAccounting.Aggregate(
        calls.Select(call => call.Finish()).ToArray());
}

// Only this validated, immutable projection crosses the existing outcome seam.
// Counts/sums are observed lower bounds unless their completeness is Complete.
// Null token sums denote overflow; null outcome.Accounting denotes no finalizer.
internal sealed class ProviderAccounting
{
    private ProviderAccounting() { }

    internal int ModelCalls { get; private init; }
    internal int ProviderAttempts { get; private init; }
    internal int ProviderRetries => 0;
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
        if (attempts.Count > AgentLimits.ModelCalls ||
            attempts.Where((attempt, index) => attempt.LogicalCallOrdinal != index ||
                attempt.AttemptOrdinal != 0 ||
                (attempt.Dispatched && !attempt.DispatchObserved) ||
                (attempt.Reconciled && !attempt.DispatchObserved) ||
                (attempt.DispatchObserved && !attempt.Dispatched && attempt.Usage.HasAny) ||
                (attempt.Dispatched && attempt.Succeeded && !attempt.Reconciled)).Any())
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
            ModelCalls = attempts.Count,
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
