using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Accounting;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Comparison;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Contracts;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Histories;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Prefix;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;
using AgenticPrReview.Runtime.Tests.Host.Action;
using EvaluatorProgram = AgenticPrReview.Runtime.ReviewEvaluationFixture.Program;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class R6ComparisonTests
{
    private const string Canary = "APR277_PRIVATE_CANARY";
    private static string Hash(char c) => new(c, 64);
    private sealed record Sample(PricingReportDocument Pricing, ComparisonEvidence Evidence);

    [Fact]
    public async Task ActualCommandComparesObservedReferenceCostsWithoutClaimingRegressionOrQuality()
    {
        var pair = Pair(Make("left"), Make("right", output: 6), rule: new("observed_reference_total", 0, true));
        var command = await Command(pair);
        Assert.Equal(0, command.Exit); Assert.Empty(command.Error);
        var report = Assert.IsType<ComparisonReportDocument>(ComparisonJson.Read(Encoding.UTF8.GetBytes(command.Output)));
        Assert.Equal("comparable", report.Result.DescriptiveComparison.Status);
        Assert.Equal(0.10m, report.Result.ObservedReferenceTotalDifference);
        Assert.Equal("increased", report.Result.DescriptiveDirection);
        Assert.Equal("inconclusive", report.Result.FormalRegression);
        Assert.Equal("unverified_no_producer_receipt", report.Result.RulePredeclaration);
        Assert.Equal("no_promotion_approval", report.Result.PromotionDisposition);
        Assert.Equal("native_campaign_link_unproven", report.Result.Left.CampaignLifecycleAssociation);
        Assert.Equal("inconclusive", report.Result.HistoricalR5Quality);
        Assert.Equal("not_evidenced", report.Result.Left.IndependentHumanConfirmation);
        Assert.Equal("not_evaluated", report.Result.R7Readiness);
        Assert.Equal(0.42m, report.Left.Pricing.ObservedUsage.TotalAmount);
        Assert.Equal(0.60m, report.Left.Pricing.SameTokenAllMiss.TotalAmount);
        var markdown = await Command(pair, "markdown", reverseOptions: true);
        Assert.Equal(0, markdown.Exit); Assert.Contains("Same-token all-miss hypothetical", markdown.Output);
        Assert.Contains("Historical R5 quality: inconclusive", markdown.Output);
        Assert.DoesNotContain(Canary, markdown.Output + markdown.Error);
    }

    [Theory]
    [InlineData("none", "not_comparable")]
    [InlineData("source_build", "comparable")]
    public void DeclaredSourceAndBuildAxisAllowsSourceDependentPlanDigests(string axis, string status)
    {
        var pair = Pair(Make("left"), Make("right", source: 'f', build: "different-build"), axis);
        var report = Report(pair);
        Assert.NotEqual(pair.Left.Pricing.Journal.Provenance.PlanSha256, pair.Right.Pricing.Journal.Provenance.PlanSha256);
        Assert.Equal(status, report.Result.DescriptiveComparison.Status);
    }

    [Theory]
    [InlineData("configuration", "evaluation_configuration_mismatch")]
    [InlineData("model", "response_model_mismatch")]
    [InlineData("tariff", "tariff_mismatch")]
    [InlineData("currency", "tariff_mismatch")]
    [InlineData("population", "scheduled_population_mismatch")]
    [InlineData("corpus", "corpus_mismatch")]
    [InlineData("case", "case_obligations_mismatch")]
    [InlineData("bounds", "bounds_mismatch")]
    public void ActualAdmittedFixedFactorDifferencesCannotBecomeComparable(string change, string reason)
    {
        var right = Make("right", scheduled: change == "population" ? 2 : 1,
            completed: change == "population" ? 2 : 1, config: change == "configuration" ? 'd' : 'e',
            model: change == "model" ? "deepseek-flash" : DeepSeekAdapterContext.Model,
            missRate: change == "tariff" ? 9 : 4, currency: change == "currency" ? "CNY" : "USD",
            corpus: change == "corpus" ? '9' : 'c', caseHash: change == "case" ? '8' : 'd',
            maxSeconds: change == "bounds" ? 121 : 120);
        var report = Report(Pair(Make("left"), right, "source_build"));
        Assert.Equal("not_comparable", report.Result.DescriptiveComparison.Status);
        Assert.Contains(reason, report.Result.DescriptiveComparison.Reasons);
        Assert.Null(report.Result.ObservedReferenceTotalDifference);
    }

    [Theory]
    [InlineData("http_stateless")]
    [InlineData("new_process")]
    [InlineData("new_session")]
    [InlineData("history_removed")]
    [InlineData("user_isolation")]
    [InlineData("prompt_salt")]
    [InlineData("cache_disabled_asserted")]
    public void CacheDisableProxiesCannotEstablishControlledComparison(string claim)
    {
        var report = Report(Pair(Make("left"), Make("right"), "cache_policy", control: claim));
        Assert.Equal("not_comparable", report.Result.DescriptiveComparison.Status);
        Assert.Contains("cache_control_unsupported", report.Result.ExperimentalControl.Reasons);
        Assert.Equal(0.42m, report.Left.Pricing.ObservedUsage.TotalAmount);
    }

    [Fact]
    public void MissingFailedOutcomesCannotShrinkCampaignSpendOrCompletionDenominator()
    {
        var left = Make("left", scheduled: 3, completed: 1, failed: 1);
        left = left with { Evidence = left.Evidence with { Outcomes = [left.Evidence.Outcomes[0]] } };
        var report = Report(Pair(left, Make("right", scheduled: 3, completed: 1, failed: 1)));
        Assert.Equal(3, report.Result.Left.CompletionRate.Scheduled);
        Assert.Equal(1, report.Result.Left.Campaign.Completed);
        Assert.Equal(1, report.Result.Left.Campaign.Failed);
        Assert.Equal(1, report.Result.Left.Campaign.Unattempted);
        Assert.Equal(0.333333m, report.Result.Left.CompletionRate.Value);
        Assert.Equal(0.84m, report.Left.Pricing.ObservedUsage.TotalAmount);
        Assert.Equal("incomplete", report.Result.Left.Execution.Status);
        Assert.NotEqual("comparable", report.Result.DescriptiveComparison.Status);
    }

    [Fact]
    public void CompleteZeroTrafficDoesNotMeanWorkCompletedOrZeroEffectiveReviewCost()
    {
        var pair = Pair(Make("left", completed: 0), Make("right", completed: 0), definition: Definition());
        var result = Report(pair);
        Assert.Equal(0m, result.Left.Pricing.ObservedUsage.TotalAmount);
        Assert.Equal("complete", result.Result.Left.Pricing.Status);
        Assert.Equal("incomplete", result.Result.Left.Execution.Status);
        Assert.Equal("effective_population_empty", result.Result.Left.EffectiveReviewCost.Reason);
        Assert.Null(result.Result.Left.EffectiveReviewCost.Value);
    }

    [Fact]
    public void InvalidLocalRefusedAndUnattemptedWorkRemainInTheFullInventory()
    {
        var left = Make("left", scheduled: 4, maxCalls: 1);
        var journal = left.Pricing.Journal;
        var collector = new UsageJournalCollector(new(journal.Provenance, journal.Plan));
        var first = collector.BeginAttempt(0); first.AgentStarted();
        var sent = first.BeginCall()!; sent.Dispatch(); sent.TransportFinished(DeepSeekTransportResult.Success([]));
        sent.Returned(new(10, 4, new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Model, 6, 4)));
        first.AdmitEvaluation(left.Evidence.Outcomes[0].AttemptSha256); first.AgentFinished(true); first.Finish("completed");
        collector.BeginAttempt(1).Finish("invalid");
        var refused = collector.BeginAttempt(2); refused.AgentStarted();
        var local = refused.BeginCall()!; local.Refuse("budget_refused"); local.Threw();
        refused.AgentFinished(false); refused.Finish("failed");
        var tariff = AdmittedTariff.Admit(left.Pricing.Tariff, out _)!;
        left = left with { Pricing = PricingReport.Create(collector.Seal("bound_stop", journal.Reservations), tariff).Document };
        var report = Report(Pair(Annotate(left, "ai"), Make("right"), definition: Definition("ai")));
        var summary = report.Result.Left;
        Assert.Equal(4, summary.Campaign.Scheduled); Assert.Equal(1, summary.Campaign.Completed);
        Assert.Equal(1, summary.Campaign.Invalid); Assert.Equal(1, summary.Campaign.Failed);
        Assert.Equal(1, summary.Campaign.Unattempted); Assert.Equal(1, summary.Campaign.LocalRefusals);
        Assert.Equal(1, summary.Campaign.ActualSends); Assert.Equal(0.25m, summary.CompletionRate.Value);
        Assert.Equal(0.42m, summary.EffectiveReviewCost.FullCampaignAmount); Assert.Equal(3, summary.EffectiveReviewCost.Excluded);
        Assert.NotEqual("comparable", report.Result.DescriptiveComparison.Status);
    }

    [Fact]
    public void MissingCompletedOutcomeLeavesFixedFactorsAndEffectivePopulationUnassessed()
    {
        var left = Annotate(Make("left", scheduled: 2, completed: 2), "human_declared");
        left = left with { Evidence = left.Evidence with { Outcomes = [left.Evidence.Outcomes[0]], Annotations = [left.Evidence.Annotations[0]] } };
        var report = Report(Pair(left, Make("right", scheduled: 2, completed: 2), definition: Definition()));
        Assert.Equal("complete", report.Result.Left.Execution.Status);
        Assert.Equal("incomplete", report.Result.Left.QualityEligibility.Status);
        Assert.Contains("evaluation_fixed_factors_unbound", report.Result.DescriptiveComparison.Reasons);
        Assert.Equal(0.84m, report.Result.Left.EffectiveReviewCost.FullCampaignAmount);
        Assert.Equal(1, report.Result.Left.EffectiveReviewCost.Unassessed); Assert.Null(report.Result.Left.EffectiveReviewCost.Value);
    }

    [Fact]
    public void UnknownUsageAndAbsentCachePartitionRemainIndependentFromExecution()
    {
        var unknown = Report(Pair(Make("left", completed: 0, failed: 1, unknown: true), Make("right")));
        Assert.Equal("incomplete", unknown.Result.Left.Usage.Status);
        Assert.Null(unknown.Left.Pricing.ObservedUsage.TotalAmount);
        var missing = Report(Pair(Make("left", missingCache: true), Make("right")));
        Assert.Equal("complete", missing.Result.Left.Usage.Status);
        Assert.Equal("complete", missing.Result.Left.Execution.Status);
        Assert.Equal("incomplete", missing.Result.Left.Pricing.Status);
        Assert.NotNull(missing.Left.Pricing.SameTokenAllMiss.TotalAmount);
        Assert.Null(missing.Result.ObservedReferenceTotalDifference);
    }

    [Fact]
    public void QualityScenarioFailureDoesNotDisableBoundDescriptiveComparison()
    {
        var left = Make("left");
        var outcome = left.Evidence.Outcomes[0] with { ExpectedDefects = 1, StructurallyMissingDefects = 1,
            ScenarioStatus = AssertionStatus.Failed, Code = EvaluationCode.ExpectedFindingMissing };
        left = left with { Evidence = left.Evidence with { Outcomes = [outcome] } };
        var right = Make("right");
        right = right with { Evidence = right.Evidence with { Outcomes = [right.Evidence.Outcomes[0] with
            { ExpectedDefects = 1, StructurallyMissingDefects = 1, ScenarioStatus = AssertionStatus.Failed, Code = EvaluationCode.ExpectedFindingMissing }] } };
        var report = Report(Pair(left, right, definition: Definition()));
        Assert.Equal("comparable", report.Result.DescriptiveComparison.Status);
        Assert.Equal(1, report.Result.Left.QualityEligibleOutcomes);
        Assert.Equal(0, report.Result.Left.EffectiveReviewCost.Eligible);
        Assert.Null(report.Result.Left.EffectiveReviewCost.Value);
    }

    [Theory]
    [InlineData("ai", "human_declared", 1, "effective_population_empty")]
    [InlineData("human_declared", "human_declared", 2, "effective_population_below_declared_minimum")]
    [InlineData("unknown", "human_declared", 1, "effective_population_unassessed")]
    [InlineData("human_declared", "human_declared", 1, "conditional_on_declared_definition_and_origin")]
    [InlineData("ai", "ai", 1, "conditional_on_declared_definition_and_origin")]
    public void ExplicitOriginAndMinimumPopulationControlOnlyConditionalEffectiveness(string origin, string required, int minimum, string reason)
    {
        var left = Annotate(Make("left", scheduled: 3, completed: 1, failed: 1), origin);
        var pair = Pair(left, Make("right"), definition: Definition(required, minimum));
        var report = Report(pair);
        Assert.Equal(reason, report.Result.Left.EffectiveReviewCost.Reason);
        Assert.Equal(0.84m, report.Result.Left.EffectiveReviewCost.FullCampaignAmount);
        Assert.Equal(reason.StartsWith("conditional", StringComparison.Ordinal) ? 0.84m : null, report.Result.Left.EffectiveReviewCost.Value);
        Assert.Equal("not_evidenced", report.Result.Left.IndependentHumanConfirmation);
        Assert.Equal("inconclusive", report.Result.HistoricalR5Quality);
    }

    [Fact]
    public void AdjudicatedZeroFindingsWithoutOriginDoesNotInventHumanConfirmation()
    {
        var report = Report(Pair(Make("left"), Make("right"), definition: Definition()));
        Assert.Equal(1, report.Result.Left.QualityEligibleOutcomes);
        Assert.Equal(0, report.Result.Left.HumanDeclaredAnnotations);
        Assert.Equal(1, report.Result.Left.UnknownAnnotationOrigins);
        Assert.Equal("effective_population_unassessed", report.Result.Left.EffectiveReviewCost.Reason);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("attempt")]
    [InlineData("configuration")]
    [InlineData("source")]
    [InlineData("case")]
    [InlineData("mode")]
    [InlineData("origin_execution")]
    [InlineData("origin_outcome")]
    [InlineData("origin_duplicate")]
    public void RejectsStaleOrUnmatchedOutcomeAndOriginAttachments(string fault)
    {
        var left = Annotate(Make("left"), "ai");
        var outcome = left.Evidence.Outcomes[0];
        outcome = fault switch
        {
            "attempt" => outcome with { AttemptSha256 = Hash('9') },
            "configuration" => outcome with { ConfigurationSha256 = Hash('9') },
            "source" => outcome with { SourceCommit = new string('9', 40) },
            "case" => outcome with { CaseId = "other" },
            "mode" => outcome with { Mode = "live" },
            _ => outcome,
        };
        var origin = left.Evidence.Annotations[0];
        origin = fault switch
        {
            "origin_execution" => origin with { ExecutionSha256 = Hash('9') },
            "origin_outcome" => origin with { OutcomeSha256 = Hash('9') },
            _ => origin,
        };
        left = left with { Evidence = left.Evidence with { Outcomes = fault == "duplicate" ? [outcome, outcome] : [outcome],
            Annotations = fault == "origin_duplicate" ? [origin, origin] : [origin] } };
        Assert.Null(ComparisonJson.ReadInput(ComparisonJson.WriteInput(Pair(left, Make("right")).Left)));
    }

    [Theory]
    [InlineData("hash", "left", true)]
    [InlineData("hash", "right", true)]
    [InlineData("hash", "both", true)]
    [InlineData("defects", "left", true)]
    [InlineData("defects", "right", true)]
    [InlineData("defects", "both", true)]
    [InlineData("hash", "left", false)]
    [InlineData("hash", "right", false)]
    [InlineData("hash", "both", false)]
    [InlineData("defects", "left", false)]
    [InlineData("defects", "right", false)]
    [InlineData("defects", "both", false)]
    public async Task ConflictingRepeatedCaseDefinitionsRejectBeforePairwiseComparison(string fault, string side, bool completed)
    {
        Sample Change(Sample sample)
        {
            var outcome = sample.Evidence.Outcomes[1];
            outcome = fault == "hash" ? outcome with { CaseSha256 = Hash('9') } : outcome with { ExpectedDefects = 1 };
            if (fault == "defects" && completed)
                outcome = outcome with { StructurallyMissingDefects = 1, ScenarioStatus = AssertionStatus.Failed,
                    Code = EvaluationCode.ExpectedFindingMissing };
            Assert.NotNull(EvaluationJson.ReadOutcome(EvaluationJson.Write(outcome)));
            return sample with { Evidence = sample.Evidence with { Outcomes = [sample.Evidence.Outcomes[0], outcome] } };
        }
        var left = Make("left", scheduled: 2, completed: completed ? 2 : 1, failed: completed ? 0 : 1);
        var right = Make("right", scheduled: 2, completed: completed ? 2 : 1, failed: completed ? 0 : 1);
        var baseline = Report(Pair(left, right));
        if (side is "left" or "both") left = Change(left);
        if (side is "right" or "both") right = Change(right);
        var pair = Pair(left, right);
        var rejected = side == "right" ? pair.Right : pair.Left;
        Assert.Null(ComparisonJson.ReadInput(ComparisonJson.WriteInput(rejected)));
        var command = await Command(pair);
        Assert.Equal(2, command.Exit); Assert.Empty(command.Output);
        Assert.Contains(side == "right" ? "right_invalid" : "left_invalid", command.Error);
        AssertReportRejectsPair(baseline, pair);
    }

    [Fact]
    public async Task DifferentInternallyConsistentCaseDefinitionsRemainValidButNotComparable()
    {
        var command = await Command(Pair(Make("left", scheduled: 2, completed: 2),
            Make("right", scheduled: 2, completed: 2, caseHash: '9')));
        Assert.Equal(0, command.Exit);
        var report = Assert.IsType<ComparisonReportDocument>(ComparisonJson.Read(Encoding.UTF8.GetBytes(command.Output)));
        Assert.Equal("not_comparable", report.Result.DescriptiveComparison.Status);
        Assert.Contains("case_obligations_mismatch", report.Result.DescriptiveComparison.Reasons);
        Assert.Equal(0.84m, report.Left.Pricing.ObservedUsage.TotalAmount);
    }

    [Theory]
    [InlineData("left", true)]
    [InlineData("right", true)]
    [InlineData("both", true)]
    [InlineData("left", false)]
    [InlineData("right", false)]
    [InlineData("both", false)]
    public async Task ReusedExecutionCannotCreditDistinctAttemptsEvenWithExactOrigins(string side, bool definition)
    {
        static Sample Duplicate(Sample sample) => sample with { Evidence = sample.Evidence with { Outcomes =
            [sample.Evidence.Outcomes[0], sample.Evidence.Outcomes[1] with { ExecutionSha256 = sample.Evidence.Outcomes[0].ExecutionSha256 }] } };
        var left = Make("left", scheduled: 2, completed: 2); var right = Make("right", scheduled: 2, completed: 2);
        var criterion = definition ? Definition(minimum: 2) : null;
        var baseline = Report(Pair(Annotate(left, "human_declared"), Annotate(right, "human_declared"), definition: criterion));
        if (side is "left" or "both") left = Duplicate(left);
        if (side is "right" or "both") right = Duplicate(right);
        var pair = Pair(Annotate(left, "human_declared"), Annotate(right, "human_declared"), definition: criterion);
        var rejected = side == "right" ? pair.Right : pair.Left;
        Assert.NotEqual(rejected.Evidence.Outcomes[0].AttemptSha256, rejected.Evidence.Outcomes[1].AttemptSha256);
        Assert.All(rejected.Evidence.Outcomes, o => Assert.NotNull(EvaluationJson.ReadOutcome(EvaluationJson.Write(o))));
        Assert.Null(ComparisonJson.ReadInput(ComparisonJson.WriteInput(rejected)));
        var command = await Command(pair);
        Assert.Equal(2, command.Exit); Assert.Empty(command.Output);
        Assert.Contains(side == "right" ? "right_invalid" : "left_invalid", command.Error);
        AssertReportRejectsPair(baseline, pair);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutionAttributionContradictionsCannotBeMovedAcrossSides(bool differentSource)
    {
        var left = Annotate(Make("left"), "human_declared");
        var right = Make("right", source: differentSource ? 'f' : 'a');
        var baseline = Report(Pair(left, Annotate(right, "human_declared"), "source_build", Definition()));
        right = right with { Evidence = right.Evidence with { Outcomes =
            [right.Evidence.Outcomes[0] with { ExecutionSha256 = left.Evidence.Outcomes[0].ExecutionSha256 }] } };
        var pair = Pair(left, Annotate(right, "human_declared"), "source_build", Definition());
        Assert.NotNull(ComparisonJson.ReadInput(ComparisonJson.WriteInput(pair.Left)));
        Assert.NotNull(ComparisonJson.ReadInput(ComparisonJson.WriteInput(pair.Right)));
        var command = await Command(pair);
        Assert.Equal(2, command.Exit); Assert.Empty(command.Output); Assert.Contains("execution_conflict", command.Error);
        AssertReportRejectsPair(baseline, pair);
    }

    [Fact]
    public async Task ExactSelfComparisonAndDistinctExecutionsOfOneAttemptRemainAdmissible()
    {
        var sample = Annotate(Make("same"), "human_declared");
        var self = await Command(Pair(sample, sample, definition: Definition()));
        Assert.Equal(0, self.Exit);
        var other = sample with { Evidence = sample.Evidence with { Outcomes =
            [sample.Evidence.Outcomes[0] with { ExecutionSha256 = Hash('9') }], Annotations = [] } };
        var distinct = await Command(Pair(sample, Annotate(other, "human_declared"), definition: Definition()));
        Assert.Equal(0, distinct.Exit);
        foreach (var output in new[] { self.Output, distinct.Output })
        {
            var report = Assert.IsType<ComparisonReportDocument>(ComparisonJson.Read(Encoding.UTF8.GetBytes(output)));
            Assert.Equal("comparable", report.Result.DescriptiveComparison.Status);
            Assert.Equal(0m, report.Result.ObservedReferenceTotalDifference);
            Assert.Equal(1, report.Result.Left.EffectiveReviewCost.Eligible);
        }
    }

    [Theory]
    [InlineData("caller_cancelled")]
    [InlineData("deadline")]
    public async Task LateStopAfterFinalizedWorkPreservesCompleteExecutionAndDescriptiveComparison(string stop)
    {
        var pair = Pair(Make("left", scheduled: 2, completed: 2, stopReason: stop),
            Make("right", scheduled: 2, completed: 2, output: 6));
        var command = await Command(pair);
        Assert.Equal(0, command.Exit);
        var report = Assert.IsType<ComparisonReportDocument>(ComparisonJson.Read(Encoding.UTF8.GetBytes(command.Output)));
        Assert.Equal(stop, report.Left.Pricing.Journal.StopReason);
        Assert.Equal(2, report.Result.Left.Campaign.Completed); Assert.Equal(1m, report.Result.Left.CompletionRate.Value);
        Assert.Equal("complete", report.Result.Left.Execution.Status); Assert.Empty(report.Result.Left.Execution.Reasons);
        Assert.Equal("complete", report.Result.Left.Usage.Status); Assert.Equal("complete", report.Result.Left.Pricing.Status);
        Assert.Equal("comparable", report.Result.DescriptiveComparison.Status); Assert.Equal(0.20m, report.Result.ObservedReferenceTotalDifference);
        var markdown = await Command(pair, "markdown");
        Assert.Equal(0, markdown.Exit); Assert.Contains($"| Campaign stop cause | {stop} | complete |", markdown.Output);
    }

    [Theory]
    [InlineData("caller_cancelled", false)]
    [InlineData("deadline", false)]
    [InlineData("caller_cancelled", true)]
    [InlineData("deadline", true)]
    public async Task StopWithUnfinishedOrUnknownWorkCannotBecomeComplete(string stop, bool failed)
    {
        var command = await Command(Pair(Make("left", scheduled: 2, completed: failed ? 0 : 1,
            failed: failed ? 1 : 0, unknown: failed, stopReason: stop), Make("right", scheduled: 2, completed: 2)));
        Assert.Equal(0, command.Exit);
        var report = Assert.IsType<ComparisonReportDocument>(ComparisonJson.Read(Encoding.UTF8.GetBytes(command.Output)));
        Assert.Equal(stop, report.Left.Pricing.Journal.StopReason);
        Assert.Equal(1, report.Result.Left.Campaign.Unattempted); Assert.Equal(failed ? 1 : 0, report.Result.Left.Campaign.Failed);
        Assert.Equal("incomplete", report.Result.Left.Execution.Status);
        Assert.NotEqual("comparable", report.Result.DescriptiveComparison.Status); Assert.Null(report.Result.ObservedReferenceTotalDifference);
        Assert.Equal(failed ? "incomplete" : "complete", report.Result.Left.Usage.Status);
    }

    private static void AssertReportRejectsPair(ComparisonReportDocument baseline, (ComparisonInput Left, ComparisonInput Right) pair)
    {
        var raw = JsonNode.Parse(ComparisonJson.Write(baseline))!;
        raw["left"] = JsonNode.Parse(ComparisonJson.WriteInput(pair.Left));
        raw["right"] = JsonNode.Parse(ComparisonJson.WriteInput(pair.Right));
        Assert.Null(ComparisonJson.Read(Encoding.UTF8.GetBytes(raw.ToJsonString())));
    }

    [Theory]
    [InlineData("artifact")]
    [InlineData("phase")]
    [InlineData("call")]
    [InlineData("observation")]
    public async Task UnavailableOptionalExpectationPreservesCommandReportAndPopulation(string fault)
    {
        var left = Histories(Make("left", scheduled: 3, completed: 1, failed: 1), [History(unstable: true)], "stable_continuity");
        var expected = left.Evidence.Expectations[0];
        expected = fault switch
        {
            "artifact" => expected with { HistorySha256 = Hash('9') },
            "phase" => expected with { Phase = 1 },
            "call" => expected with { CallOrdinal = 2 },
            _ => expected with { ObservationSha256 = Hash('9') },
        };
        left = left with { Evidence = left.Evidence with { Expectations = [expected] } };
        var command = await Command(Pair(left, Make("right")));
        Assert.Equal(0, command.Exit);
        var report = Assert.IsType<ComparisonReportDocument>(ComparisonJson.Read(Encoding.UTF8.GetBytes(command.Output)));
        Assert.Equal(3, report.Result.Left.Campaign.Scheduled);
        Assert.Equal(0.84m, report.Left.Pricing.ObservedUsage.TotalAmount);
        Assert.Equal(1, report.Result.PrefixEvidence.UnboundExpectations);
        var source = Assert.Single(report.Result.PrefixEvidence.Sources);
        Assert.Equal(1, source.UnknownExpectedness); Assert.Equal(1, source.ObservedInstability);
        Assert.Equal(0, source.UnexpectedDrift); Assert.Equal(0, source.StableUnderDeclaredExpectation);
    }

    [Theory]
    [InlineData("intentional_fault", 0)]
    [InlineData("unknown", 0)]
    [InlineData("stable_continuity", 2)]
    public void ExpectednessSeparatesObservedInstabilityFromRepeatedUnexpectedDrift(string expectedness, int unexpected)
    {
        var left = Histories(Make("left"), [History('1', true), History('2', true)], expectedness);
        var report = Report(Pair(left, Make("right")));
        var source = Assert.Single(report.Result.PrefixEvidence.Sources);
        Assert.Equal(2, source.ObservedInstability); Assert.Equal(unexpected, source.UnexpectedDrift);
        Assert.Equal(unexpected == 2 ? "blocked_unexpected_prefix_drift" : "no_promotion_approval", report.Result.PromotionDisposition);
        Assert.Equal("comparable", report.Result.DescriptiveComparison.Status);
    }

    [Fact]
    public void EventConsistencyAndRepetitionApplyAcrossBothSidesAndBySource()
    {
        var history = History('1', true);
        var left = Histories(Make("left"), [history, history], "stable_continuity");
        var same = Histories(Make("right"), [history], "stable_continuity");
        Assert.Equal(1, Assert.Single(Report(Pair(left, same)).Result.PrefixEvidence.Sources).UnexpectedDrift);
        var distinct = Histories(Make("right"), [History('2', true)], "stable_continuity");
        Assert.Equal("blocked_unexpected_prefix_drift", Report(Pair(left, distinct)).Result.PromotionDisposition);
        var otherSource = Histories(Make("right", source: 'f'), [History('2', true, source: 'f')], "stable_continuity");
        var grouped = Report(Pair(left, otherSource, "source_build"));
        Assert.Equal(2, grouped.Result.PrefixEvidence.Sources.Length);
        Assert.All(grouped.Result.PrefixEvidence.Sources, s => Assert.Equal(1, s.UnexpectedDrift));
        Assert.Equal("no_promotion_approval", grouped.Result.PromotionDisposition);
        var conflict = Histories(Make("right"), [history], "intentional_fault");
        Assert.Equal("r6_comparison_expectation_conflict", Assert.Throws<ComparisonInputException>(() => Report(Pair(left, conflict))).Code);
        var changed = history with { Rows = [history.Rows[0] with { WireMatch = false }] };
        Assert.Equal("r6_comparison_history_contradiction", Assert.Throws<ComparisonInputException>(() =>
            Report(Pair(left, Histories(Make("right"), [changed], "stable_continuity")))).Code);
    }

    [Fact]
    public void DynamicChangesCacheMissesAndUnmatchedHistoriesDoNotProveUnexpectedDrift()
    {
        var stable = Histories(Make("left", hit: 0), [History()], "stable_continuity");
        var report = Report(Pair(stable, Make("right", hit: 0)));
        Assert.Equal(1, Assert.Single(report.Result.PrefixEvidence.Sources).StableUnderDeclaredExpectation);
        Assert.Equal(0, Assert.Single(report.Result.PrefixEvidence.Sources).ObservedInstability);
        var unmatched = Histories(Make("left"), [History('1', true, 'f'), History('2', true, 'f')], "stable_continuity");
        var different = Report(Pair(unmatched, Make("right")));
        Assert.Equal(2, different.Result.Left.UnmatchedHistorySources);
        Assert.Equal(0, Assert.Single(different.Result.PrefixEvidence.Sources).UnexpectedDrift);
        Assert.Equal("native_campaign_link_unproven", different.Result.Left.CampaignLifecycleAssociation);
    }

    [Fact]
    public async Task ActualChangedContinuationNegativeControlRetainsIntentionalInstability()
    {
        var history = await HistoryRunner.ReplayAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "agent", "r5", "replay"),
            ReplayFault.ChangedContinuation);
        Assert.Equal("session_failed", history.Code);
        Assert.Equal(new PrefixComparison("compared", false, false), history.Rows[1].Comparison);
        var left = Histories(Make("left"), [history], "intentional_fault");
        var report = Report(Pair(left, Make("right")));
        Assert.True(report.Result.PrefixEvidence.Sources.Sum(s => s.ObservedInstability) > 0);
        Assert.True(report.Result.PrefixEvidence.Sources.Sum(s => s.IntentionalFaults) > 0);
        Assert.All(report.Result.PrefixEvidence.Sources, s => Assert.Equal(0, s.UnexpectedDrift));
    }

    [Fact]
    public void LaterCallsWithoutIndividualWireCoverageCannotMultiplyUnexpectedDrift()
    {
        var history = History('1', true);
        var row = history.Rows[0];
        history = history with { Rows = [row with { Capture = row.Capture with
            { Calls = [row.Capture.Calls[0], row.Capture.Calls[0]] } }] };
        var report = Report(Pair(Histories(Make("left"), [history], "stable_continuity"), Make("right")));
        var source = Assert.Single(report.Result.PrefixEvidence.Sources);
        Assert.Equal(2, source.Observations); Assert.Equal(2, source.ObservedInstability);
        Assert.Equal(1, source.UnverifiedObservations); Assert.Equal(1, source.UnexpectedDrift);
        Assert.Equal("no_promotion_approval", report.Result.PromotionDisposition);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void RestorationAndWireEvidenceAreBothRequiredForDriftCredit(bool restored, bool wire)
    {
        var history = History('1', true);
        history = history with { Rows = [history.Rows[0] with { RestoredMatch = restored, WireMatch = wire }] };
        var report = Report(Pair(Histories(Make("left"), [history], "stable_continuity"), Make("right")));
        var source = Assert.Single(report.Result.PrefixEvidence.Sources);
        Assert.Equal(1, source.ObservedInstability); Assert.Equal(1, source.UnverifiedObservations);
        Assert.Equal(0, source.UnexpectedDrift);
    }

    [Fact]
    public void UnboundContextCannotOverrideAnotherExactlyBoundPresentation()
    {
        var history = History();
        var left = Histories(Make("left"), [history], "stable_continuity");
        var right = Histories(Make("right"), [history], "intentional_fault");
        right = right with { Evidence = right.Evidence with { Expectations =
            [right.Evidence.Expectations[0] with { ObservationSha256 = Hash('9') }] } };
        var report = Report(Pair(left, right));
        Assert.Equal(1, report.Result.PrefixEvidence.UnboundExpectations);
        Assert.Equal(1, Assert.Single(report.Result.PrefixEvidence.Sources).StableUnderDeclaredExpectation);
        Assert.Equal(0, Assert.Single(report.Result.PrefixEvidence.Sources).IntentionalFaults);
    }

    [Fact]
    public void IncomparablePrefixDomainHasNoStabilityOrDriftCredit()
    {
        var history = History();
        var row = history.Rows[0];
        var call = row.Capture.Calls[0]!;
        call = call with { Domain = call.Domain with { StablePlanSha256 = Hash('9') } };
        history = history with { Rows = [row with { Capture = row.Capture with { Calls = [call] },
            Comparison = row.Capture.Baseline!.Compare(call) }] };
        var report = Report(Pair(Histories(Make("left"), [history], "stable_continuity"), Make("right")));
        var source = Assert.Single(report.Result.PrefixEvidence.Sources);
        Assert.Equal(1, source.Incomparable); Assert.Equal(0, source.StableUnderDeclaredExpectation);
        Assert.Equal(0, source.UnexpectedDrift);
    }

    [Theory]
    [InlineData("unknown_field")]
    [InlineData("missing_field")]
    [InlineData("null_evidence")]
    [InlineData("numeric_enum")]
    [InlineData("tampered_price")]
    [InlineData("provider_configuration")]
    [InlineData("malformed_expectation")]
    public async Task MalformedInputsRejectWithoutLeakingData(string fault)
    {
        var pair = Pair(Make("left"), Make("right"));
        var raw = JsonNode.Parse(ComparisonJson.WriteInput(pair.Left))!;
        switch (fault)
        {
            case "unknown_field": raw[Canary] = Canary; break;
            case "missing_field": raw.AsObject().Remove("evidence"); break;
            case "null_evidence": raw["evidence"] = null; break;
            case "numeric_enum": raw["evidence"]!["outcomes"]![0]!["execution_status"] = 0; break;
            case "tampered_price": raw["pricing"]!["observed_usage"]!["total_amount"] = 123; break;
            case "provider_configuration": raw["pricing"]!["journal"]!["plan"]!["provider"]!["configuration_sha256"] = Hash('9'); break;
            case "malformed_expectation": raw["evidence"]!["expectations"] = new JsonArray(new JsonObject
                { ["history_sha256"] = Hash('a'), ["phase"] = -1, ["call_ordinal"] = 1,
                    ["observation_sha256"] = Hash('b'), ["expectation"] = "stable_continuity" }); break;
        }
        var command = await Command(pair, rawLeft: Encoding.UTF8.GetBytes(raw.ToJsonString()));
        Assert.Equal(2, command.Exit); Assert.Empty(command.Output);
        Assert.Equal("r6_comparison_left_invalid", command.Error.Trim()); Assert.DoesNotContain(Canary, command.Error);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("format")]
    [InlineData("unreadable")]
    public async Task InvalidCommandArgumentsAndPathsExposeOnlyFixedErrors(string fault)
    {
        string[] args = fault switch
        {
            "missing" => ["economics-compare", "--left", Canary],
            "duplicate" => ["economics-compare", "--left", Canary, "--left", Canary],
            "unknown" => ["economics-compare", "--left", Canary, "--private", Canary],
            "format" => ["economics-compare", "--left", Canary, "--right", Canary, "--format", Canary],
            _ => ["economics-compare", "--left", Canary, "--right", Canary],
        };
        var command = await Command(Pair(Make("left"), Make("right")), overrideArguments: args);
        Assert.Equal(2, command.Exit); Assert.Empty(command.Output);
        Assert.Equal(fault == "unreadable" ? "r6_comparison_left_invalid" : "r6_comparison_arguments_invalid", command.Error.Trim());
        Assert.DoesNotContain(Canary, command.Error);
    }

    [Fact]
    public async Task MandatorySelectionsAndBoundContradictionsRejectAtTheCommandBoundary()
    {
        var pair = Pair(Make("left"), Make("right"));
        var declaration = pair.Left.Declaration with { Left = pair.Left.Declaration.Left with { JournalSha256 = Hash('9') } };
        var invalid = await Command((pair.Left with { Declaration = declaration }, pair.Right with { Declaration = declaration }));
        Assert.Equal(2, invalid.Exit); Assert.Contains("selection_mismatch", invalid.Error); Assert.Empty(invalid.Output);
        var history = History('1', true);
        var conflict = await Command(Pair(Histories(Make("left"), [history], "stable_continuity"),
            Histories(Make("right"), [history], "intentional_fault")));
        Assert.Equal(2, conflict.Exit); Assert.Contains("expectation_conflict", conflict.Error); Assert.Empty(conflict.Output);
    }

    [Fact]
    public async Task StrictCodecBoundsAndMaximumPopulationUseActualAdmission()
    {
        var pair = Pair(Make("left", scheduled: 256, completed: 255, failed: 1), Make("right", scheduled: 256, completed: 256));
        var report = Report(pair);
        var bytes = ComparisonJson.Write(report);
        Assert.NotNull(ComparisonJson.Read(bytes)); Assert.True(bytes.Length < ComparisonLimits.ReportBytes);
        Assert.Equal(256, report.Result.Left.Campaign.Scheduled);
        var input = ComparisonJson.WriteInput(pair.Left);
        var padded = new byte[ComparisonLimits.InputBytes]; input.CopyTo(padded, 0); Array.Fill(padded, (byte)' ', input.Length, padded.Length - input.Length);
        Assert.NotNull(ComparisonJson.ReadInput(padded));
        Assert.Null(ComparisonJson.ReadInput(new byte[ComparisonLimits.InputBytes + 1]));
        Assert.Null(ComparisonJson.ReadInput([0xc3, 0x28]));
        var duplicate = Encoding.UTF8.GetString(input).Replace("{\"format\":", "{\"format\":\"duplicate\",\"format\":", StringComparison.Ordinal);
        Assert.Null(ComparisonJson.ReadInput(Encoding.UTF8.GetBytes(duplicate)));
        var tampered = JsonNode.Parse(bytes)!; tampered["result"]!["left"]!["campaign"]!["scheduled"] = 255;
        Assert.Null(ComparisonJson.Read(Encoding.UTF8.GetBytes(tampered.ToJsonString())));
        var command = await Command(pair, rawLeft: new byte[ComparisonLimits.InputBytes + 1]);
        Assert.Equal(2, command.Exit); Assert.Empty(command.Output);
    }

    [Theory]
    [InlineData("1", 8, 2, "0.12")]
    [InlineData("3", 8, 2, "0.38")]
    [InlineData("1", 3, 3, "0.333")]
    [InlineData("0", 2, 3, "0")]
    public void ExactCoefficientDivisionUsesBothHalfEvenTieParities(string amount, int count, int places, string expected)
    {
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), ComparisonNumbers.PerReview(
            decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), count, places));
        Assert.Throws<OverflowException>(() => ComparisonNumbers.Difference(decimal.MaxValue, -1));
        Assert.Throws<OverflowException>(() => ComparisonNumbers.Difference(decimal.MaxValue, 0.1m));
        Assert.Equal(-0.1m, ComparisonNumbers.Difference(0, 0.1m));
    }

    private static EffectiveReviewDefinition Definition(string origin = "human_declared", int minimum = 1) =>
        new("completed_evidence_scenario_adjudicated", origin, minimum);

    private static Sample Make(string campaign, int scheduled = 1, int completed = 1, int failed = 0,
        bool unknown = false, bool missingCache = false, long output = 4, long hit = 6,
        char source = 'a', char config = 'e', string build = LiveRunner.BuildId,
        string model = DeepSeekAdapterContext.Model, long missRate = 4, string currency = "USD",
        char corpus = 'c', char caseHash = 'd', int maxSeconds = 120, int? maxCalls = null, string? stopReason = null)
    {
        var calls = maxCalls ?? scheduled * 8;
        var plan = new LivePlanDigestInput(LiveLimits.PlanFormat, new(new string(source, 40), new string('b', 40), true),
            Hash(corpus), new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Adapter,
                LivePlanAdmission.ProviderConfigurationSha256()), Enumerable.Repeat("cs-safe", scheduled).ToImmutableArray(),
            new(scheduled, calls, scheduled * AgentLimits.InputTokens, scheduled * AgentLimits.OutputTokens,
                scheduled * AgentLimits.CombinedTokens, maxSeconds, calls * 1000,
                new(AgentLimits.InputTokens / 8, AgentLimits.OutputTokens / 8, 1000)));
        var expected = new UsageJournalExpectation(new(campaign, plan.Source.Commit, plan.Source.Tree, true, build,
            plan.CorpusSha256, plan.Provider.ConfigurationSha256, LivePlanAdmission.Digest(plan), "loopback"), plan);
        var collector = new UsageJournalCollector(expected);
        var outcomes = ImmutableArray.CreateBuilder<EvaluationOutcome>();
        var attempted = completed + failed;
        for (var i = 0; i < attempted; i++)
        {
            var scope = collector.BeginAttempt(i); scope.AgentStarted();
            var call = scope.BeginCall()!; Assert.True(call.Dispatch());
            if (unknown) { call.TransportFinished(DeepSeekTransportResult.TransportFailure()); call.Threw(); }
            else
            {
                call.TransportFinished(DeepSeekTransportResult.Success([]));
                call.Returned(missingCache ? new ProjectChatUsage(10, output) : new(10, output,
                    new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, model, hit, 10 - hit)));
            }
            var run = new EvaluationRunInput(expected.AttemptId(i), "deterministic", plan.Source.Commit, plan.Source.Tree, true,
                plan.Provider.ConfigurationSha256);
            var attemptHash = EvaluationAttempt.Hash("attempt", Hash(config), AgentCanonical.HashRaw(EvaluationJson.Write(run)));
            var done = i < completed;
            var outcome = new EvaluationOutcome("cs-safe", Hash(corpus), Hash(caseHash), Hash(config), attemptHash,
                done ? EvaluationAttempt.Hash("synthetic-execution", attemptHash) : null, plan.Source.Commit, plan.Source.Tree, true,
                "deterministic", done ? EvaluationStatus.Completed : EvaluationStatus.Failed,
                done ? AssertionStatus.Passed : AssertionStatus.NotEvaluated, done ? AssertionStatus.Passed : AssertionStatus.NotEvaluated,
                done ? ModelObservationStatus.Adjudicated : ModelObservationStatus.NotEvaluated,
                done ? EvaluationCode.Scored : EvaluationCode.ExecutionFailed,
                done ? EvaluationFailureSource.None : EvaluationFailureSource.Agent,
                done ? EvaluationFailureKind.None : EvaluationFailureKind.MalformedOutput,
                0, done ? 1 : 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            Assert.NotNull(EvaluationJson.ReadOutcome(EvaluationJson.Write(outcome)));
            outcomes.Add(outcome); scope.AdmitEvaluation(attemptHash); scope.AgentFinished(done);
            scope.Finish(done ? "completed" : "failed", cancelled: i == attempted - 1 && (stopReason is "caller_cancelled" or "deadline"));
        }
        var perCall = expected.Bounds.PerCall;
        var journal = collector.Seal(stopReason ?? (attempted == scheduled ? "complete" : "caller_cancelled"),
            new(attempted, attempted * perCall.MaxInputTokens, attempted * perCall.MaxOutputTokens,
                attempted * (perCall.MaxInputTokens + perCall.MaxOutputTokens), attempted * perCall.MaxChargeMicroUsd));
        var tariff = new TariffInput(PricingLimits.TariffFormat, "https://example.com/synthetic", "2026-09-20",
            new(PricingLimits.Formula, DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, null, "standard",
                "2026-09-20T12:00:00Z", new("known", "2026-09-20T00:00:00Z", "2026-09-21T00:00:00Z"), 100, 0,
                new(new(currency, 1), new(currency, missRate), new(currency, 5)),
                new("half_even", 3, PricingLimits.Aggregation, PricingLimits.Normalization)));
        var admittedTariff = AdmittedTariff.Read(JsonSerializer.SerializeToUtf8Bytes(tariff, PricingJsonContext.Default.TariffInput), out _)!;
        return new(PricingReport.Create(journal, admittedTariff).Document, new(outcomes.ToImmutable(), [], [], []));
    }

    private static Sample Annotate(Sample sample, string origin) => sample with { Evidence = sample.Evidence with
    {
        Annotations = sample.Evidence.Outcomes.Where(o => o.ExecutionSha256 is not null).Select(o =>
            new AnnotationOrigin(ComparisonJson.OutcomeHash(o), o.ExecutionSha256!, origin)).ToImmutableArray(),
    } };

    private static HistoryReport History(char session = '1', bool unstable = false, char source = 'a')
    {
        var segment = new PrefixSegment(Hash('a'), 1, 1);
        var projection = new PrefixProjection(segment, segment, segment, segment, segment);
        var baseline = new PrefixObservation(new(new string(source, 40), new string('b', 40), true, Hash('f'), Hash(session), -1, null),
            1, 0, projection, projection);
        var callProjection = projection with { Dynamic = new(Hash('c'), 2, 1), Whole = new(Hash('d'), 4, 1),
            History = unstable ? new(Hash('e'), 1, 1) : segment };
        var call = baseline with { Logical = callProjection, Provider = callProjection };
        var row = new HistoryRow(0, 123, unstable ? "session_failed" : "completed", false, null, null, null, true, true, true,
            baseline.Compare(call), new("observed", baseline, [call]));
        var report = new HistoryReport("deterministic", "replay", unstable ? "session_failed" : "completed", "cleaned",
            baseline.Domain.SourceCommit, baseline.Domain.SourceTree, true, [row]);
        Assert.NotNull(HistoryJson.Read(HistoryJson.Write(report)));
        return report;
    }

    private static Sample Histories(Sample sample, ImmutableArray<HistoryReport> histories, string expectation) => sample with
    {
        Evidence = sample.Evidence with { Histories = histories, Expectations = histories.SelectMany(h => h.Rows.SelectMany(r =>
            r.Capture.Calls.Select((c, i) => c is null ? null : new ScenarioExpectation(ComparisonJson.HistoryHash(h), r.Phase,
                i + 1, ComparisonJson.ObservationHash(c), expectation)))).OfType<ScenarioExpectation>().ToImmutableArray() },
    };

    private static (ComparisonInput Left, ComparisonInput Right) Pair(Sample left, Sample right, string axis = "none",
        EffectiveReviewDefinition? definition = null, DeclaredRegressionRule? rule = null, string control = "not_requested")
    {
        var declaration = new ComparisonDeclaration(axis, control, ComparisonJson.Select(left.Pricing, left.Evidence),
            ComparisonJson.Select(right.Pricing, right.Evidence), definition, rule);
        return (new(ComparisonLimits.InputFormat, declaration, left.Pricing, left.Evidence),
            new(ComparisonLimits.InputFormat, declaration, right.Pricing, right.Evidence));
    }

    private static ComparisonReportDocument Report((ComparisonInput Left, ComparisonInput Right) pair) => ComparisonReport.Create(
        Assert.IsType<ComparisonInput>(ComparisonJson.ReadInput(ComparisonJson.WriteInput(pair.Left))),
        Assert.IsType<ComparisonInput>(ComparisonJson.ReadInput(ComparisonJson.WriteInput(pair.Right))));

    private static async Task<(int Exit, string Output, string Error)> Command((ComparisonInput Left, ComparisonInput Right) pair,
        string format = "json", bool reverseOptions = false, byte[]? rawLeft = null, string[]? overrideArguments = null)
    {
        var root = Path.Combine(Path.GetTempPath(), Canary + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var stdout = Console.Out; var stderr = Console.Error;
        using var output = new StringWriter(); using var error = new StringWriter();
        try
        {
            var left = Path.Combine(root, "left.json"); var right = Path.Combine(root, "right.json");
            File.WriteAllBytes(left, rawLeft ?? ComparisonJson.WriteInput(pair.Left));
            File.WriteAllBytes(right, ComparisonJson.WriteInput(pair.Right));
            Console.SetOut(output); Console.SetError(error);
            var args = reverseOptions ? new[] { "economics-compare", "--format", format, "--right", right, "--left", left } :
                ["economics-compare", "--left", left, "--right", right, "--format", format];
            var exit = await EvaluatorProgram.Main(overrideArguments ?? args);
            return (exit, output.ToString(), error.ToString());
        }
        finally { Console.SetOut(stdout); Console.SetError(stderr); Directory.Delete(root, true); }
    }
}
