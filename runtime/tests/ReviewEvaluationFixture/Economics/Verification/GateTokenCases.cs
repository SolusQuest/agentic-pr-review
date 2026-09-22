using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Accounting;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Contracts;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using static AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification.GateContracts;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;

internal static class GateTokenCases
{
    internal static void Run(List<GateCase> cases, GateSelection selection, string root)
    {
        void JournalCase(string id, string selected, UsageJournal journal) => cases.Add(GateCase.Create(id, selected, UsageJournalJson.Write(journal)));
        var parsed = Parse(10, 4, 6, 4, DeepSeekAdapterContext.Model);
        var alias = Parse(10, 4, 6, 4, "deepseek-flash");
        JournalCase("t1-partition", "10/4/6/4", Journal(selection, [parsed]));
        JournalCase("t1-alias", "documented-alias", Journal(selection, [alias]));
        JournalCase("t1-zero", "measured-zero", Journal(selection, [Parse(0, 0, 0, 0, DeepSeekAdapterContext.Model)]));
        JournalCase("t1-total-only", "optional-cache-absent", Journal(selection, [new(10, 4)]));
        Require(Response(10, 4, 6, 5, DeepSeekAdapterContext.Model).Outcome == DeepSeekResponseParseOutcome.Invalid);
        cases.Add(Scalar("t1-malformed-partition", "partition-mismatch", "parser_invalid", []));

        JournalCase("t2-known-failed", "known-after-failure", Journal(selection, [Measured(10, 4, 6)]));
        JournalCase("t2-unknown", "unknown-send", Journal(selection, [null]));
        var expected = Expected(selection, 1, maximumCalls: 1);
        var collector = new UsageJournalCollector(expected);
        var attempt = collector.BeginAttempt(0);
        attempt.AgentStarted();
        var first = attempt.BeginCall()!;
        first.Dispatch(); first.TransportFinished(DeepSeekTransportResult.Success([])); first.Returned(new(0, 0));
        var refusal = attempt.BeginCall()!;
        refusal.Refuse("budget_refused"); refusal.Threw(); attempt.AgentFinished(false); attempt.Finish("failed");
        JournalCase("t2-local-refusal", "reservation-refused", collector.Seal("bound_stop", Reservations(expected, 1)));
        JournalCase("t2-unattempted-tail", "cancelled-tail", Journal(selection, [Measured(10, 4, 6)], scheduled: 3));

        expected = Expected(selection, 1);
        collector = new(expected); attempt = collector.BeginAttempt(0); attempt.AgentStarted();
        for (var i = 0; i < 2; i++)
        {
            var call = attempt.BeginCall()!;
            call.Dispatch(); call.TransportFinished(DeepSeekTransportResult.Success([])); call.Returned(new(0, 0));
        }
        attempt.AgentFinished(false); attempt.Finish("failed");
        var original = collector.Seal("complete", Reservations(expected, 2)).Document;
        var calls = original.Calls.SetItem(0, original.Calls[0] with
        { TransportOutcome = "transport_failure", ChatOutcome = "threw", UsageStatus = "unknown", Usage = null });
        var invalid = original with { Calls = calls, Totals = UsageJournal.Totals(original.Attempts, calls) };
        var invalidBytes = JsonSerializer.SerializeToUtf8Bytes(invalid, UsageJournalJsonContext.Default.UsageJournalDocument);
        Require(UsageJournalJson.Read(invalidBytes) is null);
        cases.Add(GateCase.Create("t2-terminal-order", "send-after-terminal", invalidBytes));

        collector = new(expected); attempt = collector.BeginAttempt(0); attempt.AgentStarted();
        var late = attempt.BeginCall()!; late.Dispatch();
        attempt.Finish("failed", cancelled: true);
        var sealedJournal = collector.Seal("caller_cancelled", Reservations(expected, 1));
        var before = UsageJournalJson.Write(sealedJournal);
        late.TransportFinished(DeepSeekTransportResult.Success([])); late.Returned(Measured(10, 4, 6));
        attempt.AdmitEvaluation(Hash('d')); attempt.AgentFinished(true); attempt.Finish("completed");
        Require(before.AsSpan().SequenceEqual(UsageJournalJson.Write(collector.Seal("complete", Reservations(expected, 2)))) && attempt.BeginCall() is null);
        JournalCase("t2-late-finalization", "sealed-call", sealedJournal);

        void PriceCase(string id, string selected, UsageJournal journal, TariffInput? input = null)
        {
            var tariff = Tariff(input ?? Input());
            var price = PricingReport.Create(journal, tariff);
            cases.Add(GateCase.Create(id, selected, PricingJson.Write(price)));
        }
        var standard = Journal(selection, [Measured(10, 4, 6)]);
        PriceCase("t3-usd", "USD", standard);
        PriceCase("t3-cny", "CNY", standard, Input(currency: "CNY"));
        PriceCase("t3-half-even-low", "3/2", Journal(selection, [Measured(1, 0, 0)]), Input(miss: 3, unit: 2, places: 0));
        PriceCase("t3-half-even-even", "5/2", Journal(selection, [Measured(1, 0, 0)]), Input(miss: 5, unit: 2, places: 0));
        PriceCase("t3-half-even-high", "7/2", Journal(selection, [Measured(1, 0, 0)]), Input(miss: 7, unit: 2, places: 0));
        PriceCase("t3-aggregate", "two-calls-one-component", Journal(selection, [Measured(1, 0, 0), Measured(1, 0, 0)]), Input(miss: 5, unit: 1000, places: 2));
        PriceCase("t3-zero-denominator", "zero-input", Journal(selection, [new(0, 0)]));
        PriceCase("t3-missing-partition", "total-only", Journal(selection, [new(10, 4)]));
        PriceCase("t3-unknown-zero-rate", "unknown-zero-rate", Journal(selection, [Measured(10, 4, 6), null]), Input(hit: 0, miss: 0, output: 0));
        var overflow = false;
        try { _ = PricingReport.Create(Journal(selection, [Measured(long.MaxValue, 0, 0)]), Tariff(Input(miss: long.MaxValue, unit: 1))); }
        catch (OverflowException) { overflow = true; }
        Require(overflow);
        cases.Add(Scalar("t3-overflow", "unrepresentable", "arithmetic_overflow", []));

        var journalPath = Path.Combine(root, "pricing-journal.json");
        var tariffPath = Path.Combine(root, "pricing-tariff.json");
        File.WriteAllBytes(journalPath, UsageJournalJson.Write(standard));
        File.WriteAllBytes(tariffPath, TariffBytes(Input()));
        var json = GateCommandCapture.Run(() => PricingCommand.Invoke(["economics-price", "--journal", journalPath, "--tariff", tariffPath]));
        Require(PricingJson.Read(Encoding.UTF8.GetBytes(json))?.Document.ObservedUsage.TotalAmount == .42m);
        var markdown = GateCommandCapture.Run(() => PricingCommand.Invoke(["economics-price", "--journal", journalPath, "--tariff", tariffPath, "--format", "markdown"]));
        Require(markdown.Contains("0.42", StringComparison.Ordinal));
    }

