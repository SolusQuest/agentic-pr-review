namespace AgenticPrReview.Runtime.Execution.DeepSeek;

internal interface IDeepSeekTransport : IDisposable
{
    Task<DeepSeekTransportResult> SendAsync(
        ReadOnlyMemory<byte> requestBody,
        CancellationToken cancellationToken);
}
