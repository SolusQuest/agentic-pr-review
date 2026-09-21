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

internal sealed class EconomicsOptions
{
    // Internal deterministic injection only; there is no CLI transport/fault switch.
    internal bool LoopbackExecute { get; init; }
    internal bool CredentialProbe { get; init; }
    internal EconomicsFault Fault { get; init; }
    internal int FaultIndex { get; init; } = 1;
    internal ILiveSecretSource Secrets { get; init; } = new LiveEnvironmentSecretSource();
    internal Func<EconomicsChildInput, Func<string?>, CancellationToken, Task<EconomicsProcessResult>> Process { get; init; } = EconomicsProcess.RunAsync;
    internal Func<string, bool> Cleanup { get; init; } = ReplayProcess.Cleanup;
    internal Action<string>? PrivateRoot { get; init; }
    internal Action<EconomicsChildInput>? BeforeChild { get; init; }
    internal Action<EconomicsReceipt>? ObservedReceipt { get; init; }
    internal Action? BeforeAccept { get; init; }
    internal Action? Finalized { get; init; }
    internal Action<int>? Completed { get; init; }
}

internal static class EconomicsRunner
{
    internal static async Task<EconomicsReport> RunAsync(string path, bool execute, EconomicsOptions? options = null,
        CancellationToken token = default)
    {
        options ??= new();
        var selected = EconomicsPlan.Load(path, execute, token);
        var tariff = selected.LoadTariff(token);
        _ = EconomicsWorkload.Load(selected, token);
        var transport = execute && !options.LoopbackExecute ? "live" : "loopback";
        if (transport == "live" && (options.Fault != EconomicsFault.None || options.CredentialProbe)) EconomicsPlan.Reject("plan_invalid");
        var campaign = "c2-" + Guid.NewGuid().ToString("N")[..20];
        var operation = Guid.NewGuid().ToString("N");
        var ledger = new EconomicsLedger(selected.Ceiling);
        var receipts = new List<EconomicsReceipt>();
        var steps = selected.Slots.Select(slot => Empty(slot)).ToArray();
        var sessions = selected.Slots.Select(slot => slot.Chain).Distinct().ToDictionary(chain => chain, _ => Guid.NewGuid().ToString("N"));
        var lineages = new Dictionary<int, AcceptedLineage>();
        var acceptedRuns = new Dictionary<int, AdmittedReplayRun>();
        var startups = new HashSet<string>(StringComparer.Ordinal);
        var key = RandomNumberGenerator.GetBytes(32);
        string? root = null, providerSecret = null;
        var stop = "infrastructure_failed"; var cleanup = "not_created";
        var reaped = true; var attempted = 0; var missing = 0;
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
                if (slot.Index > 0 && plan.Input.SpacingMilliseconds > 0)
                    await Task.Delay(plan.Input.SpacingMilliseconds, deadline.Token);
                var reset = false;
                if (slot.ResetChain is { } oldChain)
                {
                    if (!lineages.TryGetValue(oldChain, out var old) || !acceptedRuns.TryGetValue(oldChain, out var oldRun) ||
                        !await ResetAsync(oldRun, sessions[oldChain], root, oldChain, key, old, deadline.Token))
                    { stop = "reset_failed"; steps[slot.Index] = steps[slot.Index] with { Code = stop }; break; }
                    reset = true;
                }
                var lease = ledger.Reserve(slot.Index, plan.ChildAllocation);
                if (lease is null) { stop = "allocation_refused"; break; }
                var started = clock.ElapsedMilliseconds;
                steps[slot.Index] = steps[slot.Index] with { Allocated = true, Code = "child_started", ReceiptCoverage = "missing",
                    StartedMilliseconds = started, Reset = reset,
                    IntervalMilliseconds = slot.Index == 0 ? null : started - steps[slot.Index - 1].FinishedMilliseconds };
                attempted++; missing++;
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
                var candidate = receipts.Append(receipt).ToArray();
                var receiptStop = EconomicsJournal.Stop(receipt, plan);
                // A candidate prefix is admitted before it can affect accepted state.
                if (EconomicsJournal.Create(plan, campaign, transport, candidate, receiptStop ?? "infrastructure_failed") is null || !ledger.Receipt(lease))
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
                if (slot.ExpectedCapacity)
                {
                    if (!EconomicsJournal.Capacity(receipt) || predecessor is null ||
                        !await ReadbackAsync(acceptedRuns[slot.Chain], input.Session, root, slot.Chain, key, predecessor, deadline.Token))
                    { stop = "representative_history_insufficient"; break; }
                    steps[slot.Index] = step with { Code = "capacity_stop", Readback = true };
                    continue;
                }
                if (receipt.Code != "prepared" || receipt.Prepared is null)
                { stop = slot.Index < 2 ? "representative_history_insufficient" : receipt.Code; break; }
                if (fault == EconomicsFault.CancelAfterPrepare) { stop = "caller_cancelled"; break; }
                options.BeforeAccept?.Invoke(); deadline.Token.ThrowIfCancellationRequested();
                if (fault == EconomicsFault.RejectAccept) { stop = "state_failed"; break; }
                using var state = new ReplayState(run, input.Session, EconomicsChild.StateRoot(root, slot.Chain), key);
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
                if (!await ReadbackAsync(run, input.Session, root, slot.Chain, key, lineage, deadline.Token))
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
            if (attempted > 0 && steps[attempted - 1].FinishedMilliseconds == 0)
                steps[attempted - 1] = steps[attempted - 1] with { FinishedMilliseconds = clock.ElapsedMilliseconds, Code = stop };
            CryptographicOperations.ZeroMemory(key);
            providerSecret = null;
            if (root is not null)
            {
                try { cleanup = reaped && options.Fault != EconomicsFault.CleanupFailure && options.Cleanup(root) ? "cleaned" : "cleanup_failed"; }
                catch { cleanup = "cleanup_failed"; }
            }
        }
        var journal = missing == 0 ? EconomicsJournal.Create(selected, campaign, transport, receipts, JournalStop(stop)) : null;
        if (missing == 0 && journal is null) throw new InvalidOperationException("r6_economics_journal_invalid");
        var pricing = journal is null ? null : PricingReport.Create(journal, tariff);
        var report = new EconomicsReport(EconomicsLiveLimits.ReportFormat, transport, selected.Selection,
            selected.Sha256, selected.WorkloadSha256, campaign, stop, cleanup, ledger.Total, selected.Slots.Length,
            attempted, missing, missing == 0 && journal!.Document.Totals.UsageComplete,
            missing == 0 && pricing!.Document.ObservedUsage.TotalComplete,
            missing == 0 ? "available" : "unavailable_missing_receipt", steps.ToImmutableArray(),
            receipts.Select(receipt => receipt.Evaluation!).ToImmutableArray(), journal?.Document, pricing?.Document);
        options.Finalized?.Invoke();
        return report;
    }

    internal static string JournalStop(string stop) => stop is "complete" or "accounting_violation" or "rate_limited" or
        "caller_cancelled" or "deadline" ? stop : "infrastructure_failed";
    private static EconomicsStep Empty(EconomicsSlot slot) => new(slot.Index, slot.CaseId, "unattempted", "unattempted", false,
        null, null, false, false, false, false, false, null, null, 0, 0, null, null, null, null, null);

    internal static async Task<bool> ReadbackAsync(AdmittedReplayRun run, string session, string root, int chain,
        byte[] key, AcceptedLineage predecessor, CancellationToken token)
    {
        using var state = new ReplayState(run, session, EconomicsChild.StateRoot(root, chain), key);
        var identity = run.Input.ReviewedIdentity.Runtime;
        var context = state.Context(identity, identity, predecessor.Generation, predecessor.ExpectedPredecessorEnvelopeSha256,
            AgentSessionHeadTransition.SameHead, run.InitialContext);
        var restored = await state.Service.RestoreAsync(state.Access,
            new(RestrictedStateLocatorFamily.Current, RestrictedStateRestoreIntent.Explicit, predecessor, context), token);
        return restored.Result.Action == StateAction.Restored && restored.Session?.SessionSha256 == predecessor.SessionSha256;
    }

    private static async Task<bool> ResetAsync(AdmittedReplayRun run, string session, string root, int chain,
        byte[] key, AcceptedLineage predecessor, CancellationToken token)
    {
        if (!await ReadbackAsync(run, session, root, chain, key, predecessor, token)) return false;
        using var state = new ReplayState(run, session, EconomicsChild.StateRoot(root, chain), key);
        var reset = await state.Service.ResetAsync(state.Access, token);
        if (reset.Action != StateAction.Reset) return false;
        var identity = run.Input.ReviewedIdentity.Runtime;
        var absent = await state.Service.RestoreAsync(state.Access,
            new(RestrictedStateLocatorFamily.Absent, RestrictedStateRestoreIntent.Automatic, null,
                state.Context(identity, identity, 0, null, AgentSessionHeadTransition.SameHead, run.InitialContext)), token);
        return absent.Result.Action == StateAction.Bootstrap;
    }
}
