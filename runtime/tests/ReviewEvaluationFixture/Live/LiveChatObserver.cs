using AgenticPrReview.Runtime.Agent.Chat;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

// Chat-layer observer: the only seam that sees admitted usage and typed
// backend exceptions, both erased inside AgentRunOutcome.
internal sealed class LiveChatObserver(IProjectChatClient inner, LiveAccounting accounting)
    : IProjectChatClient
{
    public async Task<ProjectChatResponse> GetResponseAsync(
        ProjectChatRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await inner.GetResponseAsync(request, cancellationToken);
            if (response.Usage is { } usage) accounting.RecordUsage(usage);
            else accounting.RecordUsageUnknown();
            return response;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The call's reservation stands; its usage can no longer be known.
            accounting.RecordUsageUnknown();
            accounting.RecordChatException(error);
            throw;
        }
    }
}
