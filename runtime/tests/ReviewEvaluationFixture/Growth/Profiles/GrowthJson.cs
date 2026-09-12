using System.Collections.Immutable;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Reporting;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles;

internal static class GrowthJson
{
    internal const int MaximumBytes = 4 * 1024 * 1024;
    internal static byte[] Write(GrowthReport report)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, GrowthJsonContext.Default.GrowthReport);
        if (bytes.Length > MaximumBytes) throw new InvalidOperationException("growth_output_limit");
        return bytes;
    }

    // Data admission is not proof that unseen execution occurred. Recomputes accounting,
    // links existing Q1/Q4 outcomes and checks the closed producer measurement domains.
    internal static GrowthReport? Read(ReadOnlySpan<byte> bytes)
    {
        var report = ReplayWire.Read(bytes, GrowthJsonContext.Default.GrowthReport, MaximumBytes);
        try { return report is not null && Valid(report) ? report : null; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NullReferenceException or OverflowException) { return null; }
    }

    internal static string Normalize(ImmutableArray<GrowthProfileReport> profiles)
    {
        GrowthState? State(GrowthState? value) => value is null ? null : value with
        {
            SessionSha256 = value.LogicalSha256, EnvelopeSha256 = new string('0', 64),
            PredecessorEnvelopeSha256 = value.PredecessorEnvelopeSha256 is null ? null : new string('0', 64),
        };
        var rows = profiles.SelectMany(p => p.Rows.Select(r => r with { Before = State(r.Before), State = State(r.State) })).ToImmutableArray();
        return AgentCanonical.HashDomain("apr.r5.growth.rows", JsonSerializer.SerializeToUtf8Bytes(rows, GrowthJsonContext.Default.ImmutableArrayGrowthRow));
    }

    private static bool Valid(GrowthReport report)
    {
        if (report.Code is not ("verified" or "observation_incomplete" or "input_invalid" or "cancelled" or "infrastructure_failed" or "cleanup_failed") ||
            report.Cleanup is not ("cleaned" or "cleanup_failed") || report.Schedule is not { Valid: true } || !EvaluationLimits.Hash(report.SourceCommit, 40) ||
            !EvaluationLimits.Hash(report.SourceTree, 40) || report.Profiles.IsDefault || report.Profiles.Length > GrowthProfiles.Names.Length ||
            report.Profiles.Select(p => p.Profile).Distinct().Count() != report.Profiles.Length ||
            (report.Cleanup == "cleanup_failed") != (report.Code == "cleanup_failed")) return false;
        if (report.Profiles.IsEmpty) return report.NormalizedSha256 is null && report.Code != "verified";
        if (!EvaluationLimits.Hash(report.SeedCorpusSha256) || report.Schedule is not { Valid: true } ||
            report.CorpusSha256 != GrowthProfiles.Corpus(report.SeedCorpusSha256!, report.Schedule) || report.NormalizedSha256 != Normalize(report.Profiles)) return false;
        foreach (var profile in report.Profiles)
        {
            if (!GrowthProfiles.Names.Contains(profile.Profile) || profile.AttemptLimit != report.Schedule.AttemptLimit ||
                profile.Rows.IsDefaultOrEmpty || profile.Rows.Length > profile.AttemptLimit) return false;
            var evaluationBytes = JsonSerializer.SerializeToUtf8Bytes(profile.Evaluation, EvaluationReportJsonContext.Default.EvaluationReportDocument);
            var evaluation = EvaluationReportJson.Read(evaluationBytes);
            if (!evaluation.Succeeded || profile.Evaluation.Outcomes.Length != profile.Rows.Length) return false;
            var outcomes = profile.Evaluation.Outcomes.ToDictionary(o => o.CaseId, StringComparer.Ordinal);
            GrowthState? previous = null;
            for (var index = 0; index < profile.Rows.Length; index++)
            {
                var row = profile.Rows[index];
                var caseId = "growth-" + profile.Profile + "-" + index;
                var transition = index == 0 ? "initial" : profile.Profile == "updates" && index % 2 == 0 ? "verified_ahead" : "same_head";
                if (row.Attempt != index || row.CaseId != caseId || row.Transition != transition ||
                    !outcomes.TryGetValue(caseId, out var outcome) || outcome.AttemptSha256 != row.AttemptSha256 ||
                    !EvaluationLimits.Hash(row.AttemptSha256) || outcome.CorpusSha256 != report.CorpusSha256 ||
                    outcome.SourceCommit != report.SourceCommit || outcome.SourceTree != report.SourceTree || outcome.SourceClean != report.SourceClean ||
                    outcome.Mode != "deterministic" || row.Before != previous || row.Accepted != (row.State is not null) ||
                    row.Classification != GrowthRunner.Classify(row.Stage, row.Code, row.Project) ||
                    row.ModelCalls < 0 || row.ModelCalls > AgentLimits.ModelCalls || row.ToolCalls < 0 || row.ToolCalls > AgentLimits.ToolCalls ||
                    row.ProviderRequests < 0 || row.ProviderRequests > row.ModelCalls || row.ProviderRequestBytes < 0 ||
                    row.ProviderRequestBytes > (long)(row.ProviderRequests ?? 0) * AgentLimits.RequestBytes ||
                    (row.ProviderRequests is { } requests && (requests == 0 ? row.LastProviderRequestBytes is not null || row.ProviderRequestBytes != 0 :
                        row.LastProviderRequestBytes is not (> 0 and <= AgentLimits.RequestBytes) || row.LastProviderRequestBytes > row.ProviderRequestBytes)) ||
                    !Code(row.Stage, row.Code) || !OutcomeMatches(row.Stage, outcome)) return false;
                if (row.Code == "process_unreaped" && (report.Cleanup != "cleanup_failed" || row.PredecessorPreserved || row.ModelCalls is not null)) return false;
                if (row.ModelCalls is null ? row.ToolCalls is not null || row.ProviderRequests is not null || row.ProviderRequestBytes is not null ||
                    row.LastProviderRequestBytes is not null || row.Project is not null || row.Stage != "executor" :
                    row.ToolCalls is null || row.ProviderRequests is null || row.ProviderRequestBytes is null) return false;
                if (row.Project is null && row.ModelCalls is not null && (row.ModelCalls != 0 || row.ToolCalls != 0 || row.ProviderRequests != 0)) return false;
                if (row.Project is { } project && (project.Calls < 1 || project.Calls != row.ModelCalls || project.Calls < row.ProviderRequests || project.Calls > AgentLimits.ModelCalls ||
                    project.LastProjectRequestBytes is < 1 or > AgentLimits.RequestBytes || project.LastMessages is < 1 or > AgentLimits.Messages ||
                    project.LastResponseMessages < project.LastMessages || project.LastResponseMessages > project.LastMessages + 1 + AgentLimits.ToolCallsPerResponse ||
                    project.LastContinuationBeforeBytes < 0 ||
                    project.LastContinuationBeforeBytes > AgentLimits.ContinuationTotalBytes ||
                    project.LastContinuationAfterBytes < project.LastContinuationBeforeBytes ||
                    project.LastContinuationAfterBytes > 2L * AgentLimits.ContinuationTotalBytes)) return false;
                if (row.State is { } state)
                {
                    if (!row.PredecessorPreserved || row.Stage != "accept" || row.Code != RestrictedStateCodes.Accepted ||
                        outcome.ExecutionStatus != EvaluationStatus.Completed || !StateValid(state, previous, index)) return false;
                    previous = state;
                }
                else if (index != profile.Rows.Length - 1) return false;
            }
            var last = profile.Rows[^1];
            var observed = !last.Accepted && last.Classification is "append_limit" or "run_budget" or "continuation_limit" or "message_limit";
            if (profile.LimitObserved != observed || (last.Accepted
                ? profile.TerminalStage != "schedule" || (profile.TerminalCode == "attempt_limit"
                    ? profile.Rows.Length != profile.AttemptLimit : profile.TerminalCode is not ("cancelled" or "infrastructure_failed" or "predecessor_unavailable"))
                : profile.TerminalStage != last.Stage || profile.TerminalCode != last.Code)) return false;
        }
        return report.Code != "verified" || report.Profiles.All(p => p.LimitObserved && p.Rows.All(r => r.PredecessorPreserved));
    }

    private static bool StateValid(GrowthState state, GrowthState? previous, int index) =>
        state.Generation == index && state.CompletedRuns == index + 1 && state.CompletedRuns <= AgentSessionFormat.MaximumCompletedRuns &&
        state.Records > (previous?.Records ?? 0) && state.Records <= AgentLimits.SessionRecords &&
        state.ContinuationBytes >= (previous?.ContinuationBytes ?? 0) && state.ContinuationBytes <= AgentLimits.ContinuationTotalBytes &&
        state.PlaintextBytes > (previous?.PlaintextBytes ?? 0) && state.PlaintextBytes <= AgentLimits.SessionPlaintextBytes &&
        state.EnvelopeBytes > state.PlaintextBytes && state.EnvelopeBytes <= AgentLimits.StateEnvelopeBytes &&
        state.AcceptedCandidates == Math.Min(index + 1, AgentLimits.AcceptedCandidates) &&
        state.MetadataBytes is > 0 and <= AgentLimits.CandidateMetadataBytes &&
        state.ScopeBytes == state.EnvelopeBytes + (previous?.EnvelopeBytes ?? 0) + state.MetadataBytes && state.ScopeBytes <= AgentLimits.StateScopeTotalBytes &&
        EvaluationLimits.Hash(state.SessionSha256) && EvaluationLimits.Hash(state.EnvelopeSha256) && EvaluationLimits.Hash(state.LogicalSha256) &&
        state.PredecessorEnvelopeSha256 == previous?.EnvelopeSha256;

    private static bool OutcomeMatches(string stage, EvaluationOutcome outcome) => stage switch
    {
        "agent" => outcome.ExecutionStatus == EvaluationStatus.Failed,
        "build" => outcome.ExecutionStatus == EvaluationStatus.Failed &&
            outcome.FailureSource == EvaluationFailureSource.HostState && outcome.FailureKind == EvaluationFailureKind.StateAdmission,
        "restore" => outcome.ExecutionStatus == EvaluationStatus.Failed,
        "prepare" or "accept" or "readback" => outcome.ExecutionStatus == EvaluationStatus.Completed,
        // Some admitted executor assertions can follow a completed SESSION; unknown replies use Invalid.
        "executor" => true,
        _ => false,
    };

    private static bool Code(string stage, string code) => stage switch
    {
        "agent" => GrowthProfiles.AgentCode(code),
        "build" => GrowthProfiles.SessionCode(code),
        "restore" or "prepare" or "accept" => GrowthProfiles.StateCode(code),
        "readback" => code == "accepted_readback_failed",
        "executor" => code is "result_invalid" or "input_invalid" or "infrastructure_failed" or "assertion_failed" or "state_failed" or
            "session_failed" or "process_failed" or "process_unreaped" or "process_timeout" or "process_start_failed" or "cancelled" or "output_limit" or "reply_invalid",
        _ => false,
    };
}
