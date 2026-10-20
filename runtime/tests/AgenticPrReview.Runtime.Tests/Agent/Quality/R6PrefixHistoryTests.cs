using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent;
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
    public async Task ExplicitHostResetChangesDomainAndContinuesNewEpochAfterTwoAccepts()
    {
        // This production-Host assertion proves epoch change, generation0, two accepted generations,
        // predecessor preservation, old-fact exclusion, and independent new-epoch continuation.
        await ActionHostCompositionTests.VerifyExplicitResetAsync();
        var callable = await GateHostCases.RunAsync(Bundle("growth"), GateContracts.Select(Path.GetDirectoryName(Bundle("growth"))!));
        Assert.Contains(callable.Rows, row => row.Action == "reset" && row.Generation == 0 && row.HostRecordAuthenticated);
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
        Assert.Null(continued.Provider.Request.Continuation);
        Assert.Null(continued.Provider.Request.StablePlan.PriorSessionSha256);
    }

    private static void AssertSafe(HistoryReport report)
    {
        var text = Encoding.UTF8.GetString(HistoryJson.WriteCurrent(report));
        foreach (var secret in new[] { ReplayCoverage.Fact, ReplayCoverage.Current, "reasoning_content", "arguments_json",
            "synthetic-continuation-", "environment_bytes", "plaintext", "src/fact.txt", Path.GetTempPath(), "exception" })
            Assert.False(text.Contains(secret, StringComparison.Ordinal));
    }
}
