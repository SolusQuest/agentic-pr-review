using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Reporting;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

public sealed class R5EvaluationReportTests
{
    [Fact]
    public async Task MixedProducerResultsRetainEveryIndependentDimensionAndDenominator()
    {
        var rows = await EvaluationReportSelfTest.MixedAsync();
        var report = Report(rows);
        var s = report.Document.Summary;
        Assert.Equal(new ReportExecutionCounts(9, 8, 1, 7, 1, 1), s.Execution);
        Assert.Equal(new ReportAssertionCounts(6, 1, 2, 4, 2, 3), s.Assertions);
        Assert.Equal(new ReportObservationCounts(4, 1, 4, 4, 4, 7, 8, 3, 2, 0, 0, 1, 1, 1, 1, 3, 2), s.Observations);
        Assert.Equal(new ReportFailureCounts(6, 1, 0, 0, 0, 2, 0, 1), s.Failures);
        Assert.Equal(AssertionStatus.Failed, s.EngineeringStatus);
        Assert.True(s.HasBlockingFailures);
        Assert.Equal(ReportTelemetry.NotSupplied, s.Telemetry);
        Assert.Equal(new ReportRatio(1, 2, 4, 5, RatioAvailability.Incomplete, null), s.Precision);
        Assert.Equal(new ReportRatio(1, 3, 4, 5, RatioAvailability.Incomplete, null), s.Recall);
        Assert.Equal(9, report.Document.Cohorts.Sum(c => c.RowIndexes.Length));
        Assert.All(report.Document.Cohorts, c => Assert.Null(c.Summary.Precision.Value));
        Assert.Equal(Json(report), Json(EvaluationReportJson.Read(Json(report)).Value!));
        Assert.Contains("eligible numerator/denominator 1/3", Markdown(report));
        Assert.True((await EvaluationReportSelfTest.RunAsync()).Passed);
    }

    [Fact]
    public async Task EmptyAndMissedReviewsDistinguishZeroFromUnavailable()
    {
        var none = Report([]).Document.Summary;
        Assert.Equal(0, none.Execution.AttemptedCases);
        Assert.Equal(AssertionStatus.NotEvaluated, none.EngineeringStatus);
        Assert.Equal(RatioAvailability.EmptyDenominator, none.Recall.Availability);
        Assert.Null(none.Recall.Value);
        var empty = Report([await EvaluationReportSelfTest.RowAsync("empty", "empty", findings: 0, defects: 0)]).Document.Summary;
        Assert.Equal(1, empty.Observations.EligibleCases);
        Assert.Null(empty.Precision.Value);
        Assert.Null(empty.Recall.Value);
        var missing = Report([await EvaluationReportSelfTest.RowAsync("missing", "missing", findings: 0)]).Document.Summary;
        Assert.Equal(0m, missing.Recall.Value);
        Assert.Equal(1, missing.Recall.Denominator);
        Assert.Equal(RatioAvailability.Available, missing.Recall.Availability);
        Assert.Null(missing.Precision.Value);
        Assert.True(missing.HasBlockingFailures);
        Assert.Equal(AssertionStatus.Failed, missing.EngineeringStatus);
    }

    [Fact]
    public async Task SemanticCreditAndStructuralObservationsAreNotInterchangeable()
    {
        var falseFinding = await EvaluationReportSelfTest.RowAsync("false", "false", rejected: true);
        var duplicate = await EvaluationReportSelfTest.RowAsync("duplicate", "duplicate", findings: 2);
        var s = Report([falseFinding, duplicate]).Document.Summary;
        Assert.Equal(2, s.Observations.StructuralMatches);
        Assert.Equal(0, s.Observations.StructurallyMissingDefects);
        Assert.Equal(1, s.Observations.EligibleUncreditedDefects);
        Assert.Equal(1, s.Observations.DuplicateObservations);
        Assert.Equal(2, s.Observations.AdjudicatedTrue);
        Assert.Equal(1, s.Observations.AdjudicatedDefects);
        Assert.Equal(1, s.Observations.AdjudicatedFalse);
        Assert.Equal(2m / 3, s.Precision.Value);
        Assert.Equal(0.5m, s.Recall.Value);
        Assert.True(s.HasBlockingFailures);
        var noExpected = Report([await EvaluationReportSelfTest.RowAsync("unbound", "unbound", defects: 0)]).Document.Summary;
        Assert.Equal(1, noExpected.Observations.AdjudicatedTrue);
        Assert.Equal(0, noExpected.Observations.AdjudicatedDefects);
        Assert.Equal(1m, noExpected.Precision.Value);
        Assert.Null(noExpected.Recall.Value);
    }

