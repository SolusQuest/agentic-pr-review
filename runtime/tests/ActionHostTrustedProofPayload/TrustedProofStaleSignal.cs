namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

internal interface ITrustedProofStaleSignal
{
    ValueTask SignalReadyAndWaitForReleaseAsync(
        CancellationToken cancellationToken);
}

internal sealed class TrustedProofNoStaleSignal : ITrustedProofStaleSignal
{
    internal static TrustedProofNoStaleSignal Instance { get; } = new();

    private TrustedProofNoStaleSignal() { }

    public ValueTask SignalReadyAndWaitForReleaseAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

internal sealed class TrustedProofStaleSignal : ITrustedProofStaleSignal
{
    private readonly TaskCompletionSource ready = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int signalled;

    internal Task Ready => ready.Task;

    internal void Release() => released.TrySetResult();

    internal void Cancel(CancellationToken cancellationToken)
    {
        ready.TrySetCanceled(cancellationToken);
        released.TrySetCanceled(cancellationToken);
    }

    public async ValueTask SignalReadyAndWaitForReleaseAsync(
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref signalled, 1) != 0)
        {
            throw new InvalidOperationException(
                "The stale-window signal may be emitted only once.");
        }

        ready.TrySetResult();
        await released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
