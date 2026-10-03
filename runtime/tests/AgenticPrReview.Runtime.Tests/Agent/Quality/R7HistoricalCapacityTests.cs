using System.Security.Cryptography;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Histories;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

public sealed class R7HistoricalCapacityTests
{
    [Fact]
    public void CurrentContextRefusalRetainsItsDiagnosticWithoutReclassifyingHistoricalData()
    {
        var diagnostic = LiveAgentDiagnostic.Capture(0, new(AgentFailureCodes.ContextLimit, 1, 0));
        Assert.Equal(AgentFailureCodes.ContextLimit, diagnostic.Code);
        Assert.True(GrowthProfiles.AgentCode(diagnostic.Code));
        Assert.Equal("run_budget", GrowthRunner.Classify("agent", diagnostic.Code, null));
        Assert.Equal("agent_failure", GrowthRunner.Classify("agent", diagnostic.Code, null, MeasurementLimits.Historical));
    }

    private const string Source = "aea38e3f8f14eeae3e2dc915598aade031b3c6e7";
    private static byte[] Read(string name, string digest)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "agent", "r7", "session-capacity", name));
        Assert.Equal(digest, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        return bytes;
    }

    [Fact]
    public void OriginalGrowthObservationsRetainTheirOriginalLimitClassification()
    {
        var bytes = Read("historical-growth.json", "6ca56b30d071f3639baf52c03e9f92f24851310bc28b6fd54dfe8e3f0eb050c5");
        var report = Assert.IsType<GrowthReport>(GrowthJson.Read(bytes));
        Assert.Equal(Source, report.SourceCommit);
        Assert.Equal("verified", report.Code);
        Assert.Equal(new[] { 21, 6, 7, 13 }, report.Profiles.Select(p => p.Rows.Length));
        Assert.Equal(new[] { "append_limit", "message_limit", "continuation_limit", "message_limit" },
            report.Profiles.Select(p => p.Rows[^1].Classification));
        Assert.Null(GrowthJson.ReadCurrent(bytes));
        Assert.False(CurrentGrowthOracle.Valid(report));
    }

    [Theory]
    [InlineData("tools", 6, "8e1ff1267bfdeafed4c97f818759af515d901fa80490bb676f72fe922a60a0fd")]
    [InlineData("continuation", 7, "f474ca414617c4e0b31dd6346346ec6dddb6d3582975793008df7435ec84786b")]
    public void OriginalPrefixHistoryIsDataAdmissionWithoutCurrentProcessClaims(string profile, int rows, string digest)
    {
        var bytes = Read("historical-p2-" + profile + ".json", digest);
        var report = Assert.IsType<HistoryReport>(HistoryJson.Read(bytes));
        Assert.Equal(Source, report.SourceCommit);
        Assert.Equal(rows, report.Rows.Length);
        Assert.True(report.ContinuityVerified);
        Assert.False(report.CurrentContinuityVerified);
        Assert.Null(HistoryJson.ReadCurrent(bytes));
        // No PID lookup: an archived PID is not current execution evidence.
    }

    [Fact]
    public void ArchivedEconomicsCapacityStopsAreNotReexecutedOrRelabeled()
    {
        var bytes = Read("historical-c2-full.json", "4cd892f5a91f41fb7a38904038d65b6b266946f02e97f7d0d87d5e717956c441");
        using var document = JsonDocument.Parse(bytes);
        var report = document.RootElement.GetProperty("report");
        Assert.Equal(2, report.GetProperty("steps").EnumerateArray().Count(s => s.GetProperty("code").GetString() == "capacity_stop"));
        Assert.Equal(2, report.GetProperty("steps").EnumerateArray().Count(s => s.GetProperty("reset").GetBoolean()));
        Assert.Equal(39, report.GetProperty("journal").GetProperty("totals").GetProperty("actual_sends").GetInt32());
    }
}
