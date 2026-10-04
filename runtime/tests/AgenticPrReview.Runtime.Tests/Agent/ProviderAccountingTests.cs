using AgenticPrReview.Runtime.Agent.Core;

namespace AgenticPrReview.Runtime.Tests.Agent;

public sealed class ProviderAccountingTests
{
    [Fact]
    public void UndispatchedReservationsDoNotSpendTheGlobalRetryLimit()
    {
        var review = new ReviewAccounting();
        var noSend = review.BeginCall();
        noSend.BeginAttempt().Freeze(true, false);
        var reservation = noSend.BeginAttempt();
        reservation.ObserveNoDispatch();
        reservation.Freeze(true, false);
        for (var callIndex = 0; callIndex < 4; callIndex++)
        {
            var call = review.BeginCall();
            for (var ordinal = 0; ordinal <= 2; ordinal++)
            {
                var attempt = call.BeginAttempt();
                Assert.True(attempt.TryBeginDispatch());
                attempt.Freeze(true, false);
            }
            Assert.Throws<InvalidOperationException>(() => call.BeginAttempt());
        }
        var last = review.BeginCall();
        var first = last.BeginAttempt();
        Assert.True(first.TryBeginDispatch());
        first.Freeze(true, false);
        var denied = last.BeginAttempt();
        denied.ObserveNoDispatch();
        Assert.False(denied.TryBeginDispatch());
        denied.Freeze(true, false);
        Assert.Equal(8, review.Finish().ProviderRetries);
        Assert.Equal(13, review.Finish().ProviderAttempts);
        Assert.False(review.CanRetry);
    }

    [Fact]
    public void RetryRequiresCompletedFailureAndKeepsLateUsageFrozen()
    {
        foreach (var completed in new[] { false, true })
        {
            var call = new ReviewAccounting().BeginCall();
            var capture = call.BeginAttempt();
            capture.TryBeginDispatch();
            capture.Freeze(completed, completed);
            Assert.Throws<InvalidOperationException>(() => call.BeginAttempt());
        }
    }

    [Fact]
    public void LogicalCallsRejectOverlappingAttempts()
    {
        var review = new ReviewAccounting();
        var call = review.BeginCall();
        var attempt = call.BeginAttempt();
        Assert.Throws<InvalidOperationException>(() => call.BeginAttempt());
        attempt.ObserveNoDispatch();
        var result = review.Finish();
        Assert.Equal(1, result.ModelCalls);
        Assert.Equal(0, result.ProviderAttempts);
        Assert.Equal(0, result.ProviderFailedAttempts);
        Assert.Equal(0, result.ProviderRetries);
        Assert.Equal(AccountingCompleteness.Complete, result.AttemptCompleteness);
        Assert.Equal(AccountingCompleteness.Complete, result.UsageCompleteness);
    }

    [Fact]
    public void CompletenessDistinguishesZeroUnknownMixedAndUnresolved()
    {
        var empty = new ReviewAccounting().Finish();
        Assert.Equal(AccountingCompleteness.Complete, empty.UsageCompleteness);
        Assert.Equal(0, empty.InputTokens);

        var unknown = new ReviewAccounting();
        unknown.BeginCall().BeginAttempt().Freeze(true, false);
        Assert.Equal(AccountingCompleteness.Unavailable, unknown.Finish().AttemptCompleteness);
        Assert.Equal(AccountingCompleteness.Unavailable, unknown.Finish().UsageCompleteness);
        var known = unknown.BeginCall().BeginAttempt();
        known.ObserveNoDispatch();
        known.Freeze(true, false);
        Assert.Equal(AccountingCompleteness.Partial, unknown.Finish().AttemptCompleteness);

        var pending = new ReviewAccounting();
        var attempt = pending.BeginCall().BeginAttempt();
        Assert.True(attempt.TryBeginDispatch());
        var result = pending.Finish();
        Assert.Equal(1, result.ProviderAttempts);
        Assert.Equal(1, result.ProviderFailedAttempts);
        Assert.Equal(1, result.UnknownUsageAttempts);
        Assert.Equal(AccountingCompleteness.Partial, result.AttemptCompleteness);
    }

