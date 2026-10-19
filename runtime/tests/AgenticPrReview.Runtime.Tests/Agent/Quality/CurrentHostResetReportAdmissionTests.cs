using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

// Pure report-admission mutants. Actual encrypted Host execution is separately
// exercised by the Linux reset fixture and is never established by these rows.
public sealed class CurrentHostResetReportAdmissionTests
{
    [Theory]
    [InlineData("owner")]
    [InlineData("role")]
    [InlineData("sdk")]
    [InlineData("missing-owner")]
    [InlineData("missing-role")]
    [InlineData("missing-sdk")]
    [InlineData("missing-source-clean")]
    [InlineData("missing-bootstrap-claim")]
    [InlineData("old-marker-field")]
    [InlineData("old-stored-field")]
    [InlineData("old-consumed-field")]
    [InlineData("claim-link")]
    [InlineData("claim-absent")]
    [InlineData("claim-bootstrap")]
    [InlineData("record-auth")]
    [InlineData("wire-exclusion")]
    [InlineData("input-history")]
    [InlineData("row-missing")]
    [InlineData("row-duplicate")]
    [InlineData("unknown-field")]
    public void CurrentHostReportRejectsHistoricalMixingAndUnboundSourceClaims(string mutation)
    {
        var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", "agent", "r5");
        var selection = GateContracts.Select(fixtures);
        var report = Valid(selection);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, GateJson.Default.GateHostReport);
        GateCase Item(byte[] body) => GateCase.Create("p2-host-explicit-reset", "production-host-explicit-reset", body);
        var expected = GateHistoryOracle.Verify(Item(bytes), selection);
        var value = JsonNode.Parse(bytes)!;
        var rows = value["rows"]!.AsArray();
        switch (mutation)
        {
            case "owner": value["owner_issue"] = 403; break;
            case "role": value["evidence_role"] = "installed-sdk-context"; break;
            case "sdk": value["sdk_context_import_qualified"] = true; break;
            case "missing-owner": value.AsObject().Remove("owner_issue"); break;
            case "missing-role": value.AsObject().Remove("evidence_role"); break;
            case "missing-sdk": value.AsObject().Remove("sdk_context_import_qualified"); break;
            case "missing-source-clean": value.AsObject().Remove("source_clean"); break;
            case "missing-bootstrap-claim": rows[0]!.AsObject().Remove("consumed_record_sha256"); break;
            case "old-marker-field": rows[0]!["stored_markers_match"] = true; break;
            case "old-stored-field": rows[0]!["stored_session_sha256"] = GateContracts.Hash('5'); break;
            case "old-consumed-field": rows[1]!["restored_session_sha256"] = GateContracts.Hash('5'); break;
            case "claim-link": rows[1]!["consumed_record_sha256"] = GateContracts.Hash('9'); break;
            case "claim-absent": rows[2]!["consumed_record_sha256"] = null; break;
            case "claim-bootstrap": rows[0]!["consumed_record_sha256"] = GateContracts.Hash('9'); break;
            case "record-auth": rows[3]!["host_record_authenticated"] = false; break;
            case "wire-exclusion": rows[3]!["wire_exclusion"] = false; break;
            case "input-history": rows[4]!["initial_messages"] = 4; break;
            case "row-missing": rows.RemoveAt(4); break;
            case "row-duplicate": rows[4] = rows[3]!.DeepClone(); break;
            case "unknown-field": value["unclassified"] = true; break;
        }
        Assert.Throws<InvalidOperationException>(() => GateHistoryOracle.Verify(
            Item(System.Text.Encoding.UTF8.GetBytes(value.ToJsonString())), selection));
        Assert.Equal(JsonValueKind.Object, expected.ValueKind);
    }

    private static GateHostReport Valid(GateSelection selection)
    {
        string H(char value) => GateContracts.Hash(value);
        GateHostRow Row(int phase, string action, string disposition, string? code, int? calls,
            long generation, char epoch, char session, char stored, char? consumed, int publications,
            params char[] receipts) => new(phase, action, disposition, code, calls, 2, receipts.Length,
                generation, H(epoch), H(session), H(stored), consumed is { } prior ? H(prior) : null,
                receipts.Select(H).ToImmutableArray(), true, true, publications);
        return new(409, "s5-current-host-structural-records", false,
            selection.Mode, selection.SourceCommit, selection.SourceTree, selection.SourceClean, selection.GrowthSha256,
            [Row(0, "grow", "Accepted", null, null, 0, 'a', 'c', '5', null, 1, '1'),
             Row(1, "grow", "Accepted", null, null, 1, 'a', 'c', '6', '5', 2, '1', '2'),
             Row(2, "restore-only", "NotCommitted", AgentFailureCodes.ChatFailed, 0, 1, 'a', 'c', '6', '6', 2, '1', '2'),
             Row(3, "reset", "Accepted", null, null, 0, 'b', 'd', '7', null, 3, '3'),
             Row(4, "continue", "Accepted", null, null, 1, 'b', 'd', '8', '7', 4, '3', '4')]);
    }
}
