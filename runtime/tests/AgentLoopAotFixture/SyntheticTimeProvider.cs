namespace AgenticPrReview.Runtime.AgentLoopAotFixture;

internal sealed class SyntheticTimeProvider : TimeProvider
{
    private long timestamp;
    private readonly long advanceAfterFirstRead;
    private int timestampReads;

    internal SyntheticTimeProvider(
        long unixTimeSeconds,
        long initialTimestamp = 0,
        TimeSpan? advanceAfterFirstRead = null)
    {
        UtcNow = DateTimeOffset.FromUnixTimeSeconds(unixTimeSeconds);
        timestamp = initialTimestamp;
        this.advanceAfterFirstRead =
            advanceAfterFirstRead?.Ticks ?? 0;
    }

    internal DateTimeOffset UtcNow { get; }

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public override long GetTimestamp()
    {
        var current = Volatile.Read(ref timestamp);
        if (Interlocked.Increment(ref timestampReads) == 1 &&
            advanceAfterFirstRead > 0)
        {
            Interlocked.Add(ref timestamp, advanceAfterFirstRead);
        }

        return current;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    internal void Advance(TimeSpan duration) =>
        Interlocked.Add(ref timestamp, duration.Ticks);
}
