using System.Reflection;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.Tests.Application;

public sealed partial class LiveAgentFreshProcessTests
{
    [Theory]
    [InlineData("absent")]
    [InlineData("non_current")]
    public void CurrentAutomaticContinueSelectsFreshPhaseBeforeComposition(string locator)
    {
        var trusted = Trusted();
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var stable));
        var scope = Scope(trusted, stable!, "session-109");
        var identity = new ReviewedIdentity(scope.RepositoryId, scope.ReviewTarget,
            new string('a', 40), new string('b', 40));
        var document = Authorization(trusted, scope, scope.SessionId, "phase_fresh", locator,
            "automatic", "same_head", identity.HeadSha, identity.BaseSha, identity.HeadSha, null);
        Assert.True(LiveAgentFreshProcessCommand.TryAdmitAuthorization(document,
            out var input, out _, out _));

        var selected = InvokeCurrentPhase(input!, identity, null);

        Assert.True(selected.Admitted);
        Assert.Equal(0, selected.Generation);
        Assert.Null(selected.Predecessor);
        Assert.Equal("bootstrap", selected.Phase);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("diverged")]
    [InlineData("unrelated")]
    [InlineData("automatic")]
    [InlineData("reused-invocation")]
    public void CurrentSelectedPhaseRejectsTransitionIntentAndInvocationBeforeComposition(string mutation)
    {
        var trusted = Trusted();
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var stable));
        var scope = Scope(trusted, stable!, "session-109");
        var identity = new ReviewedIdentity(scope.RepositoryId, scope.ReviewTarget,
            new string('a', 40), new string('c', 40));
        var producer = identity with { HeadSha = new string('b', 40) };
        var rawSha256 = new string('c', 64);
        // Authored metadata isolates the actual phase guard. It does not issue
        // a context claim, prove acceptance, or reconstruct a runtime payload.
        var prior = new LiveAgentFreshProcessAdmittedLineage(
            new(scope, 0, new string('d', 64), new string('e', 64), null,
                1_800_000_000, 1_800_000_000 + RestrictedStateFormat.MaximumRetentionSeconds, true),
            producer, new string('f', 64), new(1, 2, 3), rawSha256);
        LiveAgentFreshProcessAuthorizationDocument Document(string intent, string transition) =>
            Authorization(trusted, scope, scope.SessionId, "phase_current", "current", intent,
                transition, producer.HeadSha, identity.BaseSha, identity.HeadSha, rawSha256);
        Assert.True(LiveAgentFreshProcessCommand.TryAdmitAuthorization(
            Document("explicit", "verified_ahead"), out var control, out _, out _));
        var accepted = InvokeCurrentPhase(control!, identity, prior);
        Assert.True(accepted.Admitted);
        Assert.Equal(1, accepted.Generation);
        Assert.Equal(prior.Lineage.EnvelopeSha256, accepted.Predecessor);
        Assert.Equal("continue", accepted.Phase);

        var document = Document(mutation == "automatic" ? "automatic" : "explicit",
            mutation is "unknown" or "diverged" or "unrelated" ? mutation : "verified_ahead");
        Assert.True(LiveAgentFreshProcessCommand.TryAdmitAuthorization(document,
            out var input, out _, out _));
        var selected = InvokeCurrentPhase(input!, identity,
            mutation == "reused-invocation" ? prior with { InvocationIdentitySha256 = input!.InvocationIdentitySha256 } : prior);

        Assert.False(selected.Admitted);
        Assert.Equal(0, selected.Generation);
        Assert.Null(selected.Predecessor);
        Assert.Null(selected.Phase);
    }

    private static (bool Admitted, long Generation, string? Predecessor, string? Phase) InvokeCurrentPhase(
        LiveAgentFreshProcessAuthorizedInput input, ReviewedIdentity identity,
        LiveAgentFreshProcessAdmittedLineage? prior)
    {
        var method = typeof(LiveAgentFreshProcessCommand).GetMethod("TryAdmitPhaseAndTransition",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        object?[] arguments = ["continue", input, identity, prior, 0L, null, null];
        var admitted = Assert.IsType<bool>(method.Invoke(null, arguments));
        return (admitted, Assert.IsType<long>(arguments[4]), (string?)arguments[5], (string?)arguments[6]);
    }
}
