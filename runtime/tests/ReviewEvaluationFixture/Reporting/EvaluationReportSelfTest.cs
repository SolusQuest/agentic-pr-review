using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Reporting;

// Small authored reporting vectors. Source identities below describe synthetic test inputs,
// not the binary running them. The validation driver records the actual compiled source separately.
internal static class EvaluationReportSelfTest
{
    internal const string Canary = "APR244_PRIVATE_REPORT_CANARY";

    internal static async Task<EvaluationOutcome> RowAsync(string caseId, string runId, int findings = 1,
        int defects = 1, bool annotate = true, bool rejected = false, bool wrongEvidence = false,
        bool staleAnnotation = false, int? annotationCount = null, string mode = "deterministic",
        string model = "synthetic-model", string policy = "Review synthetic inputs using bounded tools.",
        string provider = "synthetic-provider", string adapter = "synthetic-adapter",
        string? sourceCommit = null, string? sourceTree = null, bool sourceClean = true, string? corpusSha256 = null,
        bool prohibited = false)
    {
        var fixture = await EvaluationSelfTest.CreateAsync(findingCount: findings, prose: Canary,
            mode: mode, model: model, policy: policy, provider: provider, adapter: adapter);
        var testCase = EvaluationCase.Admit(fixture.Case.Input with
        {
            Id = caseId,
            CorpusSha256 = corpusSha256 ?? fixture.Case.Input.CorpusSha256,
            Defects = Enumerable.Range(0, defects).Select(i => fixture.Case.Input.Defects[0] with { Id = "defect-" + i }).ToImmutableArray(),
            RequiredObservations = wrongEvidence ? [fixture.Case.Input.RequiredObservations[0] with { ObservationId = new string('f', 64) }] : fixture.Case.Input.RequiredObservations,
            ProhibitedFindings = prohibited ? [new(EvaluationSelfTest.SourcePath, 1, 1)] : [],
        })!;
        var run = fixture.Run with { RunId = runId, SourceCommit = sourceCommit ?? new string('a', 40), SourceTree = sourceTree ?? new string('b', 40), SourceClean = sourceClean };
        var subject = EvaluationSubject.Admit(fixture.Input, run)!;
        EvaluationAdjudication? annotation = annotate ? EvaluationSelfTest.Annotation(testCase, subject,
            Enumerable.Range(0, annotationCount ?? findings).Select(i => new FindingAdjudication(i,
                rejected ? "rejected" : "confirmed", rejected || i >= defects ? null : "defect-" + i)).ToArray()) : null;
        if (staleAnnotation && annotation is not null) annotation = annotation with { ExecutionSha256 = new string('0', 64) };
        return EvaluationScorer.Evaluate(testCase, subject, annotation);
    }

    internal static async Task<ImmutableArray<EvaluationOutcome>> MixedAsync()
    {
        var positive = await RowAsync("confirmed", "run-positive");
        var rejected = await RowAsync("rejected", "run-rejected", rejected: true);
        var missed = await RowAsync("missing", "run-missing", findings: 0);
        var pending = await RowAsync("pending", "run-pending", annotate: false);
        var fixture = await EvaluationSelfTest.CreateAsync();
        var providerCase = EvaluationCase.Admit(fixture.Case.Input with { Id = "provider-failed" })!;
        var attempt = EvaluationAttempt.Admit(fixture.Input.TrustedRequest,
            fixture.Run with { RunId = "run-provider", SourceCommit = new string('a', 40), SourceTree = new string('b', 40), SourceClean = true })!;
        var provider = EvaluationScorer.Failure(providerCase, EvaluationFailure.FromProviderTransport(new HttpRequestException(Canary)), attempt);
        var detached = EvaluationScorer.Failure(EvaluationCase.Admit(fixture.Case.Input with { Id = "detached" })!, EvaluationFailure.Invalid);
        var evidence = await RowAsync("evidence-rejected", "run-evidence", wrongEvidence: true);
        var invalidAnnotation = await RowAsync("invalid-annotation", "run-annotation", findings: 0, staleAnnotation: true);
        var empty = await RowAsync("empty-control", "run-empty", findings: 0, defects: 0);
        return [positive, rejected, missed, pending, provider, detached, evidence, invalidAnnotation, empty];
    }

    internal static ReportingResult<EvaluationReport> Report(IEnumerable<EvaluationOutcome> rows) =>
        EvaluationReport.Create(rows.Select(r => (ReadOnlyMemory<byte>)EvaluationJson.Write(r)).ToImmutableArray());

    internal static async Task<(bool Passed, EvaluationReport? Mixed, EvaluationReportComparison? Comparison)> RunAsync()
    {
        var mixed = Report(await MixedAsync());
        var before = Report([await RowAsync("comparison", "before")]);
        var afterRow = await RowAsync("comparison", "after", findings: 0, sourceCommit: new string('c', 40), sourceTree: new string('d', 40));
        var after = Report([afterRow]);
        if (!mixed.Succeeded || !before.Succeeded || !after.Succeeded) return (false, null, null);
        var comparison = EvaluationReportComparison.Create(before.Value, after.Value);
        if (!comparison.Succeeded) return (false, mixed.Value, null);
        var summary = mixed.Value!.Document.Summary;
        var json = EvaluationReportJson.Write(mixed.Value);
        var markdown = EvaluationReportMarkdown.Write(mixed.Value);
        var compareJson = EvaluationReportJson.Write(comparison.Value);
        var compareMarkdown = EvaluationReportMarkdown.Write(comparison.Value);
        var passed = summary.Execution == new ReportExecutionCounts(9, 8, 1, 7, 1, 1) &&
            summary.Observations.NotEvaluatedCases == 4 && summary.Observations.UnadjudicatedCases == 1 &&
            summary.Observations.EligibleCases == 4 && summary.Precision.Numerator == 1 && summary.Precision.Denominator == 2 &&
            summary.Recall.Numerator == 1 && summary.Recall.Denominator == 3 &&
            summary.Precision.Value is null && summary.Recall.Value is null && summary.HasBlockingFailures &&
            comparison.Value!.Document.Comparable && json.Succeeded && markdown.Succeeded && compareJson.Succeeded && compareMarkdown.Succeeded;
        if (!passed) return (false, mixed.Value, comparison.Value);
        passed &= EvaluationReportJson.Read(json.Value!).Succeeded &&
            EvaluationReportJson.ReadComparison(compareJson.Value!, before.Value, after.Value).Succeeded &&
            !EvaluationReportJson.Read("{\"private\":\"APR244_PRIVATE_REPORT_CANARY\"}"u8).Succeeded;
        foreach (var value in new[] { Encoding.UTF8.GetString(json.Value!), markdown.Value!,
            Encoding.UTF8.GetString(compareJson.Value!), compareMarkdown.Value!, mixed.ToString(), comparison.ToString() })
            passed &= !value.Contains(Canary, StringComparison.Ordinal) && !value.Contains(EvaluationSelfTest.Canary, StringComparison.Ordinal);
        return (passed, mixed.Value, comparison.Value);
    }
}
