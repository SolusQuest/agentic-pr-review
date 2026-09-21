using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Comparison;

internal static class ComparisonAdmission
{
    internal static bool Valid(ComparisonInput input)
    {
        if (input.Format != ComparisonLimits.InputFormat || input.Pricing is null || input.Evidence is not { } evidence ||
            input.Declaration is not { } declaration || !Selection(declaration.Left) || !Selection(declaration.Right) ||
            declaration.Axis is not ("none" or "source_build" or "cache_policy") ||
            declaration.ControlClaim is not ("not_requested" or "http_stateless" or "new_process" or "new_session" or
                "history_removed" or "user_isolation" or "prompt_salt" or "cache_disabled_asserted") ||
            evidence.Outcomes.IsDefault || evidence.Outcomes.Length > 256 ||
            evidence.Annotations.IsDefault || evidence.Annotations.Length > 256 ||
            evidence.Histories.IsDefault || evidence.Histories.Length > ComparisonLimits.Histories ||
            evidence.Expectations.IsDefault || evidence.Expectations.Length > ComparisonLimits.Expectations)
            return false;
        if (declaration.EffectiveReview is { } definition &&
            (definition.Predicate != "completed_evidence_scenario_adjudicated" ||
                definition.RequiredOrigin is not ("ai" or "human_declared") || definition.MinimumPopulation is < 1 or > 256))
            return false;
        if (declaration.RegressionRule is { } rule &&
            (rule.Metric != "observed_reference_total" || rule.AbsoluteIncreaseThreshold < 0)) return false;
        if (evidence.Expectations.Any(e => e is null || !EvaluationLimits.Hash(e.HistorySha256) ||
            !EvaluationLimits.Hash(e.ObservationSha256) || e.Phase < 0 || e.Phase >= GrowthProfiles.Attempts ||
            e.CallOrdinal is < 1 or > AgentLimits.ModelCalls ||
            e.Expectation is not ("stable_continuity" or "intentional_fault" or "unknown"))) return false;

        var journal = input.Pricing.Journal;
        var source = journal.Provenance;
        var mode = source.ExecutionKind == "live" ? "live" : "deterministic";
        var used = new HashSet<string>(StringComparer.Ordinal);
        var cases = new Dictionary<(string Corpus, string Case), (string Hash, int Defects)>();
        foreach (var outcome in evidence.Outcomes)
        {
            if (outcome is null || outcome.AttemptSha256 is null || !used.Add(outcome.AttemptSha256)) return false;
            var caseKey = (outcome.CorpusSha256, outcome.CaseId);
            var caseDefinition = (outcome.CaseSha256, outcome.ExpectedDefects);
            if (cases.TryGetValue(caseKey, out var previous) && previous != caseDefinition) return false;
            cases[caseKey] = caseDefinition;
            var attempts = journal.Attempts.Where(a => a.EvaluationAttemptSha256 == outcome.AttemptSha256).ToArray();
            if (attempts.Length != 1) return false;
            var attempt = attempts[0];
            var status = outcome.ExecutionStatus.ToString().ToLowerInvariant();
            if (attempt.Status != status || outcome.CaseId != attempt.CaseId || outcome.CorpusSha256 != source.CorpusSha256 ||
                outcome.SourceCommit != source.SourceCommit || outcome.SourceTree != source.SourceTree ||
                outcome.SourceClean != source.SourceClean || outcome.Mode != mode || outcome.ConfigurationSha256 is null)
                return false;
            var descriptor = new EvaluationRunInput(attempt.AttemptId, mode, source.SourceCommit, source.SourceTree,
                source.SourceClean, source.ProviderConfigurationSha256);
            var expected = EvaluationAttempt.Hash("attempt", outcome.ConfigurationSha256,
                AgentCanonical.HashRaw(EvaluationJson.Write(descriptor)));
            if (outcome.AttemptSha256 != expected) return false;
        }
        var origins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var origin in evidence.Annotations)
        {
            if (origin is null || !EvaluationLimits.Hash(origin.OutcomeSha256) ||
                !EvaluationLimits.Hash(origin.ExecutionSha256) || !origins.Add(origin.OutcomeSha256) ||
                origin.Origin is not ("ai" or "human_declared" or "unknown")) return false;
            if (!evidence.Outcomes.Any(o => ComparisonJson.OutcomeHash(o) == origin.OutcomeSha256 &&
                o.ExecutionSha256 == origin.ExecutionSha256)) return false;
        }
        return ExecutionsConsistent(evidence.Outcomes);
    }

    // A producer execution digest includes its attempt digest. Repeated presentation of
    // the same attribution is allowed, but one execution cannot belong to two attempts.
    internal static bool ExecutionsConsistent(IEnumerable<EvaluationOutcome> outcomes)
    {
        var executions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var outcome in outcomes.Where(o => o.ExecutionSha256 is not null))
        {
            var execution = outcome.ExecutionSha256!;
            if (executions.TryGetValue(execution, out var previous) && previous != outcome.AttemptSha256) return false;
            executions[execution] = outcome.AttemptSha256!;
        }
        return true;
    }

    private static bool Selection(ComparisonSelection? value) => value is not null &&
        EvaluationLimits.Hash(value.SourceCommit, 40) && EvaluationLimits.Hash(value.SourceTree, 40) &&
        EvaluationLimits.Id(value.BuildId) && EvaluationLimits.Hash(value.PlanSha256) &&
        EvaluationLimits.Hash(value.JournalSha256) && EvaluationLimits.Hash(value.TariffSha256) &&
        EvaluationLimits.Hash(value.EvidenceSha256);
}
