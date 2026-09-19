using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Quality;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Reporting;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

public sealed class R5LiveCoverageTests
{
    private static EvaluationCase CoverageCase(EvaluationCase exact) => EvaluationCase.Admit(exact.Input with
    {
        Defects = exact.Input.Defects.Select(d => d with { ObservationId = null }).ToImmutableArray(),
        RequiredObservations = [new(AgentToolRegistry.ReadFileName, null, new(EvaluationSelfTest.SourcePath, 1, 2))],
    })!;

    [Fact]
    public async Task AlternateReadWindowPreservesCoverageButNotExactIdentityThroughAdjudicationAndReport()
    {
        var original = await EvaluationSelfTest.CreateAsync();
        var alternate = await EvaluationSelfTest.CreateAsync(readLineCount: 20);
        var subject = alternate.Admit()!;
        Assert.NotNull(subject);
        Assert.NotEqual(original.Admit()!.Observations[0], subject.Observations[0]);
        Assert.Equal(EvaluationCode.RequiredObservationMissing, EvaluationScorer.Evaluate(original.Case, subject).Code);
        var testCase = CoverageCase(original.Case);
        Assert.NotEqual(original.Case.Sha256, testCase.Sha256);
        Assert.Equal(testCase.Sha256, EvaluationJson.ReadCase(EvaluationJson.Write(testCase.Input))!.Sha256);
        var pending = EvaluationScorer.Evaluate(testCase, subject);
        Assert.Equal(AssertionStatus.Passed, pending.EvidenceStatus);
        Assert.Equal(ModelObservationStatus.Unadjudicated, pending.ModelStatus);
        var annotation = EvaluationSelfTest.Annotation(testCase, subject, new FindingAdjudication(0, "confirmed", "defect-a"));
        Assert.True(EvaluationScorer.IsValidAdjudication(testCase, subject, annotation));
        var scored = EvaluationScorer.Evaluate(testCase, subject, annotation);
        Assert.Equal(1, scored.AdjudicatedDefects);
        Assert.True(EvaluationReport.Create([EvaluationJson.Write(scored)]).Succeeded);
        var stale = EvaluationSelfTest.Annotation(testCase, original.Admit()!, new FindingAdjudication(0, "confirmed", "defect-a"));
        Assert.False(EvaluationScorer.IsValidAdjudication(testCase, subject, stale));
        Assert.Equal(EvaluationCode.AdjudicationInvalid, EvaluationScorer.Evaluate(testCase, subject, stale).Code);
    }

    [Fact]
    public async Task CoverageCannotInventGroundingOrOverrideSnapshotToolPathAndReturnedLines()
    {
        var fixture = await EvaluationSelfTest.CreateAsync(readLineCount: 20);
        var testCase = CoverageCase(fixture.Case);
        var subject = fixture.Admit()!;
        foreach (var required in new[]
        {
            new RequiredObservation(AgentToolRegistry.ReadDiffName, null, new(EvaluationSelfTest.SourcePath, 1, 2)),
            new RequiredObservation(AgentToolRegistry.ReadFileName, null, new("src/Unrelated.cs", 1, 2)),
            new RequiredObservation(AgentToolRegistry.ReadFileName, null, new(EvaluationSelfTest.SourcePath, 1, 3)),
        })
        {
            var wrong = EvaluationCase.Admit(testCase.Input with { RequiredObservations = [required] })!;
            var row = EvaluationScorer.Evaluate(wrong, subject);
            Assert.Equal(AssertionStatus.Failed, row.EvidenceStatus);
            Assert.Equal(ModelObservationStatus.NotEvaluated, row.ModelStatus);
        }
        var wrongSnapshot = EvaluationCase.Admit(testCase.Input with
        { ReviewedIdentity = testCase.Input.ReviewedIdentity with { HeadSha = new string('a', 40) } })!;
        Assert.Equal(EvaluationCode.WrongSnapshot, EvaluationScorer.Evaluate(wrongSnapshot, subject).Code);
        Assert.Null((await EvaluationSelfTest.CreateAsync(ungrounded: true)).Admit());
        var duplicate = await EvaluationSelfTest.CreateAsync(findingCount: 2, readLineCount: 20);
        var pair = duplicate.Admit()!;
        Assert.Equal(1, EvaluationScorer.Evaluate(testCase, pair).DuplicateObservations);
        var annotations = EvaluationSelfTest.Annotation(testCase, pair, new(0, "confirmed", "defect-a"), new(1, "confirmed", "defect-a"));
        Assert.False(EvaluationScorer.IsValidAdjudication(testCase, pair, annotations));
        var gap = new AgentObservation(new string('a', 64), subject.ReviewedIdentity,
            ImmutableDictionary<string, ImmutableHashSet<int>>.Empty.Add(EvaluationSelfTest.SourcePath, [1, 3]));
        Assert.False(new RequiredObservation(AgentToolRegistry.ReadFileName, null,
            new(EvaluationSelfTest.SourcePath, 1, 3)).Matches(AgentToolRegistry.ReadFileName, gap));
    }

