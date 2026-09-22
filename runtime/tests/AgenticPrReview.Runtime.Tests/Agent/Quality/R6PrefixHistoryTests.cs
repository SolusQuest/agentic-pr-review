using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Histories;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Prefix;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Reset;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;
using AgenticPrReview.Runtime.Tests.Host.Action;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class R6PrefixHistoryTests
{
    private static string Bundle(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "agent", "r5", name);

    [Fact]
    public async Task AcceptedPredecessorIsRestoredInDistinctProcessesAndPriorOnlyFactIsUsed()
    {
        var report = await HistoryRunner.ReplayAsync(Bundle("replay"));
        Assert.Equal("verified", report.Code);
        Assert.True(report.ContinuityVerified);
        Assert.Equal(3, report.Rows.Length);
        Assert.Equal(3, report.Rows.Select(r => r.ProcessId).Distinct().Count());
        Assert.DoesNotContain(Environment.ProcessId, report.Rows.Select(r => r.ProcessId));
        for (var phase = 0; phase < 3; phase++)
        {
            var row = report.Rows[phase];
            Assert.Equal(phase, row.Generation);
            Assert.True(HistoryReport.Positive(row));
            Assert.Equal(phase - 1, row.Capture.Baseline!.Domain.Generation);
            Assert.Equal(phase == 0 ? null : report.Rows[phase - 1].SessionSha256,
                row.Capture.Baseline.Domain.AcceptedSessionSha256);
            Assert.Equal(2, row.Capture.Calls.Length);
            Assert.NotEqual(row.Capture.Calls[0]!.Provider.Whole, row.Capture.Calls[1]!.Provider.Whole);
            if (phase > 0)
            {
                Assert.True(row.Capture.Baseline.HistoricalMessages > report.Rows[phase - 1].Capture.Baseline!.HistoricalMessages);
                Assert.Equal("incomparable", row.Capture.Baseline.Compare(report.Rows[phase - 1].Capture.Baseline!).Code);
            }
        }
        // ReplayRunner's independent compiled oracle also requires the history-only script reference,
        // exact prior tool fact, and absence from current ahead repository/context/policy/diff.
        AssertSafe(report);
    }

    [Theory]
    [InlineData("tools", 5)]
    [InlineData("continuation", 6)]
    public async Task ActualGrowthBoundaryRejectsWithoutChangingAcceptedPredecessor(string profile, int accepted)
    {
        var report = await HistoryRunner.GrowthAsync(Bundle("growth"), profile);
        Assert.Equal("verified", report.Code);
        Assert.True(report.ContinuityVerified);
        Assert.Equal(accepted + 1, report.Rows.Length);
        Assert.All(report.Rows.Take(accepted), row => Assert.True(HistoryReport.Positive(row)));
        var rejected = report.Rows[^1];
        Assert.False(rejected.Accepted);
        Assert.Null(rejected.Generation);
        Assert.Null(rejected.SessionSha256);
        Assert.True(rejected.PredecessorPreserved);
        Assert.Equal(report.Rows[^2].SessionSha256, rejected.Capture.Baseline!.Domain.AcceptedSessionSha256);
        Assert.Equal(AgentFailureCodes.ResponseInvalid, rejected.Code);
        Assert.NotNull(rejected.Capacity);
        if (profile == "tools")
        {
            Assert.True(report.Rows[^2].Capacity!.LastResponseMessages <= AgentLimits.Messages);
            Assert.True(rejected.Capacity.LastMessages <= AgentLimits.Messages);
            Assert.True(rejected.Capacity.LastResponseMessages > AgentLimits.Messages);
        }
        else
        {
            Assert.True(report.Rows[^2].Capacity!.LastContinuationAfterBytes <= AgentLimits.ContinuationTotalBytes);
            Assert.True(rejected.Capacity.LastContinuationBeforeBytes <= AgentLimits.ContinuationTotalBytes);
            Assert.True(rejected.Capacity.LastContinuationAfterBytes > AgentLimits.ContinuationTotalBytes);
        }
        AssertSafe(report);
    }

    [Theory]
    [InlineData("MissingHistory", "history_failed")]
    [InlineData("ReorderedHistory", "unknown_failed")]
    [InlineData("ChangedContinuation", "session_failed")]
    [InlineData("MissingContinuation", "unknown_failed")]
    [InlineData("WrongContinuationPosition", "unknown_failed")]
    [InlineData("WrongScope", "state_failed")]
    [InlineData("WrongHead", "state_failed")]
    [InlineData("StaleGeneration", "state_failed")]
    [InlineData("ChangedPolicy", "state_failed")]
    [InlineData("ChangedModel", "state_failed")]
    [InlineData("ChangedAdapter", "state_failed")]
    [InlineData("ChangedToolset", "state_failed")]
    public async Task NegativeLifecycleHasExplicitOutcomeAndPreservesPredecessor(string fault, string expected)
    {
        var report = await HistoryRunner.ReplayAsync(Bundle("replay"), Enum.Parse<ReplayFault>(fault));
        Assert.Equal(expected, report.Code);
        Assert.False(report.ContinuityVerified);
        Assert.Equal("cleaned", report.Cleanup);
        Assert.Equal(2, report.Rows.Length);
        Assert.True(HistoryReport.Positive(report.Rows[0]));
        var failed = report.Rows[1];
        Assert.False(failed.Accepted);
        Assert.True(failed.PredecessorPreserved);
        Assert.Null(failed.SessionSha256);
        if (fault is "WrongScope" or "WrongHead" or "StaleGeneration" or "ChangedPolicy" or "ChangedModel" or "ChangedAdapter")
        {
            Assert.Equal("unavailable", failed.Capture.Code);
            Assert.Empty(failed.Capture.Calls);
            Assert.False(failed.WireMatch);
        }
        if (fault == "MissingHistory") Assert.Equal("unmeasurable", failed.Capture.Code);
        if (fault == "ChangedContinuation")
        {
            Assert.True(failed.RestoredMatch);
            Assert.Equal(new PrefixComparison("compared", false, false), failed.Comparison);
        }
        AssertSafe(report);
    }

    [Fact]
    public async Task ExplicitHostResetChangesDomainAndContinuesNewEpochAfterCapacityRejection()
    {
        // This production-Host assertion proves epoch change, generation0, actual capacity rejection,
        // predecessor preservation, old-fact exclusion, and independent new-epoch continuation.
        await ActionHostCompositionTests.VerifyCapacityResetAsync();
        var callable = await GateHostCases.RunAsync(Bundle("growth"), GateContracts.Select(Path.GetDirectoryName(Bundle("growth"))!));
        Assert.Contains(callable.Rows, row => row.Action == "reset" && row.Generation == 0 && row.StoredMarkersMatch);
        Assert.Equal("continue", callable.Rows[^1].Action);
        var world = new ResetProbeWorld();
        var initial = await world.RunAsync(0);
        var reset = await world.RunAsync(1, reset: true);
        var continued = await world.RunAsync(2);
        Assert.Equal(ActionHost.Contracts.ActionHostStateDisposition.Accepted, continued.Completion.Summary.StateDisposition);
        var old = initial.Provider.Request!;
        var fresh = reset.Provider.Request!;
        Assert.NotEqual(HistoryCapture.SessionHash(old.SessionId), HistoryCapture.SessionHash(fresh.SessionId));
        Assert.Null(fresh.Continuation);
        Assert.Null(fresh.StablePlan.PriorSessionSha256);
        Assert.Equal(fresh.SessionId, continued.Provider.Request!.SessionId);
        Assert.NotNull(continued.Provider.Request.Continuation);
        Assert.NotNull(continued.Provider.Request.StablePlan.PriorSessionSha256);
    }

    [Fact]
    public async Task ReportCodecIsSourceGeneratedBoundedAndRejectsUnknownFields()
    {
        var report = await HistoryRunner.ReplayAsync(Bundle("replay"));
        var bytes = HistoryJson.Write(report);
        Assert.True(HistoryJson.Read(bytes)!.ContinuityVerified);
        var json = Encoding.UTF8.GetString(bytes);
        Assert.Null(HistoryJson.Read(Encoding.UTF8.GetBytes(json.Insert(1, "\"private_request\":{},"))));
        Assert.Null(HistoryJson.Read(new byte[HistoryJson.MaximumBytes + 1]));
        Assert.Null(HistoryJson.Read([0xff]));
        foreach (var mutate in new Action<JsonObject>[]
        {
            root => root["rows"]![0]!["capture"] = null,
            root => root["rows"]![0]!["capture"]!["baseline"]!["domain"]!["source_commit"] = "private-content",
            root => root["rows"]![1]!["capture"]!["baseline"]!["domain"]!["generation"] = 9,
            root => root["rows"]![0]!["capture"]!["calls"]![0]!["logical"]!["history"]!["bytes"] = -1,
            root => root["rows"]![0]!["capture"]!["baseline"]!["historical_messages"] = int.MaxValue,
            root => root["rows"]![0]!["process_id"] = 0,
            root => root["rows"]![2]!["accepted"] = false,
        })
        {
            var root = JsonNode.Parse(bytes)!.AsObject();
            mutate(root);
            Assert.Null(HistoryJson.Read(Encoding.UTF8.GetBytes(root.ToJsonString())));
        }
    }

    [Fact]
    public async Task PrivateSidecarTamperingCannotPassCommonAdmission()
    {
        var checkedReplies = 0;
        var result = await ReplayRunner.RunAsync(Bundle("replay"), new()
        {
            ObserveReply = (input, run, reply) =>
            {
                Assert.True(ReplayRunner.AdmitReply(input, run, reply, out _));
                var capture = reply.PrefixHistory!;
                var baseline = capture.Baseline!;
                foreach (var changed in new[]
                {
                    capture with { Code = "private-content" },
                    capture with { Baseline = baseline with { Domain = baseline.Domain with { Generation = 42 } } },
                    capture with { Baseline = baseline with { Domain = baseline.Domain with { SourceTree = new string('a', 40) } } },
                    capture with { Baseline = baseline with { Provider = baseline.Provider with { History = baseline.Provider.History with { Bytes = -1 } } } },
                    capture with { Calls = default },
                }) Assert.False(ReplayRunner.AdmitReply(input, run, reply with { PrefixHistory = changed }, out _));
                // Unavailable measurement may not rewrite runtime failure classification or invent positive evidence.
                var unmeasurable = capture with { Code = "unmeasurable", Baseline = null };
                Assert.True(ReplayRunner.AdmitReply(input, run, reply with { PrefixHistory = unmeasurable }, out _));
                Assert.False(ReplayRunner.AdmitReply(input, run, reply with { PrefixHistory = HistoryCapture.Unavailable }, out _));
                checkedReplies++;
            },
        });
        Assert.Equal("verified", result.Code);
        Assert.Equal(3, checkedReplies);
    }

    [Fact]
    public async Task CancelledAndInvalidSchedulesCannotProducePositiveLifecycleEvidence()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await HistoryRunner.ReplayAsync(Bundle("replay"), token: cancellation.Token);
        Assert.Equal("cancelled", cancelled.Code);
        Assert.Equal("cleaned", cancelled.Cleanup);
        Assert.Empty(cancelled.Rows);
        Assert.False(cancelled.ContinuityVerified);
        AssertSafe(cancelled);
        var invalid = await HistoryRunner.GrowthAsync(Bundle("growth"), "private-invalid-profile");
        Assert.Equal("input_invalid", invalid.Code);
        Assert.Equal("not_created", invalid.Cleanup);
        Assert.Empty(invalid.Rows);
        Assert.False(invalid.ContinuityVerified);
        AssertSafe(invalid);
    }

    private static void AssertSafe(HistoryReport report)
    {
        var text = Encoding.UTF8.GetString(HistoryJson.Write(report));
        foreach (var secret in new[] { ReplayCoverage.Fact, ReplayCoverage.Current, "reasoning_content", "arguments_json",
            "synthetic-continuation-", "environment_bytes", "plaintext", "src/fact.txt", Path.GetTempPath(), "exception" })
            Assert.False(text.Contains(secret, StringComparison.Ordinal));
    }
}