    [Fact]
    public async Task EvidenceAndStaleAnnotationFailuresSurviveHumanConfirmation()
    {
        var evidence = await EvaluationReportSelfTest.RowAsync("wrong", "wrong", wrongEvidence: true);
        Assert.Equal(EvaluationCode.RequiredObservationMissing, evidence.Code);
        Assert.Equal(0, evidence.AdjudicatedTrue);
        var missing = await EvaluationReportSelfTest.RowAsync("stale-missing", "stale-missing", findings: 0, staleAnnotation: true);
        var duplicate = await EvaluationReportSelfTest.RowAsync("stale-duplicate", "stale-duplicate", findings: 2, staleAnnotation: true);
        var prohibited = await EvaluationReportSelfTest.RowAsync("stale-prohibited", "stale-prohibited", staleAnnotation: true, prohibited: true);
        var report = Report([evidence, missing, duplicate, prohibited]);
        var s = report.Document.Summary;
        Assert.Equal(4, s.Execution.Completed);
        Assert.Equal(4, s.Observations.NotEvaluatedCases);
        Assert.Equal(3, s.Failures.InvalidAdjudications);
        Assert.Equal(1, s.Observations.StructurallyMissingDefects);
        Assert.Equal(1, s.Observations.DuplicateObservations);
        Assert.Equal(1, s.Observations.ProhibitedObservations);
        Assert.Equal(0, s.Observations.UnadjudicatedFindings);
        Assert.Equal(0, s.Observations.EligibleCases);
        Assert.True(s.HasBlockingFailures);
        Assert.Equal(AssertionStatus.Failed, s.EngineeringStatus);
        Assert.Null(s.Recall.Value);
        Assert.Equal(Json(report), Json(EvaluationReportJson.Read(Json(report)).Value!));
    }

    [Fact]
    public async Task FailedOrPendingCasesCannotImproveACompletePopulationRatio()
    {
        var positive = await EvaluationReportSelfTest.RowAsync("positive", "positive");
        var negative = await EvaluationReportSelfTest.RowAsync("negative", "negative", rejected: true);
        var baseline = Report([positive, negative]);
        Assert.Equal(0.5m, baseline.Document.Summary.Precision.Value);
        var mixed = await EvaluationReportSelfTest.MixedAsync();
        foreach (var bad in mixed.Where(r => !EvaluationReportSummary.Eligible(r)))
        {
            var changed = Report([positive, bad]);
            Assert.Equal(2, changed.Document.Summary.Execution.AttemptedCases);
            Assert.Null(changed.Document.Summary.Precision.Value);
            Assert.Equal(1, changed.Document.Summary.Precision.Denominator);
            Assert.False(Compare(baseline, changed).Document.Comparable);
        }
        var partial = await EvaluationReportSelfTest.RowAsync("partial", "partial", findings: 2, annotationCount: 1);
        var pending = Report([partial]).Document.Summary;
        Assert.Equal(1, pending.Observations.AdjudicatedTrue);
        Assert.Equal(1, pending.Observations.UnadjudicatedFindings);
        Assert.Equal(0, pending.Precision.Denominator);
        Assert.Equal(RatioAvailability.Incomplete, pending.Precision.Availability);
    }

