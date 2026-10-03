using System.Collections.Immutable;
using System.Text.Json;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Histories;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Prefix;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Reporting;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

public sealed class R7RetainedEvidenceCountsTests
{
    [Theory]
    [InlineData(8, 8, true, true)]
    [InlineData(9, 8, false, true)]
    [InlineData(64, 16, false, true)]
    [InlineData(65, 16, false, false)]
    [InlineData(8, 9, false, true)]
    [InlineData(8, 16, false, true)]
    [InlineData(8, 17, false, false)]
    public void HistoryReadersSelectTheirOwnCallAndBatchDomain(int calls, int batch,
        bool historical, bool current)
    {
        var report = History(calls, batch);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, HistoryJsonContext.Default.HistoryReport);
        Assert.Equal(historical, HistoryJson.Read(bytes) is not null);
        Assert.Equal(current, HistoryJson.ReadCurrent(bytes) is not null);
        Assert.Equal(calls <= 8, HistoryCapture.Safe(report.Rows[0].Capture, MeasurementLimits.Historical));
        Assert.Equal(calls <= 64, HistoryCapture.Safe(report.Rows[0].Capture, MeasurementLimits.Current));
    }

    [Theory]
    [InlineData(8, true, true)]
    [InlineData(9, false, true)]
    [InlineData(64, false, true)]
    [InlineData(65, false, false)]
    public void HistoryCapacityCountIsBoundedIndependentlyOfCapturedCalls(int calls,
        bool historical, bool current)
    {
        var report = History(1, 8);
        var row = report.Rows[0];
        report = report with { Rows = [row with { Capacity = row.Capacity! with { Calls = calls } }] };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, HistoryJsonContext.Default.HistoryReport);
        Assert.Equal(historical, HistoryJson.Read(bytes) is not null);
        Assert.Equal(current, HistoryJson.ReadCurrent(bytes) is not null);
    }

    [Theory]
    [InlineData(8, 24, 8, true)]
    [InlineData(9, 24, 8, false)]
    [InlineData(8, 25, 8, false)]
    [InlineData(8, 24, 9, false)]
    public void HistoricalGrowthReaderKeepsOriginalCountDomain(int calls, int tools, int batch, bool admitted)
    {
        var original = HistoricalGrowth();
        var profile = original.Profiles[0];
        var row = profile.Rows[0];
        row = row with { ModelCalls = calls, ToolObservations = tools,
            ProviderRequests = calls, ProviderRequestBytes = (long)calls * row.LastProviderRequestBytes!.Value,
            Project = row.Project! with { Calls = calls,
                LastResponseMessages = row.Project.LastMessages + 1 + batch } };
        var profiles = original.Profiles.SetItem(0, profile with { Rows = profile.Rows.SetItem(0, row) });
        var candidate = original with { Profiles = profiles, NormalizedSha256 = GrowthJson.Normalize(profiles) };
        Assert.Equal(admitted, GrowthJson.Read(GrowthJson.Write(candidate)) is not null);
    }

    [Theory]
    [InlineData(64, 512, 16, true)]
    [InlineData(65, 512, 16, false)]
    [InlineData(64, 513, 16, false)]
    [InlineData(64, 512, 17, false)]
    public void CurrentGrowthReaderKeepsCurrentCountDomain(int calls, int tools, int batch, bool admitted)
    {
        var original = HistoricalGrowth();
        var profile = original.Profiles[0];
        var row = profile.Rows[0];
        var outcome = profile.Evaluation.Outcomes.Single(item => item.CaseId == row.CaseId);
        var evaluation = Assert.IsType<EvaluationReportDocument>(EvaluationReportBuilder.Build(
            [(ReadOnlyMemory<byte>)EvaluationJson.Write(outcome)]).Value);
        row = row with { ModelCalls = calls, ToolObservations = tools,
            ProviderRequests = calls, ProviderRequestBytes = (long)calls * row.LastProviderRequestBytes!.Value,
            Project = row.Project! with { Calls = calls,
                LastResponseMessages = row.Project.LastMessages + 1 + batch } };
        // Structural reader coverage: retain one valid archived data row, recompute
        // its report inventory/digest, and select current bounds. This claims no execution.
        ImmutableArray<GrowthProfileReport> profiles = [profile with { Rows = [row], Evaluation = evaluation,
            LimitObserved = false, TerminalStage = "schedule", TerminalCode = "cancelled" }];
        var candidate = original with { Code = "observation_incomplete", Profiles = profiles,
            NormalizedSha256 = GrowthJson.Normalize(profiles) };
        Assert.Equal(admitted, GrowthJson.ReadCurrent(GrowthJson.Write(candidate)) is not null);
    }

    private static GrowthReport HistoricalGrowth()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "agent", "r7", "session-capacity", "historical-growth.json");
        return Assert.IsType<GrowthReport>(GrowthJson.Read(File.ReadAllBytes(path)));
    }

    private static HistoryReport History(int calls, int batch)
    {
        var segment = new PrefixSegment(new string('a', 64), 1, 1);
        var projection = new PrefixProjection(segment, segment, segment, segment, segment);
        var observation = new PrefixObservation(new(new string('a', 40), new string('b', 40), true,
            new string('f', 64), new string('1', 64), -1, null), 1, 0, projection, projection);
        var capture = new HistoryCapture("observed", observation,
            Enumerable.Repeat<PrefixObservation?>(observation, calls).ToImmutableArray());
        // Capacity permits overflow above the selected global message cap by
        // one response and its batch allowance; it is not a completion claim.
        var messages = batch >= 16 ? 4096 : 64;
        var row = new HistoryRow(0, 123, "completed", false, null, null, null, true, true, true,
            observation.Compare(observation), capture, new(calls, 1, messages, messages + 1 + batch, 0, 0));
        return new("deterministic", "replay", "completed", "cleaned",
            observation.Domain.SourceCommit, observation.Domain.SourceTree, true, [row]);
    }
}