    [Fact]
    public void FreezeIsImmutableAndRefusesLateAndDuplicateDispatch()
    {
        var before = new ProviderAttemptCapture(0, 0);
        before.ObserveNoDispatch();
        var frozen = before.Freeze(false, false);
        Assert.False(before.TryBeginDispatch());
        before.RecordUsage(ProviderUsageObservation.Create(10, 2, 7, 3));
        Assert.Same(frozen, before.Freeze(true, true));
        Assert.False(frozen.Dispatched);
        Assert.False(frozen.Usage.HasAny);

        var after = new ProviderAttemptCapture(0, 0);
        Assert.True(after.TryBeginDispatch());
        Assert.False(after.TryBeginDispatch());
        after.RecordUsage(ProviderUsageObservation.Create(10, 2, 7, 3));
        var pending = after.Freeze(false, false);
        after.RecordUsage(ProviderUsageObservation.Create(100, 20, 70, 30));
        Assert.Same(pending, after.Freeze(true, true));
        Assert.False(pending.Reconciled);
        Assert.Equal(10, pending.Usage.InputTokens);
    }

    [Fact]
    public void MixedUsageAndOverflowDoNotChangeIndependentAttemptFacts()
    {
        var review = new ReviewAccounting();
        var first = review.BeginCall().BeginAttempt();
        first.TryBeginDispatch();
        first.RecordUsage(ProviderUsageObservation.Create(long.MaxValue, 2, 0, long.MaxValue));
        first.Freeze(true, false);
        var second = review.BeginCall().BeginAttempt();
        second.TryBeginDispatch();
        second.RecordUsage(ProviderUsageObservation.Create(1, null));
        second.Freeze(true, false);
        var result = review.Finish();
        Assert.Null(result.InputTokens);
        Assert.Equal(2, result.OutputTokens);
        Assert.Equal(long.MaxValue, result.CacheMissTokens);
        Assert.Equal(2, result.ProviderAttempts);
        Assert.Equal(2, result.ProviderFailedAttempts);
        Assert.Equal(1, result.UnknownUsageAttempts);
        Assert.Equal(1, result.UnknownCachePartitionAttempts);
        Assert.Equal(AccountingCompleteness.Complete, result.AttemptCompleteness);
        Assert.Equal(AccountingCompleteness.Partial, result.UsageCompleteness);
    }

    [Fact]
    public void ObservationDoesNotInventPartitionsOrRepairContradictions()
    {
        var single = ProviderUsageObservation.Create(10, 3, 7);
        Assert.Equal(7, single.CacheHitTokens);
        Assert.Null(single.CacheMissTokens);
        var impossible = ProviderUsageObservation.Create(10, 3, 8, 8);
        Assert.Equal(10, impossible.InputTokens);
        Assert.Null(impossible.CacheHitTokens);
        Assert.Null(impossible.CacheMissTokens);
        var contradictory = ProviderUsageObservation.Create(10, 3, 7, 3, 12);
        Assert.False(contradictory.HasAny);
        var partial = ProviderUsageObservation.Create(-1, 3, 7, 3);
        Assert.Null(partial.InputTokens);
        Assert.Equal(3, partial.OutputTokens);
        Assert.Null(partial.CacheHitTokens);
    }

    [Fact]
    public void OnlyBoundedNumericFactsCrossTheOutcomeSeam()
    {
        var fields = typeof(ProviderAccounting).GetProperties(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.All(fields, field => Assert.Contains(field.PropertyType,
            new[] { typeof(int), typeof(long?), typeof(AccountingCompleteness) }));
        Assert.Throws<ArgumentException>(() => ProviderAccounting.Aggregate([
            new(0, 0, false, true, false, false, ProviderUsageObservation.Unknown),
        ]));
        var missing = AgentRunOutcome.Failure("failure", 0, 0, []);
        Assert.Null(missing.Accounting);
    }
}
