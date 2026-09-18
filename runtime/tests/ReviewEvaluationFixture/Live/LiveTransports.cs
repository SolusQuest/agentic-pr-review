using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

// Live transport construction is reachable only through execute mode after
// plan+corpus+credential admission; dry-run never touches the factory.
internal interface ILiveTransportFactory
{
    IDeepSeekTransport Create(DeepSeekCredential credential);
}

internal sealed class LiveDeepSeekTransportFactory : ILiveTransportFactory
{
    internal static readonly LiveDeepSeekTransportFactory Instance = new();

    public IDeepSeekTransport Create(DeepSeekCredential credential) =>
        DeepSeekTransport.Create(credential);
}

// Metered wrapper at the transport boundary: the only place that sees the
// reservation gate, typed provider outcomes (429, timeouts) and send counts.
internal sealed class LiveMeteredTransport(IDeepSeekTransport inner, LiveAccounting accounting)
    : IDeepSeekTransport
{
    public async Task<DeepSeekTransportResult> SendAsync(
        ReadOnlyMemory<byte> requestBody, CancellationToken cancellationToken)
    {
        // Cancellation is recorded when the token fires, not when the abandoned
        // continuation happens to run: AgentLoop waits the chat call via
        // WaitAsync, so the inner task's own OCE would race summary publication.
        var flag = new CancellationFlag();
        using var registration = cancellationToken.Register(static state =>
        {
            var (metered, seen) = ((LiveMeteredTransport, CancellationFlag))state!;
            metered.RecordCancelledOnce(seen);
        }, (this, flag));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!accounting.TryReserve())
                return DeepSeekTransportResult.TransportFailure();
            var result = await inner.SendAsync(requestBody, cancellationToken);
            accounting.RecordOutcome(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            RecordCancelledOnce(flag);
            throw;
        }
    }

    private void RecordCancelledOnce(CancellationFlag flag)
    {
        if (Interlocked.Exchange(ref flag.Seen, 1) == 0) accounting.RecordCancelled();
    }

    private sealed class CancellationFlag { internal int Seen; }

    public void Dispose() => inner.Dispose();
}
