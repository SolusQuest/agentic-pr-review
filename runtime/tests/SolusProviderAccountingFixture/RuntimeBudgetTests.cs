using System.Net;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Execution.Solus;
using SolusAgent.Api.Execution;
using SolusAgent.Runtime.Api.Configuration;

static class RuntimeBudgetTests
{
    internal static async Task Run()
    {
        foreach (var amount in new long[] { 1024, 1025, long.MaxValue })
        {
            using var handler = new ControlledHandler((_, _) => Task.FromResult(F.Http(F.Response(F.Usage(output: amount)))));
            using var bridge = F.Bridge(handler, new(100, 100, 1024)); var domain = new ProofDomain();
            var result = await F.Run(bridge, domain);
            F.Check(result.Accounting?.OutputTokens == amount, "actual_final_measurement");
            F.Check(amount == 1024 ? result.EligibleForProductValidation && domain.Calls == 1
                : !result.EligibleForProductValidation && domain.Calls == 0 && result.Termination == ActionHostTerminationReason.TokenLimit, "final_exact_overrun");
            F.Check(!result.ToString().Contains(F.Reasoning) && !result.ToString().Contains(F.Credential), "outcome_privacy");
        }
        foreach (var usage in new object?[] { null, new { completion_tokens = 2 }, new { prompt_tokens = 2 } })
        {
            using var handler = new ControlledHandler((_, _) => Task.FromResult(F.Http(F.Response(usage))));
            using var bridge = F.Bridge(handler); var domain = new ProofDomain();
            var result = await F.Run(bridge, domain);
            F.Check(!result.EligibleForProductValidation && domain.Calls == 0 && result.Accounting?.UnknownUsageAttempts == 1, "missing_core_not_clean");
        }
        foreach (var axis in new[] { "cached", "uncached", "partial_cached" })
        {
            object usage = axis == "partial_cached" ? new { prompt_tokens = 10, completion_tokens = 5, prompt_cache_hit_tokens = 4 }
                : F.Usage();
            var budget = axis == "uncached" ? new ReviewTokenBudget(5, 100, 1024) : new(100, 3, 1024);
            using var handler = new ControlledHandler((_, _) => Task.FromResult(F.Http(F.Response(usage))));
            using var bridge = F.Bridge(handler, budget); var domain = new ProofDomain();
            var result = await F.Run(bridge, domain);
            F.Check(!result.EligibleForProductValidation && domain.Calls == 0 && result.Termination == ActionHostTerminationReason.TokenLimit, "independent_input_axes");
        }
        using (var partialHandler = new ControlledHandler((_, _) => Task.FromResult(F.Http(F.Response(
            new { completion_tokens = 2, prompt_cache_hit_tokens = 4 })))))
        using (var partial = F.Bridge(partialHandler))
        {
            var result = await F.Run(partial);
            F.Check(!result.EligibleForProductValidation && result.Accounting is null &&
                result.CurrentUsage!.Attempts[0].Usage.ProviderCounters.Any(c =>
                    c.Kind == SolusAgent.Api.Usage.ProviderTokenCounterKind.CacheRead && c.Value == 4), "unrepresentable_fact_retained");
        }
        var partialCount = 0;
        using (var mixedHandler = new ControlledHandler((_, _) => Task.FromResult(F.Http(++partialCount == 1
            ? F.Response(new { prompt_tokens = 10, completion_tokens = 5, prompt_cache_hit_tokens = 4 }, tools: true)
            : F.Response(new { completion_tokens = 2, prompt_cache_hit_tokens = 2 })))))
        using (var mixed = F.Bridge(mixedHandler))
        {
            var capability = new PauseCapability(_ => Task.CompletedTask);
            var result = await F.Run(mixed, tools: [new(new PauseTool(), capability)]);
            F.Check(!result.EligibleForProductValidation && result.Accounting?.CacheHitTokens == 6 &&
                result.Accounting.InputTokens == 10 && result.Accounting.OutputTokens == 7 &&
                result.Accounting.UsageCompleteness == AccountingCompleteness.Partial, "independent_detail_subtotals");
        }
        await ProfileInputAndAdmission();
        await HostClosureCauses();
        await ToolsAndHeadroom();
        await CancellationAndDeadline();
        await ProductionRetryAndRefusal();
    }