    [Fact]
    public async Task KnownFailureSourcesAndDetachedRowsRemainExplicit()
    {
        var seed = (await EvaluationReportSelfTest.MixedAsync()).Single(r => r.FailureSource == EvaluationFailureSource.Provider);
        var sources = new[]
        {
            (EvaluationFailureSource.Provider, EvaluationFailureKind.ProviderCall),
            (EvaluationFailureSource.Agent, EvaluationFailureKind.MalformedOutput),
            (EvaluationFailureSource.Tool, EvaluationFailureKind.ToolOperation),
            (EvaluationFailureSource.HostState, EvaluationFailureKind.StateAdmission),
            (EvaluationFailureSource.Evaluator, EvaluationFailureKind.InvalidInput),
            (EvaluationFailureSource.Unknown, EvaluationFailureKind.Unknown),
        };
        var rows = sources.Select((p, i) => seed with
        {
            CaseId = "failure-" + i, AttemptSha256 = Hash("failure-" + i), FailureSource = p.Item1, FailureKind = p.Item2,
            ExecutionStatus = p.Item1 == EvaluationFailureSource.Evaluator ? EvaluationStatus.Invalid : EvaluationStatus.Failed,
        }).ToImmutableArray();
        var s = Report(rows).Document.Summary;
        Assert.Equal(new ReportFailureCounts(0, 1, 1, 1, 1, 1, 1, 0), s.Failures);
        Assert.Equal(new ReportExecutionCounts(6, 6, 0, 0, 5, 1), s.Execution);
        Assert.Null(s.Recall.Value);
        var detached = (await EvaluationReportSelfTest.MixedAsync()).Single(r => r.AttemptSha256 is null);
        var unknown = Report([detached, detached]).Document.Summary;
        Assert.Equal(2, unknown.Execution.DetachedCases);
        Assert.Contains(ReportReason.UnknownContext, unknown.Reasons);
        Assert.Equal(0, unknown.Observations.Findings);
        Assert.Equal(2, unknown.Observations.NotEvaluatedCases);
        Assert.Equal(ReportTelemetry.NotSupplied, unknown.Telemetry);
    }

    [Fact]
    public async Task DuplicateSelectionsRejectButLegitimateRepeatAttemptsAreCounted()
    {
        var row = await EvaluationReportSelfTest.RowAsync("repeat", "first");
        var later = await EvaluationReportSelfTest.RowAsync("repeat", "first", annotate: false);
        Assert.Equal(row.AttemptSha256, later.AttemptSha256);
        foreach (var repeated in new[] { row, later })
        {
            var result = EvaluationReportSelfTest.Report([row, repeated]);
            Assert.Equal(ReportingError.DuplicateAttempt, result.Error);
            Assert.Null(result.Value);
            Assert.Equal(2, result.SubmittedRows);
        }
        var second = await EvaluationReportSelfTest.RowAsync("repeat", "second");
        Assert.Equal(2, Report([row, second]).Document.Summary.Execution.AttemptedCases);
        var conflict = second with { CaseSha256 = Hash("different-expectations") };
        Assert.Equal(ReportingError.ConflictingCase, EvaluationReportSelfTest.Report([row, conflict]).Error);
        var invalid = EvaluationReport.Create([(ReadOnlyMemory<byte>)EvaluationJson.Write(row), "{\"raw\":\"APR244_PRIVATE_REPORT_CANARY\"}"u8.ToArray()]);
        Assert.Equal(ReportingError.InvalidInput, invalid.Error);
        Assert.Equal(2, invalid.SubmittedRows);
        Assert.Null(invalid.Value);
    }