    [Fact]
    public async Task CoverageAlternativesAreExplicitClosedAndBounded()
    {
        var testCase = CoverageCase((await EvaluationSelfTest.CreateAsync()).Case);
        foreach (var mutation in new Action<JsonObject>[]
        {
            value => value["defects"]![0]!.AsObject().Remove("observation_id"),
            value => value["required_observations"]![0]!.AsObject().Remove("observation_id"),
            value => value["required_observations"]![0]!["observation_id"] = new string('a', 64),
            value => value["required_observations"]![0]!["coverage"] = null,
            value => value["required_observations"]![0]!["coverage"]!["start_line"] = 0,
            value => value["required_observations"]![0]!["coverage"]!["end_line"] = int.MaxValue,
            value => value["required_observations"]![0]!["coverage"]!.AsObject().Remove("path"),
            value => value["required_observations"]![0]!["coverage"]!["unknown"] = true,
        })
        {
            var json = JsonNode.Parse(EvaluationJson.Write(testCase.Input))!.AsObject();
            mutation(json);
            Assert.Null(EvaluationJson.ReadCase(Encoding.UTF8.GetBytes(json.ToJsonString())));
        }
    }

    [Fact]
    public async Task AuthoredLiveCorpusCoversIndependentFactsAndUsesRepairedSource()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "agent", "r5");
        var live = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Path.Combine(root, "live-coverage")).Fixture);
        var quality = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Path.Combine(root, "quality", "bundle")).Fixture);
        Assert.Equal(new[] { "cs-defect", "cs-safe", "ts-defect", "ts-safe", "repository-rule" }, live.Runs.Select(r => r.Input.CaseId));
        foreach (var run in live.Runs)
        {
            var spec = QualityCoverage.Cases.Single(s => s.Id == run.Input.CaseId);
            var audit = await QualityAudit.ObserveAsync(run, spec);
            Assert.True(audit.FactsMatch);
            var original = quality.Runs.Single(r => r.Input.CaseId == run.Input.CaseId);
            Assert.Equal(original.Input.Repository.ToArray(), run.Input.Repository.ToArray());
            foreach (var entry in original.Input.Repository)
                Assert.Equal(File.ReadAllBytes(Path.Combine(root, "quality", "bundle", entry.File)),
                    File.ReadAllBytes(Path.Combine(root, "live-coverage", entry.File)));
            var expected = run.Expected.Input;
            Assert.All(expected.Defects, d => Assert.Null(d.ObservationId));
            Assert.All(expected.RequiredObservations, required =>
            {
                Assert.Null(required.ObservationId);
                Assert.Equal(AgentToolRegistry.ReadFileName, required.Tool);
                Assert.NotNull(required.Coverage);
            });
            foreach (var fact in spec.Facts)
                Assert.Contains(expected.RequiredObservations, r => r.Coverage!.Path == fact.Path &&
                    r.Coverage.StartLine <= fact.Line && r.Coverage.EndLine >= fact.Line);
        }
    }
}