    private static async Task ProfileInputAndAdmission()
    {
        // Orthogonal selected total-input ceiling; neither cache bucket alone
        // exceeds its product ceiling for these valid split measurements.
        foreach (var tools in new[] { false, true })
        foreach (var amount in new long[] { 65536, 65537 })
        {
            using var handler = new ControlledHandler((_, _) => Task.FromResult(F.Http(F.Response(
                F.Usage(amount, 5, amount - 32768, 32768), tools: tools))));
            using var bridge = F.Bridge(handler); var domain = new ProofDomain();
            var capability = new PauseCapability(_ => Task.CompletedTask);
            var result = await F.Run(bridge, domain, [new(new PauseTool(), capability)]);
            var accepted = !tools && amount == 65536;
            F.Check(result.Accounting?.InputTokens == amount && result.Accounting.CacheMissTokens == 32768 &&
                result.Accounting.CacheHitTokens == amount - 32768, "profile_total_measurement");
            F.Check(result.EligibleForProductValidation == accepted && domain.Calls == (accepted ? 1 : 0) &&
                result.Termination == (accepted ? ActionHostTerminationReason.ReviewCompleted : ActionHostTerminationReason.TokenLimit) &&
                handler.Sends == 1 && capability.Entries == 0, "profile_total_final_and_tool_boundary");
        }
        foreach (var amount in new long[] { 65536, 65537 })
        {
            var calls = 0;
            using var handler = new ControlledHandler((_, _) =>
            {
                var first = ++calls == 1; var input = first ? 32768 : amount - 32768;
                return Task.FromResult(F.Http(F.Response(F.Usage(input, 5, input - 16384, 16384), tools: first)));
            });
            using var bridge = F.Bridge(handler); var domain = new ProofDomain();
            var capability = new PauseCapability(_ => Task.CompletedTask);
            var result = await F.Run(bridge, domain, [new(new PauseTool(), capability)]);
            F.Check(result.Accounting?.InputTokens == amount && handler.Sends == 2 && capability.Entries == 1 &&
                result.EligibleForProductValidation == (amount == 65536) && domain.Calls == (amount == 65536 ? 1 : 0) &&
                result.Termination == (amount == 65536 ? ActionHostTerminationReason.ReviewCompleted : ActionHostTerminationReason.TokenLimit),
                "profile_total_cumulative_boundary");
        }
        foreach (var budget in new[] { new ReviewTokenBudget(65537, 65536, 8192), new(65536, 65537, 8192),
            new(65536, 65536, 8193), ReviewTokenBudget.Default })
        {
            using var handler = new ControlledHandler((_, _) => throw new ProofFailure("unsupported_profile_send"));
            F.Throws<ArgumentException>(() => { using var bridge = F.Bridge(handler, budget); }, "profile_budget_admission");
            F.Check(handler.Sends == 0, "unsupported_profile_no_send");
        }
        foreach (var amount in new long[] { 8192, 8193 })
        {
            using var handler = new ControlledHandler((_, _) => Task.FromResult(F.Http(F.Response(F.Usage(output: amount)))));
            using var bridge = F.Bridge(handler, new(65536, 65536, 8192)); var domain = new ProofDomain();
            var result = await F.Run(bridge, domain);
            F.Check(result.Accounting?.OutputTokens == amount && result.EligibleForProductValidation == (amount == 8192) &&
                domain.Calls == (amount == 8192 ? 1 : 0) &&
                result.Termination == (amount == 8192 ? ActionHostTerminationReason.ReviewCompleted : ActionHostTerminationReason.TokenLimit),
                "profile_output_exact_overrun");
        }
    }

