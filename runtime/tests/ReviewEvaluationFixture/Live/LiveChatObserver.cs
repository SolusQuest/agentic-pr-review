using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Accounting;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

// Chat-layer observer: the only seam that sees admitted usage and typed
// backend exceptions, both erased inside AgentRunOutcome.
internal sealed class LiveChatObserver(IProjectChatClient inner, LiveAccounting accounting,
    ILiveAttemptObserver? attempt = null,
    Func<ProjectChatResponse, LiveToolRejectionProjection?>? rejectionProjector = null)
    : IProjectChatClient
{
    private LiveToolRejectionProjection? lastRejection;
    private ProjectChatNormalizationReason? lastNormalizationReason;

    internal LiveToolRejectionProjection? TakeRejection()
    {
        var value = lastRejection;
        lastRejection = null;
        return value;
    }

    internal ProjectChatNormalizationReason? TakeNormalizationReason()
    {
        var value = lastNormalizationReason;
        lastNormalizationReason = null;
        return value;
    }

    public async Task<ProjectChatResponse> GetResponseAsync(
        ProjectChatRequest request, CancellationToken cancellationToken)
    {
        lastRejection = null;
        lastNormalizationReason = null;
        var call = attempt?.BeginCall();
        try
        {
            request = LiveOutputCapClient.Restrict(request, accounting.PerCallOutputAllowance);
            var response = await inner.GetResponseAsync(request, cancellationToken);
            // The transport already records oversized responses as usage unknown.
            // Their convertible sentinel carries zero placeholders, not measured
            // usage; AgentLoop rejects the byte count before admitting usage.
            if (response.CapturedResponseBodyBytes > AgentLimits.ResponseBytes)
            {
                call?.Returned(null);
                return response;
            }
            if (rejectionProjector is not null)
            {
                try { lastRejection = rejectionProjector(response); }
                catch { lastRejection = null; }
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
            if (error is ProjectChatNormalizationException normalization)
                lastNormalizationReason = normalization.Reason;
            call?.Threw();
            // A local gate refusal produced no provider usage at all; only a
            // call that was actually sent can have unobservable usage.
            if (!accounting.TryAttributeRefusal()) accounting.RecordUsageUnknown();
            accounting.RecordChatException(error);
            // The retained evaluation journal admits a subsequent call only
            // after success. It has no provider-retry authority, even when its
            // synthetic plan selects Current; preserve that existing boundary.
            if (error is ProjectChatRetryException)
                throw new AgenticPrReview.Runtime.Execution.DeepSeek.DeepSeekChatBackendException();
            throw;
        }
    }
}

// Apply before measurement as well as before dispatch. The same immutable
// admitted allowance restricts both views; it never refunds a reservation.
internal sealed class LiveOutputCapClient(IProjectChatClient inner, long admittedAllowance) : IProjectChatClient
{
    public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token) =>
        inner.GetResponseAsync(Restrict(request, admittedAllowance), token);

    internal static ProjectChatRequest Restrict(ProjectChatRequest request, long admittedAllowance)
    {
        if (admittedAllowance is < 1 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(admittedAllowance));
        // Admitted named profiles are fixed8192/65536. Never repair a supplied
        // differing value: their writer must reject it without a physical send.
        if (request.MaxOutputTokens is null)
            return request with { MaxOutputTokens = (int)admittedAllowance };
        if (admittedAllowance <= LivePlanAdmission.RequestOutputFor(
                AgenticPrReview.Runtime.Execution.DeepSeek.DeepSeekRequestProfile.Current) &&
            request.MaxOutputTokens is >= 1 and <= 65_536)
            return request with { MaxOutputTokens = (int)Math.Min(request.MaxOutputTokens.Value, admittedAllowance) };
        return request;
    }
}
