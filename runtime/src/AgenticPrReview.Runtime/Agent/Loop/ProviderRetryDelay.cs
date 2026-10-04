namespace AgenticPrReview.Runtime.Agent.Loop;

internal static class ProviderRetryDelay
{
    // Additional-attempt ordinal starts at 1: full jitter [0,1s), then [0,2s).
    // The same exclusive upper endpoint is used by production and injected RNGs.
    internal static TimeSpan Choose(int ordinal, double unitSample, TimeSpan? retryAfter)
    {
        if (ordinal < 1 || !double.IsFinite(unitSample) || unitSample < 0 || unitSample >= 1)
            throw new ArgumentOutOfRangeException(nameof(unitSample));
        var window = Math.Min(30, Math.Pow(2, Math.Min(ordinal - 1, 5)));
        var jitter = TimeSpan.FromSeconds(window * unitSample);
        return retryAfter > jitter ? retryAfter.Value : jitter;
    }
}
