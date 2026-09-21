using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;
using System.Text.Json;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class R6VerifierCoverageTests
{
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "fixtures", "agent", "r5");

    [Fact]
    public void CoverageAndStrictEnvelopeRemainOutsideTheProducer()
    {
        const string required = """
            t1-partition t1-alias t1-zero t1-total-only t1-malformed-partition
            t2-known-failed t2-unknown t2-local-refusal t2-unattempted-tail t2-terminal-order t2-late-finalization
            t3-usd t3-cny t3-half-even-low t3-half-even-even t3-half-even-high t3-aggregate
            t3-zero-denominator t3-missing-partition t3-unknown-zero-rate t3-overflow
            p1-bootstrap-restored p1-dynamic-suffix p1-control p1-history p1-settings
            p1-continuation-position p1-logical-only p1-domain p1-invalid-position
            p2-replay p2-tools p2-continuation p2-missing-history p2-reordered-history
            p2-missing-continuation p2-wrong-position p2-changed-continuation p2-wrong-scope
            p2-wrong-head p2-stale-generation p2-policy p2-model p2-adapter p2-toolset p2-host-capacity-reset
            c1-descriptive c1-source-axis c1-fixed-mismatch c1-partial-population c1-effective-conditional
            c1-control-unsupported c1-prefix-bound c1-prefix-unbound c1-prefix-conflict c1-c2-handoff
            c2-replay c2-full c2-execute-loopback c2-wrong-source c2-wrong-build c2-wrong-predecessor
            c2-before-ready-crash c2-after-prepare-crash c2-partial-reply c2-oversized-reply c2-wrong-reply
            c2-rate-limit c2-usage-violation c2-provider-failure c2-cancel-after-usage c2-cancel-after-prepare
            c2-reject-accept c2-hang c2-preparation-failure c2-credential-probe c2-plan-budget
            c2-spaced-repeat c2-final-missing c2-cleanup c2-unreaped c2-campaign-deadline
            """;
        Assert.Equal(required.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), GateInventory.Cases.Select(item => item.Id));
        var selection = GateContracts.Select(Fixtures);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new GateReport(selection, []), GateJson.Default.GateReport);
        Assert.NotNull(GateContracts.Read(bytes));
        Assert.Throws<InvalidOperationException>(() => GateVerifier.Verify(bytes, selection));
        var json = System.Text.Encoding.UTF8.GetString(bytes);
        foreach (var invalid in new[] { json + "{}", "{\"cases\":[]," + json[1..], "{\"extra\":1," + json[1..], "{}" })
            Assert.Null(GateContracts.Read(System.Text.Encoding.UTF8.GetBytes(invalid)));
        using var canary = JsonDocument.Parse("{\"value\":\"\\u0041PR279_PRIVATE_GATE_CANARY\"}");
        Assert.False(GateContracts.Safe(canary.RootElement));
    }

    [Fact]
    public void ReboundWrongArtifactsCannotSatisfyTheTokenOracle()
    {
        var selection = GateContracts.Select(Fixtures);
        var root = ReplayProcess.CreatePrivateRoot();
        try
        {
            var cases = new List<GateCase>();
            GateTokenCases.Run(cases, selection, root);
            var usd = cases.Single(item => item.Id == "t3-usd");
            GateTokenOracle.Verify(usd, selection);
            var cny = cases.Single(item => item.Id == "t3-cny");
            var substituted = GateCase.Create(usd.Id, usd.Selection, GateContracts.Bytes(cny.Evidence));
            Assert.Throws<InvalidOperationException>(() => GateTokenOracle.Verify(substituted, selection));
            var alias = GateCase.Create("t1-partition", "10/4/6/4", GateContracts.Bytes(cases.Single(item => item.Id == "t1-alias").Evidence));
            Assert.Throws<InvalidOperationException>(() => GateTokenOracle.Verify(alias, selection));
        }
        finally { Assert.True(ReplayProcess.Cleanup(root)); }
    }

    [Fact]
    public void ComparisonOracleRetainsConditionalQualityAndRejectsConflictingInputs()
    {
        // These are explicitly synthetic C1 reader inputs, independent of the
        // executing build's clean/dirty state. The full gate binds actual C2 too.
        var selection = GateContracts.Select(Fixtures) with { SourceClean = true };
        var root = ReplayProcess.CreatePrivateRoot();
        try
        {
            var cases = new List<GateCase>();
            GateComparisonCases.Run(cases, selection, root);
            Assert.Equal(9, cases.Count);
            foreach (var item in cases) GateComparisonOracle.Verify(item, selection, cases.ToDictionary(value => value.Id));
        }
        finally { Assert.True(ReplayProcess.Cleanup(root)); }
    }

    [Theory]
    [InlineData("c2-replay", "None")]
    [InlineData("c2-wrong-source", "WrongSource")]
    [InlineData("c2-wrong-build", "WrongBuild")]
    [InlineData("c2-wrong-predecessor", "WrongPredecessor")]
    [InlineData("c2-before-ready-crash", "BeforeReadyCrash")]
    [InlineData("c2-after-prepare-crash", "AfterPrepareCrash")]
    [InlineData("c2-partial-reply", "PartialReply")]
    [InlineData("c2-oversized-reply", "OversizedReply")]
    [InlineData("c2-wrong-reply", "WrongReply")]
    [InlineData("c2-rate-limit", "RateLimit")]
    [InlineData("c2-usage-violation", "UsageViolation")]
    [InlineData("c2-provider-failure", "ProviderFailure")]
    [InlineData("c2-cancel-after-usage", "CancelAfterUsage")]
    [InlineData("c2-cancel-after-prepare", "CancelAfterPrepare")]
    [InlineData("c2-reject-accept", "RejectAccept")]
    [InlineData("c2-preparation-failure", "PrepareWriteFailure")]
    [InlineData("c2-credential-probe", "None")]
    [InlineData("c2-cleanup", "None")]
    [InlineData("c2-unreaped", "None")]
    public async Task EconomicsOracleChecksActualWorkerAndFailureSemantics(string id, string fault)
    {
        var selection = GateContracts.Select(Fixtures);
        var root = ReplayProcess.CreatePrivateRoot();
        try
        {
            var tariff = Path.Combine(root, "tariff.json");
            File.WriteAllBytes(tariff, GateTokenCases.TariffBytes(GateTokenCases.Input(unit: 1_000_000, places: 6)));
            var plan = EconomicsCommand.Prepare(Path.Combine(Fixtures, "replay"), Path.Combine(Fixtures, "growth"), tariff,
                [new("replay", 3, 1, false)]);
            var evidence = await GateEconomicsCases.RunCase(root, id, plan, Enum.Parse<EconomicsFault>(fault));
            var item = GateCase.Create(id, fault, JsonSerializer.SerializeToUtf8Bytes(evidence, GateJson.Default.GateEconomics));
            Assert.True(GateContracts.Safe(item.Evidence));
            var projection = GateEconomicsOracle.Verify(item, selection);
            if (id == "c2-replay")
            {
                var repeated = await GateEconomicsCases.RunCase(root, id, plan);
                var repeatedItem = GateCase.Create(id, fault, JsonSerializer.SerializeToUtf8Bytes(repeated, GateJson.Default.GateEconomics));
                Assert.True(JsonElement.DeepEquals(projection, GateEconomicsOracle.Verify(repeatedItem, selection)));
                var forged = evidence with { Report = evidence.Report with { Cleanup = "cleanup_failed" } };
                var rebound = GateCase.Create(id, fault, JsonSerializer.SerializeToUtf8Bytes(forged, GateJson.Default.GateEconomics));
                Assert.Throws<InvalidOperationException>(() => GateEconomicsOracle.Verify(rebound, selection));
            }
        }
        finally { Assert.True(ReplayProcess.Cleanup(root)); }
    }

    [Fact]
    public async Task HostProbeReadsActualEncryptedResetGenerationAndFreshContinuation()
    {
        var selection = GateContracts.Select(Fixtures);
        var host = await GateHostCases.RunAsync(Path.Combine(Fixtures, "growth"), selection);
        var item = GateCase.Create("p2-host-capacity-reset", "production-host-capacity-reset",
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(host, GateJson.Default.GateHostReport));
        GateHistoryOracle.Verify(item, selection);
    }

    [Fact]
    public async Task HistoryOwnersProduceFreshWorkersAndTheProductionHostResetChain()
    {
        var selection = GateContracts.Select(Fixtures);
        var cases = new List<GateCase>();
        await GateHistoryCases.RunAsync(cases, selection, Fixtures);
        Assert.Equal(16, cases.Count);
        foreach (var item in cases)
        {
            Assert.True(GateContracts.Safe(item.Evidence), item.Id);
            GateHistoryOracle.Verify(item, selection);
        }
    }

    [Fact]
    public async Task TokenAndPrefixOwnersProduceTheIndependentlyRequiredSemantics()
    {
        var selection = GateContracts.Select(Fixtures);
        var root = ReplayProcess.CreatePrivateRoot();
        try
        {
            var cases = new List<GateCase>();
            GateTokenCases.Run(cases, selection, root);
            await GatePrefixCases.RunAsync(cases);
            Assert.Equal(30, cases.Count);
            foreach (var item in cases)
            {
                Assert.True(GateContracts.Safe(item.Evidence), item.Id);
                if (item.Id.StartsWith('t')) GateTokenOracle.Verify(item, selection);
                else GatePrefixOracle.Verify(item, selection);
            }
        }
        finally { Assert.True(ReplayProcess.Cleanup(root)); }
    }
}
