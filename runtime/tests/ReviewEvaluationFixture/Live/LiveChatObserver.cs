using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Accounting;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

// Chat-layer observer: the only seam that sees admitted usage and typed
// backend exceptions, both erased inside AgentRunOutcome.
internal sealed class LiveChatObserver(IProjectChatClient inner, LiveAccounting accounting,
    UsageJournalCollector.AttemptScope? attempt = null)
    : IProjectChatClient
{
    public async Task<ProjectChatResponse> GetResponseAsync(
        ProjectChatRequest request, CancellationToken cancellationToken)
    {
        var call = attempt?.BeginCall();
        try
        {
            var response = await inner.GetResponseAsync(request, cancellationToken);
            // The transport already records oversized responses as usage unknown.
            // Their convertible sentinel carries zero placeholders, not measured
            // usage; AgentLoop rejects the byte count before admitting usage.
            if (response.CapturedResponseBodyBytes > AgentLimits.ResponseBytes)
            {
                call?.Returned(null);
                return response;
            }
            call?.Returned(response.Usage);
            if (response.Usage is { } usage) accounting.RecordUsage(usage);
            else accounting.RecordUsageUnknown();
            return response;
        }
        catch (OperationCanceledException)
        {
            call?.Cancel();
            throw;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            call?.Threw();
            // A local gate refusal produced no provider usage at all; only a
            // call that was actually sent can have unobservable usage.
            if (!accounting.TryAttributeRefusal()) accounting.RecordUsageUnknown();
            accounting.RecordChatException(error);
            throw;
        }
    }
}
