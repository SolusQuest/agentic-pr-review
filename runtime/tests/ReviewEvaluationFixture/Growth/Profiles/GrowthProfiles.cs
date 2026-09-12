using System.Collections.Immutable;
using System.Globalization;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles;

internal sealed record GrowthSchedule(int AttemptLimit, ReplayFault Fault, int FaultPhase)
{
    internal static GrowthSchedule Default => new(GrowthProfiles.Attempts, ReplayFault.None, 1);
    internal bool Valid => AttemptLimit > 0 && AttemptLimit <= GrowthProfiles.Attempts && Enum.IsDefined(Fault) &&
        FaultPhase >= 0 && FaultPhase < GrowthProfiles.Attempts;
    internal ReplayFault At(int phase) => phase == FaultPhase ? Fault : ReplayFault.None;
}

internal static class GrowthProfiles
{
    internal static readonly ImmutableArray<string> Names = ["short", "tools", "continuation", "updates"];
    internal static int Attempts => AgentSessionFormat.MaximumCompletedRuns + 1;
    internal static bool ValidPhase(string name, int phase) => Names.Contains(name) && phase >= 0 && phase < Attempts;
    internal static bool IsCandidate(AdmittedReplayFixture fixture) => fixture.Runs.Any(r => r.Input.CaseId.StartsWith("growth-", StringComparison.Ordinal));
    internal static bool Matches(AdmittedReplayFixture fixture) =>
        fixture.Runs.Select(r => r.Input.CaseId).SequenceEqual(Names.Select(n => "growth-" + n)) &&
        fixture.Runs.All(r => (r.Input.CaseId == "growth-short" ? r.Script.Turns.Length == 1 :
            r.Script.Turns.Length == 2 && r.Script.Turns[0].ToolCalls.Length is > 0 and <= AgentLimits.ToolCallsPerResponse &&
            r.Script.Turns[0].ToolCalls.All(c => c.Name == "read_file")) &&
            r.Script.Turns[^1].ToolCalls is [{ Name: "finish_review" }] &&
            r.Expected.Input.Defects.IsEmpty && r.Expected.Input.RequiredObservations.IsEmpty && r.Expected.Input.ProhibitedFindings.IsEmpty &&
            r.ExpectedCode == EvaluationCode.Scored);

    internal static string Corpus(AdmittedReplayFixture fixture, GrowthSchedule? schedule = null) => Corpus(fixture.CorpusSha256, schedule ?? GrowthSchedule.Default);
    internal static string Corpus(string seed, GrowthSchedule schedule) => EvaluationAttempt.Hash("growth-corpus",
        seed, string.Join(',', Names), Attempts.ToString(CultureInfo.InvariantCulture), "minimal-short-two-turn-others-alternate-ahead",
        schedule.AttemptLimit.ToString(CultureInfo.InvariantCulture), schedule.Fault.ToString(), schedule.FaultPhase.ToString(CultureInfo.InvariantCulture));

    internal static AdmittedReplayRun Run(AdmittedReplayFixture fixture, string profile, int phase, ReplayFault fault = ReplayFault.None,
        GrowthSchedule? schedule = null)
    {
        if (!Matches(fixture) || !ValidPhase(profile, phase)) throw new ReplayRejected(ReplayAdmissionCode.InvalidContent);
        var seed = fixture.Runs[Names.IndexOf(profile)];
        string Id(int index) => "growth-" + profile + "-" + index.ToString(CultureInfo.InvariantCulture);
        var identity = seed.Input.ReviewedIdentity;
        if (profile == "updates") identity = identity with
        { HeadSha = (phase / 2 + 1).ToString("x40", CultureInfo.InvariantCulture) };
        var input = seed.Input with
        {
            Id = Id(phase), CaseId = Id(phase), PreviousRunId = phase == 0 ? null : Id(phase - 1),
            Transition = phase == 0 ? "initial" : profile == "updates" && phase % 2 == 0 ? "verified_ahead" : "same_head",
            ReviewedIdentity = identity,
        };
        var turns = seed.Script.Turns;
        if (fault == ReplayFault.GrowthModelBudget)
            turns = Enumerable.Range(0, AgentLimits.ModelCalls).Select(_ => new ReplayScriptTurn(
                [new("read", "read_file", "{\"path\":\"src/source.txt\",\"start_line\":1,\"line_count\":1}")], "budget control")).ToImmutableArray();
        var script = new ReplayScript(turns.Select((turn, index) => turn with
        {
            ToolCalls = turn.ToolCalls.Select((call, ordinal) => call with
            { Id = "g" + phase + "t" + index + "c" + ordinal }).ToImmutableArray(),
        }).ToImmutableArray());
        return seed.Derive(input, script, Corpus(fixture, schedule));
    }

