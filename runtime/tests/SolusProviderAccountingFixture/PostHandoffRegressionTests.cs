using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Execution.Solus;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
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
        await ProviderBatchAccounting();
        await CandidateFeedback();
        await PendingHostFeedback();
        await ManagedFailureDiagnostic();
    }

    private static async Task ManagedFailureDiagnostic()
    {
        var sends = 0;
        using var handler = new ControlledHandler((_, _) => Task.FromResult(F.Http(++sends == 1
            ? F.Response(F.Usage(), tools: true, reasoning: "synthetic-short")
            : F.Response(F.Usage(), reasoning: F.Reasoning + new string('r', 2048)))));
        using var bridge = F.Bridge(handler);
        var capability = new PauseCapability(_ => Task.CompletedTask);
        var domain = new ProofDomain();
        var result = await F.Run(bridge, domain, tools: [new(new PauseTool(), capability)]);
        F.Check(result.Termination == ActionHostTerminationReason.ResourceLimit &&
            !result.EligibleForProductValidation && domain.Calls == 0 && sends == 2 && capability.Entries == 1,
            "diagnostic_preserves_rejection_gate");
        F.Check(result.Accounting is { ModelCalls: 2, ProviderAttempts: 2, ProviderRetries: 0,
            ProviderFailedAttempts: 1, UnknownUsageAttempts: 0,
            AttemptCompleteness: AccountingCompleteness.Complete, UsageCompleteness: AccountingCompleteness.Complete },
            "diagnostic_preserves_accounting");
        var property = typeof(SolusReviewOutcome).GetProperty("FailureDiagnostic");
        F.Check(property?.GetValue(result) is not null, "managed_failure_diagnostic_missing");
        var diagnostic = result.FailureDiagnostic!;
        F.Check(diagnostic.Attempts.Length == 2 && diagnostic.Attempts[0].Ordinal == 1 &&
            diagnostic.Attempts[1].Ordinal == 2 &&
            diagnostic.Attempts[0].Error == ActionHostRuntimeDiagnosticValue.None &&
            diagnostic.Attempts[1].Error == ActionHostRuntimeDiagnosticValue.LimitExceeded &&
            diagnostic.Attempts[1].LimitKind == ActionHostProviderLimitKind.ResponseReplayFrameBytes &&
            diagnostic.Attempts[0].LimitKind == ActionHostProviderLimitKind.Unknown &&
            diagnostic.Attempts[1].Outcome == ActionHostRuntimeDiagnosticValue.Rejected &&
            diagnostic.Attempts[1].Exposure == ActionHostRuntimeDiagnosticValue.Dispatched &&
            diagnostic.Attempts[1].Stop == ActionHostRuntimeDiagnosticValue.None &&
            diagnostic.Attempts.All(a => a.ProviderInvoked == true && a.CoreUsageKnown == true) &&
            diagnostic.CandidateStop == ActionHostRuntimeDiagnosticValue.RuntimeLimit &&
            diagnostic.RuntimeReason == ActionHostRuntimeDiagnosticValue.ResourceLimit,
            "diagnostic_preserves_associated_closed_metadata");

        using var noDispatchHandler = new ControlledHandler((_, _) => throw new ProofFailure("diagnostic_unexpected_send"));
        using var noDispatchProvider = F.Provider(noDispatchHandler);
        var rejected = await noDispatchProvider.ExchangeAsync(new(new("deepseek", "wrong"),
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), [ProviderInput.Data("synthetic input")]));
        var fact = new SolusAttemptFact(rejected.Observation, rejected.Outcome, RuntimeStop.None,
            rejected.Error, true);
        var noDispatch = SolusReviewFacts.DescribeAttempt(1, fact)!;
        F.Check(noDispatchHandler.Sends == 0 && noDispatch.Exposure == ActionHostRuntimeDiagnosticValue.NotDispatched &&
            noDispatch.ProviderInvoked == true && noDispatch.Error != ActionHostRuntimeDiagnosticValue.Unknown,
            "diagnostic_not_dispatched_is_not_not_invoked");
        var partialFact = SolusReviewFacts.DescribeAttempt(1, new(rejected.Observation, rejected.Outcome, RuntimeStop.None))!;
        var missing = SolusReviewFacts.DescribeAttempt(1, null)!;
        F.Check(partialFact.Error == ActionHostRuntimeDiagnosticValue.Unknown && partialFact.ProviderInvoked is null &&
            missing.Error == ActionHostRuntimeDiagnosticValue.Unknown && missing.Outcome == ActionHostRuntimeDiagnosticValue.Unknown &&
            missing.Exposure == ActionHostRuntimeDiagnosticValue.Unknown && missing.Stop == ActionHostRuntimeDiagnosticValue.Unknown &&
            missing.ProviderInvoked is null && missing.CoreUsageKnown is null &&
            missing.LimitKind == ActionHostProviderLimitKind.Unknown && partialFact.LimitKind == ActionHostProviderLimitKind.Unknown,
            "diagnostic_missing_facts_remain_unknown");
        F.Check(missing.Render().Contains("invoked=unknown", StringComparison.Ordinal) &&
            missing.Render().Contains("core=unknown", StringComparison.Ordinal), "diagnostic_null_boolean_labels");
        var notInvokedValid = ActionHostAttemptDiagnostic.TryCreate(1, false, ActionHostRuntimeDiagnosticValue.Rejected,
            ActionHostRuntimeDiagnosticValue.InvalidInput, ActionHostRuntimeDiagnosticValue.NotDispatched,
            ActionHostRuntimeDiagnosticValue.None, false, out var notInvoked);
        F.Check(notInvokedValid && notInvoked!.Render().Contains("invoked=no", StringComparison.Ordinal) &&
            notInvoked.Render().Contains("core=no", StringComparison.Ordinal), "diagnostic_false_boolean_labels");
        F.Check(SolusReviewFacts.DescribeAttempt(1, fact with { Error = (ProviderError)999 })!.Error ==
                ActionHostRuntimeDiagnosticValue.Unknown && SolusReviewFacts.DescribeAttempt(0, fact) is null &&
            SolusReviewFacts.DescribeAttempt(7, fact) is null,
            "diagnostic_rejects_ordinals_and_normalizes_undefined_enum");
        F.Check(!ActionHostRuntimeFailureDiagnostic.TryCreate(Enumerable.Repeat(missing, 7).ToArray(),
                ActionHostRuntimeDiagnosticValue.RuntimeLimit, ActionHostRuntimeDiagnosticValue.ResourceLimit, out _) &&
            !ActionHostRuntimeFailureDiagnostic.TryCreate([missing, missing], ActionHostRuntimeDiagnosticValue.RuntimeLimit,
                ActionHostRuntimeDiagnosticValue.ResourceLimit, out _) &&
            !ActionHostRuntimeFailureDiagnostic.TryCreate([missing], (ActionHostRuntimeDiagnosticValue)999,
                ActionHostRuntimeDiagnosticValue.ResourceLimit, out _),
            "diagnostic_rejects_count_order_and_undefined_global");
        F.Check(!ActionHostAttemptDiagnostic.TryCreate(1, null, ActionHostRuntimeDiagnosticValue.LimitExceeded,
            ActionHostRuntimeDiagnosticValue.Unknown, ActionHostRuntimeDiagnosticValue.Unknown,
            ActionHostRuntimeDiagnosticValue.Unknown, null, out _), "diagnostic_rejects_wrong_enum_role");
        foreach (var kind in Enum.GetValues<ProviderLimitKind>())
        {
            var mapped = SolusReviewFacts.DescribeAttempt(1, fact with { Error = ProviderError.LimitExceeded, LimitKind = kind })!;
            F.Check(mapped.LimitKind != ActionHostProviderLimitKind.Unknown || kind == ProviderLimitKind.Unknown,
                "diagnostic_maps_each_finite_provider_limit");
        }
        F.Check(SolusReviewFacts.DescribeAttempt(1, fact with { LimitKind = (ProviderLimitKind)999 })!.LimitKind ==
            ActionHostProviderLimitKind.Unknown, "diagnostic_unknown_provider_limit_stays_unknown");
        var emptyId = Guid.NewGuid();
        var emptyFacts = new SolusReviewFacts(emptyId, new(100, 100, 1024));
        F.Check(emptyFacts.DescribeFailure(CandidateStopReason.RuntimeLimit, AgentTerminationReason.ResourceLimit) is null,
            "diagnostic_requires_freeze");
        emptyFacts.Freeze(new(emptyId, UsageInventoryCoverage.Complete, []));
        F.Check(emptyFacts.DescribeFailure(CandidateStopReason.RuntimeLimit, AgentTerminationReason.ResourceLimit)?.Attempts.IsEmpty == true,
            "diagnostic_known_empty_is_not_missing_attempt");

        var summaryValid = ActionHostStepSummary.TryCreate(null, null, null, ActionHostStateDisposition.NotCommitted, out var summary);
        var annotationValid = ActionHostAnnotation.TryCreate(ActionHostAnnotationCode.AgentResultInvalid, out var annotation);
        var completionValid = ActionHostCompletion.TryCreate("r4-h1", ActionHostStatus.AgentResultInvalid,
            summary, [annotation!], out var completion);
        F.Check(summaryValid && annotationValid && completionValid,
            "diagnostic_completion_fixture");
        var capture = new AgenticPrReview.Runtime.ActionHost.ActionHostAccountingCapture();
        capture.Complete(result.Accounting, result.Termination, diagnostic);
        var projected = capture.Apply(completion!);
        F.Check(AgenticPrReview.Runtime.ActionHost.Serialization.ActionHostJsonCodec.TryWriteCompletion(projected, out var wire),
            "diagnostic_completion_serializes");
        using var json = System.Text.Json.JsonDocument.Parse(wire);
        var root = json.RootElement; var text = root.GetProperty("summary").GetString()!;
        F.Check(root.EnumerateObject().Count() == 6 && root.GetProperty("outputs").EnumerateObject().Count() == 14 &&
            root.GetProperty("annotations").GetArrayLength() == 1 &&
            text.Split('\n').Count(line => line.StartsWith("Runtime diagnostic: ", StringComparison.Ordinal)) == 1 &&
            text.Contains("error=limit_exceeded", StringComparison.Ordinal) &&
            !text.Contains(F.Credential, StringComparison.Ordinal) && !text.Contains(F.Reasoning, StringComparison.Ordinal) &&
            !text.Contains(bridge.Facts.ExecutionId.ToString(), StringComparison.Ordinal) &&
            !text.Contains("999", StringComparison.Ordinal) && !text.Contains("{\"wait\"", StringComparison.Ordinal),
            "diagnostic_closed_summary_privacy_and_wire_shape");
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

    private static async Task ProviderBatchAccounting()
    {
        foreach (var count in new[] { 3, 4 })
        {
            var response = JsonNode.Parse(F.Response(F.Usage(), tools: true))!;
            response["choices"]![0]!["message"]!["tool_calls"] = new JsonArray(
                Enumerable.Range(0, count).Select(i =>
                    JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(F.Call("batch-" + i)))).ToArray());
            var body = response.ToJsonString();
            var expectedOutcome = count == 3 ? ProviderOutcome.Succeeded : ProviderOutcome.Rejected;
            using var directHandler = new ControlledHandler((_, _) => Task.FromResult(F.Http(body)));
            using var directProvider = F.Provider(directHandler);
            var first = F.Request(Guid.NewGuid());
            var exchange = await directProvider.ExchangeAsync(new(first.Scope, first.Attempt, first.Inputs,
                [new PauseTool().Descriptor], requiredCapabilities: ProviderCapabilities.Continuation | ProviderCapabilities.ToolCalls,
                bounds: SolusReviewBridge.Bounds()));
            F.Check(exchange.Outcome == expectedOutcome &&
                exchange.Error == (count == 3 ? ProviderError.None : ProviderError.LimitExceeded) &&
                directHandler.Sends == 1 && exchange.Observation.Exposure == DispatchExposure.Dispatched &&
                exchange.Observation.Usage.InputTokens == 10 && exchange.Observation.Usage.OutputTokens == 5,
                "actual_provider_batch_bound_" + count);

            var id = Guid.NewGuid(); var facts = new SolusReviewFacts(id, new(100, 100, 1024));
            var hooks = new ProofHooks();
            using var handler = new ControlledHandler((_, _) => Task.FromResult(F.Http(body)));
            using var provider = new SolusDeepSeekAdapter(DeepSeekCredential.Create(F.Credential), facts, handler);
            var capability = new PauseCapability(_ => Task.CompletedTask);
            var agent = RuntimeAgentFactory.Create(new(provider, [new(new PauseTool(), capability)],
                new SolusAccountingHooks(facts, hooks), SolusReviewBridge.Bounds()), new RuntimeOptions(requireContinuation: true));
            // A legal batch succeeds at the provider, then stops locally before
            // a second call. The oversized batch is rejected before any tool.
            var request = new AgentRequest(id, "trusted policy", [new(AgentInputSource.Repository, "synthetic data")],
                new(6, TimeSpan.FromSeconds(10)), AgentCapability.WorkUnitLimit | AgentCapability.DurationLimit |
                AgentCapability.Cancellation | AgentCapability.UsageReporting | AgentCapability.DispatchLimits |
                AgentCapability.ToolInvocationLimit,
                new(maximumLogicalCalls: 1, maximumPhysicalDispatches: 1, maximumToolInvocations: 6));
            var result = await agent.ExecuteAsync(request);
            var accounting = facts.Freeze(result.Usage);
            F.Check(result.Reason == AgentTerminationReason.ResourceLimit && handler.Sends == 1 &&
                hooks.Attempts.Single().Outcome == expectedOutcome && capability.Entries == (count == 3 ? 3 : 0),
                "actual_batch_or_local_stop_" + count);
            F.Check(accounting is { ProviderAttempts: 1, ProviderRetries: 0, InputTokens: 10, OutputTokens: 5,
                CacheHitTokens: 4, CacheMissTokens: 6, AttemptCompleteness: AccountingCompleteness.Complete,
                UsageCompleteness: AccountingCompleteness.Complete } && accounting.ProviderFailedAttempts == (count == 3 ? 0 : 1),
                "actual_batch_failure_preserves_usage_" + count);
        }
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

    private static async Task PendingHostFeedback()
    {
        using var handler = new ControlledHandler((_, _) => Task.FromResult(F.Http(F.Response(F.Usage()))));
        var hooks = new ProofHooks { SettlementMode = "held" };
        using var bridge = F.Bridge(handler, hooks: hooks); var domain = new ProofDomain();
        var result = await F.Run(bridge, domain).WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            F.Check(result.Termination == ActionHostTerminationReason.HostFailure && domain.Calls == 0 && hooks.After == 1 &&
                bridge.Facts.HostStopped, "held_settlement_unknown_host_failure");
            F.Check(result.Accounting?.ProviderAttempts == 1 && result.Accounting.ProviderFailedAttempts == 0 &&
                result.Accounting.InputTokens == 10, "held_settlement_provider_facts");
        }
        finally { hooks.SettlementRelease.TrySetResult(); }
        await F.Wait(hooks.SettlementReturned.Task);
        await Task.Delay(50);
        F.Check(result.Termination == ActionHostTerminationReason.HostFailure && bridge.Facts.HostStopped &&
            result.Accounting?.ProviderFailedAttempts == 0 && result.Accounting.InputTokens == 10, "held_settlement_late_freeze");

        using var beforeHandler = new ControlledHandler((_, _) => throw new ProofFailure("late_host_permit_send"));
        var beforeHooks = new ProofHooks { BeforeMode = "held" };
        using var before = F.Bridge(beforeHandler, hooks: beforeHooks); var beforeDomain = new ProofDomain();
        var cut = await F.Run(before, beforeDomain, remaining: TimeSpan.FromSeconds(2)).WaitAsync(TimeSpan.FromSeconds(10));
        beforeHooks.ExposureRelease.TrySetResult();
        await F.Wait(beforeHooks.ExposureReturned.Task);
        await Task.Delay(50);
        F.Check(cut.Termination == ActionHostTerminationReason.DeadlineExceeded && !cut.EligibleForProductValidation &&
            beforeHandler.Sends == 0 && beforeDomain.Calls == 0 && cut.Accounting?.ProviderAttempts == 0,
            "pending_exposure_deadline_not_host_or_budget");
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
