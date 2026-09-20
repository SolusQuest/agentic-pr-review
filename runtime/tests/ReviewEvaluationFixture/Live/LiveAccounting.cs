using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

// Reservation accounting for one schedule. Reserved counters only grow:
// observed usage updates known counters and can never release reservation
// capacity, because the repository has no provider billing oracle.
internal sealed class LiveAccounting(LivePlanBounds bounds)
{
    private readonly object _gate = new();
    private LiveAccountingSnapshot? _sealed;
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
    private int _refusalsAttributed;
    private int _cacheMeasured;
    private int _cacheUnavailable;
    private long _cacheReadInput;
    private long _uncachedInput;
    private bool _cacheOverflow;
    private bool _responseV4Flash;
    private bool _responseFlash;

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

    internal LiveCacheUsageSummary CacheUsage
    {
        get
        {
            // Unknown total usage and missing cache partition are independent.
            // Sends also covers abandoned/cancelled calls without claiming a
            // per-send journal (the next leaf owns exact reconciliation).
            var incomplete = _cacheUnavailable > 0 || UsageUnknownCalls > 0 ||
                Sends > _cacheMeasured;
            return new(DeepSeekAdapterContext.Provider,
                _cacheOverflow || _cacheMeasured == 0 ? "unavailable" :
                    incomplete ? "partial" : "measured",
                _cacheMeasured, _cacheUnavailable,
                _cacheOverflow || _cacheMeasured == 0 ? null : _cacheReadInput,
                _cacheOverflow || _cacheMeasured == 0 ? null : _uncachedInput,
                "not_applicable", DeepSeekRequestWriter.Model,
                [.. (_responseV4Flash ? new[] { DeepSeekRequestWriter.Model } : []),
                 .. (_responseFlash ? new[] { "deepseek-flash" } : [])],
                "unavailable");
        }
    }

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
        lock (_gate) return _sealed is null && TryReserveCore();
    }

    internal LiveAccountingSnapshot Seal()
    {
        lock (_gate)
            return _sealed ??= new(Sends, ReservedInputTokens, ReservedOutputTokens, ReservedCombinedTokens,
                ReservedSpendMicroUsd, KnownInputTokens, KnownOutputTokens, KnownCombinedTokens,
                UsageUnknownCalls, AccountingViolation, Outcomes, CacheUsage);
    }

    private bool TryReserveCore()
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
        lock (_gate) if (_sealed is null) RecordOutcomeCore(result);
    }

    private void RecordOutcomeCore(DeepSeekTransportResult result)
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

    internal void RecordCancelled()
    {
        lock (_gate) if (_sealed is null) _cancelled++;
    }

    // Observed usage is recorded only as known counters. Usage above the
    // authorized per-call bound falsifies the reservation basis itself.
    internal void RecordUsage(ProjectChatUsage usage)
    {
        lock (_gate) if (_sealed is null) RecordUsageCore(usage);
    }

    private void RecordUsageCore(ProjectChatUsage usage)
    {
        if (usage.InputTokens > bounds.PerCall.MaxInputTokens ||
            usage.OutputTokens > bounds.PerCall.MaxOutputTokens ||
            usage.InputTokens + usage.OutputTokens >
                bounds.PerCall.MaxInputTokens + bounds.PerCall.MaxOutputTokens)
            AccountingViolation = true;
        KnownInputTokens += usage.InputTokens;
        KnownOutputTokens += usage.OutputTokens;
        KnownCombinedTokens += usage.InputTokens + usage.OutputTokens;
        RecordCacheUsage(usage);
    }

    private void RecordCacheUsage(ProjectChatUsage usage)
    {
        var observed = usage.ProviderUsage;
        // Fail closed for the optional projection only. Synthetic backends
        // need not provide it, and invalid optional data cannot reject a run.
        if (observed is null ||
            observed.ProviderId != DeepSeekAdapterContext.Provider ||
            observed.RequestedModel != DeepSeekRequestWriter.Model ||
            observed.ResponseModel is not (DeepSeekRequestWriter.Model or "deepseek-flash") ||
            observed.CacheReadInputTokens < 0 || observed.UncachedInputTokens < 0 ||
            observed.CacheReadInputTokens > usage.InputTokens ||
            observed.UncachedInputTokens != usage.InputTokens - observed.CacheReadInputTokens)
        {
            _cacheUnavailable++;
            return;
        }
        _cacheMeasured++;
        _responseV4Flash |= observed.ResponseModel == DeepSeekRequestWriter.Model;
        _responseFlash |= observed.ResponseModel == "deepseek-flash";
        if (_cacheOverflow) return;
        try
        {
            var hit = checked(_cacheReadInput + observed.CacheReadInputTokens);
            var miss = checked(_uncachedInput + observed.UncachedInputTokens);
            _cacheReadInput = hit;
            _uncachedInput = miss;
        }
        catch (OverflowException)
        {
            _cacheOverflow = true;
        }
    }

    internal void RecordUsageUnknown()
    {
        lock (_gate) if (_sealed is null) UsageUnknownCalls++;
    }

    // Sends are strictly sequential, so a refusal counter increment always
    // precedes the backend exception it produces. Pairing them prevents a
    // local gate refusal — where no provider usage ever existed — from
    // inflating usage_unknown_calls.
    internal bool TryAttributeRefusal()
    {
        lock (_gate) return _sealed is null && TryAttributeRefusalCore();
    }

    private bool TryAttributeRefusalCore()
    {
        if (_budgetRefused + _violationRefused > _refusalsAttributed)
        {
            _refusalsAttributed++;
            return true;
        }
        return false;
    }

    internal void RecordChatException(Exception error)
    {
        lock (_gate)
        {
            if (_sealed is not null) return;
            if (error is ProjectChatNormalizationException) _normalizationExceptions++;
            else _backendExceptions++;
        }
    }
}

internal sealed record LiveAccountingSnapshot(
    long Sends, long ReservedInputTokens, long ReservedOutputTokens, long ReservedCombinedTokens,
    long ReservedSpendMicroUsd, long KnownInputTokens, long KnownOutputTokens, long KnownCombinedTokens,
    int UsageUnknownCalls, bool AccountingViolation, LiveTransportOutcomeCounts Outcomes, LiveCacheUsageSummary CacheUsage);
