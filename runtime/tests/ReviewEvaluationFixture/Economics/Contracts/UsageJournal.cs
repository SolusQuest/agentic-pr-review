using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Contracts;

internal sealed class UsageJournal
{
    private UsageJournal(UsageJournalDocument document) => Document = document;
    internal UsageJournalDocument Document { get; }

    internal bool Matches(UsageJournalExpectation expected) =>
        Document.Provenance == expected.Provenance && Document.BindingSha256 == expected.BindingSha256;

    internal static UsageJournal? Admit(UsageJournalDocument? document, UsageJournalExpectation? expected = null)
    {
        if (document is null) return null;
        UsageJournalExpectation embedded;
        try { embedded = new(document.Provenance, document.Plan); }
        catch (ArgumentException) { return null; }
        if (expected is not null && embedded.Provenance != expected.Provenance) return null;
        expected ??= embedded;
        if (document.Provenance != expected.Provenance ||
            document.BindingSha256 != expected.BindingSha256 || document.CacheWriteBillingStatus != "not_applicable" ||
            document.StopReason is not ("complete" or "bound_stop" or "accounting_violation" or
                "rate_limited" or "caller_cancelled" or "deadline" or "infrastructure_failed") ||
            document.Attempts.IsDefault || document.Attempts.Length != expected.Schedule.Length ||
            document.Calls.IsDefault || document.Calls.Length > UsageJournalLimits.Calls ||
            document.Totals is null || document.Reservations is null)
            return null;
        try
        {
            var cursor = 0;
            var untouched = false;
            for (var index = 0; index < document.Attempts.Length; index++)
            {
                var attempt = document.Attempts[index];
                if (attempt is null || attempt.BindingSha256 != expected.BindingSha256 ||
                    attempt.AttemptId != expected.AttemptId(index) || attempt.ScheduleIndex != index ||
                    attempt.CaseId != expected.Schedule[index] ||
                    attempt.Status is not ("completed" or "failed" or "invalid" or "unattempted") ||
                    attempt.AgentStatus is not ("succeeded" or "failed" or "not_started" or "unknown") ||
                    attempt.EvaluationAttemptSha256 is { } hash && !EvaluationLimits.Hash(hash) ||
                    attempt.Calls is < 0 or > UsageJournalLimits.CallsPerAttempt ||
                    attempt.Sends < 0 || attempt.LocalRefusals < 0 ||
                    attempt.Calls != attempt.Sends + attempt.LocalRefusals ||
                    cursor + attempt.Calls > document.Calls.Length)
                    return null;
                if (attempt.Status == "unattempted")
                {
                    untouched = true;
                    if (attempt.Calls != 0 || attempt.AgentStatus != "not_started" ||
                        attempt.EvaluationAttemptSha256 is not null) return null;
                }
                else if (untouched) return null;
                // Outcome attribution can defensively become invalid after
                // Agent start, and post-Agent admission can fail after success.
                // Constrain the unambiguous relationships, not those outcomes.
                if (attempt.AgentStatus == "not_started" && attempt.Calls != 0 ||
                    attempt.AgentStatus == "succeeded" &&
                        (attempt.Sends == 0 || attempt.EvaluationAttemptSha256 is null)) return null;
                if (attempt.Status == "completed" && (attempt.AgentStatus != "succeeded" ||
                    attempt.EvaluationAttemptSha256 is null || attempt.Sends == 0)) return null;
                var sends = 0;
                for (var ordinal = 1; ordinal <= attempt.Calls; ordinal++)
                {
                    var call = document.Calls[cursor++];
                    if (call is null || call.BindingSha256 != expected.BindingSha256 ||
                        call.AttemptId != attempt.AttemptId || call.Ordinal != ordinal ||
                        call.CallId != expected.CallId(index, ordinal) || !ValidCall(call)) return null;
                    if ((ordinal < attempt.Calls || attempt.AgentStatus == "succeeded") &&
                        !CanContinueAgent(call)) return null;
                    if (call.Dispatched) sends++;
                }
                if (sends != attempt.Sends) return null;
            }
            if (cursor != document.Calls.Length ||
                document.StopReason == "complete" && untouched ||
                document.Totals != Totals(document.Attempts, document.Calls)) return null;
            var reservation = document.Reservations;
            var bounds = expected.Bounds;
            if (reservation.Calls > bounds.MaxModelCalls ||
                reservation.InputTokens != checked(reservation.Calls * bounds.PerCall.MaxInputTokens) ||
                reservation.OutputTokens != checked(reservation.Calls * bounds.PerCall.MaxOutputTokens) ||
                reservation.CombinedTokens != checked(reservation.InputTokens + reservation.OutputTokens) ||
                reservation.SpendMicroUsd != checked(reservation.Calls * bounds.PerCall.MaxChargeMicroUsd) ||
                reservation.InputTokens > bounds.MaxInputTokens || reservation.OutputTokens > bounds.MaxOutputTokens ||
                reservation.CombinedTokens > bounds.MaxCombinedTokens || reservation.SpendMicroUsd > bounds.SpendCeilingMicroUsd ||
                !ValidCauses(document, bounds))
                return null;
            return new(document);
        }
        catch (OverflowException) { return null; }
    }

