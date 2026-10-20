namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;

// Generic monotonic scheduling only. Native SESSION campaigns are retired.
internal static class EconomicsRunner
{
    internal static string JournalStop(string stop) => stop is "complete" or "accounting_violation" or "rate_limited" or
        "caller_cancelled" or "deadline" ? stop : "infrastructure_failed";


    // Timer completion is only a wake-up hint. Some timer clocks wake slightly
    // early relative to Stopwatch; recheck the monotonic floor before admission.
    internal static async Task WaitSpacingAsync(int milliseconds, Func<long> elapsed,
        Func<int, CancellationToken, Task> delay, CancellationToken token)
    {
        var target = checked(elapsed() + milliseconds);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var remaining = target - elapsed();
            if (remaining <= 0) return;
            await delay(checked((int)remaining), token);
        }
    }

}
