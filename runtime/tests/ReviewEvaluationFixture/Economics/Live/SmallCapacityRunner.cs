using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;

// Isolated event-driven experiment; not a native C1 economics report.
internal static class SmallCapacityRunner
{
    internal static async Task<EconomicsReport> RunAsync(string path, bool execute, EconomicsOptions? options = null,
        CancellationToken token = default)
    {
        options ??= new();
        var selected = EconomicsPlan.Load(path, execute, token);
        if (Agent.AgentLimits.Messages != 32 || selected.Input.StopRule != "small_capacity_event_v1" || selected.Input.Scenarios.Length != 2 ||
            selected.Input.Scenarios[0] != new EconomicsScenario("replay", 2, 1, false) ||
            selected.Input.Scenarios[1] != new EconomicsScenario("tools", 12, 1, true))
            EconomicsPlan.Reject("small_capacity_selection_invalid");
        var tariff = selected.LoadTariff(token);
        _ = EconomicsWorkload.Load(selected, token);
        var transport = execute && !options.LoopbackExecute ? "live" : "loopback";
        if (transport == "live" && (options.Fault != EconomicsFault.None || options.CredentialProbe)) EconomicsPlan.Reject("plan_invalid");
        var campaign = "c2-" + Guid.NewGuid().ToString("N")[..20];
        var operation = Guid.NewGuid().ToString("N");
        var ledger = new EconomicsLedger(selected.Ceiling);
        var receipts = new List<EconomicsReceipt>();
        var steps = selected.Slots.Select(slot => Empty(slot) with { Allocated = true }).ToArray();
        var sessions = selected.Slots.Select(slot => slot.Chain).Distinct().ToDictionary(chain => chain, _ => Guid.NewGuid().ToString("N"));
        var lineages = new Dictionary<int, AcceptedLineage>();
        var acceptedRuns = new Dictionary<int, AdmittedReplayRun>();
        var startups = new HashSet<string>(StringComparer.Ordinal);
        var key = RandomNumberGenerator.GetBytes(32);
        string? root = null, providerSecret = null;
        var stop = "infrastructure_failed"; var cleanup = "not_created";
        var reaped = true; var attempted = 0; var missing = 0;
        var capacityObserved = false;
        var lastStartedIndex = -1;
        // Reserve every possible slot before any secret is requested; unused leases are not recycled.
        var leases = selected.Slots.ToDictionary(slot => slot.Index,
            slot => ledger.Reserve(slot.Index, selected.ChildAllocation) ?? throw new EconomicsRejected("allocation_invalid"));
        var clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(selected.Input.Bounds.MaxSeconds));
        try
        {
            root = ReplayProcess.CreatePrivateRoot(); options.PrivateRoot?.Invoke(root);
            var plan = EconomicsWorkload.Capture(selected, root, deadline.Token);
            if (plan.Sha256 != selected.Sha256) throw new IOException();
            _ = plan.LoadTariff(deadline.Token);
            var workload = EconomicsWorkload.Load(plan, deadline.Token);
            stop = "complete";
            foreach (var slot in plan.Slots)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (slot.Chain == 1 && capacityObserved)
                {
                    steps[slot.Index] = steps[slot.Index] with { Code = "not_needed_after_capacity", Allocated = true };
                    continue;
                }
                if (slot.ResetChain is not null && !capacityObserved) { stop = "capacity_not_observed"; break; }
                if (slot.Index > 0 && plan.Input.SpacingMilliseconds > 0)
                    await WaitSpacingAsync(plan.Input.SpacingMilliseconds, () => clock.ElapsedMilliseconds, Task.Delay, deadline.Token);
                var reset = false;
                if (slot.ResetChain is { } oldChain)
                {
                    if (!lineages.TryGetValue(oldChain, out var old) || !acceptedRuns.TryGetValue(oldChain, out var oldRun) ||
                        !await ResetAsync(plan, oldRun, sessions[oldChain], root, oldChain, key, old, deadline.Token))
                    { stop = "reset_failed"; steps[slot.Index] = steps[slot.Index] with { Code = stop }; break; }
                    reset = true;
                }
                var lease = leases[slot.Index];
                if (lease is null) { stop = "allocation_refused"; break; }
                var started = clock.ElapsedMilliseconds;
                steps[slot.Index] = steps[slot.Index] with { Allocated = true, Code = "child_started", ReceiptCoverage = "missing",
                    StartedMilliseconds = started, Reset = reset,
                    IntervalMilliseconds = lastStartedIndex < 0 ? null : started - steps[lastStartedIndex].FinishedMilliseconds };
                attempted++; missing++; lastStartedIndex = slot.Index;
                var fault = options.CredentialProbe ? EconomicsFault.CredentialProbe :
                    slot.Index == options.FaultIndex ? options.Fault : EconomicsFault.None;
                var predecessor = lineages.GetValueOrDefault(slot.Chain);
                var input = new EconomicsChildInput(operation, root, plan.Input, plan.Sha256, plan.WorkloadSha256,
                    campaign, slot, lease, sessions[slot.Chain], key, predecessor, transport, fault);
                if (fault == EconomicsFault.WrongSource)
                    input = input with { Plan = input.Plan with { Source = input.Plan.Source with { Commit = new string('f', 40) } }, Fault = EconomicsFault.None };
                if (fault == EconomicsFault.WrongBuild)
                    input = input with { Plan = input.Plan with { BuildSha256 = new string('f', 64) }, Fault = EconomicsFault.None };
                var run = workload.Run(slot, campaign);
                options.BeforeChild?.Invoke(input);
                var result = await options.Process(input, () => providerSecret ??= options.Secrets.TakeProviderCredential(), deadline.Token);
                // The worker sees the linked campaign token; only this owner can
                // distinguish a parent deadline from the operator's cancellation.
                if (result.Code == "caller_cancelled" && deadline.IsCancellationRequested && !token.IsCancellationRequested)
                    result = result with { Code = "deadline" };
                var finished = clock.ElapsedMilliseconds;
                steps[slot.Index] = steps[slot.Index] with { FinishedMilliseconds = finished };
                var receipt = result.Receipt;
                if (result.Code != "received" || receipt is null ||
                    !EconomicsJournal.ValidReceipt(input, result.Ready, receipt, plan, run) || !startups.Add(receipt.Startup))
                { stop = result.Code == "received" ? "receipt_invalid" : result.Code; steps[slot.Index] = steps[slot.Index] with { Code = stop }; break; }
                // Receipt identities and accounting are checked independently of the experimental schedule.
                var receiptStop = EconomicsJournal.Stop(receipt, plan);
                // Accept the one-time allocation receipt before it can affect accepted state.
                if (!SmallCapacityAdmission.Valid(plan, campaign, transport, receipts.Append(receipt).ToArray(),
                        receiptStop ?? "infrastructure_failed") || !ledger.Receipt(lease))
                { stop = "receipt_invalid"; steps[slot.Index] = steps[slot.Index] with { Code = stop }; break; }
                receipts.Add(receipt); missing--;
                options.ObservedReceipt?.Invoke(receipt);
                var step = steps[slot.Index] with { ReceiptCoverage = "complete", Code = receipt.Code,
                    AttemptSha256 = receipt.Evaluation!.AttemptSha256, EvaluationStatus = receipt.Evaluation.ExecutionStatus.ToString().ToLowerInvariant(),
                    Restored = receipt.Restored, Prepared = receipt.Prepared is not null, ProcessId = receipt.ProcessId,
                    Startup = receipt.Startup, PredecessorSha256 = receipt.PredecessorSha256, ToolCalls = receipt.ToolCalls,
                    Observation = EconomicsJournal.Observe(receipt) };
                steps[slot.Index] = step;
                if (receiptStop is not null) { stop = receiptStop; break; }
                if (slot.Chain == 1 && EconomicsJournal.Capacity(receipt))
                {
                    if (slot.Phase < 2 || predecessor is null ||
                        !await ReadbackAsync(plan, acceptedRuns[slot.Chain], input.Session, root, slot.Chain, key, predecessor, deadline.Token))
                    { stop = "representative_history_insufficient"; break; }
                    capacityObserved = true;
                    steps[slot.Index] = step with { Code = "capacity_stop", Readback = true };
                    continue;
                }
                if (receipt.Code != "prepared" || receipt.Prepared is null)
                { stop = slot.Index < 2 ? "representative_history_insufficient" : receipt.Code; break; }
                if (fault == EconomicsFault.CancelAfterPrepare) { stop = "caller_cancelled"; break; }
                options.BeforeAccept?.Invoke(); deadline.Token.ThrowIfCancellationRequested();
                if (fault == EconomicsFault.RejectAccept) { stop = "state_failed"; break; }
                using var state = new ReplayState(run, input.Session, EconomicsChild.StateRoot(root, slot.Chain), key,
                    trustedOverride: plan.TrustedRequest(run));
                var identity = run.Input.ReviewedIdentity.Runtime;
                var prior = slot.Previous is { } previous ? workload.Run(plan.Slots[previous], campaign) : null;
                var context = state.Context(identity, identity, slot.Phase, predecessor?.EnvelopeSha256,
                    ReplayState.Transition(run, prior), run.InitialContext);
                var accepted = await state.Service.AcceptAsync(state.Access, predecessor, receipt.Prepared, context, deadline.Token);
                if (accepted.Action != StateAction.Accepted || accepted.Generation != slot.Phase ||
                    accepted.SessionSha256 != receipt.Prepared.SessionSha256 || accepted.EnvelopeSha256 != receipt.Prepared.EnvelopeSha256)
                { stop = "state_failed"; break; }
                var lineage = new AcceptedLineage(state.Access.Scope, slot.Phase, accepted.SessionSha256!, accepted.EnvelopeSha256!,
                    predecessor?.EnvelopeSha256, ReplayState.Now, ReplayState.Now + RestrictedStateFormat.MaximumRetentionSeconds, true);
                lineages[slot.Chain] = lineage; acceptedRuns[slot.Chain] = run;
                steps[slot.Index] = step with { Accepted = true, SessionSha256 = lineage.SessionSha256 };
                if (!await ReadbackAsync(plan, run, input.Session, root, slot.Chain, key, lineage, deadline.Token))
                { stop = "state_failed"; break; }
                steps[slot.Index] = steps[slot.Index] with { Code = "completed", Readback = true, FinishedMilliseconds = clock.ElapsedMilliseconds };
                if (slot.Index == 0 && receipt.ToolCalls == 0 || slot.Index == 1 && !receipt.Restored)
                { stop = "representative_history_insufficient"; break; }
                options.Completed?.Invoke(slot.Index);
            }
        }
        catch (ReplayProcessUnreaped) { reaped = false; stop = "child_unreaped"; }
        catch (OperationCanceledException) { stop = token.IsCancellationRequested ? "caller_cancelled" : "deadline"; }
        catch { stop = "infrastructure_failed"; }
        finally
        {
            ledger.Seal();
            if (attempted > 0 && steps[lastStartedIndex].FinishedMilliseconds == 0)
                steps[lastStartedIndex] = steps[lastStartedIndex] with { FinishedMilliseconds = clock.ElapsedMilliseconds, Code = stop };
            CryptographicOperations.ZeroMemory(key);
            providerSecret = null;
            if (root is not null)
            {
                try { cleanup = reaped && options.Fault != EconomicsFault.CleanupFailure && options.Cleanup(root) ? "cleaned" : "cleanup_failed"; }
                catch { cleanup = "cleanup_failed"; }
            }
        }
        var usageComplete = missing == 0 && receipts.SelectMany(r => r.Calls).All(c => !c.Dispatched || c.UsageStatus == "known");
        var report = new EconomicsReport("apr.r6.small-capacity-experiment.v1", transport, selected.Selection,
            selected.Sha256, selected.WorkloadSha256, campaign, stop, cleanup, ledger.Total, selected.Slots.Length,
            attempted, missing, usageComplete, false, "not_applicable_experimental_capacity",
            steps.ToImmutableArray(), receipts.Select(r => r.Evaluation!).ToImmutableArray(), null, null);
        options.Finalized?.Invoke();
        return report;
    }

    // Timer completion is only a wake-up hint. Some timer clocks wake slightly
    // early relative to Stopwatch; recheck the monotonic floor before admission.
    internal static async Task WaitSpacingAsync(int milliseconds, Func<long> elapsed,
        Func<int, CancellationToken, Task> delay, CancellationToken token)
    {
        var target = checked(elapsed() + milliseconds);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var remaining = target - elapsed();
            if (remaining <= 0) return;
            await delay(checked((int)remaining), token);
        }
    }

    internal static string JournalStop(string stop) => stop is "complete" or "accounting_violation" or "rate_limited" or
        "caller_cancelled" or "deadline" ? stop : "infrastructure_failed";
    private static EconomicsStep Empty(EconomicsSlot slot) => new(slot.Index, slot.CaseId, "unattempted", "unattempted", false,
        null, null, false, false, false, false, false, null, null, 0, 0, null, null, null, null, null);

    internal static async Task<bool> ReadbackAsync(EconomicsPlan plan, AdmittedReplayRun run, string session, string root, int chain,
        byte[] key, AcceptedLineage predecessor, CancellationToken token)
    {
        using var state = new ReplayState(run, session, EconomicsChild.StateRoot(root, chain), key,
            trustedOverride: plan.TrustedRequest(run));
        var identity = run.Input.ReviewedIdentity.Runtime;
        var context = state.Context(identity, identity, predecessor.Generation, predecessor.ExpectedPredecessorEnvelopeSha256,
            AgentSessionHeadTransition.SameHead, run.InitialContext);
        var restored = await state.Service.RestoreAsync(state.Access,
            new(RestrictedStateLocatorFamily.Current, RestrictedStateRestoreIntent.Explicit, predecessor, context), token);
        return restored.Result.Action == StateAction.Restored && restored.Session?.SessionSha256 == predecessor.SessionSha256;
    }

    private static async Task<bool> ResetAsync(EconomicsPlan plan, AdmittedReplayRun run, string session, string root, int chain,
        byte[] key, AcceptedLineage predecessor, CancellationToken token)
    {
        if (!await ReadbackAsync(plan, run, session, root, chain, key, predecessor, token)) return false;
        using var state = new ReplayState(run, session, EconomicsChild.StateRoot(root, chain), key,
            trustedOverride: plan.TrustedRequest(run));
        var reset = await state.Service.ResetAsync(state.Access, token);
        if (reset.Action != StateAction.Reset) return false;
        var identity = run.Input.ReviewedIdentity.Runtime;
        var absent = await state.Service.RestoreAsync(state.Access,
            new(RestrictedStateLocatorFamily.Absent, RestrictedStateRestoreIntent.Automatic, null,
                state.Context(identity, identity, 0, null, AgentSessionHeadTransition.SameHead, run.InitialContext)), token);
        return absent.Result.Action == StateAction.Bootstrap;
    }
}
