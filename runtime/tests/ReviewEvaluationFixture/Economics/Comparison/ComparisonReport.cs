using System.Collections.Immutable;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Reporting;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Comparison;

internal static class ComparisonReport
{
    internal static ComparisonReportDocument Create(ComparisonInput left, ComparisonInput right)
    {
        if (ComparisonJson.DeclarationHash(left.Declaration) != ComparisonJson.DeclarationHash(right.Declaration))
            throw new ComparisonInputException("r6_comparison_declaration_mismatch");
        var declaration = left.Declaration;
        if (ComparisonJson.Select(left.Pricing, left.Evidence) != declaration.Left ||
            ComparisonJson.Select(right.Pricing, right.Evidence) != declaration.Right)
            throw new ComparisonInputException("r6_comparison_selection_mismatch");
        if (!ComparisonAdmission.ExecutionsConsistent(left.Evidence.Outcomes.Concat(right.Evidence.Outcomes)))
            throw new ComparisonInputException("r6_comparison_execution_conflict");
        var l = Side(left); var r = Side(right);
        var fixedReasons = FixedFactors(left, right);
        var reasons = new HashSet<string>(fixedReasons, StringComparer.Ordinal);
        if (l.Execution.Status != "complete" || r.Execution.Status != "complete") reasons.Add("execution_incomplete");
        if (l.Usage.Status != "complete" || r.Usage.Status != "complete") reasons.Add("usage_incomplete");
        if (l.Pricing.Status != "complete" || r.Pricing.Status != "complete") reasons.Add("pricing_incomplete");
        if (declaration.Axis == "cache_policy") reasons.Add("cache_control_unsupported");
        var comparable = reasons.Count == 0;
        var prefix = ComparisonPrefix.Build(left, right);
        var difference = comparable ? ComparisonNumbers.Difference(right.Pricing.ObservedUsage.TotalAmount!.Value,
            left.Pricing.ObservedUsage.TotalAmount!.Value) : (decimal?)null;
        var result = new ComparisonResult(l, r,
            Dimension(fixedReasons.Count == 0 ? "comparable" : "not_comparable", fixedReasons),
            "native_history_equivalence_unproven",
            Dimension(comparable ? "comparable" : reasons.Any(x => x.EndsWith("_incomplete", StringComparison.Ordinal)) &&
                fixedReasons.Count == 0 && declaration.Axis != "cache_policy" ? "incomplete" : "not_comparable", reasons),
            difference, difference is null ? "unavailable" : difference > 0 ? "increased" : difference < 0 ? "decreased" : "unchanged",
            prefix,
            Dimension("not_comparable", [declaration.Axis == "cache_policy" || declaration.ControlClaim != "not_requested"
                ? "cache_control_unsupported" : "controlled_experiment_not_declared"]),
            declaration.RegressionRule is null ? "not_declared" : "unverified_no_producer_receipt",
            "inconclusive", prefix.Sources.Any(s => s.PromotionDisposition == "blocked_unexpected_prefix_drift")
                ? "blocked_unexpected_prefix_drift" : "no_promotion_approval",
            "inconclusive", "not_evaluated", "supplied_artifact_consistency_not_origin_authentication");
        return new(ComparisonLimits.ReportFormat, left, right, result);
    }

    private static HashSet<string> FixedFactors(ComparisonInput left, ComparisonInput right)
    {
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var l = left.Pricing.Journal; var r = right.Pricing.Journal;
        if (!l.Provenance.SourceClean || !r.Provenance.SourceClean) reasons.Add("dirty_source");
        if (left.Declaration.Axis != "source_build" &&
            (l.Provenance.SourceCommit != r.Provenance.SourceCommit || l.Provenance.SourceTree != r.Provenance.SourceTree ||
                l.Provenance.BuildId != r.Provenance.BuildId)) reasons.Add("source_build_change_undeclared");
        if (l.Plan.CorpusSha256 != r.Plan.CorpusSha256) reasons.Add("corpus_mismatch");
        if (!l.Plan.Schedule.SequenceEqual(r.Plan.Schedule)) reasons.Add("scheduled_population_mismatch");
        if (l.Plan.Bounds != r.Plan.Bounds) reasons.Add("bounds_mismatch");
        if (l.Plan.Provider != r.Plan.Provider) reasons.Add("provider_configuration_mismatch");
        if (l.Provenance.ExecutionKind != r.Provenance.ExecutionKind) reasons.Add("execution_kind_mismatch");
        if (left.Pricing.TariffSha256 != right.Pricing.TariffSha256) reasons.Add("tariff_mismatch");
        static string? Model(ComparisonInput input)
        {
            var sends = input.Pricing.Journal.Calls.Where(c => c.Dispatched).ToArray();
            var models = sends.Select(c => c.Usage?.Cache?.ResponseModel).Distinct(StringComparer.Ordinal).ToArray();
            return sends.Length > 0 && models.Length == 1 ? models[0] : null;
        }
        var lm = Model(left); var rm = Model(right);
        if (lm is null || rm is null) reasons.Add("response_model_unbound");
        else if (lm != rm) reasons.Add("response_model_mismatch");
        var lo = left.Evidence.Outcomes.ToDictionary(o => o.AttemptSha256!, StringComparer.Ordinal);
        var ro = right.Evidence.Outcomes.ToDictionary(o => o.AttemptSha256!, StringComparer.Ordinal);
        for (var i = 0; i < Math.Min(l.Attempts.Length, r.Attempts.Length); i++)
        {
            if (!lo.TryGetValue(l.Attempts[i].EvaluationAttemptSha256 ?? "", out var a) ||
                !ro.TryGetValue(r.Attempts[i].EvaluationAttemptSha256 ?? "", out var b))
            { reasons.Add("evaluation_fixed_factors_unbound"); continue; }
            if (a.ConfigurationSha256 != b.ConfigurationSha256) reasons.Add("evaluation_configuration_mismatch");
            if (a.CaseSha256 != b.CaseSha256 || a.CaseId != b.CaseId || a.ExpectedDefects != b.ExpectedDefects)
                reasons.Add("case_obligations_mismatch");
        }
        return reasons;
    }

