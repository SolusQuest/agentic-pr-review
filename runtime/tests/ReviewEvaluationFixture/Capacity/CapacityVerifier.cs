using System.Security.Cryptography;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Capacity;

// Outside-in acceptance inventory. This verifier reads receipts; it does not execute the runner
// or trust its passed flag. Each omitted/refused/forged outcome has a distinct coverage requirement.
internal static class CapacityVerifier
{
    internal static string? Verify(CapacityReport? report, byte[] corpus, bool requireClean = true)
    {
        try { CapacitySpec.Admit(corpus); } catch { return "r7_capacity_corpus_invalid"; }
        if (report is null || report.Schema != CapacitySpec.ReportSchema || report.ObservationDomain != CapacitySpec.ObservationDomain || report.Code != "r7_capacity_passed" ||
            !report.Cleanup || requireClean && !report.SourceClean || !Hex(report.SourceCommit, 40) || !Hex(report.SourceTree, 40) ||
            report.CorpusSha256 != Convert.ToHexStringLower(SHA256.HashData(corpus))) return "r7_capacity_report_identity";
        if (report.Cases is null || report.Cases.Length != CapacitySpec.Cases.Length || report.Cases.Any(item => item is null) ||
            !report.Cases.Select(item => item.Id).SequenceEqual(CapacitySpec.Cases.Select(item => item.Id)) ||
            report.Cases.Select(item => item.StartupId).Distinct(StringComparer.Ordinal).Count() != report.Cases.Length)
            return "r7_capacity_case_inventory";
        for (var index = 0; index < CapacitySpec.Cases.Length; index++)
        {
            var expected = CapacitySpec.Cases[index]; var actual = report.Cases[index];
            var success = expected.Mode is "success" or "reset";
            var negative = expected.Mode is "role" or "association" or "policy" or "scope";
            var tools = success ? (expected.Calls - 1) * expected.ToolsPerTurn + 1 : 0;
            if (actual.Code != CapacitySpec.ExpectedCode(expected.Mode) || actual.Calls != expected.Calls || actual.Tools != tools ||
                actual.Sends != expected.Calls || actual.ObservationDomain != CapacitySpec.ObservationDomain || actual.SourceCommit != report.SourceCommit || actual.SourceTree != report.SourceTree ||
                actual.SourceClean != report.SourceClean || !actual.RolesAndAssociations || !actual.CiphertextPrivate ||
                actual.ProcessId <= 0 || !Hex(actual.StartupId, 32) || actual.Metrics is null)
                return "r7_capacity_case_semantics";
            if (actual.RestoredExact != (expected.Id is not ("default64" or "configured128")) ||
                actual.PredecessorPreserved != !success || actual.SessionRejected != (!success && !negative) ||
                actual.NoTerminalReview != (!success && !negative) || actual.ResetExecuted != (expected.Mode == "reset") ||
                actual.OldHistoryAbsent != (expected.Id is "reset" or "reset_restore")) return "r7_capacity_state_semantics";
            if (negative) continue;
            var metrics = actual.Metrics;
            if (metrics.ElapsedMilliseconds < 0 || metrics.AllocatedBytes < 0 || metrics.WorkingSetBytes <= 0 || metrics.PeakWorkingSetBytes <= 0 ||
                metrics.ProjectRequestBytes < 0 || (expected.Mode == "context" ? metrics.ProjectRequestBytes != 0 : metrics.ProjectRequestBytes == 0) ||
                metrics.ProjectRequestBytes > AgentLimits.RequestBytes || metrics.LogicalInputBytes < 0 ||
                metrics.LogicalInputBytes > AgentLimits.RequestBytes || metrics.ProviderResponseBytes is not null ||
                metrics.GenericResponseAdmissionBytes < 0 || metrics.GenericResponseAdmissionBytes > AgentLimits.ResponseBytes + 1 ||
                metrics.ReasoningContentBytes < 0 || metrics.ReasoningContentBytes > AgentLimits.ContentBytes + 1 ||
                metrics.InitialUserContentBytes < 1 || metrics.InitialUserContentBytes > AgentLimits.ContentBytes + 1 ||
                metrics.Messages > AgentLimits.Messages || metrics.Parts > AgentLimits.PartsTotal ||
                metrics.SessionRecords > AgentLimits.SessionRecords || metrics.SessionBytes <= 0 || metrics.SessionBytes > AgentLimits.SessionPlaintextBytes ||
                metrics.EnvelopeBytes <= 0 || metrics.EnvelopeBytes > AgentLimits.StateEnvelopeBytes ||
                metrics.StoredBytes <= 0 || metrics.StoredBytes > AgentLimits.StateScopeTotalBytes ||
                metrics.SnapshotBytes <= 8 * 1024 * 1024 || metrics.SnapshotBytes > AgentLimits.DiffSnapshotBytes)
                return "r7_capacity_measurement_bounds";
            if (expected.Id is "default64" or "configured128")
            {
                var assistants = expected.Calls - 1;
                if (actual.VerifiedAssistants != assistants || actual.VerifiedToolResults != tools - 1 || actual.ListedFiles != 320 || actual.DiffReads != 1 ||
                    metrics.Messages <= 64 || metrics.SessionRecords <= 256) return "r7_capacity_growth_missing";
            }
            if (expected.Id == "default_restore" && (actual.VerifiedAssistants != 64 || actual.VerifiedToolResults != 442) ||
                expected.Id == "configured_restore" && (actual.VerifiedAssistants != 128 || actual.VerifiedToolResults != 382))
                return "r7_capacity_fresh_restore_missing";
            if (expected.Mode == "context" && (metrics.LogicalInputBytes != 0 || actual.Sends != 0 || actual.Calls != 0 ||
                    metrics.InitialUserContentBytes != AgentLimits.ContentBytes + 1) ||
                expected.Mode != "context" && metrics.InitialUserContentBytes > AgentLimits.ContentBytes ||
                expected.Mode == "capacity" && metrics.ReasoningContentBytes != AgentLimits.ContentBytes + 1 ||
                expected.Mode != "capacity" && metrics.ReasoningContentBytes > AgentLimits.ContentBytes ||
                expected.Mode == "response" && metrics.GenericResponseAdmissionBytes != AgentLimits.ResponseBytes + 1 ||
                expected.Mode != "response" && metrics.GenericResponseAdmissionBytes != 0)
                return "r7_capacity_failure_stimulus";
        }
        if (report.HostCases is null || report.HostCases.Length != 8 || report.HostCases.Any(item => item is null) ||
            !report.HostCases.Select(item => item.Id).SequenceEqual(new[] { "host_seed" }.Concat(CapacitySpec.Failures.Select(mode => "host_" + mode))
                .Concat(["host_reset", "host_reset_restore"]))) return "r7_capacity_host_inventory";
        var currentHost = report.HostCases.All(item => item.CurrentHost is not null);
        if (!currentHost && report.HostCases.Any(item => item.CurrentHost is not null))
            return "r7_capacity_host_mixed_domain";
        foreach (var item in report.HostCases)
        {
            var mode = item.Id.StartsWith("host_reset", StringComparison.Ordinal) || item.Id == "host_seed" ? "success" : item.Id[5..];
            var success = mode == "success";
            var calls = item.Id == "host_seed" ? 64 : item.Id == "host_reset" ? 2 : mode == "context" ? 0 : 1;
            if (currentHost)
            {
                var evidence = item.CurrentHost!;
                var failureIndex = Array.IndexOf(CapacitySpec.Failures, mode);
                var world = failureIndex < 0 ? 0 : failureIndex + 1;
                var invocation = item.Id == "host_seed" ? 0 : item.Id == "host_reset_restore" ? 2 : 1;
                var claims = item.Id is "host_seed" or "host_reset" ? 0 : 1;
                var authored = evidence.Domain == "apr-r7-authored-capacity-host-v1" && evidence.OwnerIssue == 403;
                var historical = evidence.Domain == "apr-s5-current-capacity-host-v1" && evidence.OwnerIssue == 409 && evidence.AuthoredWork is null;
                if (authored && (evidence.AuthoredWork is not { } work ||
                    work.ExecutedTools != (success ? calls - 1 : 0) ||
                    work.ObservedInputs != (success ? calls : 1) ||
                    work.OutcomeDomain != (success ? "actual-apr-terminal" : "authored-failure-outcome")))
                    return "r7_capacity_authored_host_work";
                if ((!authored && !historical) ||
                    evidence.SdkContextImportQualified || evidence.SourceCommit != report.SourceCommit ||
                    evidence.SourceTree != report.SourceTree || evidence.SourceClean != report.SourceClean ||
                    evidence.World != world || evidence.Invocation != invocation || evidence.GenuineSeedAcceptances != 1 ||
                    evidence.SourceClaims != claims || !evidence.SourceRecordBound || !evidence.NativeHistoryAbsent)
                    return "r7_capacity_current_host_identity";
            }
            var authoredWork = item.CurrentHost?.AuthoredWork is not null;
            if (item.AgentCode != CapacitySpec.ExpectedCode(mode) || item.Calls != (authoredWork || mode == "context" ? 0 : calls) || item.Sends != (authoredWork ? 0 : calls) ||
                item.Continuation != (!currentHost && item.Id is not ("host_seed" or "host_reset")) ||
                item.FreshSession != item.Id.StartsWith("host_reset", StringComparison.Ordinal) ||
                (success ? item.Status != "Reviewed" || item.ExitCode != 0 || item.StateDisposition != "Accepted" || item.NoPublicationMutation :
                    item.Status != (mode == "deadline" ? "ProviderFailed" : "AgentResultInvalid") || item.ExitCode != 1 || item.StateDisposition != "NotCommitted" ||
                    !item.NoSuccessSummary || !item.NoPublicationMutation || !item.NoCandidateMutation || !item.PredecessorPreserved))
                return "r7_capacity_host_semantics";
        }
        return null;
    }

    internal static bool Equivalent(CapacityReport first, CapacityReport second)
    {
        // Caller must first verify each report. Runtime measurements and process identity are observations,
        // and encrypted state/transaction identity is deliberately randomized within each independent run.
        static CapacityReport Semantic(CapacityReport report) => report with
        {
            Cases = report.Cases.Select(item => item with
            {
                ProcessId = 0, StartupId = "", Metrics = item.Metrics with
                { ElapsedMilliseconds = 0, AllocatedBytes = 0, WorkingSetBytes = 0, PeakWorkingSetBytes = 0, StoredBytes = 0 },
            }).ToArray(),
        };
        return JsonSerializer.Serialize(Semantic(first), CapacityJson.Default.CapacityReport) ==
            JsonSerializer.Serialize(Semantic(second), CapacityJson.Default.CapacityReport);
    }
    private static bool Hex(string? value, int length) => value?.Length == length && value.All(character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