    private static bool ValidCauses(UsageJournalDocument document, LivePlanBounds bounds)
    {
        var perCall = bounds.PerCall;
        var capacity = Math.Min(bounds.MaxModelCalls, Math.Min(bounds.MaxInputTokens / perCall.MaxInputTokens,
            Math.Min(bounds.MaxOutputTokens / perCall.MaxOutputTokens,
                Math.Min(bounds.MaxCombinedTokens / (perCall.MaxInputTokens + perCall.MaxOutputTokens),
                    bounds.SpendCeilingMicroUsd / perCall.MaxChargeMicroUsd))));
        long minimum = 0, maximum = 0;
        var possibleViolation = false;
        var definiteViolation = false;
        var possibleRateLimit = false;
        var definiteRateLimit = false;
        var possibleBudgetRefusal = false;
        var definiteBudgetRefusal = false;
        var cursor = 0;
        for (var index = 0; index < document.Attempts.Length; index++)
        {
            var attempt = document.Attempts[index];
            var overBoundInAttempt = false;
            for (var ordinal = 0; ordinal < attempt.Calls; ordinal++)
            {
                var call = document.Calls[cursor++];
                // Another call in this serial Agent attempt proves the prior
                // response task (including its R5 accounting update) returned.
                definiteViolation |= overBoundInAttempt;
                if (call.Dispatched || call.TransportOutcome == "request_rejected")
                {
                    if (definiteViolation || minimum == capacity) return false;
                    minimum++;
                    maximum = Math.Min(capacity, maximum + 1);
                    // A granted reservation rules out any earlier ambiguous
                    // budget refusal: exhausted reservations never recover.
                    possibleBudgetRefusal = false;
                }
                else if (call.TransportOutcome == "budget_refused")
                {
                    if (definiteViolation || maximum < capacity) return false;
                    minimum = maximum = capacity;
                    possibleBudgetRefusal = definiteBudgetRefusal = true;
                }
                else if (call.TransportOutcome == "violation_refused")
                {
                    if (!possibleViolation) return false;
                    definiteViolation = true;
                }
                else if (call.TransportOutcome == "cancelled" ||
                    call.TransportOutcome == "not_dispatched" && call.ChatOutcome == "not_observed")
                {
                    // Cancellation/sealing may hide either the refusal or a
                    // reservation taken before dispatch. Keep its feasible range.
                    possibleBudgetRefusal |= !definiteViolation && maximum == capacity;
                    if (!definiteViolation) maximum = Math.Min(capacity, maximum + 1);
                }
                var overBound = call.Usage is { } usage &&
                    (usage.InputTokens > perCall.MaxInputTokens || usage.OutputTokens > perCall.MaxOutputTokens);
                overBoundInAttempt |= overBound;
                // Journal and R5 observation complete independently when Agent
                // waiting is cancelled. These unknowns may hide an R5 receipt;
                // HTTP/normalization failures and oversized sentinels cannot.
                var hiddenReceipt = call.Dispatched && call.TransportOutcome is "cancelled" or "incomplete";
                possibleViolation |= overBound || hiddenReceipt || call.Dispatched &&
                    call.TransportOutcome == "success" && call.ChatOutcome is "returned" or "not_observed" &&
                    call.UsageStatus == "unknown";
                possibleRateLimit |= call.TransportOutcome == "http429" || hiddenReceipt;
                definiteRateLimit |= call.TransportOutcome == "http429" && call.ChatOutcome == "threw";
            }
            definiteViolation |= overBoundInAttempt && attempt.AgentStatus == "succeeded";
            if (index + 1 < document.Attempts.Length && document.Attempts[index + 1].Status != "unattempted" &&
                (definiteViolation || definiteBudgetRefusal || definiteRateLimit)) return false;
        }
        if (document.Reservations.Calls < minimum || document.Reservations.Calls > maximum) return false;
        return document.StopReason switch
        {
            "complete" => !definiteViolation && !definiteBudgetRefusal && !definiteRateLimit,
            "accounting_violation" => possibleViolation,
            "bound_stop" => !definiteViolation && possibleBudgetRefusal && document.Reservations.Calls == capacity,
            "rate_limited" => !definiteViolation && !definiteBudgetRefusal && possibleRateLimit,
            // These signals can arrive between calls or after the final return;
            // their external cause is not recoverable from call rows alone.
            _ => true,
        };
    }

