using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Histories;
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
            c2-replay c2-output8192 c2-output65536 c2-full c2-execute-loopback c2-wrong-source c2-wrong-build c2-wrong-predecessor
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
            // Both round to 2. Equal output cannot substitute a different
            // independently selected numerator into the low-half probe.
            var sameAmount = GateCase.Create("t3-half-even-low", "3/2",
                GateContracts.Bytes(cases.Single(item => item.Id == "t3-half-even-even").Evidence));
            Assert.Throws<InvalidOperationException>(() => GateTokenOracle.Verify(sameAmount, selection));
            void RejectPrice(string id, ProjectChatUsage?[] usage, TariffInput input, int? scheduled = null)
            {
                var selected = cases.Single(item => item.Id == id);
                var wrong = PricingReport.Create(GateTokenCases.Journal(selection, usage, scheduled), GateTokenCases.Tariff(input));
                var rebound = GateCase.Create(id, selected.Selection, PricingJson.Write(wrong));
                Assert.Throws<InvalidOperationException>(() => GateTokenOracle.Verify(rebound, selection));
            }
            var aggregate = GateTokenCases.Input(miss: 5, unit: 1000, places: 2);
            RejectPrice("t3-aggregate", [GateTokenCases.Measured(0, 0, 0), GateTokenCases.Measured(2, 0, 0)], aggregate);
            RejectPrice("t3-aggregate", [GateTokenCases.Measured(2, 0, 0), GateTokenCases.Measured(0, 0, 0)], aggregate);
            RejectPrice("t3-aggregate", [GateTokenCases.Measured(1, 0, 0), GateTokenCases.Measured(1, 0, 0)], aggregate, scheduled: 3);
            RejectPrice("t3-unknown-zero-rate", [null, GateTokenCases.Measured(10, 4, 6)], GateTokenCases.Input(hit: 0, miss: 0, output: 0));
            RejectPrice("t3-zero-denominator", [GateTokenCases.Measured(0, 0, 0)], GateTokenCases.Input());
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
            Assert.NotEqual(selection.ReplaySha256, selection.GrowthSha256);
            var substituted = new List<GateCase>();
            GateComparisonCases.Run(substituted, selection with { ReplaySha256 = selection.GrowthSha256 }, root);
            foreach (var item in substituted)
                Assert.Throws<InvalidOperationException>(() => GateComparisonOracle.Verify(item, selection, cases.ToDictionary(value => value.Id)));
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
    [InlineData("c2-output65536", "None")]
    [InlineData("c2-cleanup", "None")]
    [InlineData("c2-unreaped", "None")]
    [InlineData("c2-hang", "Hang")]
    public async Task EconomicsOracleChecksActualWorkerAndFailureSemantics(string id, string fault)
    {
        var selection = GateContracts.Select(Fixtures);
        var root = ReplayProcess.CreatePrivateRoot();
        try
        {
            var tariff = Path.Combine(root, "tariff.json");
            File.WriteAllBytes(tariff, GateTokenCases.TariffBytes(GateTokenCases.Input(unit: 1_000_000, places: 6)));
            var plan = EconomicsCommand.Prepare(Path.Combine(Fixtures, "replay"), Path.Combine(Fixtures, "growth"), tariff,
                [new("replay", 3, 1, false)], childSeconds: id == "c2-hang" ? 5 : 30);
            if (id == "c2-output65536")
            {
                var per = plan.Bounds.PerCall with { MaxOutputTokens = 65_536 };
                plan = plan with
                {
                    Provider = plan.Provider with
                    {
                        AdapterId = DeepSeekAdapterContext.Output65536Adapter,
                        ConfigurationSha256 = LivePlanAdmission.ProviderConfigurationSha256(DeepSeekRequestProfile.Output65536),
                    },
                    Bounds = plan.Bounds with
                    {
                        PerCall = per,
                        MaxOutputTokens = plan.Bounds.MaxModelCalls * per.MaxOutputTokens,
                        MaxCombinedTokens = plan.Bounds.MaxModelCalls * (per.MaxInputTokens + per.MaxOutputTokens),
                    },
                };
            }
            var evidence = await GateEconomicsCases.RunCase(root, id, plan, Enum.Parse<EconomicsFault>(fault), index: id == "c2-hang" ? 0 : 1);
            var item = GateCase.Create(id, fault, JsonSerializer.SerializeToUtf8Bytes(evidence, GateJson.Default.GateEconomics));
            Assert.True(GateContracts.Safe(item.Evidence));
            var projection = GateEconomicsOracle.Verify(item, selection);
            if (id is "c2-replay" or "c2-cancel-after-usage")
            {
                var repeated = await GateEconomicsCases.RunCase(root, id, plan, Enum.Parse<EconomicsFault>(fault));
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
        var projected = GateHistoryOracle.Verify(item, selection);
        foreach (var phase in new[] { 5, 6 })
            foreach (var field in new[] { "epoch_sha256", "session_id_sha256" })
                Assert.Throws<InvalidOperationException>(() => GateHistoryOracle.Verify(
                    Rewrite(item, value => value["rows"]![phase]![field] = GateContracts.Hash('e')), selection));
        foreach (var phase in new[] { 0, 4, 7, 8 })
        {
            var wrong = Rewrite(item, value => value["rows"]![phase]!["agent_code"] = "UNLISTED_PRIVATE_PAYLOAD_279");
            Assert.True(GateContracts.Safe(wrong.Evidence));
            Assert.Throws<InvalidOperationException>(() => GateHistoryOracle.Verify(wrong, selection));
            Assert.Throws<InvalidOperationException>(() => GateHistoryOracle.Verify(
                Rewrite(item, value => value["rows"]![phase]!["model_calls"] = 1), selection));
        }
        foreach (var phase in new[] { 5, 6 })
            Assert.Throws<InvalidOperationException>(() => GateHistoryOracle.Verify(
                Rewrite(item, value => value["rows"]![phase]!["disposition"] = "UNLISTED_PRIVATE_PAYLOAD_279"), selection));
        var renamed = Rewrite(item, value =>
        {
            for (var phase = 0; phase < 9; phase++)
            {
                value["rows"]![phase]!["epoch_sha256"] = GateContracts.Hash(phase < 7 ? 'a' : 'b');
                value["rows"]![phase]!["session_id_sha256"] = GateContracts.Hash(phase < 7 ? 'c' : 'd');
            }
        });
        Assert.True(JsonElement.DeepEquals(projected, GateHistoryOracle.Verify(renamed, selection)));
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
        foreach (var id in new[] { "p2-replay", "p2-tools", "p2-continuation" })
        {
            var item = cases.Single(value => value.Id == id);
            var projected = GateHistoryOracle.Verify(item, selection);
            Assert.Throws<InvalidOperationException>(() => GateHistoryOracle.Verify(Rewrite(item, value =>
                ChangeDomain(value["rows"]![1]!["capture"]!, "session_sha256", GateContracts.Hash('e'))), selection));
            Assert.Throws<InvalidOperationException>(() => GateHistoryOracle.Verify(Rewrite(item, value =>
                ChangeDomain(value["rows"]![1]!["capture"]!, "stable_plan_sha256",
                    value["rows"]![0]!["capture"]!["baseline"]!["domain"]!["stable_plan_sha256"]!.GetValue<string>())), selection));
            var renamed = Rewrite(item, value =>
            {
                foreach (var row in value["rows"]!.AsArray()) ChangeDomain(row!["capture"]!, "session_sha256", GateContracts.Hash('e'));
            });
            Assert.True(JsonElement.DeepEquals(projected, GateHistoryOracle.Verify(renamed, selection)));
        }
        var repeated = GateCase.Create("p2-replay", "replay", HistoryJson.Write(await HistoryRunner.ReplayAsync(Path.Combine(Fixtures, "replay"))));
        Assert.True(JsonElement.DeepEquals(GateHistoryOracle.Verify(cases.Single(item => item.Id == "p2-replay"), selection),
            GateHistoryOracle.Verify(repeated, selection)));
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
            var dynamic = cases.Single(item => item.Id == "p1-dynamic-suffix");
            foreach (var field in new[] { "stable_plan_sha256", "session_sha256", "accepted_session_sha256" })
            {
                var wrong = Rewrite(dynamic, value =>
                {
                    foreach (var observation in value["observations"]!.AsArray())
                        observation!["domain"]![field] = "UNLISTED_PRIVATE_PAYLOAD_279";
                });
                Assert.True(GateContracts.Safe(wrong.Evidence));
                Assert.Throws<InvalidOperationException>(() => GatePrefixOracle.Verify(wrong, selection));
            }
            foreach (var generation in new long[] { -2, -1, long.MaxValue })
                Assert.Throws<InvalidOperationException>(() => GatePrefixOracle.Verify(Rewrite(dynamic, value =>
                {
                    foreach (var observation in value["observations"]!.AsArray()) observation!["domain"]!["generation"] = generation;
                }), selection));
            Assert.Throws<InvalidOperationException>(() => GatePrefixOracle.Verify(Rewrite(dynamic, value =>
            {
                foreach (var observation in value["observations"]!.AsArray()) observation!["domain"]!["accepted_session_sha256"] = null;
            }), selection));
        }
        finally { Assert.True(ReplayProcess.Cleanup(root)); }
    }

    private static GateCase Rewrite(GateCase item, Action<JsonNode> change)
    {
        var evidence = JsonNode.Parse(item.Evidence.GetRawText())!;
        change(evidence);
        return GateCase.Create(item.Id, item.Selection, Encoding.UTF8.GetBytes(evidence.ToJsonString()));
    }

    private static void ChangeDomain(JsonNode capture, string field, string value)
    {
        if (capture["baseline"] is { } baseline) baseline["domain"]![field] = value;
        foreach (var call in capture["calls"]!.AsArray()) if (call is not null) call["domain"]![field] = value;
    }
}