    private static async Task HostClosureCauses()
    {
        foreach (var mode in new[] { "missing", "failed", "unknown", "mismatched", "throw" })
        {
            using var handler = new ControlledHandler((_, _) => throw new ProofFailure("host_exposure_failure_send"));
            var hooks = new ProofHooks { BeforeMode = mode };
            using var bridge = F.Bridge(handler, hooks: hooks); var domain = new ProofDomain();
            var result = await F.Run(bridge, domain);
            F.Check(result.Termination == ActionHostTerminationReason.HostFailure && !result.EligibleForProductValidation &&
                domain.Calls == 0 && handler.Sends == 0 && hooks.Before == 1, "host_exposure_failure_cause");
        }
        foreach (var mode in new[] { "missing", "failed", "unknown", "mismatched", "throw", "stop" })
        foreach (var amount in new long[] { 5, 1024, 1025 })
        {
            using var handler = new ControlledHandler((_, _) => Task.FromResult(F.Http(F.Response(F.Usage(output: amount), tools: true))));
            var hooks = new ProofHooks { SettlementMode = mode };
            using var bridge = F.Bridge(handler, new(100, 100, 1024), hooks); var domain = new ProofDomain();
            var capability = new PauseCapability(_ => Task.CompletedTask);
            var result = await F.Run(bridge, domain, [new(new PauseTool(), capability)]);
            F.Check(result.Termination == ActionHostTerminationReason.HostFailure && !result.EligibleForProductValidation &&
                domain.Calls == 0 && capability.Entries == 0 && handler.Sends == 1 && hooks.After == 1,
                "host_closure_cause_not_numeric_stop");
            F.Check(result.Accounting?.ProviderAttempts == 1 && result.Accounting.OutputTokens == amount &&
                result.Accounting.ProviderFailedAttempts == 0, "host_closure_keeps_provider_measurements");
        }
    }

    private static async Task ToolsAndHeadroom()
    {
        foreach (var headroom in new[] { false, true })
        {
            var count = 0;
            using var handler = new ControlledHandler((_, _) => Task.FromResult(F.Http(F.Response(
                F.Usage(output: headroom ? 600 : 5), tools: ++count == 1))));
            var capability = new PauseCapability(_ => Task.CompletedTask);
            using var bridge = F.Bridge(handler, headroom ? new(100, 100, 1024) : null);
            var result = await F.Run(bridge, tools: [new(new PauseTool(), capability)]);
            F.Check(capability.Entries == 1, "real_guarded_tool");
            F.Check(headroom ? handler.Sends == 1 && !result.EligibleForProductValidation && result.Termination == ActionHostTerminationReason.TokenLimit
                : handler.Sends == 2 && result.EligibleForProductValidation && result.Accounting?.ModelCalls == 2, "frozen_output_headroom");
            if (!headroom) F.Check(handler.Bodies[1].Contains(F.Reasoning) && handler.Bodies[1].Contains("\"role\":\"tool\""), "real_tool_reconstruction");
        }
        using var exhaustedHandler = new ControlledHandler((_, _) => Task.FromResult(F.Http(F.Response(F.Usage(output: 1024), tools: true))));
        var unused = new PauseCapability(_ => Task.CompletedTask);
        using var exhausted = F.Bridge(exhaustedHandler, new(100, 100, 1024));
        var refusal = await F.Run(exhausted, tools: [new(new PauseTool(), unused)]);
        F.Check(unused.Entries == 0 && exhaustedHandler.Sends == 1 && !refusal.EligibleForProductValidation &&
            refusal.Termination == ActionHostTerminationReason.TokenLimit, "nonterminal_exhaustion_zero_tools");
    }