    // Exceptions, refusals, oversized responses and unfinished seals end this
    // Agent attempt without success. A dispatched cancellation observation can
    // race a returned response, so it does not prove the response task threw.
    private static bool CanContinueAgent(UsageJournalCall call) => call.Dispatched &&
        (call.TransportOutcome == "success" && call.ChatOutcome == "returned" ||
         call.TransportOutcome == "cancelled" && call.ChatOutcome == "cancelled");

    private static bool ValidCall(UsageJournalCall call)
    {
        if (!call.Dispatched)
        {
            if (call.UsageStatus != "not_sent" || call.Usage is not null) return false;
            return call.TransportOutcome switch
            {
                "cancelled" => call.ChatOutcome == "cancelled",
                "budget_refused" or "violation_refused" or "request_rejected" or "not_dispatched" =>
                    call.ChatOutcome is "threw" or "not_observed",
                _ => false,
            };
        }
        if (call.UsageStatus == "known")
            return call.TransportOutcome == "success" && call.ChatOutcome == "returned" && ValidUsage(call.Usage);
        if (call.UsageStatus != "unknown" || call.Usage is not null) return false;
        // A seal may retain a transport receipt before chat observation. Such
        // unfinished observations are explicit unknowns, not invented returns.
        return call.TransportOutcome switch
        {
            "success" => call.ChatOutcome is "returned" or "threw" or "not_observed",
            "response_too_large" => call.ChatOutcome is "returned" or "not_observed",
            "cancelled" => call.ChatOutcome == "cancelled",
            "incomplete" => call.ChatOutcome == "not_observed",
            "http4xx" or "http429" or "http5xx" or "connect_timeout" or "provider_timeout" or "transport_failure" =>
                call.ChatOutcome is "threw" or "not_observed",
            _ => false,
        };
    }

    internal static bool ValidUsage(UsageJournalUsage? usage)
    {
        if (usage is null || usage.InputTokens < 0 || usage.OutputTokens < 0 ||
            usage.CombinedTokens < 0 || usage.InputTokens > long.MaxValue - usage.OutputTokens ||
            usage.CombinedTokens != usage.InputTokens + usage.OutputTokens) return false;
        return usage.Cache is not { } cache ||
            cache.ResponseModel is DeepSeekRequestWriter.Model or "deepseek-flash" &&
            cache.CacheReadInputTokens >= 0 && cache.UncachedInputTokens >= 0 &&
            cache.CacheReadInputTokens <= usage.InputTokens &&
            cache.UncachedInputTokens == usage.InputTokens - cache.CacheReadInputTokens;
    }

    internal static UsageJournalTotals Totals(ImmutableArray<UsageJournalAttempt> attempts,
        ImmutableArray<UsageJournalCall> calls)
    {
        var completed = attempts.Count(a => a.Status == "completed");
        var failed = attempts.Count(a => a.Status == "failed");
        var invalid = attempts.Count(a => a.Status == "invalid");
        var unattempted = attempts.Count(a => a.Status == "unattempted");
        var sent = calls.Count(c => c.Dispatched);
        var known = calls.Where(c => c.UsageStatus == "known").ToArray();
        var measured = known.Where(c => c.Usage!.Cache is not null).ToArray();
        return new(attempts.Length, completed + failed + invalid, completed, failed, invalid, unattempted,
            calls.Length, sent, calls.Length - sent, known.Length, sent - known.Length, sent == known.Length,
            known.Sum(c => (decimal)c.Usage!.InputTokens), known.Sum(c => (decimal)c.Usage!.OutputTokens),
            known.Sum(c => (decimal)c.Usage!.CombinedTokens), measured.Length,
            measured.Length == 0 ? null : measured.Sum(c => (decimal)c.Usage!.Cache!.CacheReadInputTokens),
            measured.Length == 0 ? null : measured.Sum(c => (decimal)c.Usage!.Cache!.UncachedInputTokens));
    }
}

internal static class UsageJournalJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Write(UsageJournal admitted)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(admitted.Document, UsageJournalJsonContext.Default.UsageJournalDocument);
        if (bytes.Length > UsageJournalLimits.JsonBytes) throw new InvalidOperationException("usage_journal_too_large");
        return bytes;
    }

    internal static UsageJournal? Read(ReadOnlySpan<byte> bytes, UsageJournalExpectation? expected = null)
    {
        if (bytes.Length is < 1 or > UsageJournalLimits.JsonBytes) return null;
        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
            return UsageJournal.Admit(JsonSerializer.Deserialize(bytes, UsageJournalJsonContext.Default.UsageJournalDocument), expected);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or NotSupportedException)
        { return null; }
    }
}
