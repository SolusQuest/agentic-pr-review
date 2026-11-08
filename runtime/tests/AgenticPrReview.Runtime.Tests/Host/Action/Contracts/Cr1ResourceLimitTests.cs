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
    [Fact]
    public void ProviderLimitDiagnosticAdmitsOnlyFiniteCoherentOriginsAndRetainsBounds()
    {
        foreach (var kind in Enum.GetValues<ActionHostProviderLimitKind>())
        {
            Assert.True(ActionHostAttemptDiagnostic.TryCreate(1, true, ActionHostRuntimeDiagnosticValue.Rejected,
                ActionHostRuntimeDiagnosticValue.LimitExceeded, ActionHostRuntimeDiagnosticValue.Dispatched,
                ActionHostRuntimeDiagnosticValue.None, true, out var attempt, kind));
            Assert.Equal(kind, attempt!.LimitKind);
            Assert.Matches("^[a-z_]+$", ActionHostAttemptDiagnostic.LimitLabel(kind));
            var attempts = Enumerable.Range(1, 6).Select(i =>
            {
                Assert.True(ActionHostAttemptDiagnostic.TryCreate(i, true, ActionHostRuntimeDiagnosticValue.Rejected,
                    ActionHostRuntimeDiagnosticValue.LimitExceeded, ActionHostRuntimeDiagnosticValue.Dispatched,
                    ActionHostRuntimeDiagnosticValue.None, true, out var value, kind));
                return value!;
            }).ToArray();
            Assert.True(ActionHostRuntimeFailureDiagnostic.TryCreate(attempts, ActionHostRuntimeDiagnosticValue.RuntimeLimit,
                ActionHostRuntimeDiagnosticValue.ResourceLimit, out var diagnostic));
            Assert.InRange(diagnostic!.Render().Length, 1, 2048);
            if (kind != ActionHostProviderLimitKind.Unknown)
                Assert.False(ActionHostAttemptDiagnostic.TryCreate(1, true, ActionHostRuntimeDiagnosticValue.Succeeded,
                    ActionHostRuntimeDiagnosticValue.None, ActionHostRuntimeDiagnosticValue.Dispatched,
                    ActionHostRuntimeDiagnosticValue.None, true, out _, kind));
        }
        Assert.True(ActionHostAttemptDiagnostic.TryCreate(1, null, ActionHostRuntimeDiagnosticValue.Unknown,
            ActionHostRuntimeDiagnosticValue.Unknown, ActionHostRuntimeDiagnosticValue.Unknown,
            ActionHostRuntimeDiagnosticValue.Unknown, null, out var oldConstructor));
        Assert.Equal(ActionHostProviderLimitKind.Unknown, oldConstructor!.LimitKind);
        Assert.False(ActionHostAttemptDiagnostic.TryCreate(1, true, ActionHostRuntimeDiagnosticValue.Rejected,
            ActionHostRuntimeDiagnosticValue.LimitExceeded, ActionHostRuntimeDiagnosticValue.Dispatched,
            ActionHostRuntimeDiagnosticValue.None, true, out _, (ActionHostProviderLimitKind)999));
        Assert.Equal("unknown", ActionHostAttemptDiagnostic.LimitLabel((ActionHostProviderLimitKind)999));
    }

}
