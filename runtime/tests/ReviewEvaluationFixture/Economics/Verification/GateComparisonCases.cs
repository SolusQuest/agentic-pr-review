using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
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
using static AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification.GateContracts;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;

internal static class GateComparisonCases
{
    private sealed record Sample(PricingReportDocument Pricing, ComparisonEvidence Evidence);

    internal static void Run(List<GateCase> cases, GateSelection selection, string root, IReadOnlyList<GateCase>? economics = null)
    {
        Sample Make(string campaign, int scheduled = 1, int completed = 1, int failed = 0, long output = 4,
            string? commit = null, string build = "r6-comparison-gate", long missRate = 4) =>
            Create(selection, campaign, scheduled, completed, failed, output, commit, build, missRate);
        void Add(string id, string selected, (ComparisonInput Left, ComparisonInput Right) pair) =>
            cases.Add(GateCase.Create(id, selected, ComparisonJson.Write(Report(pair))));
        var descriptive = Pair(Make("left"), Make("right", output: 6), rule: new("observed_reference_total", 0, true));
        Add("c1-descriptive", "observed-reference", descriptive);
        Add("c1-source-axis", "source_build", Pair(Make("left"), Make("right", commit: Hash('f', 40), build: "different-build"), axis: "source_build"));
        Add("c1-fixed-mismatch", "tariff-mismatch", Pair(Make("left"), Make("right", missRate: 9)));
        var partial = Make("left", scheduled: 3, completed: 1, failed: 1);
        Add("c1-partial-population", "failed-and-tail", Pair(partial, Make("right", scheduled: 3, completed: 1, failed: 1)));
        var annotated = partial with { Evidence = partial.Evidence with
        {
            Annotations = [.. partial.Evidence.Outcomes.Where(outcome => outcome.ExecutionSha256 is not null).Select(outcome =>
                new AnnotationOrigin(ComparisonJson.OutcomeHash(outcome), outcome.ExecutionSha256!, "human_declared"))],
        } };
        Add("c1-effective-conditional", "declared-origin", Pair(annotated, Make("right"),
            definition: new("completed_evidence_scenario_adjudicated", "human_declared", 1)));
        Add("c1-control-unsupported", "cache_policy", Pair(Make("left"), Make("right"), axis: "cache_policy", control: "cache_disabled_asserted"));
        var first = History(selection, '1'); var second = History(selection, '2');
        Add("c1-prefix-bound", "stable_continuity", Pair(Histories(Make("left"), [first, second], "stable_continuity"), Make("right")));
        var unbound = Histories(Make("left"), [first], "stable_continuity");
        unbound = unbound with { Evidence = unbound.Evidence with
        { Expectations = [unbound.Evidence.Expectations[0] with { ObservationSha256 = Hash('9') }] } };
        Add("c1-prefix-unbound", "unbound-expectation", Pair(unbound, Make("right")));
        var conflicting = Pair(Histories(Make("left"), [first], "stable_continuity"), Histories(Make("right"), [first], "intentional_fault"));
        var rejected = false;
        try { _ = Report(conflicting); }
        catch (ComparisonInputException error) { rejected = error.Code == "r6_comparison_expectation_conflict"; }
        Require(rejected);
        cases.Add(GateCase.Create("c1-prefix-conflict", "conflicting-expectation", JsonSerializer.SerializeToUtf8Bytes(
            new GateComparisonConflict(conflicting.Left, conflicting.Right), GateJson.Default.GateComparisonConflict)));

        Sample Actual(string id)
        {
            var value = economics!.Single(item => item.Id == id);
            var actual = Economics.Live.EconomicsReportJson.Read(Bytes(value.Evidence.GetProperty("report")))!;
            Require(actual.Pricing is not null);
            return new(actual.Pricing!, new(actual.Outcomes, [], [], []));
        }
        if (economics is not null) Add("c1-c2-handoff", "actual-c2", Pair(Actual("c2-replay"), Actual("c2-reject-accept")));

        var left = Path.Combine(root, "comparison-left.json"); var right = Path.Combine(root, "comparison-right.json");
        File.WriteAllBytes(left, ComparisonJson.WriteInput(descriptive.Left)); File.WriteAllBytes(right, ComparisonJson.WriteInput(descriptive.Right));
        var json = GateCommandCapture.Run(() => ComparisonCommand.Invoke(["economics-compare", "--left", left, "--right", right]));
        Require(ComparisonJson.Read(Encoding.UTF8.GetBytes(json))?.Result.ObservedReferenceTotalDifference == .10m);
        var markdown = GateCommandCapture.Run(() => ComparisonCommand.Invoke(["economics-compare", "--left", left, "--right", right, "--format", "markdown"]));
        Require(markdown.Contains("Historical R5 quality: inconclusive", StringComparison.Ordinal) && markdown.Contains("Same-token all-miss hypothetical", StringComparison.Ordinal));
    }

