using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

public sealed class R6V5QualityGateTests
{
    private static readonly ImmutableArray<string> Schedule =
        ["cs-defect", "cs-safe", "ts-defect", "ts-safe", "repository-rule"];

    private static EvaluationOutcome Row(string id, bool safe)
    {
        var findings = safe ? 0 : 1;
        var row = new EvaluationOutcome(id, R6V5QualityGate.CorpusSha256,
            new string('a', 64), new string('b', 64), new string('c', 64),
            new string('d', 64), new string('e', 40), new string('f', 40), true,
            "live", EvaluationStatus.Completed, AssertionStatus.Passed,
            AssertionStatus.Passed, ModelObservationStatus.Adjudicated,
            EvaluationCode.Scored, EvaluationFailureSource.None,
            EvaluationFailureKind.None, findings, 2, findings, findings,
            0, 0, 0, findings, 0, findings, 0);
        Assert.True(EvaluationOutcomeAdmission.Valid(row));
        return row;
    }

    private static ImmutableArray<EvaluationOutcome> Rows() =>
        [Row("cs-defect", false), Row("cs-safe", true),
            Row("ts-defect", false), Row("ts-safe", true),
            Row("repository-rule", false)];

    private static LiveRunSummary Summary() => new(
        "r5-live-local-v1", "live", new string('a', 64),
        R6V5QualityGate.CorpusSha256, new string('e', 40), new string('f', 40),
        true, 5, 5, 5, 0, 0, 0, 0, 15, 100, 100, 200, 1000, 1000, 2000,
        0, false, new LiveTransportOutcomeCounts(0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0), 1000, 10000, "complete", "cleaned", [],
        "adjudicated", 0, 5);

    [Fact]
    public void CandidateRequiresActualFiveCaseAdjudicationAndCleanControls()
    {
        var rows = Rows();
        var summary = Summary();
        Assert.Equal(R6V5QualityGate.CandidatePass,
            R6V5QualityGate.Evaluate(summary, Schedule, rows));

        // The generic scorer can mark an empty safe case Adjudicated without
        // any external reviewer. Aggregate accepted origins are independent.
        Assert.Equal(ModelObservationStatus.Adjudicated, rows[3].ModelStatus);
        Assert.Equal(R6V5QualityGate.Blocked, R6V5QualityGate.Evaluate(
            summary with { AiAdjudicatedCases = 4 }, Schedule, rows));
        Assert.Equal(R6V5QualityGate.Blocked, R6V5QualityGate.Evaluate(
            summary with { AdjudicationStatus = "pending" }, Schedule, rows));
        Assert.Equal(R6V5QualityGate.Blocked, R6V5QualityGate.Evaluate(
            summary with { Completed = 4, Failed = 1 }, Schedule, rows));
        Assert.Equal(R6V5QualityGate.NotEvaluable, R6V5QualityGate.Evaluate(
            summary with { ExecutionKind = "loopback" }, Schedule, rows));
        Assert.Null(R6V5QualityGate.Evaluate(summary, Schedule.Reverse().ToImmutableArray(), rows));
        Assert.Null(R6V5QualityGate.Evaluate(summary with { CorpusSha256 = new string('0', 64) },
            Schedule, rows));
    }

    [Fact]
    public void RealOffFocusSafeFindingsBlockWithoutRewritingStructuralScored()
    {
        var rows = Rows();
        var tsSafe = rows[3] with { FindingCount = 3, AdjudicatedTrue = 3 };
        Assert.Equal(EvaluationCode.Scored, tsSafe.Code);
        Assert.True(EvaluationOutcomeAdmission.Valid(tsSafe));
        Assert.Equal(R6V5QualityGate.Blocked, R6V5QualityGate.Evaluate(
            Summary(), Schedule, rows.SetItem(3, tsSafe)));

        var rejectedSafe = rows[3] with { FindingCount = 1, AdjudicatedFalse = 1 };
        Assert.True(EvaluationOutcomeAdmission.Valid(rejectedSafe));
        Assert.Equal(R6V5QualityGate.Blocked, R6V5QualityGate.Evaluate(
            Summary(), Schedule, rows.SetItem(3, rejectedSafe)));

        // A confirmed true extra finding on a positive case earns no second
        // expected-defect credit, but is not automatically a frozen-gate failure.
        var extraTrue = rows[0] with { FindingCount = 2, AdjudicatedTrue = 2 };
        Assert.True(EvaluationOutcomeAdmission.Valid(extraTrue));
        Assert.Equal(R6V5QualityGate.CandidatePass, R6V5QualityGate.Evaluate(
            Summary(), Schedule, rows.SetItem(0, extraTrue)));
    }

    [Fact]
    public void MissingDefectFalseFindingAndWrongCitationCannotPass()
    {
        var rows = Rows();
        var wrongCitation = rows[0] with
        {
            Code = EvaluationCode.ExpectedFindingMissing,
            ScenarioStatus = AssertionStatus.Failed,
            StructuralMatches = 0,
            StructurallyMissingDefects = 1,
            AdjudicatedDefects = 0,
        };
        Assert.True(EvaluationOutcomeAdmission.Valid(wrongCitation));
        Assert.Equal(R6V5QualityGate.Blocked, R6V5QualityGate.Evaluate(
            Summary(), Schedule, rows.SetItem(0, wrongCitation)));
        var falseFinding = rows[0] with { FindingCount = 2, AdjudicatedFalse = 1 };
        Assert.True(EvaluationOutcomeAdmission.Valid(falseFinding));
        Assert.Equal(R6V5QualityGate.Blocked, R6V5QualityGate.Evaluate(
            Summary(), Schedule, rows.SetItem(0, falseFinding)));
    }

    [Fact]
    public void OptionalSummaryFieldPreservesEarlierProjectionAndHash()
    {
        var earlier = JsonSerializer.Serialize(Summary(), LiveJsonContext.Default.LiveRunSummary);
        Assert.DoesNotContain("v5_quality_candidate_status", earlier, StringComparison.Ordinal);
        var admitted = JsonSerializer.Deserialize(earlier, LiveJsonContext.Default.LiveRunSummary);
        Assert.NotNull(admitted);
        var projected = JsonSerializer.Serialize(admitted, LiveJsonContext.Default.LiveRunSummary);
        Assert.Equal(earlier, projected);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(earlier)),
            SHA256.HashData(Encoding.UTF8.GetBytes(projected)));
        Assert.True(R6V5QualityGate.ValidStatus(null));
        Assert.False(R6V5QualityGate.ValidStatus("private-canary"));
    }
}
