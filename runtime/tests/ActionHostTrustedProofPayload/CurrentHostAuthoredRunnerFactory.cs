using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

internal sealed class CurrentHostAuthoredRunnerFactory(TrustedProofPayloadRuntimePorts ports,
    ITrustedProofStaleSignal? staleSignal) : IActionHostProviderRunnerFactory, ICurrentHostTestWorkPlanConsumer
{
    private bool? successor;
    public void SelectCurrentWorkPlan(bool successor)
    {
        if (this.successor is not null) throw new InvalidOperationException("current_work_plan_already_selected");
        this.successor = successor;
    }

    public IActionHostProviderRunner Create(ActionHostProviderPolicy policy, ActionHostProviderApiKey key,
        ReviewedSnapshot snapshot, TimeProvider timeProvider)
    {
        if (successor is not { } selected) throw new InvalidOperationException("current_work_plan_not_admitted");
        return new Runner(snapshot, selected, ports.CreateCurrentWorkObserver?.Invoke(selected), staleSignal);
    }

    private sealed class Runner(ReviewedSnapshot snapshot, bool successor,
        Action<ProjectChatRequest, int, string>? observer, ITrustedProofStaleSignal? staleSignal)
        : IActionHostProviderRunner
    {
        private bool used;
        public Task<AgentRunOutcome> RunAsync(AgentRunRequest run, CancellationToken token)
        {
            if (used) throw new InvalidOperationException("authored_work_already_executed");
            used = true;
            return new CurrentHostAuthoredWork(successor, staleSignal, observer).RunAsync(run,
                new SnapshotToolExecutor(snapshot, new VerifiedReviewedFileAccess()), token);
        }
        public void Dispose() { }
    }
}
