using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Accounting;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Contracts;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using FixtureProgram = AgenticPrReview.Runtime.ReviewEvaluationFixture.Program;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class R6PricingTests
{
    private const string Canary = "APR274_PRIVATE_CANARY";

    [Fact]
    public void PricesFailedAttemptUsageWithThreeRatesAndSeparateAllMissCounterfactual()
    {
        var report = Price(Journal([Measured(10, 4, 6)]));
        var observed = report.Document.ObservedUsage;
        Assert.Equal(0.22m, observed.KnownInputAmount);
        Assert.Equal(0.20m, observed.KnownOutputAmount);
        Assert.Equal(0.42m, observed.TotalAmount);
        Assert.Equal(0.022m, observed.InputAmountPerInputToken.Value);
        Assert.Equal(0.042m, observed.TotalAmountPerInputToken.Value);
        Assert.True(observed.TotalComplete);
        Assert.Equal(1, report.Document.Journal.Totals.Failed);
        Assert.Equal(0, report.Document.Journal.Totals.Completed);
        var allMiss = report.Document.SameTokenAllMiss;
        Assert.Equal(0.4m, allMiss.KnownInputAmount);
        Assert.Equal(0.6m, allMiss.TotalAmount);
        Assert.Equal(0.06m, allMiss.TotalAmountPerInputToken.Value);
        Assert.Equal("same_token_all_miss_counterfactual", allMiss.Kind);
        Assert.Equal("not_applicable", report.Document.Journal.CacheWriteBillingStatus);
        Assert.Equal("execution_time_unknown", report.Document.ExecutionTimeApplicability);
        Assert.Null(report.Document.ExecutionTimeTotalAmount);
        Assert.Equal("not_evidenced", report.Document.InvoiceStatus);
        Assert.Equal("not_exposed", report.Document.BackendSnapshotStatus);
    }

    [Fact]
    public void MissingPartitionPreservesOutputWithoutInventingMissTokens()
    {
        var report = Price(Journal([new(10, 4)]));
        var observed = report.Document.ObservedUsage;
        Assert.True(report.Document.Journal.Totals.UsageComplete);
        Assert.Null(report.Document.Journal.Totals.UncachedInputTokens);
        Assert.Null(observed.KnownInputAmount);
        Assert.Equal(0.20m, observed.KnownOutputAmount);
        Assert.Equal(0.20m, observed.KnownTotalSubtotal);
        Assert.False(observed.InputComplete);
        Assert.True(observed.OutputComplete);
        Assert.Null(observed.TotalAmount);
        Assert.Equal("amount_incomplete", observed.InputAmountPerInputToken.Availability);
        Assert.Equal(1, observed.Coverage.MissingCachePartitionSends);
        Assert.Equal(0.6m, report.Document.SameTokenAllMiss.TotalAmount);
        Assert.True(report.Document.SameTokenAllMiss.TotalComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownSendKeepsKnownSubtotalAndNeverBecomesCompleteEvenAtZeroRates(bool zeroRates)
    {
        var tariff = zeroRates ? Input(hit: 0, miss: 0, output: 0) : Input();
        var report = Price(Journal([Measured(10, 4, 6), null]), tariff);
        Assert.Equal(2, report.Document.Journal.Totals.ActualSends);
        foreach (var view in new[] { report.Document.ObservedUsage, report.Document.SameTokenAllMiss })
        {
            Assert.NotNull(view.KnownTotalSubtotal);
            Assert.False(view.TotalComplete);
            Assert.Null(view.TotalAmount);
            Assert.Equal(1, view.Coverage.UnknownUsageSends);
            Assert.Equal("input_denominator_incomplete", view.InputAmountPerInputToken.Availability);
            Assert.Null(view.InputAmountPerInputToken.Value);
            Assert.Null(view.InputAmountPerInputToken.InputTokens);
            Assert.Null(view.TotalAmountPerInputToken.Value);
        }
        Assert.Equal(zeroRates ? 0 : 0.42m, report.Document.ObservedUsage.KnownTotalSubtotal);
    }

    [Fact]
    public void UnknownOnlyHasUnavailableAmountsRatherThanMeasuredZeros()
    {
        var report = Price(Journal([null]), Input(hit: 0, miss: 0, output: 0));
        var view = report.Document.ObservedUsage;
        Assert.Null(view.KnownInputAmount);
        Assert.Null(view.KnownOutputAmount);
        Assert.Null(view.KnownTotalSubtotal);
        Assert.Null(view.TotalAmount);
        Assert.False(view.TotalComplete);
        Assert.Equal(0, view.Coverage.PricedOutputSends);
    }

    [Fact]
    public void ZeroInputStillPricesNonzeroOutputWithoutInventingACacheObservation()
    {
        var report = Price(Journal([new(0, 7)]));
        Assert.Equal(0m, report.Document.ObservedUsage.KnownInputAmount);
        Assert.Equal(0.35m, report.Document.ObservedUsage.TotalAmount);
        Assert.Null(report.Document.Journal.Totals.CacheReadInputTokens);
        Assert.Equal("zero_input_denominator", report.Document.ObservedUsage.TotalAmountPerInputToken.Availability);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroInputAndEmptyTrafficHaveZeroArithmeticButNoNormalizedRate(bool noSends)
    {
        var journal = Journal(noSends ? [] : [new ProjectChatUsage(0, 0)]);
        var report = Price(journal);
        foreach (var view in new[] { report.Document.ObservedUsage, report.Document.SameTokenAllMiss })
        {
            Assert.Equal(0m, view.KnownInputAmount);
            Assert.Equal(0m, view.KnownOutputAmount);
            Assert.Equal(0m, view.TotalAmount);
            Assert.True(view.TotalComplete);
            Assert.Equal("zero_input_denominator", view.TotalAmountPerInputToken.Availability);
            Assert.Null(view.TotalAmountPerInputToken.Value);
        }
        Assert.Equal(noSends ? 1 : 0, report.Document.Journal.Totals.Unattempted);
        Assert.Null(report.Document.Journal.Totals.CacheReadInputTokens);
    }

    [Theory]
    [InlineData(3, 2)]
    [InlineData(5, 2)]
    [InlineData(7, 4)]
    public void HalfEvenUsesExactMidpointParity(long rate, decimal expected)
    {
        var report = Price(Journal([Measured(1, 0, 0)]), Input(miss: rate, unit: 2, places: 0));
        Assert.Equal(expected, report.Document.ObservedUsage.TotalAmount);
    }

    [Fact]
    public void RoundsAfterCampaignAggregationAndNormalizesTheExactAmount()
    {
        var tariff = Input(miss: 5, unit: 1000, places: 2);
        var single = Price(Journal([Measured(1, 0, 0)]), tariff).Document.ObservedUsage;
        var split = Price(Journal([Measured(1, 0, 0), Measured(1, 0, 0)]), tariff).Document.ObservedUsage;
        var combined = Price(Journal([Measured(2, 0, 0)]), tariff).Document.ObservedUsage;
        Assert.Equal(0m, single.TotalAmount);
        Assert.Equal(0.01m, split.TotalAmount);
        Assert.Equal(combined.TotalAmount, split.TotalAmount);
        Assert.Equal(combined.TotalAmountPerInputToken, split.TotalAmountPerInputToken);
        // 0.014 rounds to 0.01; the exact normalized rate is 0.007 -> 0.01,
        // whereas normalizing the displayed 0.01 would tie-round to 0.00.
        var exact = Price(Journal([Measured(2, 0, 0)]), Input(miss: 7, unit: 1000, places: 2));
        Assert.Equal(0.01m, exact.Document.ObservedUsage.KnownInputAmount);
        Assert.Equal(0.01m, exact.Document.ObservedUsage.InputAmountPerInputToken.Value);
    }

    [Fact]
    public void SupportsNonDecimalUnitsAndDeclaredScalingWithoutPrematureUnderflow()
    {
        Assert.Equal(0.33m, Price(Journal([Measured(1, 0, 0)]),
            Input(miss: 1, unit: 3, places: 2)).Document.ObservedUsage.TotalAmount);
        Assert.Equal(0.042m, Price(Journal([Measured(10, 4, 6)]),
            Input(ratePlaces: 1, places: 4)).Document.ObservedUsage.TotalAmount);
        var tiny = Price(Journal([Measured(1, 0, 0)]),
            Input(miss: 1, unit: 1_000_000_000, ratePlaces: 12, places: 12));
        Assert.Equal(1, tiny.Document.Tariff.Terms.Rates.CacheMissInput.Units);
        Assert.Equal(0m, tiny.Document.ObservedUsage.TotalAmount);
        Assert.Contains("Values below the declared quantum can round to zero", PricingMarkdown.Write(tiny));
    }

    [Fact]
    public void EqualRatesAndAllMissCasesHaveTheSameReferenceAndCounterfactualAmounts()
    {
        foreach (var hit in new long[] { 0, 4, 10 })
        {
            var report = Price(Journal([Measured(10, 4, hit)]), Input(hit: 4));
            Assert.Equal(report.Document.ObservedUsage.TotalAmount, report.Document.SameTokenAllMiss.TotalAmount);
        }
        var allMiss = Price(Journal([Measured(10, 4, 0)]));
        Assert.Equal(allMiss.Document.ObservedUsage.TotalAmount, allMiss.Document.SameTokenAllMiss.TotalAmount);
        Assert.Equal(0.3m, Price(Journal([Measured(10, 4, 10)])).Document.ObservedUsage.TotalAmount);
    }

    [Fact]
    public void RemovesTrailingScaleBeforeExactDecimalConversion()
    {
        var report = Price(Journal([Measured(1_000_000_000_000_000_000, 0, 0)]),
            Input(miss: 1_000_000_000_000_000_000, unit: 1_000_000_000, places: 12));
        Assert.Equal(1_000_000_000_000_000_000_000_000_000m, report.Document.ObservedUsage.TotalAmount);
    }

    [Fact]
    public void RejectsUnrepresentableAmountInsteadOfWrappingOrSubstitutingZero()
    {
        var journal = Journal([Measured(long.MaxValue, 0, 0)]);
        Assert.Throws<OverflowException>(() => Price(journal, Input(miss: long.MaxValue, unit: 1)));
    }

    [Fact]
    public void RejectsFractionLossWhenIndividuallyRepresentableComponentsCannotBeSummedExactly()
    {
        var journal = Journal([Measured(long.MaxValue - 1, 1, 0)]);
        const long miss = 8_589_934_592; // 2^33
        const long output = 17_179_869_184; // 2^34
        Assert.NotNull(Price(journal, Input(miss: miss, output: 0, unit: 1, ratePlaces: 1, places: 1)));
        Assert.NotNull(Price(journal, Input(miss: 0, output: output, unit: 1, ratePlaces: 1, places: 1)));
        // Total coefficient is exactly 2^96 at scale 1. Decimal addition can
        // silently reduce its scale; the declared one-place amount must fail.
        Assert.Throws<OverflowException>(() => Price(journal,
            Input(miss: miss, output: output, unit: 1, ratePlaces: 1, places: 1)));
    }

    [Theory]
    [InlineData("2026-09-19T23:59:59Z", "reference_period_mismatch")]
    [InlineData("2026-09-20T00:00:00Z", "reference_period_match")]
    [InlineData("2026-09-20T23:59:59Z", "reference_period_match")]
    [InlineData("2026-09-21T00:00:00Z", "reference_period_mismatch")]
    public void ReferencePeriodIsHalfOpenAndNeverInventsExecutionTime(string at, string expected)
    {
        var input = Input();
        var report = Price(Journal([Measured(10, 4, 6)]), input with { Terms = input.Terms with { ReferenceAt = at } });
        Assert.Equal(expected, report.Document.ReferenceApplicability.Period);
        Assert.Equal(0.42m, report.Document.ObservedUsage.TotalAmount);
        Assert.Null(report.Document.ExecutionTimeTotalAmount);
        Assert.Equal("execution_time_unknown", report.Document.ExecutionTimeApplicability);
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("peak")]
    [InlineData("off_peak")]
    [InlineData("unknown")]
    public void PriceClassAndUnknownPeriodAreExplicitReferenceMetadata(string priceClass)
    {
        var input = Input();
        var report = Price(Journal([Measured(10, 4, 6)]), input with
        {
            Terms = input.Terms with { PriceClass = priceClass, EffectivePeriod = new("unknown", null, null) },
        });
        Assert.Equal("effective_period_unknown", report.Document.ReferenceApplicability.Period);
        Assert.Equal(priceClass == "unknown" ? "price_class_unknown" : "reference_class_declared",
            report.Document.ReferenceApplicability.PriceClass);
        Assert.True(report.Document.ObservedUsage.TotalComplete);
        Assert.Null(report.Document.ExecutionTimeTotalAmount);
    }

    [Fact]
    public void ResponseModelRestrictionWithholdsOnlyUnprovenOrMismatchedCalls()
    {
        var input = Input();
        input = input with { Terms = input.Terms with { ResponseModel = "deepseek-flash" } };
        var report = Price(Journal([Measured(10, 4, 6, "deepseek-flash"), Measured(10, 4, 6), new(10, 4)]), input);
        foreach (var view in new[] { report.Document.ObservedUsage, report.Document.SameTokenAllMiss })
        {
            Assert.Equal(1, view.Coverage.ModelUnknownSends);
            Assert.Equal(1, view.Coverage.ModelMismatchSends);
            Assert.Equal(1, view.Coverage.PricedOutputSends);
            Assert.Equal(0.2m, view.KnownOutputAmount);
            Assert.Null(view.TotalAmount);
            Assert.Equal("amount_incomplete", view.TotalAmountPerInputToken.Availability);
        }
        Assert.Equal(0.42m, report.Document.ObservedUsage.KnownTotalSubtotal);
        Assert.Equal("not_exposed", report.Document.BackendSnapshotStatus);
    }

    [Fact]
    public void ReservationsAreNotPricesAndDoNotAdoptTheTariffCurrency()
    {
        var first = Price(Journal([Measured(10, 4, 6)], charge: 1000), Input(currency: "CNY"));
        var second = Price(Journal([Measured(10, 4, 6)], charge: 2000), Input(currency: "CNY"));
        Assert.Equal(first.Document.ObservedUsage, second.Document.ObservedUsage);
        Assert.Equal("CNY", first.Document.ObservedUsage.Currency);
        Assert.Equal(1000, first.Document.Journal.Reservations.SpendMicroUsd);
        Assert.Equal(2000, second.Document.Journal.Reservations.SpendMicroUsd);
        Assert.Contains("1000 micro-USD", PricingMarkdown.Write(first));
        Assert.Contains("Currency: CNY", PricingMarkdown.Write(first));
    }

    [Fact]
    public void LocalRefusalsAndUnattemptedTailRemainVisibleWithoutExtraCharges()
    {
        var expected = Expected(3, maximumCalls: 1);
        var collector = new UsageJournalCollector(expected);
        var first = collector.BeginAttempt(0);
        first.AgentStarted();
        var sent = first.BeginCall()!;
        sent.Dispatch(); sent.TransportFinished(DeepSeekTransportResult.Success([])); sent.Returned(Measured(10, 4, 6));
        first.AgentFinished(false); first.Finish("failed");
        var second = collector.BeginAttempt(1);
        second.AgentStarted();
        var refused = second.BeginCall()!;
        refused.Refuse("budget_refused"); refused.Threw();
        second.AgentFinished(false); second.Finish("failed");
        var report = Price(collector.Seal("bound_stop", Reservations(expected, 1)));
        Assert.Equal(0.42m, report.Document.ObservedUsage.TotalAmount);
        Assert.Equal(3, report.Document.Journal.Totals.Scheduled);
        Assert.Equal(2, report.Document.Journal.Totals.Failed);
        Assert.Equal(1, report.Document.Journal.Totals.Unattempted);
        Assert.Equal(1, report.Document.Journal.Totals.LocalRefusals);
        Assert.NotNull(PricingJson.Read(PricingJson.Write(report)));
    }

    [Fact]
    public void MaximumAdmittedPopulationRetainsCountsAboveInt64AndFitsTheReportCap()
    {
        var expected = Expected(256);
        var collector = new UsageJournalCollector(expected);
        for (var i = 0; i < 256; i++)
        {
            var attempt = collector.BeginAttempt(i);
            attempt.AgentStarted();
            for (var j = 0; j < 8; j++)
            {
                var call = attempt.BeginCall()!;
                call.Dispatch(); call.TransportFinished(DeepSeekTransportResult.Success([]));
                call.Returned(Measured(i == 255 && j == 7 ? long.MaxValue : 1, 0, 0));
            }
            attempt.AgentFinished(false); attempt.Finish("failed");
        }
        var report = Price(collector.Seal("accounting_violation", Reservations(expected, 2048)),
            Input(hit: 0, miss: 0, output: 0));
        Assert.Equal((decimal)long.MaxValue + 2047, report.Document.Journal.Totals.KnownInputTokens);
        Assert.Equal(2048, report.Document.ObservedUsage.Coverage.PricedInputSends);
        Assert.Equal(0m, report.Document.ObservedUsage.TotalAmount);
        var bytes = PricingJson.Write(report);
        Assert.True(bytes.Length < PricingLimits.ReportBytes);
        Assert.NotNull(PricingJson.Read(bytes));
    }

    [Theory]
    [InlineData("currency_mismatch", "r6_pricing_tariff_currency_mismatch")]
    [InlineData("currency_unsupported", "r6_pricing_tariff_currency_unsupported")]
    [InlineData("provider", "r6_pricing_tariff_provider_unsupported")]
    [InlineData("requested_model", "r6_pricing_tariff_model_mismatch")]
    [InlineData("response_model", "r6_pricing_tariff_model_mismatch")]
    [InlineData("formula", "r6_pricing_tariff_formula_unsupported")]
    [InlineData("price_class", "r6_pricing_tariff_price_class_unsupported")]
    [InlineData("rounding", "r6_pricing_tariff_arithmetic_unsupported")]
    [InlineData("aggregation", "r6_pricing_tariff_arithmetic_unsupported")]
    [InlineData("normalization", "r6_pricing_tariff_arithmetic_unsupported")]
    public void UnsupportedOrMixedTariffsHaveExplicitFixedDispositions(string fault, string expected)
    {
        var input = Input();
        var terms = input.Terms;
        terms = fault switch
        {
            "currency_mismatch" => terms with { Rates = terms.Rates with { Output = new("CNY", 5) } },
            "currency_unsupported" => Input(currency: "EUR").Terms,
            "provider" => terms with { ProviderId = "other-provider" },
            "requested_model" => terms with { RequestedModel = "other-model" },
            "response_model" => terms with { ResponseModel = "unobserved-backend" },
            "formula" => terms with { Formula = "tiered_with_cache_write" },
            "price_class" => terms with { PriceClass = "automatic" },
            "rounding" => terms with { Arithmetic = terms.Arithmetic with { Rounding = "away_from_zero" } },
            "aggregation" => terms with { Arithmetic = terms.Arithmetic with { Aggregation = "per_call" } },
            _ => terms with { Arithmetic = terms.Arithmetic with { Normalization = "rounded_amount" } },
        };
        Assert.Null(AdmittedTariff.Read(TariffBytes(input with { Terms = terms }), out var error));
        Assert.Equal(expected, error);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("zero_unit")]
    [InlineData("unit_limit")]
    [InlineData("negative_scale")]
    [InlineData("rate_scale_limit")]
    [InlineData("output_scale_limit")]
    [InlineData("currency_shape")]
    [InlineData("reversed_period")]
    [InlineData("empty_period")]
    [InlineData("unknown_with_dates")]
    [InlineData("known_without_dates")]
    [InlineData("period_status")]
    [InlineData("invalid_reference")]
    [InlineData("offset_reference")]
    [InlineData("invalid_retrieval")]
    [InlineData("wrong_format")]
    [InlineData("null_terms")]
    [InlineData("null_rates")]
    [InlineData("null_rate")]
    [InlineData("null_arithmetic")]
    public void ContradictoryAndUnboundedTermsAreInvalidInputs(string fault)
    {
        var input = JsonNode.Parse(TariffBytes(Input()))!;
        var terms = input["terms"]!;
        switch (fault)
        {
            case "negative": terms["rates"]!["output"]!["units"] = -1; break;
            case "zero_unit": terms["token_unit"] = 0; break;
            case "unit_limit": terms["token_unit"] = 1_000_000_001; break;
            case "negative_scale": terms["rate_decimal_places"] = -1; break;
            case "rate_scale_limit": terms["rate_decimal_places"] = 13; break;
            case "output_scale_limit": terms["arithmetic"]!["decimal_places"] = 13; break;
            case "currency_shape": terms["rates"]!["output"]!["currency"] = "usd"; break;
            case "reversed_period": terms["effective_period"]!["from_inclusive"] = "2026-09-22T00:00:00Z"; break;
            case "empty_period": terms["effective_period"]!["from_inclusive"] = "2026-09-21T00:00:00Z"; break;
            case "unknown_with_dates": terms["effective_period"]!["status"] = "unknown"; break;
            case "known_without_dates": terms["effective_period"]!["from_inclusive"] = null; break;
            case "period_status": terms["effective_period"]!["status"] = "inferred"; break;
            case "invalid_reference": terms["reference_at"] = "2026-02-30T00:00:00Z"; break;
            case "offset_reference": terms["reference_at"] = "2026-09-20T12:00:00+00:00"; break;
            case "invalid_retrieval": input["retrieved_on"] = "2026-02-30"; break;
            case "wrong_format": input["format"] = "other"; break;
            case "null_terms": input["terms"] = null; break;
            case "null_rates": terms["rates"] = null; break;
            case "null_rate": terms["rates"]!["output"] = null; break;
            case "null_arithmetic": terms["arithmetic"] = null; break;
        }
        Assert.Null(AdmittedTariff.Read(Encoding.UTF8.GetBytes(input.ToJsonString()), out var error));
        Assert.Equal("r6_pricing_tariff_invalid", error);
    }

    [Theory]
    [InlineData("unknown_field")]
    [InlineData("missing_nullable")]
    [InlineData("duplicate")]
    [InlineData("fraction")]
    [InlineData("tiny_fraction")]
    [InlineData("too_precise")]
    [InlineData("integer_overflow")]
    [InlineData("string_rate")]
    [InlineData("comment")]
    [InlineData("trailing_document")]
    [InlineData("depth")]
    public void StrictTariffCodecRejectsAmbiguousOrLossyNumericInput(string fault)
    {
        var json = Encoding.UTF8.GetString(TariffBytes(Input()));
        json = fault switch
        {
            "unknown_field" => json.Insert(1, "\"tax\":1,"),
            "missing_nullable" => json.Replace("\"response_model\":null,", "", StringComparison.Ordinal),
            "duplicate" => json.Insert(1, "\"format\":\"apr.r6.tariff.v1\","),
            "fraction" => json.Replace("\"units\":1", "\"units\":1.1", StringComparison.Ordinal),
            "tiny_fraction" => json.Replace("\"units\":1", "\"units\":0.0000000000000000000000000000001", StringComparison.Ordinal),
            "too_precise" => json.Replace("\"units\":1", "\"units\":1.000000000000000000000000000001", StringComparison.Ordinal),
            "integer_overflow" => json.Replace("\"units\":1", "\"units\":9223372036854775808", StringComparison.Ordinal),
            "string_rate" => json.Replace("\"units\":1", "\"units\":\"1\"", StringComparison.Ordinal),
            "comment" => "/* comment */" + json,
            "trailing_document" => json + "{}",
            _ => new string('[', 20) + json + new string(']', 20),
        };
        Assert.Null(AdmittedTariff.Read(Encoding.UTF8.GetBytes(json), out _));
    }

    [Theory]
    [InlineData("http://api-docs.deepseek.com/pricing")]
    [InlineData("https://user:APR274_PRIVATE_CANARY@api-docs.deepseek.com/pricing")]
    [InlineData("https://api-docs.deepseek.com/pricing?key=APR274_PRIVATE_CANARY")]
    [InlineData("https://api-docs.deepseek.com/pricing#APR274_PRIVATE_CANARY")]
    [InlineData("https://api-docs.deepseek.com:8443/pricing")]
    [InlineData("file:///private/secret")]
    [InlineData("https://api-docs.deepseek.com/\nprivate")]
    public void UnsafeSourceUrlsRejectWithoutBecomingFetchInstructions(string url) =>
        Assert.Null(AdmittedTariff.Read(TariffBytes(Input() with { SourceUrl = url }), out _));

    [Fact]
    public void InputAndReportByteCapsAndStrictUtf8AreEnforced()
    {
        var tariff = TariffBytes(Input());
        Assert.NotNull(AdmittedTariff.Read(Pad(tariff, PricingLimits.TariffBytes), out _));
        Assert.Null(AdmittedTariff.Read(Pad(tariff, PricingLimits.TariffBytes + 1), out _));
        Assert.Null(AdmittedTariff.Read([0xc3, 0x28], out _));
        Assert.Null(PricingJson.Read([0xc3, 0x28]));
        var report = PricingJson.Write(Price(Journal([Measured(10, 4, 6)])));
        Assert.NotNull(PricingJson.Read(Pad(report, PricingLimits.ReportBytes)));
        Assert.Null(PricingJson.Read(Pad(report, PricingLimits.ReportBytes + 1)));
        Assert.Null(AdmittedTariff.Read(TariffBytes(Input() with
        {
            SourceUrl = "https://api-docs.deepseek.com/" + new string('a', 2048),
        }), out _));
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("amount_precision")]
    [InlineData("coverage")]
    [InlineData("complete")]
    [InlineData("applicability")]
    [InlineData("execution_price")]
    [InlineData("invoice")]
    [InlineData("tariff_rate")]
    [InlineData("url_hash")]
    [InlineData("journal")]
    [InlineData("journal_totals")]
    [InlineData("missing_nullable")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("trailing")]
    public void StandaloneReportReaderRecomputesEveryBoundInputAndDerivedClaim(string fault)
    {
        var report = Price(Journal([Measured(10, 4, 6)]));
        var json = JsonNode.Parse(PricingJson.Write(report))!;
        switch (fault)
        {
            case "amount": json["observed_usage"]!["known_input_amount"] = 0.23m; break;
            case "amount_precision":
                json["observed_usage"]!["known_input_amount"] = JsonNode.Parse("0.220000000000000000000000000001"); break;
            case "coverage": json["observed_usage"]!["coverage"]!["priced_input_sends"] = 2; break;
            case "complete": json["observed_usage"]!["total_complete"] = false; break;
            case "applicability": json["reference_applicability"]!["period"] = "reference_period_mismatch"; break;
            case "execution_price": json["execution_time_total_amount"] = 0.42m; break;
            case "invoice": json["invoice_status"] = "verified"; break;
            case "tariff_rate": json["tariff"]!["terms"]!["rates"]!["cache_hit_input"]!["units"] = 2; break;
            case "url_hash": json["tariff"]!["source_url_sha256"] = new string('d', 64); break;
            case "journal":
                json["journal"] = JsonSerializer.SerializeToNode(Journal([Measured(11, 4, 6)]).Document,
                    UsageJournalJsonContext.Default.UsageJournalDocument); break;
            case "journal_totals": json["journal"]!["totals"]!["unknown_usage_sends"] = 1; break;
            case "missing_nullable": json.AsObject().Remove("execution_time_total_amount"); break;
            case "extra": json["private_path"] = Canary; break;
        }
        var text = json.ToJsonString();
        if (fault == "duplicate") text = text.Insert(1, "\"format\":\"apr.r6.priced-usage.v1\",");
        if (fault == "trailing") text += "{}";
        Assert.Null(PricingJson.Read(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void FullPopulationHashAndIndependentMatchAreStrongerThanProvenanceEquality()
    {
        var a = Journal([Measured(10, 4, 6)]);
        var b = Journal([Measured(11, 4, 6)]);
        var tariff = Tariff(Input());
        var first = PricingReport.Create(a, tariff);
        var second = PricingReport.Create(b, tariff);
        Assert.Equal(a.Document.BindingSha256, b.Document.BindingSha256);
        Assert.NotEqual(first.Document.JournalSha256, second.Document.JournalSha256);
        var read = Assert.IsType<PricingReport>(PricingJson.Read(PricingJson.Write(first)));
        Assert.True(read.Matches(a, tariff));
        Assert.False(read.Matches(b, tariff));
        Assert.False(read.Matches(a, Tariff(Input(miss: 5))));
    }

    [Fact]
    public async Task ActualCommandProducesStandalonePublicJsonAndMarkdownWithoutAProviderKey()
    {
        byte[] reportBytes;
        var oldCulture = CultureInfo.CurrentCulture;
        var oldKey = Environment.GetEnvironmentVariable(LiveEnvironmentSecretSource.ProviderVariable);
        try
        {
            Environment.SetEnvironmentVariable(LiveEnvironmentSecretSource.ProviderVariable, null);
            using (var files = new InputFiles(Journal([Measured(10, 4, 6), null]),
                Input() with { SourceUrl = "https://api-docs.deepseek.com/" + Canary }))
            {
                var first = await Command(["economics-price", "--journal", files.JournalPath, "--tariff", files.TariffPath]);
                Assert.Equal(0, first.Exit);
                Assert.Empty(first.Error);
                Assert.DoesNotContain(Canary, first.Output, StringComparison.Ordinal);
                reportBytes = Encoding.UTF8.GetBytes(first.Output);
                Environment.SetEnvironmentVariable(LiveEnvironmentSecretSource.ProviderVariable, Canary);
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                var repeated = await Command(["economics-price", "--tariff", files.TariffPath, "--journal", files.JournalPath]);
                Assert.Equal(first.Output, repeated.Output);
                Assert.Equal(Canary, Environment.GetEnvironmentVariable(LiveEnvironmentSecretSource.ProviderVariable));
                var markdown = await Command(["economics-price", "--journal", files.JournalPath, "--tariff", files.TariffPath,
                    "--format", "markdown"]);
                Assert.Equal(0, markdown.Exit);
                Assert.Empty(markdown.Error);
                Assert.Contains("| Combined | 0.42 | no |", markdown.Output);
                Assert.Contains("Complete total: unavailable", markdown.Output);
                Assert.Contains("not another execution", markdown.Output);
                Assert.DoesNotContain(Canary, markdown.Output, StringComparison.Ordinal);
            }
            // Both original files (including their private path and raw URL) are gone.
            var read = Assert.IsType<PricingReport>(PricingJson.Read(reportBytes));
            Assert.Equal(2, read.Document.Journal.Totals.Failed);
            Assert.Equal(0.42m, read.Document.ObservedUsage.KnownTotalSubtotal);
            Assert.Null(read.Document.ObservedUsage.TotalAmount);
        }
        finally
        {
            CultureInfo.CurrentCulture = oldCulture;
            Environment.SetEnvironmentVariable(LiveEnvironmentSecretSource.ProviderVariable, oldKey);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("format")]
    [InlineData("empty")]
    public async Task CommandRejectsMalformedArgumentsWithoutEchoingPaths(string fault)
    {
        using var files = new InputFiles(Journal([]), Input());
        string[] args = fault switch
        {
            "missing" => ["economics-price"],
            "duplicate" => ["economics-price", "--journal", files.JournalPath, "--journal", files.JournalPath],
            "unknown" => ["economics-price", "--journal", files.JournalPath, "--secret", Canary],
            "format" => ["economics-price", "--journal", files.JournalPath, "--tariff", files.TariffPath, "--format", Canary],
            _ => ["economics-price", "--journal", "", "--tariff", files.TariffPath],
        };
        var result = await Command(args);
        Assert.Equal(2, result.Exit);
        Assert.Empty(result.Output);
        Assert.Equal("r6_pricing_arguments_invalid" + Environment.NewLine, result.Error);
        Assert.DoesNotContain(Canary, result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("journal_missing", "r6_pricing_journal_invalid", 2)]
    [InlineData("journal_oversize", "r6_pricing_journal_invalid", 2)]
    [InlineData("journal_invalid", "r6_pricing_journal_invalid", 2)]
    [InlineData("tariff_missing", "r6_pricing_tariff_invalid", 2)]
    [InlineData("tariff_oversize", "r6_pricing_tariff_invalid", 2)]
    [InlineData("tariff_unsupported", "r6_pricing_tariff_currency_unsupported", 2)]
    [InlineData("overflow", "r6_pricing_arithmetic_overflow", 1)]
    public async Task CommandFailuresAreBoundedFixedDiagnosticsWithNoPartialPriceReport(string fault, string error, int exit)
    {
        using var files = new InputFiles(fault == "overflow" ? Journal([Measured(long.MaxValue, 0, 0)]) : Journal([]),
            fault == "overflow" ? Input(miss: long.MaxValue, unit: 1) : Input());
        switch (fault)
        {
            case "journal_missing": File.Delete(files.JournalPath); break;
            case "journal_oversize": using (var s = File.OpenWrite(files.JournalPath)) s.SetLength(UsageJournalLimits.JsonBytes + 1); break;
            case "journal_invalid": File.WriteAllText(files.JournalPath, "{\"private\":\"" + Canary + "\"}"); break;
            case "tariff_missing": File.Delete(files.TariffPath); break;
            case "tariff_oversize": using (var s = File.OpenWrite(files.TariffPath)) s.SetLength(PricingLimits.TariffBytes + 1); break;
            case "tariff_unsupported": File.WriteAllBytes(files.TariffPath, TariffBytes(Input(currency: "EUR"))); break;
        }
        var result = await Command(["economics-price", "--journal", files.JournalPath, "--tariff", files.TariffPath]);
        Assert.Equal(exit, result.Exit);
        Assert.Empty(result.Output);
        Assert.Equal(error + Environment.NewLine, result.Error);
        Assert.DoesNotContain(Canary, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutputFailureReturnsOnlyTheFixedPricingInfrastructureCode()
    {
        using var files = new InputFiles(Journal([]), Input());
        var oldOut = Console.Out;
        var oldError = Console.Error;
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(new FailingWriter()); Console.SetError(error);
            Assert.Equal(1, await FixtureProgram.Main(["economics-price", "--journal", files.JournalPath, "--tariff", files.TariffPath]));
            Assert.Equal("r6_pricing_infrastructure_failed" + Environment.NewLine, error.ToString());
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
    }

    private sealed class FailingWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException(Canary);
    }

    private static byte[] Pad(byte[] input, int count)
    {
        var bytes = Enumerable.Repeat((byte)' ', count).ToArray();
        input.CopyTo(bytes, 0);
        return bytes;
    }

    private static async Task<(int Exit, string Output, string Error)> Command(string[] args)
    {
        var oldOut = Console.Out;
        var oldError = Console.Error;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output); Console.SetError(error);
            var exit = await FixtureProgram.Main(args);
            return (exit, output.ToString(), error.ToString());
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
    }

    private sealed class InputFiles : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), Canary + "-" + Guid.NewGuid().ToString("N"));
        internal string JournalPath => Path.Combine(_directory, "journal.json");
        internal string TariffPath => Path.Combine(_directory, "tariff.json");
        internal InputFiles(UsageJournal journal, TariffInput tariff)
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllBytes(JournalPath, UsageJournalJson.Write(journal));
            File.WriteAllBytes(TariffPath, TariffBytes(tariff));
        }
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private static PricingReport Price(UsageJournal journal, TariffInput? input = null) =>
        PricingReport.Create(journal, Tariff(input ?? Input()));

    private static AdmittedTariff Tariff(TariffInput input)
    {
        var result = AdmittedTariff.Read(TariffBytes(input), out var error);
        Assert.Equal("", error);
        return Assert.IsType<AdmittedTariff>(result);
    }

    private static byte[] TariffBytes(TariffInput input) =>
        JsonSerializer.SerializeToUtf8Bytes(input, PricingJsonContext.Default.TariffInput);

    private static TariffInput Input(long hit = 1, long miss = 4, long output = 5, long unit = 100,
        int ratePlaces = 0, int places = 3, string currency = "USD") =>
        new(PricingLimits.TariffFormat, "https://api-docs.deepseek.com/quick_start/pricing/", "2026-09-20",
            new(PricingLimits.Formula, DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, null,
                "standard", "2026-09-20T12:00:00Z", new("known", "2026-09-20T00:00:00Z", "2026-09-21T00:00:00Z"),
                unit, ratePlaces, new(new(currency, hit), new(currency, miss), new(currency, output)),
                new("half_even", places, PricingLimits.Aggregation, PricingLimits.Normalization)));

    private static ProjectChatUsage Measured(long input, long output, long hit,
        string responseModel = DeepSeekAdapterContext.Model) => new(input, output,
            new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, responseModel, hit, input - hit));

    private static UsageJournal Journal(ProjectChatUsage?[] sends, long charge = 1000)
    {
        var expected = Expected(Math.Max(1, sends.Length), charge);
        var collector = new UsageJournalCollector(expected);
        var overBound = false;
        for (var i = 0; i < sends.Length; i++)
        {
            var attempt = collector.BeginAttempt(i);
            attempt.AgentStarted();
            var call = attempt.BeginCall()!;
            call.Dispatch();
            if (sends[i] is { } usage)
            {
                call.TransportFinished(DeepSeekTransportResult.Success([])); call.Returned(usage);
                overBound |= usage.InputTokens > expected.Bounds.PerCall.MaxInputTokens ||
                    usage.OutputTokens > expected.Bounds.PerCall.MaxOutputTokens;
            }
            else { call.TransportFinished(DeepSeekTransportResult.TransportFailure()); call.Threw(); }
            attempt.AgentFinished(false); attempt.Finish("failed");
        }
        var result = collector.Seal(overBound ? "accounting_violation" : sends.Length == 0 ? "caller_cancelled" : "complete",
            Reservations(expected, sends.Length));
        return Assert.IsType<UsageJournal>(UsageJournalJson.Read(UsageJournalJson.Write(result)));
    }

    private static UsageJournalExpectation Expected(int count, long charge = 1000, int? maximumCalls = null)
    {
        var calls = count * 8;
        var plan = new LivePlanDigestInput(LiveLimits.PlanFormat, new(new string('a', 40), new string('b', 40), true),
            new string('c', 64), new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model,
                DeepSeekAdapterContext.Adapter, LivePlanAdmission.ProviderConfigurationSha256()),
            Enumerable.Repeat("cs-safe", count).ToImmutableArray(),
            new(count, maximumCalls ?? calls, count * AgentLimits.InputTokens, count * AgentLimits.OutputTokens,
                count * AgentLimits.CombinedTokens, 120, calls * charge,
                new(AgentLimits.InputTokens / 8, AgentLimits.OutputTokens / 8, charge)));
        return new(new("pricing-test", plan.Source.Commit, plan.Source.Tree, true, LiveRunner.BuildId,
            plan.CorpusSha256, plan.Provider.ConfigurationSha256, LivePlanAdmission.Digest(plan), "loopback"), plan);
    }

    private static UsageJournalReservations Reservations(UsageJournalExpectation expected, int calls) =>
        new(calls, calls * expected.Bounds.PerCall.MaxInputTokens, calls * expected.Bounds.PerCall.MaxOutputTokens,
            calls * (expected.Bounds.PerCall.MaxInputTokens + expected.Bounds.PerCall.MaxOutputTokens),
            calls * expected.Bounds.PerCall.MaxChargeMicroUsd);
}
