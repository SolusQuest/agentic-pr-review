using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;
using static AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification.GateContracts;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;

internal static class GateEconomicsOracle
{
    internal static JsonElement Verify(GateCase item, GateSelection selection)
    {
        if (item.Id == "c2-plan-budget")
        {
            var scalar = PricingJson.ReadValue(Bytes(item.Evidence), GateJson.Default.GateScalar, 4096, 4);
            Require(scalar is not null && scalar.Code == "r6_economics_allocation_invalid" &&
                scalar.Values.SequenceEqual(new decimal?[] { 0, 0 }) && scalar.Facts.IsEmpty);
            return item.Evidence;
        }
        var evidence = PricingJson.ReadValue(Bytes(item.Evidence), GateJson.Default.GateEconomics, EconomicsLiveLimits.ReportBytes + 65536, 40)!;
        Require(evidence is not null && evidence.RootAbsent);
        // Re-admit ORIGINAL nested JSON, including enum spelling and extras.
        var report = EconomicsReportJson.Read(Bytes(item.Evidence.GetProperty("report"))) ?? throw new InvalidOperationException("r6_gate_economics");
        Require(report.ExecutionKind == "loopback" && report.Plan.Source.Commit == selection.SourceCommit &&
            report.Plan.Source.Tree == selection.SourceTree && report.Plan.Source.Clean == selection.SourceClean &&
            report.Plan.BuildSha256 == selection.BuildSha256 && report.Plan.Workload.ReplaySha256 == selection.ReplaySha256 &&
            report.Plan.Workload.GrowthSha256 == selection.GrowthSha256);
        Require(evidence!.Workers.Length <= report.Attempted && evidence.Workers.Select(worker => worker.ProcessId).Distinct().Count() == evidence.Workers.Length &&
            evidence.Workers.Select(worker => worker.Startup).Distinct().Count() == evidence.Workers.Length &&
            evidence.Workers.Select(worker => worker.Index).Distinct().Count() == evidence.Workers.Length);
        foreach (var worker in evidence.Workers)
        {
            Require(worker.Index >= 0 && worker.Index < report.Attempted && Guid.TryParseExact(worker.Startup, "N", out _));
            GateHistoryOracle.Absent(worker.ProcessId);
            var step = report.Steps[worker.Index];
            if (step.ProcessId is not null) Require(step.ProcessId == worker.ProcessId && step.Startup == worker.Startup);
        }
        Require(report.Steps.Where(step => step.ProcessId is not null).All(step => evidence.Workers.Any(worker => worker.Index == step.Index)));
        var credential = item.Id == "c2-credential-probe";
        Require(evidence.SecretReads == (credential ? 1 : 0) && evidence.CredentialProofs.Length == (credential ? 3 : 0));
        for (var i = 0; i < evidence.CredentialProofs.Length; i++)
        {
            var proof = evidence.CredentialProofs[i];
            Require(proof.Requests == 2 && proof.EnvironmentChecked && proof.CompletedSessionChecked &&
                proof.RestoredSessionChecked == (i > 0) && proof.StoredObjects > 0);
        }
        var negativeRoot = item.Id is "c2-cleanup" or "c2-unreaped";
        Require(evidence.NegativeRootObserved == negativeRoot && report.Cleanup == (negativeRoot ? "cleanup_failed" : "cleaned"));
        var complete = item.Id is "c2-replay" or "c2-full" or "c2-execute-loopback" or "c2-credential-probe" or "c2-spaced-repeat";
        var missing = item.Id is "c2-wrong-source" or "c2-wrong-build" or "c2-wrong-predecessor" or
            "c2-before-ready-crash" or "c2-after-prepare-crash" or "c2-partial-reply" or "c2-oversized-reply" or
            "c2-wrong-reply" or "c2-hang" or "c2-final-missing" or "c2-unreaped" or "c2-campaign-deadline";
        var stop = item.Id switch
        {
            "c2-wrong-source" or "c2-wrong-build" or "c2-wrong-predecessor" or "c2-before-ready-crash" or
                "c2-after-prepare-crash" or "c2-partial-reply" or "c2-oversized-reply" or "c2-final-missing" => "process_failed",
            "c2-wrong-reply" => "receipt_invalid",
            "c2-rate-limit" => "rate_limited", "c2-usage-violation" => "accounting_violation",
            "c2-provider-failure" or "c2-preparation-failure" => "representative_history_insufficient",
            "c2-cancel-after-usage" or "c2-cancel-after-prepare" or "c2-cleanup" => "caller_cancelled",
            "c2-reject-accept" => "state_failed", "c2-hang" or "c2-campaign-deadline" => "deadline",
            "c2-unreaped" => "child_unreaped",
            _ when complete => "complete",
            _ => throw new InvalidOperationException("r6_gate_economics_case"),
        };
        var scheduled = item.Id == "c2-full" ? 20 : item.Id == "c2-spaced-repeat" ? 4 : item.Id == "c2-campaign-deadline" ? 2 : 3;
        var attempted = complete ? scheduled : item.Id is "c2-hang" or "c2-cleanup" or "c2-unreaped" or "c2-campaign-deadline" ? 1 : item.Id == "c2-final-missing" ? 3 : 2;
        Require(report.StopReason == stop && report.Scheduled == scheduled && report.Attempted == attempted &&
            report.ReceiptMissing == (missing ? 1 : 0) && report.Allocations.Calls == attempted * 8);
        Require(report.Steps.Skip(attempted).All(step => step.Code == "unattempted" && !step.Allocated));
        if (missing) Require(report.Journal is null && report.Pricing is null && !report.UsageComplete && !report.MonetaryComplete &&
            report.C1Handoff == "unavailable_missing_receipt");
        else Require(report.Journal is not null && report.Pricing is not null && report.C1Handoff == "available" &&
            report.Journal.Totals.Scheduled == scheduled && report.Journal.Totals.Unattempted == scheduled - attempted);
        if (report.Pricing is { } price)
        {
            var terms = price.Tariff.Terms;
            Require(terms.TokenUnit == 1_000_000 && terms.RateDecimalPlaces == 0 && terms.Arithmetic.DecimalPlaces == 6 &&
                terms.Rates.CacheHitInput == new TariffRate("USD", 1) && terms.Rates.CacheMissInput == new TariffRate("USD", 4) &&
                terms.Rates.Output == new TariffRate("USD", 5));
        }
        if (complete)
        {
            Require(report.UsageComplete && report.MonetaryComplete && report.Steps.All(step => step.Readback));
            Require(report.Steps.Count(step => step.Code == "capacity_stop") == (item.Id == "c2-full" ? 2 : 0) &&
                report.Steps.Count(step => step.Reset) == (item.Id == "c2-full" ? 2 : 0));
            var sends = item.Id == "c2-full" ? 39 : item.Id == "c2-spaced-repeat" ? 8 : 6;
            Require(report.Journal!.Totals.ActualSends == sends && report.Journal.Totals.KnownInputTokens == sends * 3 &&
                report.Journal.Totals.KnownOutputTokens == sends * 2 && report.Pricing!.ObservedUsage.TotalAmount == sends * .000022m);
        }
        if (item.Id is "c2-reject-accept" or "c2-cancel-after-prepare" or "c2-preparation-failure")
            Require(report.Steps[1].EvaluationStatus == "completed" && !report.Steps[1].Accepted &&
                report.Steps[1].Prepared == (item.Id != "c2-preparation-failure") && report.Steps[1].Observation!.Calls.All(call => call.UsageStatus == "known"));
        if (item.Id == "c2-hang") Require(evidence.Workers.Length == 1 && report.Plan.Workload.ChildSeconds == 5);
        if (item.Id == "c2-spaced-repeat") Require(report.Plan.Workload.SpacingMilliseconds == 50 && report.Steps.Skip(1).All(step => step.IntervalMilliseconds >= 50) &&
            !report.Steps[2].Restored && report.Steps[3].Restored);
        if (item.Id == "c2-unreaped") Require(evidence.Workers.IsEmpty);
        return Project(item.Evidence, report);
    }

