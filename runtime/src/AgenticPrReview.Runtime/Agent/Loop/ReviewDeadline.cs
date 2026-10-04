namespace AgenticPrReview.Runtime.Agent.Loop;

// A per-run monotonic clock. Host headroom is transient execution authority;
// it does not change the configured canonical limits identity.
internal sealed class ReviewDeadline
{
    private readonly TimeProvider time;
    private readonly long started;
    private readonly TimeSpan allowance;

    internal ReviewDeadline(TimeProvider time, AgentLimitProfile profile, TimeSpan? hostRemaining,
        int timeoutSeconds = AgentLimits.DeadlineSeconds)
    {
        this.time = time;
        started = time.GetTimestamp();
        var selected = TimeSpan.FromSeconds(profile == AgentLimitProfile.Current
            ? timeoutSeconds : AgentLimits.RetainedDeadlineSeconds);
        allowance = hostRemaining is { } remaining && remaining < selected
            ? (remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero) : selected;
    }

    internal TimeSpan Remaining
    {
        get
        {
            var remaining = allowance - time.GetElapsedTime(started);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    internal bool Expired => Remaining == TimeSpan.Zero;
}
