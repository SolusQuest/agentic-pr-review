using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Histories;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Reset;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using static AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification.GateContracts;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;

internal static class GateHostCases
{
    internal static async Task<GateHostReport> RunAsync(string bundle, GateSelection selection)
    {
        var fixture = ReplayAdmission.Load(bundle).Fixture ?? throw new IOException();
        var world = new ResetProbeWorld();
        var rows = new List<GateHostRow>();
        ResetProbeInvocation? previous = null;
        GateHostRecords? previousRecords = null;
        var phase = 0;
        async Task<(ResetProbeInvocation Call, GateHostRecords Records)> Invoke(int index, string action, bool fresh = false)
        {
            var call = await world.RunAsync(index, reset: action == "reset", failProvider: action == "restore-only",
                script: ResetWorkload.Script(fixture, index, fresh), fresh: fresh);
            if (call.Provider.Request is null || call.Provider.Outcome is null)
                throw new InvalidOperationException("r6_gate_host_not_started_" + index + "_" + call.Completion.Status);
            var records = GateHostRecords.Read(world, call, fresh ? ResetWorkload.FreshFact : ResetWorkload.OldFact,
                fresh ? ResetWorkload.FreshReasoning : ResetWorkload.OldReasoning,
                fresh ? [ResetWorkload.OldFact, ResetWorkload.OldReasoning] : []);
            var exclusion = !fresh || call.Provider.Requests.All(bytes =>
            {
                var text = Encoding.UTF8.GetString(bytes);
                return !text.Contains(ResetWorkload.OldFact, StringComparison.Ordinal) && !text.Contains(ResetWorkload.OldReasoning, StringComparison.Ordinal);
            });
            Require(exclusion);
            rows.Add(new(index, action, call.Completion.Summary.StateDisposition.ToString(),
                call.Provider.Outcome!.Diagnostic?.Code, call.Provider.Outcome.Diagnostic?.ModelCalls,
                call.Provider.Request!.InitialMessages.Length, records.AcceptanceIdentities.Length, records.Tail.Generation,
                AgentCanonical.HashDomain("apr.r6.gate.epoch", Encoding.UTF8.GetBytes(records.HeadHeader.Epoch)),
                HistoryCapture.SessionHash(call.Provider.Request.SessionId), records.Tail.SessionSha256,
                call.Provider.Request.StablePlan.PriorSessionSha256, [.. records.AcceptanceIdentities], true, exclusion, world.Remote.Writes));
            return (call, records);
        }
        for (; phase <= AgentSessionFormat.MaximumCompletedRuns; phase++)
        {
            var (call, records) = await Invoke(phase, "grow");
            if (call.Completion.Summary.StateDisposition == ActionHostStateDisposition.Accepted)
            { previous = call; previousRecords = records; continue; }
            Require(previous is not null && previousRecords is not null && call.Provider.Outcome!.Diagnostic?.Code == AgentFailureCodes.ResponseInvalid &&
                call.Provider.Outcome.Diagnostic.ModelCalls == 1 && call.Provider.Request!.InitialMessages.Length + 9 > AgentLimits.Messages &&
                previousRecords.AcceptanceIdentities.SequenceEqual(records.AcceptanceIdentities));
            break;
        }
        Require(phase is >= 1 and <= AgentSessionFormat.MaximumCompletedRuns && previous is not null && previousRecords is not null);
        var (restored, _) = await Invoke(++phase, "restore-only");
        Require(restored.Provider.Request!.SessionId == previous!.Provider.Request!.SessionId && restored.Provider.Request.Continuation is not null);
        var (reset, resetRecords) = await Invoke(++phase, "reset", fresh: true);
        Require(reset.Completion.Summary.StateDisposition == ActionHostStateDisposition.Accepted &&
            reset.Provider.Request!.SessionId != previous.Provider.Request.SessionId && reset.Provider.Request.Continuation is null &&
            resetRecords.Head.Transition == LineageTransitionKind.Reset && resetRecords.HeadHeader.Epoch != previousRecords!.HeadHeader.Epoch &&
            resetRecords.Tail.Generation == 0 && resetRecords.AcceptanceIdentities.Length == 1);
        var (continued, _) = await Invoke(++phase, "continue", fresh: true);
        Require(continued.Completion.Summary.StateDisposition == ActionHostStateDisposition.Accepted &&
            continued.Provider.Request!.SessionId == reset.Provider.Request!.SessionId && continued.Provider.Request.Continuation is not null &&
            continued.Provider.Request.StablePlan == reset.Provider.Request.StablePlan with { PriorSessionSha256 = resetRecords.Tail.SessionSha256 });
        Require(Encoding.UTF8.GetString(continued.Provider.Requests[0]).Contains(ResetWorkload.FreshReasoning, StringComparison.Ordinal));
        return new(selection.Mode, selection.SourceCommit, selection.SourceTree, selection.SourceClean, fixture.CorpusSha256, [.. rows]);
    }
}
