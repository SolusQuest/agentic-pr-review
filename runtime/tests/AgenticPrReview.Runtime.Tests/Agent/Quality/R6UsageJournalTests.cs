using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Accounting;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Contracts;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

public sealed class R6UsageJournalTests
{
    private const string Canary = "APR273_PRIVATE_PROVIDER_PATH_REASONING_CANARY";

    [Theory]
    [InlineData("http4xx", "threw", true)]
    [InlineData("http4xx", "not_observed", true)]
    [InlineData("http429", "threw", true)]
    [InlineData("http429", "not_observed", true)]
    [InlineData("http5xx", "threw", true)]
    [InlineData("http5xx", "not_observed", true)]
    [InlineData("connect_timeout", "threw", true)]
    [InlineData("connect_timeout", "not_observed", true)]
    [InlineData("provider_timeout", "threw", true)]
    [InlineData("provider_timeout", "not_observed", true)]
    [InlineData("transport_failure", "threw", true)]
    [InlineData("transport_failure", "not_observed", true)]
    [InlineData("success", "threw", true)]
    [InlineData("success", "not_observed", true)]
    [InlineData("success", "returned", true)]
    [InlineData("cancelled", "cancelled", true)]
    [InlineData("response_too_large", "returned", true)]
    [InlineData("response_too_large", "not_observed", true)]
    [InlineData("incomplete", "not_observed", true)]
    [InlineData("request_rejected", "threw", false)]
    [InlineData("request_rejected", "not_observed", false)]
    [InlineData("not_dispatched", "threw", false)]
    [InlineData("not_dispatched", "not_observed", false)]
    [InlineData("cancelled", "cancelled", false)]
    public void StrictReaderRejectsAnotherSendAfterATerminalCall(string transport, string chat, bool dispatched)
    {
        var collector = new UsageJournalCollector(Expected(1));
        var attempt = collector.BeginAttempt(0);
        attempt.AgentStarted();
        for (var i = 0; i < 2; i++)
        {
            var call = attempt.BeginCall()!;
            call.Dispatch();
            call.TransportFinished(DeepSeekTransportResult.Success([]));
            call.Returned(new(0, 0));
        }
        attempt.AgentFinished(false);
        attempt.Finish("failed");
        var original = collector.Seal("complete", Reservations(2)).Document;
        var calls = original.Calls.SetItem(0, original.Calls[0] with
        {
            Dispatched = dispatched, TransportOutcome = transport, ChatOutcome = chat,
            UsageStatus = dispatched ? "unknown" : "not_sent", Usage = null,
        });
        var attempts = original.Attempts.SetItem(0, original.Attempts[0] with
        {
            Sends = dispatched ? 2 : 1, LocalRefusals = dispatched ? 0 : 1,
        });
        var candidate = original with
        {
            Calls = calls, Attempts = attempts, Totals = UsageJournal.Totals(attempts, calls),
            StopReason = transport == "http429" ? "rate_limited" : "complete",
            Reservations = Reservations(dispatched || transport == "request_rejected" ? 2 : 1),
        };
        Assert.Null(UsageJournalJson.Read(JsonSerializer.SerializeToUtf8Bytes(candidate,
            UsageJournalJsonContext.Default.UsageJournalDocument)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void StrictReaderRejectsAnotherLocalCallAfterARefusal(bool violation, bool unfinished)
    {
        var expected = Expected(1, violation ? null : b => b with { MaxModelCalls = 1 });
        var collector = new UsageJournalCollector(expected);
        var attempt = collector.BeginAttempt(0);
        attempt.AgentStarted();
        var sent = attempt.BeginCall()!;
        sent.Dispatch();
        sent.TransportFinished(DeepSeekTransportResult.Success([]));
        sent.Returned(new(violation ? 2 : 0, 0));
        var refused = attempt.BeginCall()!;
        refused.Refuse(violation ? "violation_refused" : "budget_refused");
        if (!unfinished) refused.Threw();
        attempt.AgentFinished(false);
        attempt.Finish("failed");
        var original = collector.Seal(violation ? "accounting_violation" : "bound_stop", Reservations(1)).Document;
        var calls = original.Calls.Add(new(expected.BindingSha256, expected.CallId(0, 3), expected.AttemptId(0),
            3, false, "not_dispatched", "threw", "not_sent", null));
        var attempts = original.Attempts.SetItem(0, original.Attempts[0] with { Calls = 3, LocalRefusals = 2 });
        var candidate = original with { Calls = calls, Attempts = attempts, Totals = UsageJournal.Totals(attempts, calls) };
        Assert.Null(UsageJournalJson.Read(JsonSerializer.SerializeToUtf8Bytes(candidate,
            UsageJournalJsonContext.Default.UsageJournalDocument)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TerminalUnknownUsageRemainsAdmissibleForFailedAgent(bool cancelled)
    {
        var collector = new UsageJournalCollector(Expected(1));
        var attempt = collector.BeginAttempt(0);
        attempt.AgentStarted();
        var call = attempt.BeginCall()!;
        call.Dispatch();
        call.TransportFinished(DeepSeekTransportResult.Success([]));
        if (cancelled) call.Cancel();
        else call.Returned(null);
        attempt.AgentFinished(false);
        attempt.Finish("failed");
        var journal = collector.Seal(cancelled ? "caller_cancelled" : "complete", Reservations(1));
        Assert.Equal(cancelled ? "cancelled" : "success", journal.Document.Calls[0].TransportOutcome);
        Assert.Equal("failed", journal.Document.Attempts[0].AgentStatus);
        Assert.Equal(1, journal.Document.Totals.UnknownUsageSends);
        Assert.NotNull(UsageJournalJson.Read(UsageJournalJson.Write(journal)));
    }

    [Theory]
    [InlineData("http429", "threw", true)]
    [InlineData("transport_failure", "threw", true)]
    [InlineData("success", "threw", true)]
    [InlineData("success", "not_observed", true)]
    [InlineData("success", "returned", true)]
    [InlineData("cancelled", "cancelled", true)]
    [InlineData("response_too_large", "returned", true)]
    [InlineData("request_rejected", "threw", false)]
    public void TerminalFinalCallCannotClaimAgentSuccess(string transport, string chat, bool dispatched)
    {
        var collector = new UsageJournalCollector(Expected(1));
        var attempt = collector.BeginAttempt(0);
        attempt.AgentStarted();
        for (var i = 0; i < 2; i++)
        {
            var call = attempt.BeginCall()!;
            call.Dispatch();
            call.TransportFinished(DeepSeekTransportResult.Success([]));
            call.Returned(new(0, 0));
        }
        attempt.AgentFinished(true);
        // A later evaluator failure may retain Agent success, but its final
        // chat cannot be a terminal failure that prevents that success.
        attempt.AdmitEvaluation(new string('d', 64));
        attempt.Finish("failed");
        var original = collector.Seal("complete", Reservations(2)).Document;
        var calls = original.Calls.SetItem(1, original.Calls[1] with
        {
            Dispatched = dispatched, TransportOutcome = transport, ChatOutcome = chat,
            UsageStatus = dispatched ? "unknown" : "not_sent", Usage = null,
        });
        var attempts = original.Attempts.SetItem(0, original.Attempts[0] with
        {
            Sends = dispatched ? 2 : 1, LocalRefusals = dispatched ? 0 : 1,
        });
        var candidate = original with
        {
            Calls = calls, Attempts = attempts, Totals = UsageJournal.Totals(attempts, calls),
            StopReason = transport == "http429" ? "rate_limited" : "complete",
        };
        Assert.Null(UsageJournalJson.Read(JsonSerializer.SerializeToUtf8Bytes(candidate,
            UsageJournalJsonContext.Default.UsageJournalDocument)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusalFinalCallCannotClaimAgentSuccess(bool violation)
    {
        using var plan = new PlanFile(p =>
        {
            if (violation) p["bounds"]!["per_call"]!["max_input_tokens"] = 2;
            else p["bounds"]!["max_model_calls"] = 1;
        });
        var original = (await Run(plan)).Journal.Document;
        var attempts = original.Attempts.SetItem(0, original.Attempts[0] with
        {
            AgentStatus = "succeeded", EvaluationAttemptSha256 = new string('d', 64),
        });
        var candidate = original with { Attempts = attempts };
        Assert.Null(UsageJournalJson.Read(JsonSerializer.SerializeToUtf8Bytes(candidate,
            UsageJournalJsonContext.Default.UsageJournalDocument)));
    }

    [Fact]
    public void TerminalCallCanEndOneAttemptWithoutStoppingTheNextAttempt()
    {
        var collector = new UsageJournalCollector(Expected(2));
        for (var i = 0; i < 2; i++)
        {
            var attempt = collector.BeginAttempt(i);
            attempt.AgentStarted();
            var call = attempt.BeginCall()!;
            call.Dispatch();
            call.TransportFinished(i == 0 ? DeepSeekTransportResult.TransportFailure() :
                DeepSeekTransportResult.Success([]));
            if (i == 0) call.Threw();
            else call.Returned(new(0, 0));
            attempt.AgentFinished(false);
            attempt.Finish("failed");
        }
        var journal = collector.Seal("complete", Reservations(2));
        Assert.NotNull(UsageJournalJson.Read(UsageJournalJson.Write(journal)));
    }

    [Theory]
    [InlineData("stop_rate")]
    [InlineData("stop_bound")]
    [InlineData("stop_violation")]
    [InlineData("hidden_rate")]
    [InlineData("hidden_bound")]
    [InlineData("hidden_violation")]
    [InlineData("first_budget")]
    [InlineData("first_violation")]
    [InlineData("budget_before_reservation")]
    [InlineData("violation_before_usage")]
    public async Task StrictReaderRejectsUnsupportedStopAndRefusalCauses(string fault)
    {
        UsageJournal journal;
        if (fault.StartsWith("first_", StringComparison.Ordinal))
        {
            var collector = new UsageJournalCollector(Expected(1));
            var attempt = collector.BeginAttempt(0);
            attempt.AgentStarted();
            attempt.BeginCall()!.Threw();
            attempt.Finish("failed");
            journal = collector.Seal("complete", Reservations(0));
        }
        else
        {
            using var plan = new PlanFile(p =>
            {
                if (fault is "hidden_bound" or "budget_before_reservation") p["bounds"]!["max_model_calls"] = 1;
                if (fault is "hidden_violation" or "violation_before_usage") p["bounds"]!["per_call"]!["max_input_tokens"] = 2;
            });
            journal = (await Run(plan, fault == "hidden_rate" ?
                _ => new FakeTransport((_, _) => Task.FromResult(
                    DeepSeekTransportResult.HttpFailure(DeepSeekHttpStatusClass.TooManyRequests, 0))) : null)).Journal;
        }
        var candidate = JsonNode.Parse(UsageJournalJson.Write(journal))!;
        if (fault.StartsWith("stop_", StringComparison.Ordinal))
            candidate["stop_reason"] = fault switch
            {
                "stop_rate" => "rate_limited", "stop_bound" => "bound_stop", _ => "accounting_violation",
            };
        else if (fault.StartsWith("hidden_", StringComparison.Ordinal)) candidate["stop_reason"] = "complete";
        else if (fault.StartsWith("first_", StringComparison.Ordinal))
            candidate["calls"]![0]!["transport_outcome"] = fault == "first_budget" ? "budget_refused" : "violation_refused";
        else
        {
            // Preserve identities, counts and final reservations; move the
            // claimed refusal before the observation that could justify it.
            var calls = candidate["calls"]!;
            var first = calls[0]!.DeepClone();
            foreach (var field in new[] { "dispatched", "transport_outcome", "chat_outcome", "usage_status", "usage" })
            {
                calls[0]![field] = calls[1]![field]?.DeepClone();
                calls[1]![field] = first[field]?.DeepClone();
            }
        }
        Assert.Null(UsageJournalJson.Read(Encoding.UTF8.GetBytes(candidate.ToJsonString())));
    }

    [Theory]
    [InlineData("calls")]
    [InlineData("input")]
    [InlineData("output")]
    [InlineData("combined")]
    [InlineData("spend")]
    public void EveryReservationDimensionCanCauseRefusalButRelaxedBoundsCannot(string dimension)
    {
        var expected = Expected(1, b => dimension switch
        {
            "calls" => b with { MaxModelCalls = 1 },
            "input" => b with { MaxInputTokens = 1 },
            "output" => b with { MaxOutputTokens = 1 },
            "combined" => b with { MaxCombinedTokens = 2 },
            _ => b with { SpendCeilingMicroUsd = 1 },
        });
        var accounting = new LiveAccounting(expected.Bounds);
        var collector = new UsageJournalCollector(expected);
        var attempt = collector.BeginAttempt(0);
        attempt.AgentStarted();
        var first = attempt.BeginCall()!;
        Assert.True(accounting.TryReserve());
        first.Dispatch();
        first.TransportFinished(DeepSeekTransportResult.Success([]));
        first.Returned(new(0, 0));
        accounting.RecordUsage(new(0, 0));
        var refused = attempt.BeginCall()!;
        Assert.False(accounting.TryReserve());
        refused.Refuse("budget_refused");
        refused.Threw();
        attempt.Finish("failed");
        var journal = collector.Seal("bound_stop", Reservations(1));
        Assert.NotNull(UsageJournalJson.Read(UsageJournalJson.Write(journal)));

        // Rebind the whole document to another valid selection. This must fail
        // because the cause disappeared, not because a digest became stale.
        var relaxed = Expected(1);
        var candidate = journal.Document with
        {
            Provenance = relaxed.Provenance, Plan = relaxed.Plan, BindingSha256 = relaxed.BindingSha256,
            Attempts = [.. journal.Document.Attempts.Select(a => a with { BindingSha256 = relaxed.BindingSha256 })],
            Calls = [.. journal.Document.Calls.Select(c => c with { BindingSha256 = relaxed.BindingSha256 })],
        };
        Assert.Null(UsageJournalJson.Read(JsonSerializer.SerializeToUtf8Bytes(candidate,
            UsageJournalJsonContext.Default.UsageJournalDocument)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefiniteViolationPreventsReservationAndHasPriorityOverBudget(bool priority)
    {
        using var plan = new PlanFile(p =>
        {
            p["bounds"]!["per_call"]!["max_input_tokens"] = 2;
            if (priority) p["bounds"]!["max_model_calls"] = 1;
        });
        var journal = (await Run(plan)).Journal;
        var candidate = JsonNode.Parse(UsageJournalJson.Write(journal))!;
        if (priority)
        {
            candidate["calls"]![1]!["transport_outcome"] = "budget_refused";
            candidate["stop_reason"] = "bound_stop";
        }
        else
        {
            candidate["calls"]![1]!["transport_outcome"] = "request_rejected";
            var perCall = journal.Document.Plan.Bounds.PerCall;
            candidate["reservations"] = new JsonObject
            {
                ["calls"] = 2, ["input_tokens"] = 2 * perCall.MaxInputTokens,
                ["output_tokens"] = 2 * perCall.MaxOutputTokens,
                ["combined_tokens"] = 2 * (perCall.MaxInputTokens + perCall.MaxOutputTokens),
                ["spend_micro_usd"] = 2 * perCall.MaxChargeMicroUsd,
            };
        }
        Assert.Null(UsageJournalJson.Read(Encoding.UTF8.GetBytes(candidate.ToJsonString())));
    }

    [Theory]
    [InlineData("bound")]
    [InlineData("violation")]
    [InlineData("rate")]
    public async Task DefiniteStopCannotBeFollowedByAnotherScheduledAttempt(string cause)
    {
        using var plan = new PlanFile(p =>
        {
            p["schedule"]![0]!["repeats"] = 2;
            if (cause == "bound") p["bounds"]!["max_model_calls"] = 1;
            if (cause == "violation") p["bounds"]!["per_call"]!["max_input_tokens"] = 2;
        });
        var journal = (await Run(plan, cause == "rate" ?
            _ => new FakeTransport((_, _) => Task.FromResult(
                DeepSeekTransportResult.HttpFailure(DeepSeekHttpStatusClass.TooManyRequests, 0))) : null)).Journal;
        var candidate = JsonNode.Parse(UsageJournalJson.Write(journal))!;
        candidate["attempts"]![1]!["status"] = "failed";
        candidate["totals"]!["attempted"] = 2;
        candidate["totals"]!["failed"] = 2;
        candidate["totals"]!["unattempted"] = 0;
        Assert.Null(UsageJournalJson.Read(Encoding.UTF8.GetBytes(candidate.ToJsonString())));
    }

    [Theory]
    [InlineData("bound")]
    [InlineData("violation")]
    [InlineData("rate")]
    public void CancellationCanHideAnAccountingReceiptWithoutInvalidatingItsStop(string cause)
    {
        var expected = Expected(1, cause == "bound" ? b => b with { MaxModelCalls = 1 } : null);
        var accounting = new LiveAccounting(expected.Bounds);
        var collector = new UsageJournalCollector(expected);
        var attempt = collector.BeginAttempt(0);
        attempt.AgentStarted();
        var call = attempt.BeginCall()!;
        Assert.True(accounting.TryReserve());
        call.Dispatch();
        if (cause == "bound")
        {
            call.TransportFinished(DeepSeekTransportResult.Success([]));
            call.Returned(new(0, 0));
            call = attempt.BeginCall()!;
            Assert.False(accounting.TryReserve());
            call.Refuse("budget_refused");
            call.Cancel();
            Assert.True(accounting.BudgetRefused);
        }
        else
        {
            var result = cause == "rate"
                ? DeepSeekTransportResult.HttpFailure(DeepSeekHttpStatusClass.TooManyRequests, 0)
                : DeepSeekTransportResult.Success([]);
            call.TransportFinished(result);
            call.Cancel();
            accounting.RecordOutcome(result);
            if (cause == "violation")
            {
                call.Returned(new(2, 0)); // Cancellation already won the journal.
                accounting.RecordUsage(new(2, 0));
                Assert.True(accounting.AccountingViolation);
            }
            else Assert.True(accounting.RateLimited);
        }
        attempt.Finish("failed");
        var stop = cause switch { "bound" => "bound_stop", "rate" => "rate_limited", _ => "accounting_violation" };
        var journal = collector.Seal(stop, Reservations(1));
        Assert.Equal("cancelled", journal.Document.Calls[^1].TransportOutcome);
        Assert.NotNull(UsageJournalJson.Read(UsageJournalJson.Write(journal)));
        if (cause != "bound")
        {
            var candidate = JsonNode.Parse(UsageJournalJson.Write(journal))!;
            candidate["calls"]![0]!["transport_outcome"] = "transport_failure";
            candidate["calls"]![0]!["chat_outcome"] = "threw";
            Assert.Null(UsageJournalJson.Read(Encoding.UTF8.GetBytes(candidate.ToJsonString())));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterruptedJournalObservationDoesNotInventAnAccountingStop(bool usage)
    {
        var expected = Expected(1);
        var accounting = new LiveAccounting(expected.Bounds);
        var collector = new UsageJournalCollector(expected);
        var attempt = collector.BeginAttempt(0);
        attempt.AgentStarted();
        var call = attempt.BeginCall()!;
        Assert.True(accounting.TryReserve());
        call.Dispatch();
        call.TransportFinished(usage ? DeepSeekTransportResult.Success([]) :
            DeepSeekTransportResult.HttpFailure(DeepSeekHttpStatusClass.TooManyRequests, 0));
        if (usage) call.Returned(new(2, 0));
        // Agent waiting finishes before the observer's next R5-accounting write.
        attempt.AgentFinished(false);
        attempt.Finish("failed");
        var frozen = accounting.Seal();
        Assert.False(frozen.AccountingViolation);
        Assert.Equal(0, frozen.Outcomes.Http429);
        var journal = collector.Seal("complete", Reservations(1));
        Assert.NotNull(UsageJournalJson.Read(UsageJournalJson.Write(journal)));
    }

    [Fact]
    public void ARefusalConstrainsAnEarlierAmbiguousReservation()
    {
        var expected = Expected(2, b => b with { MaxModelCalls = 1 });
        var accounting = new LiveAccounting(expected.Bounds);
        var collector = new UsageJournalCollector(expected);
        var attempt = collector.BeginAttempt(0);
        attempt.AgentStarted();
        var first = attempt.BeginCall()!;
        Assert.True(accounting.TryReserve());
        first.Cancel(); // Reservation taken, dispatch never reached.
        attempt.AgentFinished(false);
        attempt.Finish("failed");
        var next = collector.BeginAttempt(1);
        next.AgentStarted();
        var second = next.BeginCall()!;
        Assert.False(accounting.TryReserve());
        second.Refuse("budget_refused");
        second.Threw();
        next.AgentFinished(false);
        next.Finish("failed");
        var journal = collector.Seal("bound_stop", Reservations(1));
        Assert.NotNull(UsageJournalJson.Read(UsageJournalJson.Write(journal)));
        var candidate = journal.Document with { Reservations = Reservations(0) };
        Assert.Null(UsageJournalJson.Read(JsonSerializer.SerializeToUtf8Bytes(candidate,
            UsageJournalJsonContext.Default.UsageJournalDocument)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FirstCallCancellationCannotEstablishBudgetExhaustion(int reserved)
    {
        var expected = Expected(1, b => b with { MaxModelCalls = 1 });
        var collector = new UsageJournalCollector(expected);
        var attempt = collector.BeginAttempt(0);
        attempt.AgentStarted();
        attempt.BeginCall()!.Cancel();
        attempt.Finish("failed");
        var journal = collector.Seal("caller_cancelled", Reservations(reserved));
        Assert.NotNull(UsageJournalJson.Read(UsageJournalJson.Write(journal)));
        var candidate = journal.Document with { StopReason = "bound_stop" };
        Assert.Null(UsageJournalJson.Read(JsonSerializer.SerializeToUtf8Bytes(candidate,
            UsageJournalJsonContext.Default.UsageJournalDocument)));
    }

    [Fact]
    public void LaterReservationRulesOutAnEarlierAmbiguousBudgetRefusal()
    {
        var expected = Expected(3, b => b with { MaxModelCalls = 1 });
        var collector = new UsageJournalCollector(expected);
        for (var i = 0; i < 2; i++)
        {
            var interrupted = collector.BeginAttempt(i);
            interrupted.AgentStarted();
            interrupted.BeginCall()!.Cancel();
            interrupted.AgentFinished(false);
            interrupted.Finish("failed");
        }
        var attempt = collector.BeginAttempt(2);
        attempt.AgentStarted();
        var sent = attempt.BeginCall()!;
        sent.Dispatch();
        sent.TransportFinished(DeepSeekTransportResult.Success([]));
        sent.Returned(new(0, 0));
        attempt.Finish("failed");
        var journal = collector.Seal("complete", Reservations(1));
        var candidate = journal.Document with { StopReason = "bound_stop" };
        Assert.Null(UsageJournalJson.Read(JsonSerializer.SerializeToUtf8Bytes(candidate,
            UsageJournalJsonContext.Default.UsageJournalDocument)));
    }

    [Theory]
    [InlineData("budget")]
    [InlineData("http429")]
    [InlineData("request_rejected")]
    [InlineData("invalid_attempt")]
    [InlineData("missing_reservation")]
    [InlineData("extra_reservation")]
    public async Task StrictAdmissionRejectsImpossibleLifecycleAndReservationClaims(string fault)
    {
        UsageJournal journal;
        UsageJournalExpectation expected;
        if (fault == "invalid_attempt")
        {
            expected = Expected(1);
            var collector = new UsageJournalCollector(expected);
            collector.BeginAttempt(0).Finish("invalid");
            journal = collector.Seal("complete", Reservations(0));
        }
        else
        {
            using var plan = new PlanFile(p =>
            {
                if (fault == "budget") p["bounds"]!["max_model_calls"] = 1;
                if (fault == "extra_reservation") p["bounds"]!["per_call"]!["max_input_tokens"] = 2;
            });
            var result = await Run(plan, fault is "budget" or "extra_reservation" ? null :
                _ => new FakeTransport((_, _) => Task.FromResult(fault == "http429"
                    ? DeepSeekTransportResult.HttpFailure(DeepSeekHttpStatusClass.TooManyRequests, 0)
                    : DeepSeekTransportResult.RequestRejected())));
            journal = result.Journal;
            expected = Expect(plan, journal.Document);
        }
        var json = JsonNode.Parse(UsageJournalJson.Write(journal))!;
        if (fault == "invalid_attempt") json["attempts"]![0]!["agent_status"] = "succeeded";
        else if (fault is "missing_reservation" or "extra_reservation")
        {
            var count = fault == "missing_reservation" ? 0 : 2;
            var bounds = expected.Bounds;
            json["reservations"] = new JsonObject
            {
                ["calls"] = count, ["input_tokens"] = count * bounds.PerCall.MaxInputTokens,
                ["output_tokens"] = count * bounds.PerCall.MaxOutputTokens,
                ["combined_tokens"] = count * (bounds.PerCall.MaxInputTokens + bounds.PerCall.MaxOutputTokens),
                ["spend_micro_usd"] = count * bounds.PerCall.MaxChargeMicroUsd,
            };
        }
        else json["calls"]![json["calls"]!.AsArray().Count - 1]!["chat_outcome"] = "returned";
        Assert.Null(UsageJournalJson.Read(Encoding.UTF8.GetBytes(json.ToJsonString()), expected));
    }

    [Fact]
    public async Task StandaloneReaderNeedsOnlyJournalBytesAfterOriginalPlanIsDeleted()
    {
        byte[] bytes;
        string path;
        using (var plan = new PlanFile())
        {
            path = plan.Path;
            bytes = UsageJournalJson.Write((await Run(plan)).Journal);
        }
        Assert.False(File.Exists(path));
        var admitted = Assert.IsType<UsageJournal>(UsageJournalJson.Read(bytes));
        Assert.Equal(1, admitted.Document.Totals.Completed);
        Assert.Equal(bytes, UsageJournalJson.Write(admitted));
        Assert.Equal(DeepSeekAdapterContext.Model, admitted.Document.Plan.Provider.ModelId);
        Assert.DoesNotContain(path, Encoding.UTF8.GetString(bytes));

        var expected = Expected(3); // A historical source, independent of this compiled source.
        var empty = new UsageJournalCollector(expected).Seal("caller_cancelled", Reservations(0));
        var restored = Assert.IsType<UsageJournal>(UsageJournalJson.Read(UsageJournalJson.Write(empty)));
        Assert.Equal(3, restored.Document.Totals.Unattempted);
        Assert.Equal(0, restored.Document.Reservations.Calls);
        Assert.Equal(expected.Bounds, restored.Document.Plan.Bounds);
        Assert.True(restored.Matches(expected));
        Assert.Null(UsageJournalJson.Read(bytes, expected));
    }

    [Fact]
    public async Task StandaloneSelectionAdmissionRejectsUnboundUnsupportedAndUnboundedClaims()
    {
        using var plan = new PlanFile();
        var bytes = UsageJournalJson.Write((await Run(plan)).Journal);
        Action<JsonNode>[] mutations =
        [
            j => j.AsObject().Remove("plan"),
            j => j["plan"] = null,
            j => j["plan"]!["source"]!["commit"] = new string('0', 40),
            j => j["plan"]!["corpus_sha256"] = new string('0', 64),
            j => j["plan"]!["schedule"]![0] = "cs-defect",
            j => j["plan"]!["provider"]!["model_id"] = Canary,
            j => j["plan"]!["provider"]!["adapter_id"] = new string('0', 64),
            j => j["plan"]!["provider"]!["configuration_sha256"] = new string('0', 64),
            j => j["plan"]!["bounds"]!["max_model_calls"] = 2049,
            j => j["plan"]!["bounds"]!["max_evaluations"] = 0,
            j => j["plan"]!["bounds"]!["max_seconds"] = 86401,
            j => j["plan"]!["bounds"]!["per_call"]!["max_input_tokens"] = -1,
            j => j["plan"]!["bounds"]!["per_call"]!["max_charge_micro_usd"] = long.MaxValue,
            j => j["plan"]!["bounds"]!["spend_ceiling_micro_usd"] = 1,
            j => j["plan"]!["bounds"]!["per_call"] = null,
            j => j["plan"]!["corpus_path"] = Canary,
        ];
        foreach (var mutate in mutations)
        {
            var candidate = JsonNode.Parse(bytes)!;
            mutate(candidate);
            Assert.Null(UsageJournalJson.Read(Encoding.UTF8.GetBytes(candidate.ToJsonString())));
        }
        var expected = Expected(1);
        Assert.Throws<ArgumentException>(() => new UsageJournalExpectation(expected.Provenance,
            expected.Plan with { Bounds = expected.Bounds with { MaxModelCalls = 9 } }));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("success")]
    [InlineData("http429")]
    [InlineData("oversized")]
    [InlineData("refused")]
    [InlineData("rejected")]
    [InlineData("before_dispatch")]
    public void SealPreservesLegitimateUnfinishedObservations(string phase)
    {
        var expected = Expected(1, phase == "refused" ? b => b with { MaxModelCalls = 1 } : null);
        var collector = new UsageJournalCollector(expected);
        var attempt = collector.BeginAttempt(0);
        attempt.AgentStarted();
        if (phase == "refused")
        {
            var sent = attempt.BeginCall()!;
            sent.Dispatch();
            sent.TransportFinished(DeepSeekTransportResult.Success([]));
            sent.Returned(new(0, 0));
        }
        var call = attempt.BeginCall()!;
        var reservations = 1;
        if (phase == "refused") call.Refuse("budget_refused");
        else if (phase != "before_dispatch")
        {
            call.Dispatch();
            if (phase != "pending") call.TransportFinished(phase switch
            {
                "success" => DeepSeekTransportResult.Success([]),
                "http429" => DeepSeekTransportResult.HttpFailure(DeepSeekHttpStatusClass.TooManyRequests, 0),
                "oversized" => DeepSeekTransportResult.ResponseTooLarge(),
                _ => DeepSeekTransportResult.RequestRejected(),
            });
        }
        var journal = collector.Seal("infrastructure_failed", Reservations(reservations));
        Assert.Equal("not_observed", journal.Document.Calls[^1].ChatOutcome);
        Assert.NotNull(UsageJournalJson.Read(UsageJournalJson.Write(journal)));
        var candidate = JsonNode.Parse(UsageJournalJson.Write(journal))!;
        candidate["calls"]![candidate["calls"]!.AsArray().Count - 1]!["chat_outcome"] = "cancelled";
        Assert.Null(UsageJournalJson.Read(Encoding.UTF8.GetBytes(candidate.ToJsonString())));
    }

    [Fact]
    public void PostAgentAdmissionFailureRetainsAgentSuccessAndKnownUsage()
    {
        var expected = Expected(1);
        var collector = new UsageJournalCollector(expected);
        var attempt = collector.BeginAttempt(0);
        attempt.AgentStarted();
        var call = attempt.BeginCall()!;
        call.Dispatch();
        call.TransportFinished(DeepSeekTransportResult.Success([]));
        call.Returned(new(7, 3));
        attempt.AgentFinished(true);
        attempt.AdmitEvaluation(new string('a', 64));
        attempt.Finish("failed");
        var journal = collector.Seal("accounting_violation", Reservations(1));
        var restored = Assert.IsType<UsageJournal>(UsageJournalJson.Read(UsageJournalJson.Write(journal)));
        Assert.Equal("succeeded", restored.Document.Attempts[0].AgentStatus);
        Assert.Equal("failed", restored.Document.Attempts[0].Status);
        Assert.Equal(7, restored.Document.Totals.KnownInputTokens);
        var candidate = JsonNode.Parse(UsageJournalJson.Write(journal))!;
        candidate["attempts"]![0]!["agent_status"] = "not_started";
        Assert.Null(UsageJournalJson.Read(Encoding.UTF8.GetBytes(candidate.ToJsonString())));
    }

    [Fact]
    public async Task RepeatedScheduleRetainsEveryAttemptAndSendInExistingSummaryFraming()
    {
        using var plan = new PlanFile(p => p["schedule"]![0]!["repeats"] = 2);
        var lines = new List<string>();
        var result = await Run(plan, lines: lines);
        var journal = result.Journal.Document;
        Assert.Equal(2, journal.Totals.Completed);
        Assert.Equal(2, journal.Totals.Scheduled);
        Assert.Equal(0, journal.Totals.Unattempted);
        Assert.True(journal.Totals.UsageComplete);
        Assert.Equal(journal.Calls.Length, journal.Totals.ActualSends);
        Assert.Equal(journal.Calls.Length, journal.Totals.KnownUsageSends);
        Assert.Equal(journal.Calls.Length, journal.Calls.Select(c => c.CallId).Distinct().Count());
        Assert.Equal(2, journal.Attempts.Select(a => a.AttemptId).Distinct().Count());
        Assert.All(journal.Attempts, a => Assert.Equal("cs-safe", a.CaseId));
        Assert.Equal(result.Outcomes.Select(o => o.AttemptSha256), journal.Attempts.Select(a => a.EvaluationAttemptSha256));
        Assert.Equal(result.Summary.KnownInputTokens, journal.Totals.KnownInputTokens);
        Assert.Equal(result.Outcomes.Length + 2, lines.Count);
        var restored = JsonSerializer.Deserialize(lines[^1], LiveJsonContext.Default.LiveRunSummary)!;
        Assert.Equal(UsageJournalJson.Write(result.Journal),
            UsageJournalJson.Write(Assert.IsType<UsageJournal>(UsageJournal.Admit(restored.UsageJournal, Expect(plan, journal)))));
        Assert.DoesNotContain(plan.Root, string.Join('\n', lines));
        var second = await Run(plan);
        Assert.NotEqual(journal.Provenance.CampaignId, second.Journal.Document.Provenance.CampaignId);
        Assert.Empty(journal.Calls.Select(c => c.CallId).Intersect(second.Journal.Document.Calls.Select(c => c.CallId)));
    }

    [Theory]
    [InlineData("arguments")]
    [InlineData("sequence")]
    [InlineData("grounding")]
    public async Task AgentRejectionAfterValidatedUsagePreservesUsageAndCache(string fault)
    {
        using var plan = new PlanFile();
        var finish = new ReplayToolCall("finish", "finish_review", "{\"summary\":\"" + Canary + "\",\"findings\":[]}");
        ReplayToolCall[] calls = fault switch
        {
            "arguments" => [new("finish", "finish_review", "{\"summary\":\"" + Canary + "\"}")],
            "sequence" => [finish, new("read", "read_file", "{\"path\":\"src/Caller.cs\",\"start_line\":1,\"line_count\":20}")],
            _ => [new("finish", "finish_review", "{\"summary\":\"" + Canary + "\",\"findings\":[{\"severity\":\"high\",\"title\":\"Test\",\"message\":\"" + Canary + "\",\"evidence\":[{\"observation_id\":\"" + new string('a', 64) + "\",\"path\":\"src/Caller.cs\",\"start_line\":3,\"end_line\":3}]}]}")],
        };
        var lines = new List<string>();
        var result = await Run(plan, _ => WithUsage(new ReplayTransport(new([new([.. calls], Canary)]), ReplayFault.None)), lines);
        Assert.Equal(1, result.Failed);
        var journal = result.Journal.Document;
        Assert.Equal("failed", Assert.Single(journal.Attempts).AgentStatus);
        Assert.Equal("known", Assert.Single(journal.Calls).UsageStatus);
        Assert.Equal(7, journal.Totals.KnownInputTokens);
        Assert.Equal(3, journal.Totals.KnownOutputTokens);
        Assert.Equal(2, journal.Totals.CacheReadInputTokens);
        Assert.Equal(5, journal.Totals.UncachedInputTokens);
        Assert.True(journal.Totals.UsageComplete);
        Assert.DoesNotContain(Canary, string.Join('\n', lines));
    }

    [Theory]
    [InlineData("http4xx")]
    [InlineData("http429")]
    [InlineData("http5xx")]
    [InlineData("connect_timeout")]
    [InlineData("provider_timeout")]
    [InlineData("transport_failure")]
    [InlineData("response_too_large")]
    [InlineData("thrown")]
    [InlineData("invalid_usage")]
    [InlineData("invalid_json")]
    public async Task DispatchedFailuresStayUnknownAndNeverBecomeMeasuredZero(string fault)
    {
        using var plan = new PlanFile(p => p["schedule"]![0]!["repeats"] = 2);
        var lines = new List<string>();
        var result = await Run(plan, run => new FakeTransport(async (body, token) =>
        {
            if (fault == "thrown") throw new InvalidOperationException(Canary);
            if (fault == "invalid_usage")
            {
                var reply = await new ReplayTransport(run.Script, ReplayFault.None).SendAsync(body, token);
                var json = JsonNode.Parse(reply.Body.AsSpan())!;
                json["usage"]!["prompt_tokens"] = -1;
                return DeepSeekTransportResult.Success(Encoding.UTF8.GetBytes(json.ToJsonString()));
            }
            return fault switch
            {
                "http4xx" => DeepSeekTransportResult.HttpFailure(DeepSeekHttpStatusClass.BadRequest, 1),
                "http429" => DeepSeekTransportResult.HttpFailure(DeepSeekHttpStatusClass.TooManyRequests, 1),
                "http5xx" => DeepSeekTransportResult.HttpFailure(DeepSeekHttpStatusClass.Other5xx, 1),
                "connect_timeout" => DeepSeekTransportResult.ConnectTimeout(),
                "provider_timeout" => DeepSeekTransportResult.ProviderTimeout(),
                "response_too_large" => DeepSeekTransportResult.ResponseTooLarge(),
                "invalid_json" => DeepSeekTransportResult.Success(Encoding.UTF8.GetBytes(Canary)),
                _ => DeepSeekTransportResult.TransportFailure(),
            };
        }), lines);
        var journal = result.Journal.Document;
        var count = fault == "http429" ? 1 : 2;
        Assert.Equal(count, journal.Totals.ActualSends);
        Assert.Equal(count, journal.Totals.UnknownUsageSends);
        Assert.Equal(0, journal.Totals.KnownUsageSends);
        Assert.Equal(0, journal.Totals.LocalRefusals);
        Assert.Equal(2 - count, journal.Totals.Unattempted);
        Assert.False(journal.Totals.UsageComplete);
        Assert.All(journal.Calls, c =>
        {
            Assert.Null(c.Usage);
            Assert.Equal("unknown", c.UsageStatus);
            Assert.Equal(fault switch
            {
                "thrown" => "transport_failure",
                "invalid_usage" or "invalid_json" => "success",
                _ => fault,
            }, c.TransportOutcome);
        });
        Assert.Null(journal.Totals.CacheReadInputTokens);
        Assert.Equal(count * 1000, journal.Reservations.SpendMicroUsd);
        Assert.DoesNotContain(Canary, string.Join('\n', lines));
        Assert.NotNull(UsageJournalJson.Read(UsageJournalJson.Write(result.Journal), Expect(plan, journal)));
        var tampered = JsonNode.Parse(UsageJournalJson.Write(result.Journal))!;
        tampered["totals"]!["usage_complete"] = true;
        tampered["totals"]!["unknown_usage_sends"] = 0;
        Assert.Null(UsageJournalJson.Read(Encoding.UTF8.GetBytes(tampered.ToJsonString()), Expect(plan, journal)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BudgetAndViolationRefusalsAreSeparateFromActualSends(bool violation)
    {
        using var plan = new PlanFile(p =>
        {
            p["schedule"]![0]!["repeats"] = 2;
            if (violation) p["bounds"]!["per_call"]!["max_input_tokens"] = 2;
            else p["bounds"]!["max_model_calls"] = 1;
        });
        var result = await Run(plan);
        var journal = result.Journal.Document;
        Assert.Equal(violation ? "accounting_violation" : "bound_stop", result.StopReason);
        Assert.Equal(1, journal.Totals.ActualSends);
        Assert.Equal(1, journal.Totals.LocalRefusals);
        Assert.Equal(1, journal.Totals.KnownUsageSends);
        Assert.Equal(0, journal.Totals.UnknownUsageSends);
        Assert.Equal(1, journal.Totals.Unattempted);
        Assert.Equal(1, journal.Reservations.Calls);
        Assert.Equal(1000, journal.Reservations.SpendMicroUsd);
        Assert.Equal(3, journal.Totals.KnownInputTokens);
        Assert.Equal("not_sent", journal.Calls[1].UsageStatus);
        Assert.Equal(violation ? "violation_refused" : "budget_refused", journal.Calls[1].TransportOutcome);
    }

    [Fact]
    public async Task DefensiveRequestRejectionDoesNotTurnReservationIntoSend()
    {
        using var plan = new PlanFile();
        var result = await Run(plan, _ => new FakeTransport((_, _) => Task.FromResult(DeepSeekTransportResult.RequestRejected())));
        var journal = result.Journal.Document;
        Assert.Equal(1, journal.Reservations.Calls);
        Assert.Equal(0, journal.Totals.ActualSends);
        Assert.Equal(1, journal.Totals.LocalRefusals);
        Assert.Equal(0, journal.Totals.UnknownUsageSends);
        Assert.Equal("request_rejected", Assert.Single(journal.Calls).TransportOutcome);
        Assert.Null(journal.Calls[0].Usage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationSealsInFlightSendAndNeverStartedTailBeforeLateCompletion(bool deadline)
    {
        using var plan = new PlanFile(p =>
        {
            p["schedule"]![0]!["repeats"] = 3;
            if (deadline) p["bounds"]!["max_seconds"] = 1;
        });
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<DeepSeekTransportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        DeepSeekTransportResult? late = null;
        var lines = new List<string>();
        var task = Run(plan, run => new FakeTransport(async (body, token) =>
        {
            late = await WithUsage(new ReplayTransport(run.Script, ReplayFault.None)).SendAsync(body, CancellationToken.None);
            started.SetResult();
            return await response.Task; // Deliberately ignores cancellation.
        }), lines, cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (!deadline) cancel.Cancel();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        var journal = result.Journal.Document;
        Assert.Equal(deadline ? "deadline" : "caller_cancelled", journal.StopReason);
        Assert.Equal(1, journal.Totals.ActualSends);
        Assert.Equal(1, journal.Totals.UnknownUsageSends);
        Assert.Equal(2, journal.Totals.Unattempted);
        Assert.Equal("cancelled", Assert.Single(journal.Calls).TransportOutcome);
        var bytes = UsageJournalJson.Write(result.Journal);
        var summary = JsonSerializer.Serialize(result.Summary, LiveJsonContext.Default.LiveRunSummary);
        response.SetResult(late!);
        await Task.Delay(50);
        Assert.Equal(bytes, UsageJournalJson.Write(result.Journal));
        Assert.Equal(summary, JsonSerializer.Serialize(result.Summary, LiveJsonContext.Default.LiveRunSummary));
        Assert.Equal(summary, lines[^1]);
    }

    [Fact]
    public async Task PredispatchCancellationAndRequestProjectionFailureHaveNoUsagePopulation()
    {
        var expected = Expected(2);
        var collector = new UsageJournalCollector(expected);
        var scope = collector.BeginAttempt(0);
        scope.AgentStarted();
        var accounting = new LiveAccounting(expected.Bounds);
        var sends = 0;
        using var transport = new LiveMeteredTransport(new FakeTransport((_, _) =>
        {
            sends++;
            return Task.FromResult(DeepSeekTransportResult.TransportFailure());
        }), accounting, scope);
        var call = scope.BeginCall()!;
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.SendAsync(new byte[1], cancel.Token));
        call.Cancel();
        scope.AgentFinished(false);
        scope.Finish("failed");
        var next = collector.BeginAttempt(1);
        next.AgentStarted();
        var projection = new LiveChatObserver(new ThrowingClient(), accounting, next);
        await Assert.ThrowsAsync<ArgumentException>(() => projection.GetResponseAsync(new([], [], null), CancellationToken.None));
        next.AgentFinished(false);
        next.Finish("failed");
        var journal = collector.Seal("complete", Reservations(0));
        Assert.Equal(0, sends);
        Assert.Equal(2, journal.Document.Totals.LocalRefusals);
        Assert.Equal(0, journal.Document.Totals.UnknownUsageSends);
        Assert.Equal(new[] { "cancelled", "not_dispatched" }, journal.Document.Calls.Select(c => c.TransportOutcome));
        Assert.All(journal.Document.Calls, c => Assert.Equal("not_sent", c.UsageStatus));
    }

    [Fact]
    public void CapturedHandlesIgnoreDuplicatesAndCannotWriteToFollowingAttemptOrSealedSummary()
    {
        var expected = Expected(3);
        var collector = new UsageJournalCollector(expected);
        var first = collector.BeginAttempt(0);
        first.AgentStarted();
        Assert.Throws<InvalidOperationException>(() => collector.BeginAttempt(1));
        var call = first.BeginCall()!;
        Assert.True(call.Dispatch());
        call.TransportFinished(DeepSeekTransportResult.Success([]));
        call.Returned(new(7, 3));
        call.Returned(new(100, 100));
        call.Cancel();
        call.Threw();
        first.AgentFinished(false);
        first.Finish("failed");
        var second = collector.BeginAttempt(1);
        second.AgentStarted();
        var other = second.BeginCall()!;
        other.Dispatch();
        call.TransportFinished(DeepSeekTransportResult.TransportFailure());
        call.Returned(new(200, 200));
        other.Cancel();
        second.Finish("failed");
        var journal = collector.Seal("caller_cancelled", Reservations(2));
        var bytes = UsageJournalJson.Write(journal);
        other.Returned(new(300, 300));
        Assert.Null(second.BeginCall());
        Assert.Same(journal, collector.Seal("complete", Reservations(0)));
        Assert.Equal(bytes, UsageJournalJson.Write(journal));
        Assert.Equal(7, journal.Document.Totals.KnownInputTokens);
        Assert.Equal(1, journal.Document.Totals.UnknownUsageSends);
        Assert.Equal(1, journal.Document.Totals.Unattempted);
        var accounting = new LiveAccounting(expected.Bounds);
        Assert.True(accounting.TryReserve());
        accounting.RecordUsage(new(7, 3));
        var snapshot = accounting.Seal();
        accounting.RecordUsage(new(100, 100));
        accounting.RecordOutcome(DeepSeekTransportResult.ProviderTimeout());
        accounting.RecordCancelled();
        accounting.RecordUsageUnknown();
        Assert.False(accounting.TryReserve());
        Assert.Same(snapshot, accounting.Seal());
        Assert.Equal(snapshot.KnownInputTokens, accounting.KnownInputTokens);
        Assert.Equal(snapshot.Outcomes, accounting.Outcomes);
    }

    [Fact]
    public async Task CancellationAndUsageRaceSelectOneTerminalDisposition()
    {
        for (var i = 0; i < 32; i++)
        {
            var collector = new UsageJournalCollector(Expected(1));
            var attempt = collector.BeginAttempt(0);
            attempt.AgentStarted();
            var call = attempt.BeginCall()!;
            call.Dispatch();
            call.TransportFinished(DeepSeekTransportResult.Success([]));
            using var start = new ManualResetEventSlim();
            var accepted = Task.Run(() => { start.Wait(); call.Returned(new(7, 3)); });
            var cancelled = Task.Run(() => { start.Wait(); call.Cancel(); });
            start.Set();
            await Task.WhenAll(accepted, cancelled);
            attempt.Finish("failed");
            var journal = collector.Seal("caller_cancelled", Reservations(1)).Document;
            Assert.Equal(1, journal.Totals.ActualSends);
            Assert.Equal(1, journal.Totals.KnownUsageSends + journal.Totals.UnknownUsageSends);
            Assert.Equal(journal.Calls[0].Usage is not null, journal.Totals.UsageComplete);
            Assert.Equal(journal.Totals.UsageComplete ? 7 : 0, journal.Totals.KnownInputTokens);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(7, 3)]
    [InlineData(long.MaxValue, 0)]
    public void MaximumCardinalityRoundTripsWithoutCounterOverflowOrExceedingByteCap(long input, long output)
    {
        var expected = Expected(256);
        var collector = new UsageJournalCollector(expected);
        var earlierInput = Math.Min(input, 1);
        var earlierOutput = Math.Min(output, 1);
        var violation = input > 1 || output > 1;
        for (var index = 0; index < 256; index++)
        {
            var attempt = collector.BeginAttempt(index);
            attempt.AgentStarted();
            for (var ordinal = 0; ordinal < 8; ordinal++)
            {
                var call = attempt.BeginCall()!;
                call.Dispatch();
                call.TransportFinished(DeepSeekTransportResult.Success([]));
                // The only over-bound observation is the final send. Earlier
                // calls fit, so the population obeys the monotonic stop policy.
                var last = index == 255 && ordinal == 7;
                var observedInput = last ? input : earlierInput;
                var observedOutput = last ? output : earlierOutput;
                call.Returned(new(observedInput, observedOutput,
                    new("deepseek", "deepseek-v4-flash", "deepseek-flash", observedInput, 0)));
            }
            var failed = index == 255 && violation;
            attempt.AgentFinished(!failed);
            attempt.AdmitEvaluation(new string('a', 64));
            attempt.Finish(failed ? "failed" : "completed");
        }
        var journal = collector.Seal(violation ? "accounting_violation" : "complete", Reservations(2048));
        var bytes = UsageJournalJson.Write(journal);
        Assert.InRange(bytes.Length, 1, UsageJournalLimits.JsonBytes);
        Assert.Equal(bytes, UsageJournalJson.Write(Assert.IsType<UsageJournal>(UsageJournalJson.Read(bytes, expected))));
        Assert.Equal(2047m * earlierInput + input, journal.Document.Totals.KnownInputTokens);
        Assert.Equal(2047m * (earlierInput + earlierOutput) + input + output, journal.Document.Totals.KnownCombinedTokens);
        Assert.Equal(2047m * earlierInput + input, journal.Document.Totals.CacheReadInputTokens);
        Assert.Equal(2048, journal.Document.Totals.KnownUsageSends);
        Assert.True(journal.Document.Totals.UsageComplete);
    }

    [Fact]
    public void InvalidAndUnattemptedRemainVisibleAndCacheAvailabilityIsIndependent()
    {
        var expected = Expected(3);
        var collector = new UsageJournalCollector(expected);
        collector.BeginAttempt(0).Finish("invalid");
        var attempt = collector.BeginAttempt(1);
        attempt.AgentStarted();
        var call = attempt.BeginCall()!;
        call.Dispatch();
        call.TransportFinished(DeepSeekTransportResult.Success([]));
        call.Returned(new(0, 0, new(Canary, Canary, Canary, 0, 0)));
        attempt.Finish("failed");
        var journal = collector.Seal("caller_cancelled", Reservations(1));
        Assert.Equal(new[] { "invalid", "failed", "unattempted" }, journal.Document.Attempts.Select(a => a.Status));
        Assert.Equal(1, journal.Document.Totals.Invalid);
        Assert.Equal(2, journal.Document.Totals.Attempted);
        Assert.Equal(1, journal.Document.Totals.KnownUsageSends);
        Assert.True(journal.Document.Totals.UsageComplete);
        Assert.Null(journal.Document.Totals.CacheReadInputTokens);
        Assert.Null(journal.Document.Calls[0].Usage!.Cache);
        Assert.DoesNotContain(Canary, Encoding.UTF8.GetString(UsageJournalJson.Write(journal)));
    }

    [Fact]
    public async Task StrictReaderRejectsTamperedRowsCountersIdentitiesAndSchema()
    {
        using var plan = new PlanFile(p => p["schedule"]![0]!["repeats"] = 2);
        var run = await Run(plan, r => WithUsage(new ReplayTransport(r.Script, ReplayFault.None)));
        var bytes = UsageJournalJson.Write(run.Journal);
        var expected = Expect(plan, run.Journal.Document);
        Action<JsonNode>[] mutations =
        [
            j => j["provenance"]!["source_commit"] = new string('0', 40),
            j => j["provenance"]!["source_tree"] = new string('0', 40),
            j => j["provenance"]!["source_clean"] = !expected.Provenance.SourceClean,
            j => j["provenance"]!["build_id"] = "another-build",
            j => j["provenance"]!["corpus_sha256"] = new string('0', 64),
            j => j["provenance"]!["provider_configuration_sha256"] = new string('0', 64),
            j => j["provenance"]!["plan_sha256"] = new string('0', 64),
            j => j["provenance"]!["campaign_id"] = "another-campaign",
            j => j["binding_sha256"] = new string('0', 64),
            j => j["attempts"]![0]!["binding_sha256"] = new string('0', 64),
            j => j["calls"]![0]!["binding_sha256"] = new string('0', 64),
            j => j["attempts"]!.AsArray().RemoveAt(1),
            j => j["calls"]!.AsArray().RemoveAt(0),
            j => j["attempts"]![1]!["attempt_id"] = j["attempts"]![0]!["attempt_id"]!.DeepClone(),
            j => j["calls"]![1]!["call_id"] = j["calls"]![0]!["call_id"]!.DeepClone(),
            j => j["calls"]![0]!["attempt_id"] = j["attempts"]![1]!["attempt_id"]!.DeepClone(),
            j => j["calls"]![0]!["ordinal"] = 2,
            j => j["attempts"]![0]!["case_id"] = "cs-defect",
            j => j["attempts"]![0]!["status"] = "unattempted",
            j => j["attempts"]![0]!["calls"] = 9,
            j => j["calls"]![0]!["transport_outcome"] = Canary,
            j => j["calls"]![0]!["usage_status"] = "unknown",
            j => j["calls"]![0]!["dispatched"] = false,
            j => j["calls"]![0]!["usage"]!["input_tokens"] = -1,
            j => j["calls"]![0]!["usage"]!["input_tokens"] = long.MaxValue,
            j => j["calls"]![0]!["usage"]!["combined_tokens"] = 0,
            j => j["calls"]![0]!["usage"]!["cache"]!["response_model"] = Canary,
            j => j["calls"]![0]!["usage"]!["cache"]!["cache_read_input_tokens"] = 8,
            j => j["calls"]![0]!["usage"]!["cache"]!["uncached_input_tokens"] = -1,
            j => j["totals"]!["known_input_tokens"] = 0,
            j => j["totals"]!["known_input_tokens"] = 0.5m,
            j => j["totals"]!["actual_sends"] = 0,
            j => j["reservations"]!["calls"] = -1,
            j => j["reservations"]!["spend_micro_usd"] = 0,
            j => j["cache_write_billing_status"] = "measured",
            j => j["calls"]![0]!.AsObject().Remove("usage"),
            j => j["provenance"]!.AsObject().Remove("source_clean"),
            j => j["calls"]![0]!["provider_request_id"] = Canary,
            j => j["attempts"]!.AsArray().Add(null),
            j => j["calls"]!.AsArray().Add(null),
        ];
        foreach (var mutate in mutations)
        {
            var json = JsonNode.Parse(bytes)!;
            mutate(json);
            Assert.Null(UsageJournalJson.Read(Encoding.UTF8.GetBytes(json.ToJsonString()), expected));
        }
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Null(UsageJournalJson.Read(Encoding.UTF8.GetBytes(text.Replace("\"dispatched\":true", "\"dispatched\":true,\"dispatched\":true")), expected));
        Assert.Null(UsageJournalJson.Read(new byte[UsageJournalLimits.JsonBytes + 1], expected));
        Assert.Null(UsageJournalJson.Read([0xff, 0xfe], expected));
        Assert.Null(UsageJournalJson.Read(Encoding.UTF8.GetBytes(new string('[', 20) + new string(']', 20)), expected));
        var tooMany = JsonNode.Parse(bytes)!;
        var template = tooMany["calls"]![0]!.DeepClone();
        while (tooMany["calls"]!.AsArray().Count <= UsageJournalLimits.Calls)
            tooMany["calls"]!.AsArray().Add(template.DeepClone());
        Assert.Null(UsageJournalJson.Read(Encoding.UTF8.GetBytes(tooMany.ToJsonString()), expected));
        var different = expected.Plan with { Schedule = ["cs-defect", "cs-safe"] };
        Assert.Null(UsageJournalJson.Read(bytes,
            new(expected.Provenance with { PlanSha256 = LivePlanAdmission.Digest(different) }, different)));
    }

    private static UsageJournalExpectation Expected(int count, Func<LivePlanBounds, LivePlanBounds>? bounds = null)
    {
        var calls = count * 8;
        var plan = new LivePlanDigestInput(LiveLimits.PlanFormat, new(new string('a', 40), new string('b', 40), true),
            new string('c', 64), new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model,
                DeepSeekAdapterContext.Adapter, LivePlanAdmission.ProviderConfigurationSha256()),
            Enumerable.Repeat(count == 256 ? new string('c', 64) : "cs-safe", count).ToImmutableArray(),
            new(count, calls, calls, calls, calls * 2, 120, calls, new(1, 1, 1)));
        if (bounds is not null) plan = plan with { Bounds = bounds(plan.Bounds) };
        return new(new(count == 256 ? new string('a', 57) : "live-test", plan.Source.Commit, plan.Source.Tree, true,
            count == 256 ? new string('b', 64) : LiveRunner.BuildId, plan.CorpusSha256,
            plan.Provider.ConfigurationSha256, LivePlanAdmission.Digest(plan), "loopback"), plan);
    }

    private static UsageJournalReservations Reservations(int calls) => new(calls, calls, calls, calls * 2, calls);

    private static UsageJournalExpectation Expect(PlanFile file, UsageJournalDocument document)
    {
        var plan = LivePlanAdmission.Load(file.Path, false, CancellationToken.None);
        return new(document.Provenance, new(LiveLimits.PlanFormat,
            new(EvaluationSource.Commit, EvaluationSource.Tree, EvaluationSource.Clean),
            plan.Corpus.Sha256, plan.Provider, plan.Schedule, plan.Bounds));
    }

    private static Task<LiveRunResult> Run(PlanFile plan, Func<AdmittedReplayRun, IDeepSeekTransport>? transport = null,
        List<string>? lines = null, CancellationToken token = default) => LiveRunner.RunAsync(plan.Path, false, new()
        {
            WriteLine = lines is null ? _ => { } : lines.Add,
            DryRunTransport = transport ?? (r => new ReplayTransport(r.Script, ReplayFault.None)),
        }, token);

    private static IDeepSeekTransport WithUsage(IDeepSeekTransport replay) => new FakeTransport(async (body, token) =>
    {
        var original = await replay.SendAsync(body, token);
        var json = JsonNode.Parse(original.Body.AsSpan())!;
        json["id"] = Canary;
        json["system_fingerprint"] = Canary;
        json["usage"] = new JsonObject
        {
            ["prompt_tokens"] = 7, ["completion_tokens"] = 3, ["total_tokens"] = 10,
            ["prompt_cache_hit_tokens"] = 2, ["prompt_cache_miss_tokens"] = 5,
        };
        return DeepSeekTransportResult.Success(Encoding.UTF8.GetBytes(json.ToJsonString()));
    });

    private sealed class FakeTransport(Func<ReadOnlyMemory<byte>, CancellationToken, Task<DeepSeekTransportResult>> send)
        : IDeepSeekTransport
    {
        public Task<DeepSeekTransportResult> SendAsync(ReadOnlyMemory<byte> requestBody, CancellationToken token) => send(requestBody, token);
        public void Dispose() { }
    }

    private sealed class ThrowingClient : IProjectChatClient
    {
        public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token) =>
            throw new ArgumentException(Canary);
    }

    private sealed class PlanFile : IDisposable
    {
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "r6-journal-" + Guid.NewGuid().ToString("N"));
        internal string Path { get; }
        internal PlanFile(Action<JsonObject>? edit = null)
        {
            var corpus = System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", "agent", "r5", "quality", "bundle");
            var fixture = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(corpus).Fixture);
            var plan = new JsonObject
            {
                ["format"] = LiveLimits.PlanFormat,
                ["source"] = new JsonObject { ["commit"] = EvaluationSource.Commit, ["tree"] = EvaluationSource.Tree, ["clean"] = EvaluationSource.Clean },
                ["corpus"] = new JsonObject { ["path"] = corpus, ["sha256"] = fixture.CorpusSha256 },
                ["provider"] = new JsonObject
                {
                    ["provider_id"] = DeepSeekAdapterContext.Provider, ["model_id"] = DeepSeekAdapterContext.Model,
                    ["adapter_id"] = DeepSeekAdapterContext.Adapter, ["configuration_sha256"] = LivePlanAdmission.ProviderConfigurationSha256(),
                },
                ["schedule"] = new JsonArray(new JsonObject { ["case_id"] = "cs-safe", ["repeats"] = 1 }),
                ["bounds"] = new JsonObject
                {
                    ["max_evaluations"] = 4, ["max_model_calls"] = 8, ["max_input_tokens"] = 262144,
                    ["max_output_tokens"] = 32768, ["max_combined_tokens"] = 294912, ["max_seconds"] = 120,
                    ["spend_ceiling_micro_usd"] = 100000,
                    ["per_call"] = new JsonObject { ["max_input_tokens"] = 32768, ["max_output_tokens"] = 4096, ["max_charge_micro_usd"] = 1000 },
                },
            };
            edit?.Invoke(plan);
            Directory.CreateDirectory(Root);
            Path = System.IO.Path.Combine(Root, "plan.json");
            File.WriteAllText(Path, plan.ToJsonString());
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
