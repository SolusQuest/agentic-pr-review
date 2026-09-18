using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

// Reservation accounting for one schedule. Reserved counters only grow:
// observed usage updates known counters and can never release reservation
// capacity, because the repository has no provider billing oracle.
internal sealed class LiveAccounting(LivePlanBounds bounds)
{
    private int _requestRejected;
    private int _responseTooLarge;
    private int _http4xx;
    private int _http429;
    private int _http5xx;
    private int _connectTimeout;
    private int _providerTimeout;
    private int _transportFailure;
    private int _budgetRefused;
    private int _violationRefused;
    private int _cancelled;
    private int _backendExceptions;
    private int _normalizationExceptions;

    internal long Sends { get; private set; }
    internal long ReservedInputTokens { get; private set; }
    internal long ReservedOutputTokens { get; private set; }
    internal long ReservedCombinedTokens { get; private set; }
    internal long ReservedSpendMicroUsd { get; private set; }
    internal long KnownInputTokens { get; private set; }
    internal long KnownOutputTokens { get; private set; }
    internal long KnownCombinedTokens { get; private set; }
    internal int UsageUnknownCalls { get; private set; }
    internal bool AccountingViolation { get; private set; }
    internal bool BudgetRefused => _budgetRefused > 0;
    internal bool RateLimited => _http429 > 0;

    internal LiveTransportOutcomeCounts Outcomes => new(
        _requestRejected, _responseTooLarge, _http4xx, _http429, _http5xx,
        _connectTimeout, _providerTimeout, _transportFailure, _budgetRefused,
        _violationRefused, _cancelled, _backendExceptions, _normalizationExceptions);

    // Gate evaluated before a send is attempted. Returns false when any
    // authorized reservation bound would be exceeded; the send must not occur.
    // A falsified reservation basis also refuses: accounting_violation must
    // halt further sends inside the same evaluation, not just stop the loop.
    // Subtraction form is overflow-safe because reserved <= total always holds.
    internal bool TryReserve()
    {
        if (AccountingViolation)
        {
            _violationRefused++;
            return false;
        }
        var perCall = bounds.PerCall;
        if (Sends + 1 > bounds.MaxModelCalls ||
            perCall.MaxInputTokens > bounds.MaxInputTokens - ReservedInputTokens ||
            perCall.MaxOutputTokens > bounds.MaxOutputTokens - ReservedOutputTokens ||
            perCall.MaxInputTokens + perCall.MaxOutputTokens >
                bounds.MaxCombinedTokens - ReservedCombinedTokens ||
            perCall.MaxChargeMicroUsd > bounds.SpendCeilingMicroUsd - ReservedSpendMicroUsd)
        {
            _budgetRefused++;
            return false;
        }
        Sends++;
        ReservedInputTokens += perCall.MaxInputTokens;
        ReservedOutputTokens += perCall.MaxOutputTokens;
        ReservedCombinedTokens += perCall.MaxInputTokens + perCall.MaxOutputTokens;
        ReservedSpendMicroUsd += perCall.MaxChargeMicroUsd;
        return true;
    }

    internal void RecordOutcome(DeepSeekTransportResult result)
    {
        switch (result.Outcome)
        {
            case DeepSeekTransportOutcome.RequestRejected: _requestRejected++; break;
            case DeepSeekTransportOutcome.HttpFailure:
                if (result.StatusClass == DeepSeekHttpStatusClass.TooManyRequests) _http429++;
                else if (result.StatusClass == DeepSeekHttpStatusClass.Other5xx) _http5xx++;
                else _http4xx++;
                break;
            case DeepSeekTransportOutcome.ConnectTimeout: _connectTimeout++; break;
            case DeepSeekTransportOutcome.ProviderTimeout: _providerTimeout++; break;
            case DeepSeekTransportOutcome.Success: break;
            case DeepSeekTransportOutcome.ResponseTooLarge:
                _responseTooLarge++;
                // The provider body was discarded before usage could be parsed:
                // the permanent reservation stands, and the call is honestly
                // counted as usage-unknown rather than known zero usage.
                UsageUnknownCalls++;
                break;
            default: _transportFailure++; break;
        }
    }

    internal void RecordCancelled() => _cancelled++;

    // Observed usage is recorded only as known counters. Usage above the
    // authorized per-call bound falsifies the reservation basis itself.
    internal void RecordUsage(ProjectChatUsage usage)
    {
        if (usage.InputTokens > bounds.PerCall.MaxInputTokens ||
            usage.OutputTokens > bounds.PerCall.MaxOutputTokens ||
            usage.InputTokens + usage.OutputTokens >
                bounds.PerCall.MaxInputTokens + bounds.PerCall.MaxOutputTokens)
            AccountingViolation = true;
        KnownInputTokens += usage.InputTokens;
        KnownOutputTokens += usage.OutputTokens;
        KnownCombinedTokens += usage.InputTokens + usage.OutputTokens;
    }

    internal void RecordUsageUnknown() => UsageUnknownCalls++;

    internal void RecordChatException(Exception error)
    {
        if (error is ProjectChatNormalizationException) _normalizationExceptions++;
        else _backendExceptions++;
    }
}