    [Fact]
    public async Task RuntimeRevisionComparisonAllowsChangedObservationsWithCompleteCoverage()
    {
        var leftRow = await EvaluationReportSelfTest.RowAsync("same", "before");
        var rightRow = await EvaluationReportSelfTest.RowAsync("same", "after", findings: 0,
            sourceCommit: new string('c', 40), sourceTree: new string('d', 40));
        Assert.Equal(leftRow.ConfigurationSha256, rightRow.ConfigurationSha256);
        Assert.NotEqual(leftRow.AttemptSha256, rightRow.AttemptSha256);
        Assert.NotEqual(leftRow.ExecutionSha256, rightRow.ExecutionSha256);
        var left = Report([leftRow]);
        var right = Report([rightRow]);
        var comparison = Compare(left, right);
        Assert.True(comparison.Document.Comparable);
        Assert.Empty(comparison.Document.Reasons);
        Assert.Equal(leftRow.SourceCommit, comparison.Document.Baseline.Cohorts[0].SourceCommit);
        Assert.Equal(rightRow.SourceTree, comparison.Document.Candidate.Cohorts[0].SourceTree);
        Assert.Equal(1m, comparison.Document.Baseline.Summary.Recall.Value);
        Assert.Equal(0m, comparison.Document.Candidate.Summary.Recall.Value);
        Assert.True(comparison.Document.Candidate.Summary.HasBlockingFailures);
        var encoded = EvaluationReportJson.Write(comparison).Value!;
        Assert.True(EvaluationReportJson.ReadComparison(encoded, left, right).Succeeded);
        Assert.False(EvaluationReportJson.ReadComparison(encoded, right, left).Succeeded);
    }

    [Fact]
    public async Task ConfigurationCorpusModeSourceAndCaseChangesHaveExactReasons()
    {
        var baseline = Report([await EvaluationReportSelfTest.RowAsync("case", "base")]);
        foreach (var changed in new[]
        {
            await EvaluationReportSelfTest.RowAsync("case", "model", model: "different"),
            await EvaluationReportSelfTest.RowAsync("case", "policy", policy: "Different trusted policy"),
            await EvaluationReportSelfTest.RowAsync("case", "provider", provider: "different"),
            await EvaluationReportSelfTest.RowAsync("case", "adapter", adapter: "different"),
        }) Assert.Equal([ReportReason.ConfigurationChanged], Compare(baseline, Report([changed])).Document.Reasons.ToArray());

        var live = await EvaluationReportSelfTest.RowAsync("case", "live", mode: "live");
        Assert.Contains(ReportReason.ModeChanged, Compare(baseline, Report([live])).Document.Reasons);
        var corpus = await EvaluationReportSelfTest.RowAsync("case", "corpus", corpusSha256: Hash("different-corpus"));
        Assert.Contains(ReportReason.CorpusChanged, Compare(baseline, Report([corpus])).Document.Reasons);
        var expectation = await EvaluationReportSelfTest.RowAsync("case", "expectation", defects: 2);
        Assert.Equal([ReportReason.ExpectationsChanged], Compare(baseline, Report([expectation])).Document.Reasons.ToArray());
        var dirty = await EvaluationReportSelfTest.RowAsync("case", "dirty", sourceClean: false);
        Assert.Equal([ReportReason.DirtySource], Compare(baseline, Report([dirty])).Document.Reasons.ToArray());
        var otherCase = await EvaluationReportSelfTest.RowAsync("other", "other");
        Assert.Contains(ReportReason.PopulationChanged, Compare(baseline, Report([otherCase])).Document.Reasons);
        var repeated = await EvaluationReportSelfTest.RowAsync("case", "repeat");
        Assert.Equal([ReportReason.PopulationChanged], Compare(baseline, Report([baseline.Document.Outcomes[0], repeated])).Document.Reasons.ToArray());
        var mixed = Report([baseline.Document.Outcomes[0], live]);
        Assert.Equal(2, mixed.Document.Cohorts.Length);
        Assert.Contains(ReportReason.MixedMode, mixed.Document.Summary.Reasons);
        Assert.Contains(ReportReason.MixedConfiguration, mixed.Document.Summary.Reasons);
        Assert.Equal(RatioAvailability.Incomparable, mixed.Document.Summary.Recall.Availability);
        Assert.All(mixed.Document.Cohorts, c => Assert.Equal(1m, c.Summary.Recall.Value));
        Assert.False(Compare(mixed, mixed).Document.Comparable);
        var mixedSource = Report([baseline.Document.Outcomes[0], repeated with { SourceCommit = new string('c', 40) }]);
        Assert.Contains(ReportReason.MixedSource, mixedSource.Document.Summary.Reasons);
        var mixedCorpus = Report([baseline.Document.Outcomes[0], corpus]);
        Assert.Contains(ReportReason.MixedCorpus, mixedCorpus.Document.Summary.Reasons);
    }

