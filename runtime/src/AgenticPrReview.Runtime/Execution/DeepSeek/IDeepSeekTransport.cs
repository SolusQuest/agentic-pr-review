using AgenticPrReview.Runtime.Agent.Core;

namespace AgenticPrReview.Runtime.Execution.DeepSeek;

internal interface IDeepSeekTransport : IDisposable
{
    Task<DeepSeekTransportResult> SendAsync(
        ReadOnlyMemory<byte> requestBody,
        CancellationToken cancellationToken);
}

// Uninstrumented evaluation transports do not claim observation of physical dispatch.
internal interface IAccountedDeepSeekTransport : IDeepSeekTransport
{
    Task<DeepSeekTransportResult> SendAsync(
        ReadOnlyMemory<byte> requestBody,
        CancellationToken cancellationToken,
        ProviderAttemptCapture? accounting);
}
