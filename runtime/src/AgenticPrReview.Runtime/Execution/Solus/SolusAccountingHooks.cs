using AgenticPrReview.Runtime.Solus;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;

namespace AgenticPrReview.Runtime.Execution.Solus;

// Preserve the Host's exact exposure/ledger association. Product policy may
// deny or stop, but it can never manufacture a positive Host permission.
internal sealed class SolusAccountingHooks(SolusReviewFacts facts, IRuntimeExposureHooks host,
    SolusReviewCandidateHost? candidate = null) : IRuntimeExposureHooks
{
    public async ValueTask<ExposureAcknowledgement?> BeforeDispatchAsync(RuntimeExposure exposure, CancellationToken token)
    {
        if (!facts.Admit(exposure))
            return new(exposure, RuntimeHookStatus.Acknowledged, ExposureDecision.Deny);
        facts.BeginHostFeedback();
        try
        {
            var receipt = await host.BeforeDispatchAsync(exposure, token).ConfigureAwait(false);
            if (token.IsCancellationRequested || receipt?.Assess(exposure) != RuntimeStop.None) facts.RecordHostStop();
            return receipt;
        }
        catch { facts.RecordHostStop(); throw; }
        finally { facts.EndHostFeedback(); }
    }

    public async ValueTask<SettlementAcknowledgement?> AfterAttemptAsync(RuntimeSettlement settlement, CancellationToken token)
    {
        var stop = facts.Settle(settlement);
        facts.BeginHostFeedback();
        try
        {
            var receipt = await host.AfterAttemptAsync(settlement, token).ConfigureAwait(false);
            // Failed/missing/mismatched/Host-stopped receipts retain their cause,
            // including when numeric usage independently exhausted its budget.
            if (token.IsCancellationRequested || receipt?.Assess(settlement) != RuntimeStop.None) facts.RecordHostStop();
            else if (stop)
            {
                facts.RecordBudgetStop();
                return new(settlement.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Stop, settlement.Accounting);
            }
            else if (settlement.ProviderOutcome == ProviderOutcome.Succeeded &&
                !facts.IsSuccessfulFinal(settlement) && candidate?.RefuseNonFinalRepair() == true)
            {
                // Settlement preserves provider accounting, and Stop precedes any tool invocation.
                return new(settlement.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Stop, settlement.Accounting);
            }
            return receipt;
        }
        catch { facts.RecordHostStop(); throw; }
        finally { facts.EndHostFeedback(); }
    }
    public override string ToString() => nameof(SolusAccountingHooks);
}