    private static async Task CancellationAndDeadline()
    {
        using var preHandler = new ControlledHandler((_, _) => throw new ProofFailure("predispatch_send"));
        using var before = F.Bridge(preHandler); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var pre = await F.Run(before, token: cancelled.Token);
        F.Check(preHandler.Sends == 0 && pre.Termination == ActionHostTerminationReason.Cancelled && !pre.EligibleForProductValidation, "predispatch_cancellation");
        using var expiredHandler = new ControlledHandler((_, _) => throw new ProofFailure("expired_send"));
        using var expired = F.Bridge(expiredHandler);
        F.Check((await F.Run(expired, remaining: TimeSpan.Zero)).Termination == ActionHostTerminationReason.DeadlineExceeded && expiredHandler.Sends == 0, "expired_headroom");

        foreach (var callerCancel in new[] { false, true })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var source = new CancellationTokenSource();
            using var handler = new ControlledHandler((_, _) => Task.FromResult(F.Http(F.Response(F.Usage(), tools: true))));
            var capability = new PauseCapability(async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); });
            using var bridge = F.Bridge(handler);
            var pending = F.Run(bridge, tools: [new(new PauseTool(), capability)],
                remaining: callerCancel ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(2), token: source.Token);
            await F.Wait(entered.Task);
            if (callerCancel) source.Cancel();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            F.Check(result.Termination == (callerCancel ? ActionHostTerminationReason.Cancelled : ActionHostTerminationReason.DeadlineExceeded) &&
                !result.EligibleForProductValidation && handler.Sends == 1 && capability.Entries == 1, "one_deadline_includes_tools");
        }
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new ProofHooks();
        using var lateHandler = new ControlledHandler(async (_, _) => { held.TrySetResult(); await release.Task; return F.Http(F.Response(F.Usage())); });
        using var lateBridge = F.Bridge(lateHandler, hooks: hooks);
        var latePending = F.Run(lateBridge, remaining: TimeSpan.FromSeconds(2));
        await F.Wait(held.Task);
        var cut = await latePending.WaitAsync(TimeSpan.FromSeconds(10));
        F.Check(cut.Termination == ActionHostTerminationReason.DeadlineExceeded && !cut.EligibleForProductValidation && hooks.After == 1, "bounded_settlement_after_cut");
        var sends = cut.Accounting?.ProviderAttempts; var input = cut.Accounting?.InputTokens;
        release.TrySetResult(); await F.Wait(lateHandler.Returned.Task); await Task.Delay(50);
        F.Check(cut.Accounting?.ProviderAttempts == sends && cut.Accounting?.InputTokens == input && hooks.After == 1, "frozen_late_usage");
    }

    private static async Task ProductionRetryAndRefusal()
    {
        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
        {
            using var handler = new ControlledHandler((_, _) =>
            {
                var response = F.Http(F.Response(F.Usage()), status); response.Headers.TryAddWithoutValidation("Retry-After", "1");
                return Task.FromResult(response);
            });
            using var bridge = F.Bridge(handler);
            var result = await F.Run(bridge);
            F.Check(handler.Sends == 1 && result.Accounting?.ProviderRetries == 0 && !result.EligibleForProductValidation, "selected_profile_no_retry");
        }
        using var unknownHandler = new ControlledHandler((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "SYNTHETIC_EXCEPTION_CANARY"));
        using var unknown = F.Bridge(unknownHandler);
        var ambiguous = await F.Run(unknown);
        F.Check(unknownHandler.Sends == 1 && ambiguous.Accounting?.AttemptCompleteness == AccountingCompleteness.Unavailable &&
            !ambiguous.EligibleForProductValidation, "ambiguous_runtime_stop");
        var denyHooks = new ProofHooks { Deny = true };
        using var denyHandler = new ControlledHandler((_, _) => throw new ProofFailure("host_denied_send"));
        using var denied = F.Bridge(denyHandler, hooks: denyHooks);
        F.Check(!(await F.Run(denied)).EligibleForProductValidation && denyHandler.Sends == 0 && denyHooks.Before == 1, "no_permission_strengthening");
        using var limitHandler = new ControlledHandler((_, _) => throw new ProofFailure("logical_limit_send"));
        using var limited = F.Bridge(limitHandler);
        var data = Enumerable.Range(0, 16).Select(_ => new AgentInput(AgentInputSource.Repository, new('x', 4096))).ToArray();
        var limit = await F.Run(limited, data: data);
        F.Check(limit.Termination == ActionHostTerminationReason.ResourceLimit && !limit.EligibleForProductValidation && limitHandler.Sends == 0, "runtime_resource_limit");
        using var reusedHandler = new ControlledHandler((_, _) => Task.FromResult(F.Http(F.Response(F.Usage()))));
        using var reused = F.Bridge(reusedHandler);
        await F.Run(reused);
        await F.ThrowsAsync<InvalidOperationException>(async () => { await F.Run(reused); }, "single_review_owner");
        using var rejectHandler = new ControlledHandler((_, _) => Task.FromResult(F.Http(F.Response(F.Usage()))));
        using var rejected = F.Bridge(rejectHandler);
        F.Check(!(await F.Run(rejected, new() { Accept = false })).EligibleForProductValidation, "domain_reject_end_incomplete");
    }
}
