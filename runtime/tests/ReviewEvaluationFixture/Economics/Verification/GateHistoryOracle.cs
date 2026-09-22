using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Histories;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Prefix;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;
using static AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification.GateContracts;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;

internal static class GateHistoryOracle
{
    internal static JsonElement Verify(GateCase item, GateSelection selection)
    {
        if (item.Id == "p2-host-capacity-reset") return Host(item, selection);
        var report = HistoryJson.Read(Bytes(item.Evidence)) ?? throw new InvalidOperationException("r6_gate_history");
        Require(report.SourceCommit == selection.SourceCommit && report.SourceTree == selection.SourceTree &&
            report.SourceClean == selection.SourceClean && report.Cleanup == "cleaned" && report.Rows.All(row => row.PredecessorPreserved));
        var positive = item.Id is "p2-replay" or "p2-tools" or "p2-continuation";
        var profile = item.Id == "p2-tools" ? "tools" : item.Id == "p2-continuation" ? "continuation" : "replay";
        Require(report.Profile == profile && report.Rows.Length == (positive ? profile == "replay" ? 3 : profile == "tools" ? 6 : 7 : 2));
        Require(report.Rows.Select(row => row.ProcessId).Distinct().Count() == report.Rows.Length);
        foreach (var row in report.Rows) Absent(row.ProcessId);
        Require(HistoryReport.Positive(report.Rows[0]));
        var session = report.Rows[0].Capture.Baseline!.Domain.SessionSha256;
        var phasePlans = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in report.Rows)
        {
            var observations = new[] { row.Capture.Baseline }.Concat(row.Capture.Calls).OfType<PrefixObservation>().ToArray();
            if (observations.Length == 0) continue;
            // One selected session spans the chain. A phase uses one domain,
            // and each new predecessor produces a distinct stable plan.
            Require(observations.All(value => value.Domain.SessionSha256 == session && value.Domain == observations[0].Domain) &&
                phasePlans.Add(observations[0].Domain.StablePlanSha256));
        }
        if (positive) Require(report.ContinuityVerified);
        else
        {
            var code = item.Id switch
            {
                "p2-missing-history" => "history_failed",
                "p2-reordered-history" or "p2-missing-continuation" or "p2-wrong-position" => "unknown_failed",
                "p2-changed-continuation" => "session_failed",
                "p2-wrong-scope" or "p2-wrong-head" or "p2-stale-generation" or "p2-policy" or "p2-model" or "p2-adapter" or "p2-toolset" => "state_failed",
                _ => throw new InvalidOperationException("r6_gate_history_case"),
            };
            var failed = report.Rows[1];
            Require(report.Code == code && failed.Code == code && !failed.Accepted && failed.SessionSha256 is null && failed.Generation is null);
            if (code == "state_failed") Require(failed.Capture.Code == (item.Id == "p2-toolset" ? "observed" : "unavailable") &&
                failed.Capture.Calls.IsEmpty && !failed.WireMatch);
            if (item.Id == "p2-missing-history") Require(failed.Capture.Code == "unmeasurable");
            if (item.Id == "p2-changed-continuation") Require(failed.RestoredMatch && failed.Comparison == new PrefixComparison("compared", false, false));
        }
        var accepted = report.Rows.Where(row => row.Accepted).ToArray();
        Require(accepted.Select(row => row.SessionSha256).Distinct().Count() == accepted.Length &&
            accepted.Select(row => row.EnvelopeSha256).Distinct().Count() == accepted.Length);
        var json = JsonNode.Parse(Bytes(item.Evidence))!;
        for (var index = 0; index < report.Rows.Length; index++)
        {
            var row = report.Rows[index];
            var node = json["rows"]![index]!;
            node["process_id"] = index + 1;
            if (row.Accepted) { node["session_sha256"] = "session-" + index; node["envelope_sha256"] = "envelope-" + index; }
            NormalizeObservation(node["capture"]!["baseline"], row.Phase, continuation: row.Phase > 0);
            for (var call = 0; call < row.Capture.Calls.Length; call++)
                NormalizeObservation(node["capture"]!["calls"]![call], row.Phase, continuation: row.Phase > 0 || call > 0);
        }
        return Element(json);
    }

    private static void NormalizeObservation(JsonNode? node, int phase, bool continuation)
    {
        if (node is null) return;
        node["domain"]!["session_sha256"] = "selected-session";
        if (phase > 0)
        {
            node["domain"]!["accepted_session_sha256"] = "session-" + (phase - 1);
            // The stable plan commits to the admitted predecessor SESSION.
            node["domain"]!["stable_plan_sha256"] = "plan-for-predecessor-" + (phase - 1);
        }
        if (!continuation) return;
        // Only logical continuation metadata carries the random SessionId.
        // Counts/bytes, provider segments, control/history, and every other
        // stable segment remain exact, including provider whole-request hashes.
        node["logical"]![phase > 0 ? "settings" : "dynamic"]!["sha256"] = "contains-selected-session";
        node["logical"]!["whole"]!["sha256"] = "contains-selected-session";
    }

    private static JsonElement Host(GateCase item, GateSelection selection)
    {
        var report = PricingJson.ReadValue(Bytes(item.Evidence), GateJson.Default.GateHostReport, 128 * 1024, 16)!;
        Require(report is not null && report.Mode == selection.Mode && report.SourceCommit == selection.SourceCommit &&
            report.SourceTree == selection.SourceTree && report.SourceClean == selection.SourceClean && report.CorpusSha256 == selection.GrowthSha256 &&
            report.Rows.Length == 9);
        var rows = report!.Rows;
        var capacity = rows.Length - 4;
        Require(rows.Take(capacity + 1).All(row => row.Action == "grow") && rows[capacity + 1].Action == "restore-only" &&
            rows[capacity + 2].Action == "reset" && rows[capacity + 3].Action == "continue");
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            Require(row.Phase == i && row.StoredMarkersMatch && row.WireExclusion && row.InitialMessages >= 2 &&
                row.AcceptedCount == row.AcceptanceIdentities.Length && row.AcceptanceIdentities.Distinct().Count() == row.AcceptedCount &&
                row.AcceptanceIdentities.All(Hex) && Hex(row.EpochSha256) && Hex(row.SessionIdSha256) && Hex(row.StoredSessionSha256));
            if (i < capacity || i > capacity + 1)
                Require(row.Disposition == "Accepted" && row.AgentCode is null && row.ModelCalls is null);
            else Require(row.Disposition == "NotCommitted");
            if (i < capacity)
                Require(row.Disposition == "Accepted" && row.Generation == i && row.AcceptedCount == Math.Min(2, i + 1) && row.Publications == i + 1 &&
                    row.EpochSha256 == rows[0].EpochSha256 && row.SessionIdSha256 == rows[0].SessionIdSha256 &&
                    row.RestoredSessionSha256 == (i == 0 ? null : rows[i - 1].StoredSessionSha256));
        }
        var stopped = rows[capacity];
        var previous = rows[capacity - 1];
        var restored = rows[capacity + 1];
        var reset = rows[capacity + 2];
        var next = rows[capacity + 3];
        Require(stopped.Disposition == "NotCommitted" && stopped.AgentCode == AgentFailureCodes.ResponseInvalid && stopped.ModelCalls == 1 &&
            stopped.InitialMessages + 9 > AgentLimits.Messages && stopped.AcceptanceIdentities.SequenceEqual(previous.AcceptanceIdentities) &&
            stopped.Generation == previous.Generation && stopped.EpochSha256 == previous.EpochSha256 &&
            stopped.SessionIdSha256 == previous.SessionIdSha256 && stopped.RestoredSessionSha256 == previous.StoredSessionSha256 &&
            stopped.StoredSessionSha256 == previous.StoredSessionSha256 && stopped.Publications == previous.Publications);
        Require(restored.Disposition == "NotCommitted" && restored.AgentCode == AgentFailureCodes.ChatFailed && restored.ModelCalls == 0 &&
            restored.AcceptanceIdentities.SequenceEqual(previous.AcceptanceIdentities) && restored.StoredSessionSha256 == previous.StoredSessionSha256 &&
            restored.Generation == previous.Generation && restored.EpochSha256 == previous.EpochSha256 && restored.Publications == previous.Publications &&
            restored.SessionIdSha256 == previous.SessionIdSha256 && restored.RestoredSessionSha256 == previous.StoredSessionSha256);
        Require(reset.Disposition == "Accepted" && reset.Generation == 0 && reset.AcceptedCount == 1 && reset.RestoredSessionSha256 is null &&
            reset.EpochSha256 != previous.EpochSha256 && reset.SessionIdSha256 != previous.SessionIdSha256 &&
            next.Disposition == "Accepted" && next.Generation == 1 && next.AcceptedCount == 2 && next.EpochSha256 == reset.EpochSha256 &&
            next.SessionIdSha256 == reset.SessionIdSha256 && next.RestoredSessionSha256 == reset.StoredSessionSha256 &&
            reset.Publications == previous.Publications + 1 && next.Publications == reset.Publications + 1);
        var json = JsonNode.Parse(Bytes(item.Evidence))!;
        json["mode"] = "verified-execution-mode";
        var receipts = new Dictionary<string, string>();
        var sessions = new Dictionary<string, string>();
        string Map(Dictionary<string, string> map, string value)
        { if (!map.TryGetValue(value, out var stable)) map.Add(value, stable = "identity-" + map.Count); return stable; }
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i]; var node = json["rows"]![i]!;
            node["epoch_sha256"] = i <= capacity + 1 ? "previous-epoch" : "reset-epoch";
            node["session_id_sha256"] = i <= capacity + 1 ? "previous-session" : "reset-session";
            node["stored_session_sha256"] = Map(sessions, row.StoredSessionSha256);
            if (row.RestoredSessionSha256 is not null) node["restored_session_sha256"] = Map(sessions, row.RestoredSessionSha256);
            // Random authenticated receipt IDs are represented by verified
            // membership relations, in creation order, not sorted random bytes.
            var names = row.AcceptanceIdentities.Select(value => Map(receipts, value)).Order(StringComparer.Ordinal).ToArray();
            node["acceptance_identities"] = new JsonArray(names.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        }
        return Element(json);
    }

    internal static void Absent(int pid)
    {
        Require(pid > 0 && pid != Environment.ProcessId);
        try { using var process = Process.GetProcessById(pid); Require(process.HasExited); }
        catch (ArgumentException) { }
    }
    private static bool Hex(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static JsonElement Element(JsonNode node)
    { using var document = JsonDocument.Parse(node.ToJsonString()); return document.RootElement.Clone(); }
}
