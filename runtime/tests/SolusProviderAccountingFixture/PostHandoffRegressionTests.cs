using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Execution.Solus;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Startup;
using SolusAgent.Runtime.Api.Providers;

static class PostHandoffRegressionTests
{
    internal static async Task Run()
    {
        await IndependentProviderOutcomes();
        await ActualRuntimeStops();
        await CandidateFeedback();
        await HeldSettlementCounterexample();
    }

    private static async Task IndependentProviderOutcomes()
    {
        using var handler = new ControlledHandler((_, _) => Task.FromResult(F.Http(F.Response(F.Usage()))));
        using var provider = F.Provider(handler);
        var result = await provider.ExchangeAsync(F.Request(Guid.NewGuid()));
        var usage = new AgentRunUsage(result.Observation.ExecutionId, UsageInventoryCoverage.Complete, [result.Observation]);
        foreach (var stop in Enum.GetValues<RuntimeStop>())
        {
            var value = SolusReviewFacts.Project(usage, [new(result.Observation, result.Outcome, stop)]);
            F.Check(value.ProviderAttempts == 1 && value.ProviderFailedAttempts == 0 && value.ProviderRetries == 0 &&
                value.InputTokens == 10 && value.OutputTokens == 5 && value.CacheHitTokens == 4 && value.CacheMissTokens == 6,
                "provider_success_independent_stop");
        }
        foreach (var outcome in new ProviderOutcome?[] { ProviderOutcome.Failed, ProviderOutcome.Rejected, null })
        {
            var value = SolusReviewFacts.Project(usage, [new(result.Observation, outcome, RuntimeStop.ResourceLimit)]);
            F.Check(value.ProviderFailedAttempts == 1, "non_success_not_promoted_by_stop");
        }
    }

    private static async Task ActualRuntimeStops()
    {
        var clock = new CompletionCutClock(); var id = Guid.NewGuid();
        using var handler = new ControlledHandler((_, _) =>
        {
            clock.Arm();
            return Task.FromResult(F.Http(F.Response(F.Usage())));
        });
        var facts = new SolusReviewFacts(id, new(100, 100, 1024)); var hooks = new ProofHooks();
        using var provider = new SolusDeepSeekAdapter(DeepSeekCredential.Create(F.Credential), facts, handler);
        var agent = RuntimeAgentFactory.Create(new(provider, [], new SolusAccountingHooks(facts, hooks), SolusReviewBridge.Bounds()),
            new RuntimeOptions(clock, requireContinuation: true));
        var result = await agent.ExecuteAsync(Request(id));
        var terminalEligibleBeforeFreeze = facts.CanAdmitTerminal(id);
        var accounting = facts.Freeze(result.Usage);
        F.Check(result.Reason == AgentTerminationReason.ResourceLimit && hooks.Attempts.Count == 1 &&
            hooks.Attempts[0] is { Outcome: ProviderOutcome.Succeeded, Stop: RuntimeStop.DurationLimit } && !terminalEligibleBeforeFreeze,
            "actual_success_then_local_cut");
        F.Check(accounting?.ProviderAttempts == 1 && accounting.ProviderFailedAttempts == 0 &&
            accounting.InputTokens == 10 && accounting.OutputTokens == 5, "actual_cut_success_counts");

        var capId = Guid.NewGuid(); var capFacts = new SolusReviewFacts(capId, new(100, 100, 1024)); var capHooks = new ProofHooks();
        using var capHandler = new ControlledHandler((_, _) => Task.FromResult(F.Http(F.Response(F.Usage(), tools: true))));
        using var capProvider = new SolusDeepSeekAdapter(DeepSeekCredential.Create(F.Credential), capFacts, capHandler);
        var capability = new PauseCapability(_ => Task.CompletedTask);
        // Test-only smaller retained capacity: production profile remains unchanged.
        var capAgent = RuntimeAgentFactory.Create(new(capProvider, [new(new PauseTool(), capability)],
            new SolusAccountingHooks(capFacts, capHooks), SolusReviewBridge.Bounds()),
            new RuntimeOptions(maximumRecords: 3, requireContinuation: true));
        var capped = await capAgent.ExecuteAsync(Request(capId)); var measured = capFacts.Freeze(capped.Usage);
        F.Check(capped.Reason == AgentTerminationReason.ResourceLimit && capHandler.Sends == 1 && capability.Entries == 0 &&
            capHooks.Attempts.Single().Outcome == ProviderOutcome.Succeeded && measured?.ProviderFailedAttempts == 0 &&
            measured.ProviderAttempts == 1 && measured.InputTokens == 10, "actual_capacity_stop_provider_succeeded");
    }