    // Identity relations and timing bounds have been verified above and by the
    // existing reader. Only their execution-specific representations change.
    internal static JsonElement Project(JsonElement evidence, EconomicsReport report)
    {
        var json = JsonNode.Parse(Bytes(evidence))!;
        var node = json["report"]!;
        node["campaign"] = "selected-campaign";
        node["plan"]!["build_sha256"] = "selected-mode-build";
        node["plan_sha256"] = "selected-mode-plan";
        var sessions = new Dictionary<string, string>();
        string Session(string value)
        { if (!sessions.TryGetValue(value, out var stable)) sessions.Add(value, stable = "session-" + sessions.Count); return stable; }
        for (var i = 0; i < report.Steps.Length; i++)
        {
            var step = report.Steps[i]; var target = node["steps"]![i]!;
            if (!step.Allocated) continue;
            target["started_milliseconds"] = i;
            target["finished_milliseconds"] = i;
            if (step.IntervalMilliseconds is not null) target["interval_milliseconds"] = report.Plan.Workload.SpacingMilliseconds;
            if (step.ProcessId is not null) target["process_id"] = i + 1;
            if (step.Startup is not null) target["startup"] = "startup-" + i;
            if (step.AttemptSha256 is not null) target["attempt_sha256"] = "attempt-" + i;
            if (step.PredecessorSha256 is not null) target["predecessor_sha256"] = Session(step.PredecessorSha256);
            if (step.SessionSha256 is not null) target["session_sha256"] = Session(step.SessionSha256);
            if (step.Observation is { } observation)
            {
                // Logical request hash carries the random session continuation
                // envelope; its byte/count measurements stay exact.
                if (step.Restored) target["observation"]!["initial_prefix_sha256"] = "restored-prefix-" + i;
                if (observation.CompletedSessionSha256 is not null)
                    target["observation"]!["completed_session_sha256"] = Session(observation.CompletedSessionSha256);
            }
        }
        for (var i = 0; i < report.Outcomes.Length; i++)
        {
            var outcome = node["outcomes"]![i]!;
            outcome["attempt_sha256"] = "attempt-" + i;
            if (report.Outcomes[i].ExecutionSha256 is not null) outcome["execution_sha256"] = "execution-" + i;
            // Admitted run configuration commits to the generated campaign ID.
            outcome["configuration_sha256"] = "run-configuration-" + i;
        }
        NormalizeJournal(node["journal"]);
        if (node["pricing"] is { } price)
        {
            NormalizeJournal(price["journal"]);
            price["journal_sha256"] = "selected-campaign-journal";
        }
        for (var i = 0; i < json["workers"]!.AsArray().Count; i++)
        {
            var worker = json["workers"]![i]!;
            var index = worker["index"]!.GetValue<int>();
            worker["process_id"] = index + 1; worker["startup"] = "startup-" + index;
        }
        return GateHistoryOracle.Element(json);
    }

    private static void NormalizeJournal(JsonNode? journal)
    {
        if (journal is null) return;
        journal["provenance"]!["campaign_id"] = "selected-campaign";
        journal["provenance"]!["build_id"] = "selected-mode-build";
        journal["binding_sha256"] = "selected-campaign-binding";
        foreach (var attempt in journal["attempts"]!.AsArray())
        {
            var index = attempt!["schedule_index"]!.GetValue<int>();
            attempt["binding_sha256"] = "selected-campaign-binding"; attempt["attempt_id"] = "attempt-" + index;
            if (attempt["evaluation_attempt_sha256"] is not null) attempt["evaluation_attempt_sha256"] = "attempt-" + index;
        }
        foreach (var call in journal["calls"]!.AsArray())
        {
            var oldId = call!["attempt_id"]!.GetValue<string>();
            var index = int.Parse(oldId[(oldId.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture) - 1;
            call["binding_sha256"] = "selected-campaign-binding"; call["attempt_id"] = "attempt-" + index;
            call["call_id"] = "attempt-" + index + "-call-" + call["ordinal"]!.GetValue<int>();
        }
    }
}
