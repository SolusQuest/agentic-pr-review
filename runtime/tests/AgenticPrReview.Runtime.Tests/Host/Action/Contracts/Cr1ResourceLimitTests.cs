using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Serialization;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Contracts;

public sealed class Cr1ResourceLimitTests
{
    [Fact]
    public void ResourceLimitProjectsWithoutRenumberingExistingReasonsOrAllowingCleanReview()
    {
        Assert.Equal(13, (int)ActionHostTerminationReason.HostFailure);
        Assert.Equal(14, (int)ActionHostTerminationReason.ResourceLimit);
        Assert.True(ActionHostStepSummary.TryCreate(null, null, null, ActionHostStateDisposition.NotCommitted, out var failure));
        Assert.True(ActionHostAnnotation.TryCreate(ActionHostAnnotationCode.ProviderFailed, out var annotation));
        Assert.True(ActionHostCompletion.TryCreate("r4-h1", ActionHostStatus.ProviderFailed, failure, [annotation!],
            out var completion, ActionHostAccounting.Unavailable, ActionHostTerminationReason.ResourceLimit));
        Assert.True(ActionHostJsonCodec.TryWriteCompletion(completion, out var wire));
        Assert.Equal("resource_limit", FinalProjectionAssertions.Read(wire).Outputs.TerminationReason);
        Assert.False(ActionHostCompletion.TryCreate("r4-h1", ActionHostStatus.ProviderFailed, failure, [annotation!],
            out _, ActionHostAccounting.Unavailable, (ActionHostTerminationReason)999));
        Assert.True(ActionHostStepSummary.TryCreate(new string('a', 40), "https://github.com/o/r/pull/1", 0,
            ActionHostStateDisposition.Accepted, out var summary));
        Assert.False(ActionHostCompletion.TryCreate("r4-h1", ActionHostStatus.Reviewed, summary, [], out _,
            ActionHostAccounting.Unavailable, ActionHostTerminationReason.ResourceLimit));
    }
}
