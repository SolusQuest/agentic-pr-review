using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Accounting;

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
internal sealed class LiveMeteredTransport(IDeepSeekTransport inner, LiveAccounting accounting,
    ILiveAttemptObserver? attempt = null)
    : IDeepSeekTransport
{
    public async Task<DeepSeekTransportResult> SendAsync(
        ReadOnlyMemory<byte> requestBody, CancellationToken cancellationToken)
    {
        var call = attempt?.CurrentCall;
        // Cancellation is recorded when the token fires, not when the abandoned
        // continuation happens to run: AgentLoop waits the chat call via
        // WaitAsync, so the inner task's own OCE would race summary publication.
        var flag = new CancellationFlag();
        using var registration = cancellationToken.Register(static state =>
        {
            var (metered, seen, current) =
                ((LiveMeteredTransport, CancellationFlag, ILiveCallObserver?))state!;
            current?.Cancel();
            metered.RecordCancelledOnce(seen);
        }, (this, flag, call));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!accounting.TryReserve())
            {
                call?.Refuse(accounting.AccountingViolation ? "violation_refused" : "budget_refused");
                return DeepSeekTransportResult.TransportFailure();
            }
            if (call is not null && !call.Dispatch())
            {
                cancellationToken.ThrowIfCancellationRequested();
                return DeepSeekTransportResult.TransportFailure();
            }
            var result = await inner.SendAsync(requestBody, cancellationToken);
            call?.TransportFinished(result);
            accounting.RecordOutcome(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            call?.Cancel();
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
