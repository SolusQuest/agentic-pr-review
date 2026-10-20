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

    [Theory]
    [InlineData("valid", "APR_SOLUS_COMPOSITION_REQUIRED")]
    [InlineData("unknown", LiveAgentFreshProcessCodes.TransitionRejected)]
    [InlineData("diverged", LiveAgentFreshProcessCodes.TransitionRejected)]
    [InlineData("unrelated", LiveAgentFreshProcessCodes.TransitionRejected)]
    [InlineData("automatic", LiveAgentFreshProcessCodes.TransitionRejected)]
    [InlineData("reused-invocation", LiveAgentFreshProcessCodes.ProcessIdentityReused)]
    [InlineData("null-producer", LiveAgentFreshProcessCodes.LineageInvalid)]
    public async Task CurrentArgvPhaseGuardPublishesExactFailureWithoutAdvancingState(
        string mutation, string expectedCode)
    {
        using var fixture = new FreshProcessFixture();
        var trusted = Trusted();
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var stable));
        var scope = Scope(trusted, stable!, "session-109");
        var identity = new ReviewedIdentity(scope.RepositoryId, scope.ReviewTarget,
            new string('a', 40), new string('c', 40));
        var producerHead = new string('b', 40);
        // Authored outer metadata exercises the current argv guard before
        // composition. There is no accepted object, SDK payload or source grant.
        var lineage = LiveAgentFreshProcessCodec.Write(new LiveAgentFreshProcessLineageDocument(
            LiveAgentFreshProcessDomain.LineageKind, LiveAgentFreshProcessDomain.ScopeDocument(scope),
            identity.BaseSha, producerHead, 0, new string('d', 64), new string('e', 64), null,
            1_800_000_000, 1_800_000_000 + RestrictedStateFormat.MaximumRetentionSeconds, true,
            LiveAgentFreshProcessDomain.InvocationIdentitySha256(
                mutation == "reused-invocation" ? "guard-current" : "guard-source")));
        if (mutation == "null-producer")
        {
            var valid = System.Text.Encoding.UTF8.GetString(lineage);
            var invalid = valid.Replace($"\"producer_base_sha\":\"{identity.BaseSha}\"",
                "\"producer_base_sha\":null", StringComparison.Ordinal);
            Assert.NotEqual(valid, invalid);
            lineage = System.Text.Encoding.UTF8.GetBytes(invalid);
        }
        File.WriteAllBytes(fixture.LineagePath, lineage);
        fixture.WritePhase(identity, "Review the immutable snapshot.", "Current safe content.\n",
            "guard-current", "current", mutation == "automatic" ? "automatic" : "explicit",
            mutation is "unknown" or "diverged" or "unrelated" ? mutation : "verified_ahead",
            LiveAgentFreshProcessDomain.RawSha256(lineage), producerHead);
        var untouched = "PRIVATE_EXISTING_STATE_CANARY"u8.ToArray();
        var statePath = Path.Join(fixture.StateRoot, "unrelated-state");
        File.WriteAllBytes(statePath, untouched);

        var command = await LiveAgentFreshProcessCommand.RunAsync(
            ["review-live-agent-r3", "--mode", "continue", "--root", fixture.Root], CancellationToken.None);

        Assert.Equal(1, command.ExitCode);
        Assert.Equal(expectedCode, command.DiagnosticCode);
        var result = fixture.ReadResult();
        Assert.Equal(expectedCode, result.Code);
        Assert.Equal(0, result.ModelCalls);
        Assert.Equal(0, result.ToolCalls);
        Assert.Null(result.Generation);
        Assert.False(result.HandoffReady);
        Assert.Equal(lineage, File.ReadAllBytes(fixture.LineagePath));
        Assert.Equal(statePath, Assert.Single(Directory.EnumerateFiles(
            fixture.StateRoot, "*", SearchOption.AllDirectories)));
        Assert.Equal(untouched, File.ReadAllBytes(statePath));
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
