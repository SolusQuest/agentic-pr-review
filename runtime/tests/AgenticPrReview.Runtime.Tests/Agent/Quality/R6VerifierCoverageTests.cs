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
            l1-bootstrap-restored l1-dynamic-suffix l1-control l1-history l1-settings
            l1-continuation-position l1-continuation-order l1-domain l1-invalid-position
            p2-host-explicit-reset
            c1-descriptive c1-source-axis c1-fixed-mismatch c1-partial-population c1-effective-conditional
            c1-control-unsupported c1-prefix-bound c1-prefix-unbound c1-prefix-conflict
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
    public void HostileSubstitutionChangesEvidenceAfterTheResponseIdentityConverges()
    {
        var selection = GateContracts.Select(Fixtures);
        var root = ReplayProcess.CreatePrivateRoot();
        try
        {
            var cases = new List<GateCase>();
            GateTokenCases.Run(cases, selection, root);
            // The two canonical response probes now have equal evidence.
            // The hostile producer must still substitute a different artifact.
            Assert.Equal(GateContracts.Bytes(cases[0].Evidence), GateContracts.Bytes(cases[1].Evidence));
            GateTokenOracle.Verify(cases[0], selection);
            GateTokenOracle.Verify(cases[1], selection);
            var inventory = GateInventory.Cases.Select(wanted => cases.SingleOrDefault(item => item.Id == wanted.Id)
                ?? GateContracts.Scalar(wanted.Id, wanted.Selection, "unused", [])).ToArray();
            Assert.All(inventory, item => Assert.Equal(item.BindingSha256,
                GateCase.Binding(item.Id, item.Selection, item.Evidence)));
            var report = JsonSerializer.SerializeToUtf8Bytes(new GateReport(selection, [.. inventory]), GateJson.Default.GateReport);
            var mutatedBytes = GateMutations.Create(report, "substituted-case");
            var mutated = GateContracts.Read(mutatedBytes)!;
            var target = mutated.Cases[1];
            Assert.NotEqual(cases[1].Evidence.GetRawText(), target.Evidence.GetRawText());
            Assert.NotEqual(target.BindingSha256, GateCase.Binding(target.Id, target.Selection, target.Evidence));
            Assert.Throws<InvalidOperationException>(() => GateVerifier.Verify(mutatedBytes, selection));
            // Even rebinding the substituted evidence cannot satisfy the
            // independently authored token oracle for the selected probe.
            var rebound = GateCase.Create(target.Id, target.Selection, GateContracts.Bytes(target.Evidence));
            Assert.Throws<InvalidOperationException>(() => GateTokenOracle.Verify(rebound, selection));
        }
        finally { Assert.True(ReplayProcess.Cleanup(root)); }
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
            // The retained probe ID now carries the same selected response
            // identity. A former response spelling must still be rejected.
            var response = JsonNode.Parse(GateContracts.Bytes(cases.Single(item => item.Id == "t1-alias").Evidence))!;
            response["calls"]![0]!["usage"]!["cache"]!["response_model"] = "deepseek-v4-flash";
            var formerResponse = GateCase.Create("t1-partition", "10/4/6/4", Encoding.UTF8.GetBytes(response.ToJsonString()));
            Assert.Throws<InvalidOperationException>(() => GateTokenOracle.Verify(formerResponse, selection));
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

    [Fact]
    public async Task HostProbeReadsActualEncryptedResetGenerationAndFreshContinuation()
    {
        var selection = GateContracts.Select(Fixtures);
        var host = await GateHostCases.RunAsync(Path.Combine(Fixtures, "growth"), selection);
        var item = GateCase.Create("p2-host-explicit-reset", "production-host-explicit-reset",
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(host, GateJson.Default.GateHostReport));
        var projected = GateHistoryOracle.Verify(item, selection);
        foreach (var phase in new[] { 1, 2 })
            foreach (var field in new[] { "epoch_sha256", "session_id_sha256" })
                Assert.Throws<InvalidOperationException>(() => GateHistoryOracle.Verify(
                    Rewrite(item, value => value["rows"]![phase]![field] = GateContracts.Hash('e')), selection));
        foreach (var phase in new[] { 0, 1, 3, 4 })
        {
            var wrong = Rewrite(item, value => value["rows"]![phase]!["agent_code"] = "UNLISTED_PRIVATE_PAYLOAD_279");
            Assert.True(GateContracts.Safe(wrong.Evidence));
            Assert.Throws<InvalidOperationException>(() => GateHistoryOracle.Verify(wrong, selection));
            Assert.Throws<InvalidOperationException>(() => GateHistoryOracle.Verify(
                Rewrite(item, value => value["rows"]![phase]!["model_calls"] = 1), selection));
        }
        foreach (var phase in new[] { 1, 2 })
            Assert.Throws<InvalidOperationException>(() => GateHistoryOracle.Verify(
                Rewrite(item, value => value["rows"]![phase]!["disposition"] = "UNLISTED_PRIVATE_PAYLOAD_279"), selection));
        var renamed = Rewrite(item, value =>
        {
            for (var phase = 0; phase < 5; phase++)
            {
                value["rows"]![phase]!["epoch_sha256"] = GateContracts.Hash(phase < 3 ? 'a' : 'b');
                value["rows"]![phase]!["session_id_sha256"] = GateContracts.Hash(phase < 3 ? 'c' : 'd');
            }
        });
        Assert.True(JsonElement.DeepEquals(projected, GateHistoryOracle.Verify(renamed, selection)));
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
            var positions = cases.Single(item => item.Id == "l1-invalid-position");
            Assert.Throws<InvalidOperationException>(() => GatePrefixOracle.Verify(Rewrite(positions, value =>
            {
                value["code"] = "positions_rejected";
                value["values"] = new System.Text.Json.Nodes.JsonArray(4, 0);
                value["facts"] = new System.Text.Json.Nodes.JsonArray();
            }), selection));
            Assert.Throws<InvalidOperationException>(() => GatePrefixOracle.Verify(Rewrite(positions,
                value => value["facts"] = new System.Text.Json.Nodes.JsonArray()), selection));
            var dynamic = cases.Single(item => item.Id == "l1-dynamic-suffix");
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
            foreach (var mutation in new[] { "digest", "length", "domain", "old-provider-shape", "missing-domain" })
                Assert.Throws<InvalidOperationException>(() => GatePrefixOracle.Verify(Rewrite(dynamic, value =>
                {
                    var observation = value["observations"]![1]!;
                    if (mutation == "digest") observation["logical"]!["history"]!["sha256"] = new string('f', 64);
                    else if (mutation == "length") observation["logical"]!["history"]!["bytes"] = -1;
                    else if (mutation == "domain") observation["observation_domain"] = "apr-provider-prefix-v1";
                    else if (mutation == "missing-domain") observation.AsObject().Remove("observation_domain");
                    else observation["provider"] = observation["logical"]!.DeepClone();
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
