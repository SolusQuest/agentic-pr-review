using SolusAgent.Runtime.Api.Exposure;

namespace AgenticPrReview.Runtime.Execution.Solus;

// Preserve the Host's exact exposure/ledger association. Product policy may
// deny or stop, but it can never manufacture a positive Host permission.
internal sealed class SolusAccountingHooks(SolusReviewFacts facts, IRuntimeExposureHooks host) : IRuntimeExposureHooks
{
    public ValueTask<ExposureAcknowledgement?> BeforeDispatchAsync(RuntimeExposure exposure, CancellationToken token)
    {
        if (!facts.Admit(exposure))
            return ValueTask.FromResult<ExposureAcknowledgement?>(new(exposure, RuntimeHookStatus.Acknowledged, ExposureDecision.Deny));
        return host.BeforeDispatchAsync(exposure, token);
    }

    public async ValueTask<SettlementAcknowledgement?> AfterAttemptAsync(RuntimeSettlement settlement, CancellationToken token)
    {
        var stop = facts.Settle(settlement);
        var receipt = await host.AfterAttemptAsync(settlement, token).ConfigureAwait(false);
        // Failed/missing/mismatched receipts retain their original meaning.
        if (stop && receipt?.Assess(settlement) == RuntimeStop.None)
            return new(settlement.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Stop, settlement.Accounting);
        return receipt;
    }
    public override string ToString() => nameof(SolusAccountingHooks);
}
