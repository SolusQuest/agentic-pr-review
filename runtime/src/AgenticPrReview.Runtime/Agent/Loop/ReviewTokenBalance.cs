using AgenticPrReview.Runtime.Agent.Core;

namespace AgenticPrReview.Runtime.Agent.Loop;

// Stopping debit is deliberately separate from measured provider accounting.
// Unknown partitions debit full known input to uncached, without inventing misses.
internal sealed class ReviewTokenBalance(ReviewTokenBudget budget)
{
    private long uncached;
    private long cached;
    private long output;

    internal bool Exhausted => uncached >= budget.UncachedInputTokens ||
        cached >= budget.CachedInputTokens || output >= budget.OutputTokens;
    internal bool Overrun => uncached > budget.UncachedInputTokens ||
        cached > budget.CachedInputTokens || output > budget.OutputTokens;
    internal int OutputAllowance => (int)Math.Min(65_536, Math.Max(0, budget.OutputTokens - output));

    internal void Debit(ProviderAttemptObservation attempt)
    {
        var usage = attempt.Usage;
        if (usage.InputTokens is { } input)
        {
            if (usage.CacheHitTokens is { } hit && usage.CacheMissTokens is { } miss)
            {
                cached = Add(cached, hit, budget.CachedInputTokens);
                uncached = Add(uncached, miss, budget.UncachedInputTokens);
            }
            else uncached = Add(uncached, input, budget.UncachedInputTokens);
        }
        if (usage.OutputTokens is { } knownOutput)
            output = Add(output, knownOutput, budget.OutputTokens);
    }

    // Saturate at the first overrun; even Int64.MaxValue observations cannot wrap.
    private static long Add(long current, long value, long ceiling) =>
        current + Math.Min(value, ceiling + 1 - current);
}