    internal static bool AgentCode(string? code) => code is AgentFailureCodes.Cancelled or AgentFailureCodes.DeadlineExceeded or
        AgentFailureCodes.ChatFailed or AgentFailureCodes.ModelLimit or AgentFailureCodes.ToolLimit or AgentFailureCodes.TokenLimit or
        AgentFailureCodes.RequestTooLarge or AgentFailureCodes.ResponseTooLarge or AgentFailureCodes.UsageInvalid or
        AgentFailureCodes.ResponseInvalid or AgentFailureCodes.MissingTool or AgentFailureCodes.UnknownTool or
        AgentFailureCodes.ToolArgumentsInvalid or AgentFailureCodes.TerminalSequenceInvalid or AgentFailureCodes.TerminalInvalid or
        AgentFailureCodes.ToolPathInvalid or AgentFailureCodes.ToolPathNotTracked or AgentFailureCodes.ToolCursorInvalid or
        AgentFailureCodes.ToolPathUnsafe or AgentFailureCodes.ToolFileTooLarge or AgentFailureCodes.ToolFileBinary or
        AgentFailureCodes.ToolFileInvalidUtf8 or AgentFailureCodes.ToolFileLoneCr or AgentFailureCodes.ToolIoFailed or AgentFailureCodes.ToolResultLimit;

    internal static bool SessionCode(string? code) => code is AgentSessionCodes.ConstructionLimit or AgentSessionCodes.RecordInvalid or
        AgentSessionCodes.ScopeMismatch or AgentSessionCodes.TransitionRejected or AgentSessionCodes.ClassificationInvalid or
        AgentSessionCodes.AssociationInvalid or AgentSessionCodes.ContinuationInvalid;
    internal static bool StateCode(string? code) => code is not null && (RestrictedStateCodes.All.Contains(code) || SessionCode(code) ||
        code is AgentSessionCodes.CurrentMalformed or AgentSessionCodes.CurrentOversized or AgentSessionCodes.ExplicitMissing or AgentSessionCodes.ExplicitIncompatible);

    internal static bool Diagnostic(ReplayChildReply reply, EvaluationOutcome? outcome)
    {
        if (reply.GrowthProfile is null) return reply.ObservedCode is null && reply.ObservedStage is null && reply.GrowthCounts is null && reply.GrowthSchedule is null;
        if (!Names.Contains(reply.GrowthProfile)) return false;
        if (reply.GrowthCounts is null && (reply.ModelCalls != 0 || reply.ToolCalls != 0 || !reply.Requests.IsEmpty)) return false;
        if (reply.GrowthCounts is { } counts && (counts.Calls < 1 || counts.Calls != reply.ModelCalls || counts.Calls < reply.Requests.Length ||
            counts.Calls > AgentLimits.ModelCalls || counts.LastProjectRequestBytes is < 1 or > AgentLimits.RequestBytes ||
            counts.LastMessages is < 1 or > AgentLimits.Messages || counts.LastResponseMessages < counts.LastMessages ||
            counts.LastResponseMessages > counts.LastMessages + 1 + AgentLimits.ToolCallsPerResponse ||
            counts.LastContinuationBeforeBytes < 0 || counts.LastContinuationBeforeBytes > AgentLimits.ContinuationTotalBytes ||
            counts.LastContinuationAfterBytes < counts.LastContinuationBeforeBytes || counts.LastContinuationAfterBytes > 2L * AgentLimits.ContinuationTotalBytes)) return false;
        return reply.ObservedStage switch
        {
            "agent" => AgentCode(reply.ObservedCode) && reply.Code is "agent_failed" or "tool_failed" or "unknown_failed" or
                "provider_failed" or "script_exhausted" or "history_failed" or "cancelled" &&
                reply.Plaintext is null or { Length: 0 } && reply.Prepared is null && outcome is { ExecutionStatus: EvaluationStatus.Failed },
            "build" => SessionCode(reply.ObservedCode) && reply.Code == "session_failed" && reply.Plaintext is null or { Length: 0 } &&
                reply.Prepared is null && outcome is { ExecutionStatus: EvaluationStatus.Failed },
            "restore" => reply.Code == "state_failed" && StateCode(reply.ObservedCode) &&
                reply.ObservedCode != RestrictedStateCodes.Restored && reply.Plaintext is null or { Length: 0 } && reply.Prepared is null &&
                reply.Requests.IsEmpty && outcome is null,
            "prepare" => StateCode(reply.ObservedCode) &&
                outcome is { ExecutionStatus: EvaluationStatus.Completed } && reply.Plaintext is { Length: > 0 } &&
                (reply.Code == "prepared" ? reply.ObservedCode == RestrictedStateCodes.Prepared && reply.Prepared is not null :
                    reply.Code == "state_failed" && reply.ObservedCode != RestrictedStateCodes.Prepared && reply.Prepared is null),
            null => reply.ObservedCode is null && reply.Code is "input_invalid" or "infrastructure_failed" or "assertion_failed" or "state_failed" or "session_failed",
            _ => false,
        };
    }
}
