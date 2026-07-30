namespace AgenticPrReview.Runtime.AgentLoopAotFixture;

internal sealed class SyntheticTimeProvider : TimeProvider
{
    private long timestamp;
    private long advanceAfterFirstRead;
    private int advanceAfterRead;
    private int timestampReads;

    internal SyntheticTimeProvider(
        long unixTimeSeconds,
        long initialTimestamp = 0,
        TimeSpan? advanceAfterFirstRead = null,
        int advanceAfterRead = 1)
    {
        if (advanceAfterRead < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(advanceAfterRead));
        }

        UtcNow = DateTimeOffset.FromUnixTimeSeconds(unixTimeSeconds);
        timestamp = initialTimestamp;
        this.advanceAfterFirstRead =
            advanceAfterFirstRead?.Ticks ?? 0;
        this.advanceAfterRead = advanceAfterRead;
    }

    internal DateTimeOffset UtcNow { get; }

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public override long GetTimestamp()
    {
        var current = Volatile.Read(ref timestamp);
        if (Interlocked.Increment(ref timestampReads) == advanceAfterRead &&
            advanceAfterFirstRead > 0)
        {
            Interlocked.Add(ref timestamp, advanceAfterFirstRead);
        }

        return current;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    internal void Advance(TimeSpan duration) =>
        Interlocked.Add(ref timestamp, duration.Ticks);

    internal void AdvanceAfterNextRead(TimeSpan duration)
    {
        Volatile.Write(ref advanceAfterFirstRead, duration.Ticks);
        Volatile.Write(
            ref advanceAfterRead,
            Volatile.Read(ref timestampReads) + 1);
    }
}
