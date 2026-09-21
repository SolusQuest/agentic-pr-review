using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Comparison;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;
using static AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification.GateContracts;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;

internal static class GateComparisonOracle
{
    internal static JsonElement Verify(GateCase item, GateSelection selection, IReadOnlyDictionary<string, GateCase> cases)
    {
        if (item.Id == "c1-prefix-conflict")
        {
            var value = PricingJson.ReadValue(Bytes(item.Evidence), GateJson.Default.GateComparisonConflict, ComparisonLimits.ReportBytes, 32);
            Require(value is not null);
            var left = ComparisonJson.ReadInput(Bytes(item.Evidence.GetProperty("left")))!;
            var right = ComparisonJson.ReadInput(Bytes(item.Evidence.GetProperty("right")))!;
            Require(left is not null && right is not null);
            var conflict = false;
            try { _ = ComparisonReport.Create(left!, right!); }
            catch (ComparisonInputException error) { conflict = error.Code == "r6_comparison_expectation_conflict"; }
            Require(conflict);
            return item.Evidence;
        }
        var report = ComparisonJson.Read(Bytes(item.Evidence)) ?? throw new InvalidOperationException("r6_gate_comparison");
        var result = report.Result;
        Require(report.Left.Pricing.Journal.Provenance.SourceCommit == selection.SourceCommit &&
            report.Left.Pricing.Journal.Provenance.SourceTree == selection.SourceTree &&
            report.Left.Pricing.Journal.Provenance.SourceClean == selection.SourceClean &&
            report.Right.Pricing.Journal.Provenance.SourceCommit == (item.Id == "c1-source-axis" ? Hash('f', 40) : selection.SourceCommit));
        Require(result.FormalRegression == "inconclusive" && result.HistoricalR5Quality == "inconclusive" && result.R7Readiness == "not_evaluated" &&
            result.Left.IndependentHumanConfirmation == "not_evidenced" && result.Right.IndependentHumanConfirmation == "not_evidenced" &&
            result.Left.CampaignLifecycleAssociation == "native_campaign_link_unproven" &&
            result.PromotionDisposition == (item.Id == "c1-prefix-bound" ? "blocked_unexpected_prefix_drift" : "no_promotion_approval"));
        Require(item.Id switch
        {
            "c1-descriptive" => result.DescriptiveComparison.Status == "comparable" && result.ObservedReferenceTotalDifference == .10m &&
                result.DescriptiveDirection == "increased" && result.RulePredeclaration == "unverified_no_producer_receipt" &&
                report.Left.Pricing.ObservedUsage.TotalAmount == .42m && report.Left.Pricing.SameTokenAllMiss.TotalAmount == .60m,
            "c1-source-axis" => result.DescriptiveComparison.Status == "comparable" && report.Left.Declaration.Axis == "source_build" &&
                report.Left.Pricing.Journal.Provenance.BuildId != report.Right.Pricing.Journal.Provenance.BuildId,
            "c1-fixed-mismatch" => result.DescriptiveComparison.Status == "not_comparable" && result.DescriptiveComparison.Reasons.Contains("tariff_mismatch") &&
                result.ObservedReferenceTotalDifference is null,
            "c1-partial-population" => result.Left.Campaign.Scheduled == 3 && result.Left.Campaign.Completed == 1 && result.Left.Campaign.Failed == 1 &&
                result.Left.Campaign.Unattempted == 1 && result.Left.CompletionRate.Value == .333333m && report.Left.Pricing.ObservedUsage.TotalAmount == .84m &&
                result.Left.Execution.Status == "incomplete" && result.DescriptiveComparison.Status != "comparable",
            "c1-effective-conditional" => result.Left.EffectiveReviewCost.Reason == "conditional_on_declared_definition_and_origin" &&
                result.Left.EffectiveReviewCost.FullCampaignAmount == .84m && result.Left.EffectiveReviewCost.Value == .84m && result.Left.HumanDeclaredAnnotations == 1,
            "c1-control-unsupported" => result.DescriptiveComparison.Status == "not_comparable" && result.ExperimentalControl.Reasons.Contains("cache_control_unsupported"),
            "c1-prefix-bound" => result.PrefixEvidence.Sources.Length == 1 && result.PrefixEvidence.Sources[0].ObservedInstability == 2 &&
                result.PrefixEvidence.Sources[0].UnexpectedDrift == 2 && result.PrefixEvidence.UnboundExpectations == 0,
            "c1-prefix-unbound" => result.PrefixEvidence.UnboundExpectations == 1 && result.PrefixEvidence.Sources.Length == 1 &&
                result.PrefixEvidence.Sources[0].UnknownExpectedness == 1 && result.PrefixEvidence.Sources[0].ObservedInstability == 1 &&
                result.PrefixEvidence.Sources[0].UnexpectedDrift == 0,
            "c1-c2-handoff" => result.Left.Campaign.Scheduled == 3 && result.Left.Campaign.Completed == 3 &&
                result.Right.Campaign.Scheduled == 3 && result.Right.Campaign.Unattempted == 1 && result.Right.Campaign.Completed == 2 &&
                report.Left.Pricing.ObservedUsage.TotalAmount == .000132m && report.Right.Pricing.ObservedUsage.TotalAmount == .000088m &&
                result.HistoryWorkloadEquivalence == "native_history_equivalence_unproven",
            _ => false,
        });
        if (item.Id != "c1-c2-handoff") return item.Evidence;
        var json = JsonNode.Parse(Bytes(item.Evidence))!;
        foreach (var (side, id) in new[] { ("left", "c2-replay"), ("right", "c2-reject-accept") })
        {
            var c2 = cases[id];
            var original = c2.Evidence.GetProperty("report");
            var input = item.Evidence.GetProperty(side);
            Require(JsonElement.DeepEquals(input.GetProperty("pricing"), original.GetProperty("pricing")) &&
                JsonElement.DeepEquals(input.GetProperty("evidence").GetProperty("outcomes"), original.GetProperty("outcomes")));
            var projected = GateEconomicsOracle.Verify(c2, selection).GetProperty("report");
            json[side]!["pricing"] = JsonNode.Parse(projected.GetProperty("pricing").GetRawText());
            json[side]!["evidence"]!["outcomes"] = JsonNode.Parse(projected.GetProperty("outcomes").GetRawText());
            foreach (var selectedSide in new[] { "left", "right" })
            {
                var declared = json[side]!["declaration"]![selectedSide]!;
                declared["build_id"] = "selected-mode-build";
                declared["journal_sha256"] = "selected-" + selectedSide + "-journal";
                declared["evidence_sha256"] = "selected-" + selectedSide + "-evidence";
            }
        }
        return GateHistoryOracle.Element(json);
    }
}