    private static Sample Create(GateSelection selection, string campaign, int scheduled, int completed, int failed,
        long output, string? commit, string build, long missRate)
    {
        var calls = scheduled * 8;
        var plan = new LivePlanDigestInput(LiveLimits.PlanFormat, new(commit ?? selection.SourceCommit, selection.SourceTree, selection.SourceClean),
            selection.ReplaySha256, new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Adapter,
                LivePlanAdmission.ProviderConfigurationSha256()), Enumerable.Repeat("cs-safe", scheduled).ToImmutableArray(),
            new(scheduled, calls, scheduled * AgentLimits.InputTokens, scheduled * AgentLimits.OutputTokens,
                scheduled * AgentLimits.CombinedTokens, 120, calls * 1000, new(AgentLimits.InputTokens / 8, AgentLimits.OutputTokens / 8, 1000)));
        var expected = new UsageJournalExpectation(new(campaign, plan.Source.Commit, plan.Source.Tree, plan.Source.Clean, build,
            plan.CorpusSha256, plan.Provider.ConfigurationSha256, LivePlanAdmission.Digest(plan), "loopback"), plan);
        var collector = new UsageJournalCollector(expected);
        var outcomes = ImmutableArray.CreateBuilder<EvaluationOutcome>();
        var attempted = completed + failed;
        for (var i = 0; i < attempted; i++)
        {
            var scope = collector.BeginAttempt(i); scope.AgentStarted();
            var call = scope.BeginCall()!; Require(call.Dispatch());
            call.TransportFinished(DeepSeekTransportResult.Success([])); call.Returned(GateTokenCases.Measured(10, output, 6));
            var run = new EvaluationRunInput(expected.AttemptId(i), "deterministic", plan.Source.Commit, plan.Source.Tree, plan.Source.Clean,
                plan.Provider.ConfigurationSha256);
            var attemptHash = EvaluationAttempt.Hash("attempt", Hash('e'), AgentCanonical.HashRaw(EvaluationJson.Write(run)));
            var done = i < completed;
            var outcome = new EvaluationOutcome("cs-safe", selection.ReplaySha256, Hash('d'), Hash('e'), attemptHash,
                done ? EvaluationAttempt.Hash("synthetic-execution", attemptHash) : null, plan.Source.Commit, plan.Source.Tree, plan.Source.Clean,
                "deterministic", done ? EvaluationStatus.Completed : EvaluationStatus.Failed,
                done ? AssertionStatus.Passed : AssertionStatus.NotEvaluated, done ? AssertionStatus.Passed : AssertionStatus.NotEvaluated,
                done ? ModelObservationStatus.Adjudicated : ModelObservationStatus.NotEvaluated,
                done ? EvaluationCode.Scored : EvaluationCode.ExecutionFailed,
                done ? EvaluationFailureSource.None : EvaluationFailureSource.Agent,
                done ? EvaluationFailureKind.None : EvaluationFailureKind.MalformedOutput,
                0, done ? 1 : 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            Require(EvaluationJson.ReadOutcome(EvaluationJson.Write(outcome)) is not null);
            outcomes.Add(outcome); scope.AdmitEvaluation(attemptHash); scope.AgentFinished(done); scope.Finish(done ? "completed" : "failed");
        }
        var per = expected.Bounds.PerCall;
        var journal = collector.Seal(attempted == scheduled ? "complete" : "caller_cancelled",
            new(attempted, attempted * per.MaxInputTokens, attempted * per.MaxOutputTokens,
                attempted * (per.MaxInputTokens + per.MaxOutputTokens), attempted * per.MaxChargeMicroUsd));
        return new(PricingReport.Create(journal, GateTokenCases.Tariff(GateTokenCases.Input(miss: missRate))).Document,
            new(outcomes.ToImmutable(), [], [], []));
    }

    // Hand-authored admitted C1 input, separate from P2's actual process proof.
    // Its independent sessions test aggregation of repeated unexpected drift.
    private static HistoryReport History(GateSelection selection, char session)
    {
        var segment = new PrefixSegment(Hash('a'), 1, 1);
        var projection = new PrefixProjection(segment, segment, segment, segment, segment);
        var baseline = new PrefixObservation(new(selection.SourceCommit, selection.SourceTree, selection.SourceClean,
            Hash('f'), Hash(session), -1, null), 1, 0, projection, projection);
        var changed = projection with { Dynamic = new(Hash('c'), 2, 1), Whole = new(Hash('d'), 4, 1), History = new(Hash('e'), 1, 1) };
        var call = baseline with { Logical = changed, Provider = changed };
        var row = new HistoryRow(0, 123, "session_failed", false, null, null, null, true, true, true,
            baseline.Compare(call), new("observed", baseline, [call]));
        var report = new HistoryReport("deterministic", "replay", "session_failed", "cleaned", selection.SourceCommit,
            selection.SourceTree, selection.SourceClean, [row]);
        Require(HistoryJson.Read(HistoryJson.Write(report)) is not null);
        return report;
    }
    private static Sample Histories(Sample sample, ImmutableArray<HistoryReport> histories, string expectation) => sample with
    {
        Evidence = sample.Evidence with { Histories = histories, Expectations = histories.SelectMany(history => history.Rows.SelectMany(row =>
            row.Capture.Calls.Select((call, index) => new ScenarioExpectation(ComparisonJson.HistoryHash(history), row.Phase,
                index + 1, ComparisonJson.ObservationHash(call!), expectation)))).ToImmutableArray() },
    };
    private static (ComparisonInput Left, ComparisonInput Right) Pair(Sample left, Sample right, string axis = "none",
        EffectiveReviewDefinition? definition = null, DeclaredRegressionRule? rule = null, string control = "not_requested")
    {
        var declaration = new ComparisonDeclaration(axis, control, ComparisonJson.Select(left.Pricing, left.Evidence),
            ComparisonJson.Select(right.Pricing, right.Evidence), definition, rule);
        return (new(ComparisonLimits.InputFormat, declaration, left.Pricing, left.Evidence), new(ComparisonLimits.InputFormat, declaration, right.Pricing, right.Evidence));
    }
    private static ComparisonReportDocument Report((ComparisonInput Left, ComparisonInput Right) pair)
    {
        var left = ComparisonJson.ReadInput(ComparisonJson.WriteInput(pair.Left)); var right = ComparisonJson.ReadInput(ComparisonJson.WriteInput(pair.Right));
        Require(left is not null && right is not null);
        return ComparisonReport.Create(left!, right!);
    }
}
