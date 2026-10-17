using System.Net;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Execution.Solus;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;

static class AccountingBridgeTests
{
    internal static async Task Run()
    {
        var id = Guid.NewGuid(); var logical = Guid.NewGuid(); var count = 0;
        using var handler = new ControlledHandler((_, _) => Task.FromResult(++count == 1
            ? F.Http(F.Response(F.Usage(3, 2, 1, 2)), HttpStatusCode.ServiceUnavailable)
            : F.Http(F.Response(F.Usage(7, 4, 3, 4)))));
        using var provider = F.Provider(handler);
        var first = await provider.ExchangeAsync(F.Request(id, logical));
        var second = await provider.ExchangeAsync(F.Request(id, logical, 2));
        F.Check(first.Retry?.Kind == ProviderRetryKind.Transient, "retry_classification");
        var usage = new AgentRunUsage(id, UsageInventoryCoverage.Complete, [first.Observation, second.Observation]);
        var value = SolusReviewFacts.Project(usage, [Fact(first), Fact(second)]);
        F.Check(value.ModelCalls == 1 && value.ProviderAttempts == 2 && value.ProviderRetries == 1 && value.ProviderFailedAttempts == 1, "logical_physical_identity");
        F.Check(value.InputTokens == 10 && value.OutputTokens == 6 && value.CacheHitTokens == 4 && value.CacheMissTokens == 6 &&
            value.UsageCompleteness == AccountingCompleteness.Complete, "retry_measurements");
        F.Throws<ArgumentException>(() => SolusReviewFacts.Project(usage, [Fact(first), Fact(first)]), "duplicate_fact");
        var old = new AgentRunUsage(Guid.NewGuid(), UsageInventoryCoverage.Complete, []);
        var current = new SolusReviewFacts(id, new(100, 100, 1024));
        F.Check(current.Freeze(old) is null, "historical_not_current");
        var unavailable = new AgentRunUsage(id, UsageInventoryCoverage.Unavailable, []);
        F.Throws<ArgumentException>(() => SolusReviewFacts.Project(unavailable, []), "unavailable_not_zero");
        F.Check(SolusReviewFacts.Project(new(id, UsageInventoryCoverage.Complete, []), []).UsageCompleteness == AccountingCompleteness.Complete, "known_empty_zero");

        foreach (var item in new[]
        {
            (Body: F.Response(), Unknown: 1, Cache: 0, Completeness: AccountingCompleteness.Unavailable),
            (Body: F.Response(new { prompt_tokens = 10, completion_tokens = 2, prompt_cache_hit_tokens = 4 }), Unknown: 0, Cache: 1, Completeness: AccountingCompleteness.Partial),
            (Body: F.Response(new { completion_tokens = 2 }), Unknown: 1, Cache: 0, Completeness: AccountingCompleteness.Partial),
            (Body: F.Response(F.Usage(0, 0, 0, 0)), Unknown: 0, Cache: 0, Completeness: AccountingCompleteness.Complete),
        })
        {
            using var h = new ControlledHandler((_, _) => Task.FromResult(F.Http(item.Body)));
            using var p = F.Provider(h); var result = await p.ExchangeAsync(F.Request(Guid.NewGuid()));
            var projected = SolusReviewFacts.Project(new(result.Observation.ExecutionId, UsageInventoryCoverage.Complete, [result.Observation]), [Fact(result)]);
            F.Check(projected.UnknownUsageAttempts == item.Unknown && projected.UnknownCachePartitionAttempts == item.Cache &&
                projected.UsageCompleteness == item.Completeness, "nullable_measurements");
            if (item.Cache == 1) F.Check(projected.CacheHitTokens == 4 && projected.CacheMissTokens == 0, "no_partition_inference");
        }
        var overflowId = Guid.NewGuid();
        using var overHandler = new ControlledHandler((_, _) => Task.FromResult(F.Http(F.Response(F.Usage(long.MaxValue, 0, 0, long.MaxValue)))));
        using var overProvider = F.Provider(overHandler);
        var a = await overProvider.ExchangeAsync(F.Request(overflowId));
        var b = await overProvider.ExchangeAsync(F.Request(overflowId));
        var overflow = SolusReviewFacts.Project(new(overflowId, UsageInventoryCoverage.Complete, [a.Observation, b.Observation]), [Fact(a), Fact(b)]);
        F.Check(overflow.InputTokens is null && overflow.CacheMissTokens is null && overflow.UsageCompleteness == AccountingCompleteness.Partial, "checked_overflow");
        var ambiguousId = Guid.NewGuid();
        using var ambiguousHandler = new ControlledHandler((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "SYNTHETIC_EXCEPTION_CANARY"));
        using var ambiguousProvider = F.Provider(ambiguousHandler);
        var unknown = await ambiguousProvider.ExchangeAsync(F.Request(ambiguousId));
        F.Check(unknown.Observation.Exposure == DispatchExposure.Unknown, "ambiguous_exposure");
        var unknownValue = SolusReviewFacts.Project(new(ambiguousId, UsageInventoryCoverage.Complete, [unknown.Observation]), [Fact(unknown)]);
        F.Check(unknownValue.ProviderAttempts == 0 && unknownValue.ModelCalls == 1 &&
            unknownValue.AttemptCompleteness == AccountingCompleteness.Unavailable, "unknown_not_no_send");
    }
    private static SolusAttemptFact Fact(ProviderExchangeResult result) => new(result.Observation, result.Outcome, RuntimeStop.None);
}