    private static ComparisonSideSummary Side(ComparisonInput input)
    {
        var price = input.Pricing;
        var journal = price.Journal;
        var totals = journal.Totals;
        var outcomes = input.Evidence.Outcomes;
        var eligible = outcomes.Count(EvaluationReportSummary.Eligible);
        var completed = totals.Completed == totals.Scheduled;
        var human = outcomes.Count(o => Origin(input, o) == "human_declared");
        var ai = outcomes.Count(o => Origin(input, o) == "ai");
        var source = journal.Provenance;
        return new(totals, journal.Reservations,
            new(totals.Completed, totals.Scheduled, decimal.Round((decimal)totals.Completed / totals.Scheduled, 6, MidpointRounding.ToEven)),
            Dimension(totals.UsageComplete ? "complete" : "incomplete", totals.UsageComplete ? [] : ["unknown_usage_sends"]),
            Dimension(price.ObservedUsage.TotalComplete ? "complete" : "incomplete",
                price.ObservedUsage.TotalComplete ? [] : ["observed_pricing_incomplete"]),
            Dimension(completed ? "complete" : "incomplete", completed ? [] : ["scheduled_work_incomplete"]),
            Dimension(eligible == 0 ? "inconclusive" : outcomes.Length < totals.Attempted ? "incomplete" : "eligible",
                eligible == 0 ? ["no_quality_eligible_outcomes"] : outcomes.Length < totals.Attempted ? ["evaluation_outcomes_missing"] : []),
            outcomes.Length, eligible, outcomes.Count(o => o.ScenarioStatus == AssertionStatus.Passed), ai, human,
            outcomes.Length - human - ai, "not_evidenced", Effective(input),
            input.Evidence.Histories.Length, input.Evidence.Histories.Sum(h => h.Rows.Count(x => x.Accepted)),
            input.Evidence.Histories.Sum(h => h.Rows.Count(x => !x.Accepted)),
            input.Evidence.Histories.Count(h => h.SourceCommit != source.SourceCommit || h.SourceTree != source.SourceTree ||
                h.SourceClean != source.SourceClean), "native_campaign_link_unproven");
    }

    private static EffectiveReviewCost Effective(ComparisonInput input)
    {
        var definition = input.Declaration.EffectiveReview;
        var price = input.Pricing.ObservedUsage;
        var journal = input.Pricing.Journal;
        if (definition is null) return new("inconclusive", "effective_definition_not_declared", price.Currency,
            price.TotalAmount, 0, 0, journal.Totals.Scheduled, null, null);
        var outcomes = input.Evidence.Outcomes.ToDictionary(o => o.AttemptSha256!, StringComparer.Ordinal);
        var eligible = 0; var unknown = 0;
        foreach (var attempt in journal.Attempts)
        {
            if (attempt.Status != "completed") continue;
            if (!outcomes.TryGetValue(attempt.EvaluationAttemptSha256!, out var outcome)) { unknown++; continue; }
            if (outcome.EvidenceStatus == AssertionStatus.Failed || outcome.ScenarioStatus == AssertionStatus.Failed) continue;
            if (!EvaluationReportSummary.Eligible(outcome) || outcome.ScenarioStatus != AssertionStatus.Passed ||
                Origin(input, outcome) == "unknown") { unknown++; continue; }
            if (Origin(input, outcome) == definition.RequiredOrigin) eligible++;
        }
        var reason = !price.TotalComplete ? "campaign_pricing_incomplete" : unknown > 0 ? "effective_population_unassessed" :
            eligible == 0 ? "effective_population_empty" : eligible < definition.MinimumPopulation ? "effective_population_below_declared_minimum" :
                "conditional_on_declared_definition_and_origin";
        var available = reason == "conditional_on_declared_definition_and_origin";
        return new(available ? "conditional" : "inconclusive", reason, price.Currency, price.TotalAmount,
            eligible, journal.Totals.Scheduled - eligible - unknown, unknown, definition.MinimumPopulation,
            available ? ComparisonNumbers.PerReview(price.TotalAmount!.Value, eligible, input.Pricing.Tariff.Terms.Arithmetic.DecimalPlaces) : null);
    }

    private static string Origin(ComparisonInput input, EvaluationOutcome outcome) =>
        input.Evidence.Annotations.FirstOrDefault(a => a.OutcomeSha256 == ComparisonJson.OutcomeHash(outcome))?.Origin ?? "unknown";

    private static ComparisonDimension Dimension(string status, IEnumerable<string> reasons) =>
        new(status, reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray());
}