    [Fact]
    public async Task EqualTotalsOverDifferentEligibleCasesCannotEstablishComparability()
    {
        var a = await EvaluationReportSelfTest.RowAsync("a", "a");
        var b = await EvaluationReportSelfTest.RowAsync("b", "b");
        var pendingA = await EvaluationReportSelfTest.RowAsync("a", "pa", annotate: false);
        var pendingB = await EvaluationReportSelfTest.RowAsync("b", "pb", annotate: false);
        var comparison = Compare(Report([a, pendingB]), Report([pendingA, b]));
        Assert.Equal(comparison.Document.Baseline.Summary.Execution, comparison.Document.Candidate.Summary.Execution);
        Assert.False(comparison.Document.Comparable);
        Assert.Contains(ReportReason.IncompleteAnnotations, comparison.Document.Reasons);
        Assert.NotEqual(comparison.Document.Baseline.Coverage[0].EligibleCases, comparison.Document.Candidate.Coverage[0].EligibleCases);
    }

    [Fact]
    public async Task ReadersRejectForgedDerivedFieldsAndNoncanonicalEmbeddedRows()
    {
        var report = Report([await EvaluationReportSelfTest.RowAsync("reader", "reader")]);
        var bytes = Json(report);
        JsonObject Root() => JsonNode.Parse(bytes)!.AsObject();
        foreach (var field in Root().Select(p => p.Key))
        {
            var root = Root();
            root.Remove(field);
            Reject(root);
        }
        var total = Root(); total["summary"]!["execution"]!["completed"] = 99; Reject(total);
        var ratio = Root(); ratio["summary"]!["recall"]!["value"] = 0; Reject(ratio);
        var cohort = Root(); cohort["cohorts"]![0]!["row_indexes"]![0] = 4; Reject(cohort);
        var unknown = Root(); unknown["raw_error"] = EvaluationReportSelfTest.Canary; Reject(unknown);
        foreach (var token in new JsonNode?[] { JsonValue.Create(0), JsonValue.Create("0"), JsonValue.Create("completed"), JsonValue.Create("Completed ") })
        {
            var root = Root(); root["outcomes"]![0]!["execution_status"] = token; Reject(root);
        }
        var raw = Encoding.UTF8.GetString(bytes);
        Assert.False(EvaluationReportJson.Read(Encoding.UTF8.GetBytes(raw.Insert(1, "\"cohorts\":[],"))).Succeeded);
        var reordered = new JsonObject(Root().Reverse().Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
        Assert.True(EvaluationReportJson.Read(Encoding.UTF8.GetBytes(reordered.ToJsonString())).Succeeded);
        var comparison = Compare(report, report);
        var comparisonJson = JsonNode.Parse(EvaluationReportJson.Write(comparison).Value!)!.AsObject();
        comparisonJson["comparable"] = false;
        Assert.False(EvaluationReportJson.ReadComparison(Encoding.UTF8.GetBytes(comparisonJson.ToJsonString()), report, report).Succeeded);
        void Reject(JsonNode root) => Assert.False(EvaluationReportJson.Read(Encoding.UTF8.GetBytes(root.ToJsonString())).Succeeded);
    }

    [Fact]
    public async Task RowAndByteBoundsRejectWholeInputsWithoutTruncatingFailures()
    {
        var seed = await EvaluationReportSelfTest.RowAsync("bounded", "bounded");
        var rows = Many(seed, ReportingLimits.Rows);
        var maximum = Report(rows);
        Assert.Equal(ReportingLimits.Rows, maximum.Document.Summary.Execution.AttemptedCases);
        Assert.Equal(ReportingLimits.Rows, maximum.Document.Summary.Recall.Denominator);
        Assert.True(EvaluationReportJson.Read(Json(maximum)).Succeeded);
        Assert.True(EvaluationReportMarkdown.Write(maximum).Succeeded);
        var overflow = EvaluationReportSelfTest.Report(rows.Add(seed));
        Assert.Equal(ReportingError.RowLimit, overflow.Error);
        Assert.Equal(257, overflow.SubmittedRows);
        Assert.Null(overflow.Value);
        var padded = Many(seed, 64).Select(r => (ReadOnlyMemory<byte>)Pad(EvaluationJson.Write(r), EvaluationLimits.InputBytes)).ToImmutableArray();
        Assert.True(EvaluationReport.Create(padded).Succeeded);
        Assert.Equal(ReportingError.InputByteLimit, EvaluationReport.Create(padded.Add(padded[0])).Error);
        Assert.Equal(ReportingError.InvalidInput, EvaluationReport.Create([Pad(EvaluationJson.Write(seed), EvaluationLimits.InputBytes + 1)]).Error);
        Assert.Equal(ReportingError.InvalidInput, EvaluationReport.Create(default).Error);
        Assert.Equal(ReportingError.InvalidInput, EvaluationReport.Create([ReadOnlyMemory<byte>.Empty]).Error);
        var invalidId = seed with { CaseId = new string('x', 65) };
        Assert.Equal(ReportingError.InvalidInput, EvaluationReportSelfTest.Report([invalidId]).Error);
        Assert.True(EvaluationReportSelfTest.Report([seed with { CaseId = new string('x', 64) }]).Succeeded);
        var twenty = await EvaluationReportSelfTest.RowAsync("max-counts", "max-counts", findings: 20, defects: 20);
        Assert.Equal(5120, Report(Many(twenty, 256)).Document.Summary.Precision.Denominator);
    }

    [Fact]
    public async Task OutputBudgetsAreEnforcedAtTheActualWriterBoundary()
    {
        var report = Report(await EvaluationReportSelfTest.MixedAsync());
        var json = Json(report);
        Assert.True(EvaluationReportJson.Write(report, json.Length).Succeeded);
        Assert.Equal(ReportingError.OutputLimit, EvaluationReportJson.Write(report, json.Length - 1).Error);
        Assert.Equal(ReportingError.InvalidOutputBudget, EvaluationReportJson.Write(report, ReportingLimits.JsonBytes + 1).Error);
        var markdown = Markdown(report);
        var length = Encoding.UTF8.GetByteCount(markdown);
        Assert.True(EvaluationReportMarkdown.Write(report, length).Succeeded);
        Assert.Equal(ReportingError.OutputLimit, EvaluationReportMarkdown.Write(report, length - 1).Error);
        Assert.Equal(ReportingError.InvalidOutputBudget, EvaluationReportMarkdown.Write(report, 0).Error);
        var comparison = Compare(report, report);
        var comparisonBytes = EvaluationReportJson.Write(comparison).Value!;
        Assert.Equal(ReportingError.OutputLimit, EvaluationReportJson.Write(comparison, comparisonBytes.Length - 1).Error);
        var comparisonText = EvaluationReportMarkdown.Write(comparison).Value!;
        Assert.Equal(ReportingError.OutputLimit, EvaluationReportMarkdown.Write(comparison, Encoding.UTF8.GetByteCount(comparisonText) - 1).Error);
        var seed = await EvaluationReportSelfTest.RowAsync("cohort", "cohort");
        var cohorts = Report(Many(seed, 256).Select((r, i) => r with { SourceCommit = i.ToString("x40", CultureInfo.InvariantCulture) }));
        Assert.Equal(256, cohorts.Document.Cohorts.Length);
        Assert.Equal(ReportingError.OutputLimit, EvaluationReportMarkdown.Write(cohorts).Error);
    }

    [Fact]
    public async Task InvalidBytesAndDiagnosticPathsCannotEchoPrivateMaterial()
    {
        var good = Json(Report([await EvaluationReportSelfTest.RowAsync("safe", "safe")]));
        var invalid = new[]
        {
            new byte[] { 0xff }, "null"u8.ToArray(), "[]"u8.ToArray(),
            Encoding.UTF8.GetBytes("{\"outcomes\":\"" + EvaluationReportSelfTest.Canary + "\"}"),
            Encoding.UTF8.GetBytes(new string('[', 20) + "0" + new string(']', 20)),
            Pad(good, ReportingLimits.JsonBytes + 1),
        };
        foreach (var bytes in invalid)
        {
            ReportingResult<EvaluationReport>? result = null;
            Assert.Null(Record.Exception(() => result = EvaluationReportJson.Read(bytes)));
            Assert.False(result!.Succeeded);
            var json = Encoding.UTF8.GetString(EvaluationReportJson.WriteFailure(result.Error, result.SubmittedRows));
            var text = EvaluationReportMarkdown.WriteFailure(result.Error, result.SubmittedRows);
            Assert.DoesNotContain(EvaluationReportSelfTest.Canary, json + text + result);
        }
        Assert.True(EvaluationReportJson.Read(Pad(good, ReportingLimits.JsonBytes)).Succeeded);
        Assert.Equal(ReportingError.InvalidInput, EvaluationReportJson.Write((EvaluationReport?)null).Error);
        Assert.Equal(ReportingError.InvalidInput, EvaluationReportMarkdown.Write((EvaluationReport?)null).Error);
        Assert.Equal(ReportingError.InvalidInput, EvaluationReportComparison.Create(null, null).Error);
    }

    [Fact]
    public async Task PermutationsAndCulturesDoNotChangeCountsOrRendering()
    {
        var rows = await EvaluationReportSelfTest.MixedAsync();
        var original = Report(rows);
        var beforeJson = Json(original);
        var beforeMarkdown = Markdown(original);
        Assert.Equal(beforeJson, Json(Report(rows.Reverse())));
        Assert.Equal(beforeMarkdown, Markdown(Report(rows.Reverse())));
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal(beforeJson, Json(original));
            Assert.Equal(beforeMarkdown, Markdown(original));
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Assert.DoesNotContain(EvaluationSelfTest.Canary, Encoding.UTF8.GetString(beforeJson) + beforeMarkdown);
        Assert.DoesNotContain(EvaluationReportSelfTest.Canary, Encoding.UTF8.GetString(beforeJson) + beforeMarkdown);
    }

    private static EvaluationReport Report(IEnumerable<EvaluationOutcome> rows)
    {
        var result = EvaluationReportSelfTest.Report(rows);
        Assert.Equal(ReportingError.None, result.Error);
        return Assert.IsType<EvaluationReport>(result.Value);
    }
    private static EvaluationReportComparison Compare(EvaluationReport left, EvaluationReport right) =>
        Assert.IsType<EvaluationReportComparison>(EvaluationReportComparison.Create(left, right).Value);
    private static byte[] Json(EvaluationReport report) => Assert.IsType<byte[]>(EvaluationReportJson.Write(report).Value);
    private static string Markdown(EvaluationReport report) => Assert.IsType<string>(EvaluationReportMarkdown.Write(report).Value);
    private static string Hash(string value) => EvaluationSelfTest.Hash(value);
    private static byte[] Pad(byte[] input, int length) => [.. input, .. Enumerable.Repeat((byte)' ', length - input.Length)];
    private static ImmutableArray<EvaluationOutcome> Many(EvaluationOutcome seed, int count) => Enumerable.Range(0, count)
        .Select(i => seed with { AttemptSha256 = Hash("attempt-" + i), ExecutionSha256 = Hash("execution-" + i) }).ToImmutableArray();
}