    private static async Task CandidateFeedback()
    {
        foreach (var output in new long[] { 5, 1024, 1025 })
        foreach (var mode in new[] { "accept", "reject", "missing", "failed", "unknown", "wrong_execution", "wrong_submission", "throw" })
        {
            using var handler = new ControlledHandler((_, _) => Task.FromResult(F.Http(F.Response(F.Usage(output: output),
                text: mode == "reject" ? "synthetic invalid candidate" : "{\"findings\":[]}"))));
            using var bridge = F.Bridge(handler, new(100, 100, 1024));
            var domain = new ProofDomain { Accept = mode != "reject", Mode = mode };
            var result = await F.Run(bridge, domain);
            var expected = output > 1024 ? ActionHostTerminationReason.TokenLimit
                : mode == "accept" ? ActionHostTerminationReason.ReviewCompleted
                : mode == "reject" ? ActionHostTerminationReason.InvalidResult : ActionHostTerminationReason.HostFailure;
            F.Check(result.Termination == expected && result.EligibleForProductValidation == (expected == ActionHostTerminationReason.ReviewCompleted) &&
                domain.Calls == (output > 1024 ? 0 : 1), "actual_candidate_decision_ack_and_budget");
            F.Check(result.Accounting?.ProviderAttempts == 1 && result.Accounting.ProviderFailedAttempts == 0 &&
                result.Accounting.OutputTokens == output, "candidate_decision_keeps_provider_success");
        }
    }

    private static async Task HeldSettlementCounterexample()
    {
        using var handler = new ControlledHandler((_, _) => Task.FromResult(F.Http(F.Response(F.Usage()))));
        var hooks = new ProofHooks { SettlementMode = "held" };
        using var bridge = F.Bridge(handler, hooks: hooks); var domain = new ProofDomain();
        var result = await F.Run(bridge, domain).WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            // Temporary pre-fix counterexample: actual closure grace expires
            // without Host feedback, yet current projection says provider failure.
            F.Check(result.Termination == ActionHostTerminationReason.ProviderFailure && domain.Calls == 0 && hooks.After == 1,
                "held_settlement_pre_fix_counterexample");
            F.Check(result.Accounting?.ProviderAttempts == 1 && result.Accounting.ProviderFailedAttempts == 0 &&
                result.Accounting.InputTokens == 10, "held_settlement_provider_facts");
        }
        finally { hooks.SettlementRelease.TrySetResult(); }
        await F.Wait(hooks.SettlementReturned.Task);
    }

    private static AgentRequest Request(Guid execution) => new(execution, "trusted policy",
        [new(AgentInputSource.Repository, "synthetic data")], new(6, TimeSpan.FromSeconds(10)),
        AgentCapability.WorkUnitLimit | AgentCapability.DurationLimit | AgentCapability.Cancellation | AgentCapability.UsageReporting |
        AgentCapability.DispatchLimits | AgentCapability.ToolInvocationLimit,
        new(maximumLogicalCalls: 6, maximumPhysicalDispatches: 6, maximumToolInvocations: 6));

    // Advance only after the real adapter finishes: WaitAsync obtains the
    // successful result, then the subsequent runtime cut wins before acceptance.
    private sealed class CompletionCutClock : TimeProvider
    {
        private int armed, reads;
        internal void Arm() => Volatile.Write(ref armed, 1);
        public override long GetTimestamp() => Volatile.Read(ref armed) == 0 || Interlocked.Increment(ref reads) == 1
            ? 0 : 20 * TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();
        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
