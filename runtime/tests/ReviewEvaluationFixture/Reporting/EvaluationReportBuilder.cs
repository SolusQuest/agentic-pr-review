using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Reporting;

internal static class EvaluationReportBuilder
{
    internal static ReportingResult<EvaluationReportDocument> Build(ImmutableArray<ReadOnlyMemory<byte>> inputs)
    {
        var count = inputs.IsDefault ? 0 : inputs.Length;
        ReportingResult<EvaluationReportDocument> Fail(ReportingError error) => new(null, error, count);
        if (inputs.IsDefault) return Fail(ReportingError.InvalidInput);
        if (count > ReportingLimits.Rows) return Fail(ReportingError.RowLimit);
        long bytes = 0;
        foreach (var input in inputs) bytes += input.Length;
        if (bytes > ReportingLimits.InputBytes) return Fail(ReportingError.InputByteLimit);

        var admitted = ImmutableArray.CreateBuilder<EvaluationOutcome>(count);
        var attempts = new HashSet<(string Corpus, string Case, string Attempt)>();
        var cases = new Dictionary<(string Corpus, string Case), (string Hash, int Defects)>();
        foreach (var input in inputs)
        {
            var row = EvaluationJson.ReadOutcome(input.Span);
            if (row is null) return Fail(ReportingError.InvalidInput);
            var key = (row.CorpusSha256, row.CaseId);
            var definition = (row.CaseSha256, row.ExpectedDefects);
            if (cases.TryGetValue(key, out var previous) && previous != definition)
                return Fail(ReportingError.ConflictingCase);
            cases[key] = definition;
            if (row.AttemptSha256 is not null && !attempts.Add((key.CorpusSha256, key.CaseId, row.AttemptSha256)))
                return Fail(ReportingError.DuplicateAttempt);
            admitted.Add(row);
        }

        // Canonical row projection is bounded and avoids order-dependent cohort or duplicate choices.
        var rows = admitted.OrderBy(r => Encoding.UTF8.GetString(EvaluationJson.Write(r)), StringComparer.Ordinal).ToImmutableArray();
        var unknownContext = rows.Any(r => r.ConfigurationSha256 is null);
        var cohorts = rows.Select((row, index) => (row, index)).GroupBy(x => ReportCohortIdentity.From(x.row))
            .Select(group => new ReportCohort(group.Key, group.Select(x => x.index).ToImmutableArray(),
                EvaluationReportSummary.Summarize(group.Select(x => x.row).ToImmutableArray(), unknownContext)))
            .ToImmutableArray();
        return new(new(rows, EvaluationReportSummary.Summarize(rows, unknownContext), cohorts), ReportingError.None, count);
    }
}
