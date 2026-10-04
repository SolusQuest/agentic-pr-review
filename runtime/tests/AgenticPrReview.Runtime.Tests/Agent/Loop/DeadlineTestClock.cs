namespace AgenticPrReview.Runtime.Tests.Agent.Loop;

internal sealed class DeadlineTestClock(long unixSeconds = 1_800_000_000) : TimeProvider
{
    private long ticks;
    private readonly List<Timer> timers = [];
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => ticks;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(unixSeconds).AddTicks(ticks);
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    internal void Advance(TimeSpan duration)
    {
        var target = checked(ticks + duration.Ticks);
        while (timers.Where(t => t.Due <= target).MinBy(t => t.Due) is { } next)
        {
            ticks = next.Due;
            next.Fire();
        }
        ticks = target;
    }

    // Simulate an event-loop/scheduler stall: callbacks observe the actual late
    // timestamp, not each ideal scheduled due time as in Advance.
    internal void AdvanceWithLateCallbacks(TimeSpan duration)
    {
        ticks = checked(ticks + duration.Ticks);
        foreach (var timer in timers.ToArray())
            if (timer.Due <= ticks) timer.Fire();
    }

    private sealed class Timer(DeadlineTestClock clock, TimerCallback callback, object? state) : ITimer
    {
        internal long Due { get; private set; } = long.MaxValue;
        private long period = -1;
        public bool Change(TimeSpan dueTime, TimeSpan repeat)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(clock.ticks + dueTime.Ticks);
            period = repeat.Ticks;
            return true;
        }
        internal void Fire()
        {
            Due = period > 0 ? checked(Due + period) : long.MaxValue;
            callback(state);
        }
        public void Dispose() => Due = long.MaxValue;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
