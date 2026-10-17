using System.Text;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Serialization;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Contracts;

public sealed class Cr1ResourceLimitTests
{
    [Fact]
    public void ResourceLimitRoundTripsWithoutRenumberingExistingReasonsOrAllowingCleanReview()
    {
        Assert.Equal(13, (int)ActionHostTerminationReason.HostFailure);
        Assert.Equal(14, (int)ActionHostTerminationReason.ResourceLimit);
        // Existing unavailable failure wire is independently authored here.
        var wire = Encoding.UTF8.GetBytes("""
        {"build_discriminator":"r4-h1","status":"provider_failed","exit_class":"provider","process_exit_code":1,
        "summary":{"reviewed_sha":null,"publication_url":null,"finding_count":null,"state_disposition":"not_committed"},
        "annotations":[{"code":"provider_failed","severity":"error","message":"The review provider failed."}],
        "accounting":{"model_calls":null,"provider_attempts":null,"provider_retries":null,"provider_failed_attempts":null,
        "provider_unknown_usage_attempts":null,"provider_unknown_cache_partition_attempts":null,"input_tokens":null,
        "input_cache_hit_tokens":null,"input_cache_miss_tokens":null,"output_tokens":null,
        "attempt_accounting_completeness":"unavailable","usage_completeness":"unavailable"},"termination_reason":"resource_limit"}
        """);
        Assert.True(ActionHostJsonCodec.TryReadCompletion(wire, out var completion, out _));
        Assert.Equal(ActionHostTerminationReason.ResourceLimit, completion!.TerminationReason);
        Assert.True(ActionHostJsonCodec.TryWriteCompletion(completion, out var roundTrip));
        Assert.Contains("\"termination_reason\":\"resource_limit\"", Encoding.UTF8.GetString(roundTrip));
        Assert.False(ActionHostJsonCodec.TryReadCompletion(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(wire).Replace("resource_limit", "resource_limit_extra")), out _, out _));
        Assert.True(ActionHostStepSummary.TryCreate(new string('a', 40), "https://github.com/o/r/pull/1", 0,
            ActionHostStateDisposition.Accepted, out var summary));
        Assert.False(ActionHostCompletion.TryCreate("r4-h1", ActionHostStatus.Reviewed, summary, [], out _,
            ActionHostAccounting.Unavailable, ActionHostTerminationReason.ResourceLimit));
    }
}
