using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Agent.Core;

namespace AgenticPrReview.Runtime.Tests.Agent;

public sealed class Cr1IndependentProviderMeasurementsTests
{
    [Fact]
    public void IndependentSubsetDoesNotInventItsMissingParentOrChangeNativeNormalization()
    {
        var independent = ProviderUsageObservation.FromIndependentMeasurements(null, 2, 4, null);
        Assert.Null(independent.InputTokens);
        Assert.Equal(2, independent.OutputTokens);
        Assert.Equal(4, independent.CacheHitTokens);
        Assert.Null(independent.CacheMissTokens);
        Assert.Null(ProviderUsageObservation.Create(null, 2, 4).CacheHitTokens);
        var accounting = ProviderAccounting.Aggregate([new(0, 0, true, true, true, true, independent)]);
        Assert.Equal(AccountingCompleteness.Partial, accounting.UsageCompleteness);
        Assert.Throws<InvalidOperationException>(() => ActionHostAccounting.FromProvider(accounting));
    }

    [Fact]
    public void RepresentablePartialSubtotalsKeepEveryKnownDetail()
    {
        var accounting = ProviderAccounting.Aggregate([
            new(0, 0, true, true, true, true, ProviderUsageObservation.FromIndependentMeasurements(10, 5, 4, null)),
            new(1, 0, true, true, true, true, ProviderUsageObservation.FromIndependentMeasurements(null, 2, 2, null)),
        ]);
        Assert.Equal(10, accounting.InputTokens);
        Assert.Equal(7, accounting.OutputTokens);
        Assert.Equal(6, accounting.CacheHitTokens);
        Assert.Equal(AccountingCompleteness.Partial, ActionHostAccounting.FromProvider(accounting).UsageCompleteness);
    }

    [Fact]
    public void KnownParentConstraintsAndNegativeMeasurementsStillReject()
    {
        var invalid = ProviderUsageObservation.FromIndependentMeasurements(10, 2, 4, 8);
        Assert.Null(invalid.CacheHitTokens);
        Assert.Null(invalid.CacheMissTokens);
        Assert.Null(ProviderUsageObservation.FromIndependentMeasurements(null, 2, -1, null).CacheHitTokens);
    }
}