    private static ProjectChatUsage Parse(long input, long output, long hit, long miss, string model)
    {
        var result = Response(input, output, hit, miss, model);
        Require(result.Outcome == DeepSeekResponseParseOutcome.Success);
        var response = result.Response!;
        return new(response.Usage.InputTokens, response.Usage.OutputTokens,
            new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, response.ResponseModel,
                response.Usage.CacheReadInputTokens, response.Usage.UncachedInputTokens));
    }

    private static DeepSeekResponseParseResult Response(long input, long output, long hit, long miss, string model)
    {
        // Private synthetic response; only the admitted usage projection escapes.
        var json = $$$"""{"model":"{{{model}}}","choices":[{"index":0,"finish_reason":"tool_calls","message":{"role":"assistant","content":"","reasoning_content":"{{{Canary}}}","tool_calls":[{"id":"finish-1","type":"function","function":{"name":"finish","arguments":"{}"}}]}}],"usage":{"prompt_tokens":{{{input}}},"completion_tokens":{{{output}}},"total_tokens":{{{input + output}}},"prompt_cache_hit_tokens":{{{hit}}},"prompt_cache_miss_tokens":{{{miss}}}}}""";
        return DeepSeekResponseParser.Parse(DeepSeekTransportResult.Success(Encoding.UTF8.GetBytes(json)));
    }

    internal static ProjectChatUsage Measured(long input, long output, long hit) => new(input, output,
        new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Model, hit, input - hit));

    internal static TariffInput Input(long hit = 1, long miss = 4, long output = 5, long unit = 100,
        int places = 3, string currency = "USD") => new(PricingLimits.TariffFormat,
        "https://example.invalid/r6-synthetic-tariff", "2026-09-20",
        new(PricingLimits.Formula, DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, null,
            "standard", "2026-09-20T12:00:00Z", new("known", "2026-09-20T00:00:00Z", "2026-09-21T00:00:00Z"),
            unit, 0, new(new(currency, hit), new(currency, miss), new(currency, output)),
            new("half_even", places, PricingLimits.Aggregation, PricingLimits.Normalization)));
    internal static byte[] TariffBytes(TariffInput input) => JsonSerializer.SerializeToUtf8Bytes(input, PricingJsonContext.Default.TariffInput);
    internal static AdmittedTariff Tariff(TariffInput input) => AdmittedTariff.Read(TariffBytes(input), out _) ?? throw new InvalidOperationException("r6_gate_tariff");

    internal static UsageJournal Journal(GateSelection selection, ProjectChatUsage?[] sends, int? scheduled = null)
    {
        var expected = Expected(selection, scheduled ?? Math.Max(1, sends.Length));
        var collector = new UsageJournalCollector(expected);
        var overBound = false;
        for (var i = 0; i < sends.Length; i++)
        {
            var attempt = collector.BeginAttempt(i); attempt.AgentStarted();
            var call = attempt.BeginCall()!; call.Dispatch();
            if (sends[i] is { } usage)
            {
                call.TransportFinished(DeepSeekTransportResult.Success([])); call.Returned(usage);
                overBound |= usage.InputTokens > expected.Bounds.PerCall.MaxInputTokens || usage.OutputTokens > expected.Bounds.PerCall.MaxOutputTokens;
            }
            else { call.TransportFinished(DeepSeekTransportResult.TransportFailure()); call.Threw(); }
            attempt.AgentFinished(false); attempt.Finish("failed");
        }
        return collector.Seal(overBound ? "accounting_violation" : sends.Length < expected.Schedule.Length ? "caller_cancelled" : "complete",
            Reservations(expected, sends.Length));
    }

    private static UsageJournalExpectation Expected(GateSelection selection, int count, int? maximumCalls = null)
    {
        var calls = count * 8;
        var plan = new LivePlanDigestInput(LiveLimits.PlanFormat, new(selection.SourceCommit, selection.SourceTree, selection.SourceClean),
            selection.ReplaySha256, new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model,
                DeepSeekAdapterContext.Adapter, LivePlanAdmission.ProviderConfigurationSha256()),
            Enumerable.Repeat("cs-safe", count).ToImmutableArray(),
            new(count, maximumCalls ?? calls, count * AgentLimits.InputTokens, count * AgentLimits.OutputTokens,
                count * AgentLimits.CombinedTokens, 120, calls * 1000,
                new(AgentLimits.InputTokens / 8, AgentLimits.OutputTokens / 8, 1000)));
        return new(new("r6-gate-tokens", plan.Source.Commit, plan.Source.Tree, plan.Source.Clean, "r6-gate", plan.CorpusSha256,
            plan.Provider.ConfigurationSha256, LivePlanAdmission.Digest(plan), "loopback"), plan);
    }
    private static UsageJournalReservations Reservations(UsageJournalExpectation expected, int calls) =>
        new(calls, calls * expected.Bounds.PerCall.MaxInputTokens, calls * expected.Bounds.PerCall.MaxOutputTokens,
            calls * (expected.Bounds.PerCall.MaxInputTokens + expected.Bounds.PerCall.MaxOutputTokens), calls * expected.Bounds.PerCall.MaxChargeMicroUsd);
}
