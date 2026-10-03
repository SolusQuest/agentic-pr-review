using System.Text.Json;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Histories;

// Internal bounded handoff to later R6 consumers, never the raw ReplayChildReply.
internal static class HistoryJson
{
    internal const int MaximumBytes = 512 * 1024;
    internal static byte[] Write(HistoryReport report) => Write(report, MeasurementLimits.Historical);
    internal static byte[] WriteCurrent(HistoryReport report) => Write(report, MeasurementLimits.Current);
    private static byte[] Write(HistoryReport report, MeasurementLimits limits)
    {
        if (!Valid(report, limits)) throw new InvalidOperationException("history_report_invalid");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, HistoryJsonContext.Default.HistoryReport);
        if (bytes.Length > MaximumBytes) throw new InvalidOperationException("history_report_limit");
        return bytes;
    }
    internal static HistoryReport? Read(ReadOnlySpan<byte> bytes) => Read(bytes, MeasurementLimits.Historical);
    internal static HistoryReport? ReadCurrent(ReadOnlySpan<byte> bytes) => Read(bytes, MeasurementLimits.Current);
    private static HistoryReport? Read(ReadOnlySpan<byte> bytes, MeasurementLimits limits)
    {
        var report = ReplayWire.Read(bytes, HistoryJsonContext.Default.HistoryReport, MaximumBytes);
        return report is not null && Valid(report, limits) ? report : null;
    }

    private static bool Valid(HistoryReport report, MeasurementLimits limits) => report.Mode == "deterministic" &&
        report.Profile is "replay" or "tools" or "continuation" or "invalid" &&
        HistoryCapture.Hex40(report.SourceCommit) && HistoryCapture.Hex40(report.SourceTree) &&
        Code(report.Code) && report.Cleanup is "cleaned" or "not_created" or "cleanup_failed" &&
        !report.Rows.IsDefault && report.Rows.Length <= Growth.Profiles.GrowthProfiles.Attempts &&
        report.Rows.Select((row, index) => row is not null && row.Phase == index && row.ProcessId >= 0 && Code(row.Code) &&
            (row.Accepted ? row.Generation == index && EvaluationLimits.Hash(row.SessionSha256) && EvaluationLimits.Hash(row.EnvelopeSha256) :
                row.Generation is null && row.SessionSha256 is null && row.EnvelopeSha256 is null) &&
            HistoryCapture.Safe(row.Capture, limits) && Bound(row.Capture, report, index) && row.Comparison is not null &&
            (row.Comparison.Code == "compared" ? row.Comparison.LogicalStable is not null && row.Comparison.ProviderStable is not null :
                row.Comparison.Code is "unavailable" or "incomparable" && row.Comparison.LogicalStable is null && row.Comparison.ProviderStable is null) &&
            (row.Capacity is null || row.Capacity.Calls is >= 1 and <= AgentLimits.ModelCalls &&
                row.Capacity.LastProjectRequestBytes >= 1 && row.Capacity.LastProjectRequestBytes <= limits.RequestBytes &&
                row.Capacity.LastMessages >= 1 && row.Capacity.LastMessages <= limits.Messages &&
                row.Capacity.LastResponseMessages >= row.Capacity.LastMessages &&
                row.Capacity.LastResponseMessages <= limits.Messages + AgentLimits.ToolCallsPerResponse + 1 &&
                row.Capacity.LastContinuationBeforeBytes >= 0 && row.Capacity.LastContinuationBeforeBytes <= limits.ContinuationTotalBytes &&
                row.Capacity.LastContinuationAfterBytes >= row.Capacity.LastContinuationBeforeBytes &&
                row.Capacity.LastContinuationAfterBytes <= 2L * limits.ContinuationTotalBytes)).All(valid => valid) &&
        (report.Code != "verified" || (limits == MeasurementLimits.Historical ? report.ContinuityVerified : report.CurrentContinuityVerified)) &&
        (report.Code != "observation_incomplete" || limits == MeasurementLimits.Historical || report.CurrentContinuityVerified);

    private static bool Bound(HistoryCapture capture, HistoryReport report, int phase) =>
        new[] { capture.Baseline }.Concat(capture.Calls).All(observation => observation is null ||
            observation.Domain.SourceCommit == report.SourceCommit && observation.Domain.SourceTree == report.SourceTree &&
            observation.Domain.SourceClean == report.SourceClean && observation.Domain.Generation == phase - 1 &&
            observation.Domain.AcceptedSessionSha256 == (phase == 0 ? null : report.Rows[phase - 1].SessionSha256));

    private static bool Code(string? code) => code is "verified" or "completed" or "input_invalid" or "infrastructure_failed" or
        "evidence_invalid" or "cleanup_failed" or "cancelled" or "state_failed" or "session_failed" or "agent_failed" or
        "tool_failed" or "unknown_failed" or "provider_failed" or "script_exhausted" or "history_failed" or "assertion_failed" or
        "result_invalid" or "predecessor_changed" or "process_failed" or "process_timeout" or "observation_incomplete" ||
        Growth.Profiles.GrowthProfiles.AgentCode(code) || Growth.Profiles.GrowthProfiles.SessionCode(code) ||
        Growth.Profiles.GrowthProfiles.StateCode(code);
}

[JsonSerializable(typeof(HistoryReport))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
    RespectRequiredConstructorParameters = true, MaxDepth = 20)]
internal sealed partial class HistoryJsonContext : JsonSerializerContext;
