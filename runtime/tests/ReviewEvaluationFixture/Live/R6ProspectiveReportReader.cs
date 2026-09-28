using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Reporting;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

internal sealed record R6ProspectiveReadback(string Status, string Reason,
    R6ProspectiveGateResult? Recomputed);

// Public JSONL is evidence, not a self-authorizing success claim.
internal static class R6ProspectiveReportReader
{
    private const int MaximumBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static R6ProspectiveReadback Read(ReadOnlySpan<byte> bytes)
    {
        static R6ProspectiveReadback Reject(string reason) => new("rejected", reason, null);
        if (bytes.Length is < 1 or > MaximumBytes) return Reject("report_size_invalid");
        try
        {
            var text = StrictUtf8.GetString(bytes);
            var rawLines = text.Split('\n');
            if (rawLines.Length != 8 || rawLines[^1].Length != 0 ||
                rawLines.Take(7).Any(line => line.Length == 0 || line.Contains('\r') &&
                    (!line.EndsWith('\r') || line[..^1].Contains('\r'))))
                return Reject("report_shape_invalid");
            var lines = rawLines.Take(7).Select(line => line.TrimEnd('\r')).ToArray();
            var rawRows = lines.Take(5).Select(Encoding.UTF8.GetBytes)
                .Select(value => (ReadOnlyMemory<byte>)value).ToImmutableArray();
            var outcomes = ImmutableArray.CreateBuilder<EvaluationOutcome>();
            foreach (var row in rawRows)
            {
                var admitted = EvaluationJson.ReadOutcome(row.Span);
                if (admitted is null) return Reject("outcome_invalid");
                outcomes.Add(admitted);
            }
            var reportRow = Encoding.UTF8.GetBytes(lines[5]);
            var report = EvaluationReportJson.Read(reportRow);
            var rebuilt = EvaluationReport.Create(rawRows);
            var rebuiltBytes = rebuilt.Succeeded && rebuilt.Value is not null
                ? EvaluationReportJson.Write(rebuilt.Value) : default;
            if (!report.Succeeded || !rebuilt.Succeeded || rebuiltBytes is null ||
                !rebuiltBytes.Succeeded || rebuiltBytes.Value is null ||
                !reportRow.AsSpan().SequenceEqual(rebuiltBytes.Value))
                return Reject("aggregate_report_mismatch");
            var summaryBytes = Encoding.UTF8.GetBytes(lines[6]);
            var summary = JsonSerializer.Deserialize(summaryBytes, LiveJsonContext.Default.LiveRunSummary);
            if (summary?.ProspectiveRubric is null || summary.ProspectiveQualityCandidate is null)
                return Reject("prospective_summary_missing");
            if (!summaryBytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(summary,
                LiveJsonContext.Default.LiveRunSummary)))
                return Reject("summary_canonical_invalid");
            if (!ValidReceiptShape(summary, outcomes.ToImmutable()))
                return Reject("receipt_shape_invalid");
            var recomputed = R6ProspectiveQualityGate.Evaluate(summary, outcomes.ToImmutable());
            if (recomputed is null || recomputed != summary.ProspectiveQualityCandidate)
                return Reject("candidate_claim_mismatch");
            return new(recomputed.Status, recomputed.Reason, recomputed);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or
            NotSupportedException or ArgumentException)
        {
            return Reject("report_invalid");
        }
    }

    private static bool ValidReceiptShape(LiveRunSummary summary,
        ImmutableArray<EvaluationOutcome> outcomes)
    {
        if (summary.ProspectiveRubric is not { } rubric ||
            rubric.Id != R6ProspectiveRubric.Id ||
            rubric.Sha256 != R6ProspectiveRubric.Sha256 ||
            summary.ProspectiveCaseReceipts is not { } cases || cases.IsDefault ||
            summary.ProspectiveRecoveryReceipts is not { } recoveries ||
            recoveries.IsDefault || cases.Length != 5 || recoveries.Length != 5 ||
            outcomes.Length != 5)
            return false;
        var diagnostics = summary.RecoveryDiagnostics ?? [];
        if (diagnostics.IsDefault) return false;
        for (var index = 0; index < 5; index++)
        {
            var outcome = outcomes[index];
            var receipt = cases[index];
            var recovery = recoveries[index];
            if (receipt is null || recovery is null ||
                receipt.RubricId != rubric.Id || receipt.RubricSha256 != rubric.Sha256 ||
                receipt.ScheduleIndex != index ||
                receipt.CaseId != R6ProspectiveRubric.Cases[index] ||
                receipt.CorpusSha256 != outcome.CorpusSha256 ||
                receipt.CaseSha256 != outcome.CaseSha256 ||
                receipt.ConfigurationSha256 != outcome.ConfigurationSha256 ||
                receipt.ExecutionSha256 != outcome.ExecutionSha256 ||
                receipt.FindingRowCount != outcome.FindingCount ||
                receipt.Findings.IsDefault ||
                recovery.RubricId != rubric.Id ||
                recovery.RubricSha256 != rubric.Sha256 ||
                recovery.ScheduleIndex != index || recovery.CaseSha256 != outcome.CaseSha256 ||
                recovery.ConfigurationSha256 != outcome.ConfigurationSha256 ||
                recovery.ExecutionSha256 != outcome.ExecutionSha256 ||
                recovery.ObservedCount != diagnostics.Count(d => d.ScheduleIndex == index) ||
                recovery.ObservedCount is < 0 or > 1 ||
                recovery.Status is not (R6ProspectiveRecoveryAudit.None or
                    R6ProspectiveRecoveryAudit.Qualified or R6ProspectiveRecoveryAudit.Blocked) ||
                recovery.Reason is not ("none" or "qualified" or "capture_missing" or
                    "error_pair_invalid" or "rejected_dispatched" or
                    "recovery_error_as_evidence" or "followup_missing" or
                    "completion_missing" or "count_mismatch" or "multiple_recoveries"))
                return false;
            if (receipt.Status == "pending")
            {
                if (receipt.Origin != "none" || receipt.Findings.Length != 0) return false;
            }
            else if (receipt.Status == "assessed")
            {
                if (receipt.Origin is not (R6ProspectiveAssessment.AiOrigin or
                        R6ProspectiveAssessment.HumanOrigin) ||
                    receipt.Findings.Length != receipt.FindingRowCount) return false;
            }
            else return false;
        }
        return true;
    }

    internal static int Invoke(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is < 1 or > MaximumBytes)
            {
                Console.WriteLine("r6_prospective_report_size_invalid");
                return 2;
            }
            var result = Read(File.ReadAllBytes(path));
            Console.WriteLine("r6_prospective_" + result.Status + " " + result.Reason);
            return result.Status == "candidate_pass" ? 0 : 1;
        }
        catch
        {
            Console.WriteLine("r6_prospective_report_unavailable");
            return 2;
        }
    }
}
