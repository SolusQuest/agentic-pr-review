using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.Tests.Agent.Core;

// These existing generic assertions stand independently of SESSION construction or restore.
public sealed class AgentStableRequestMaterializerAuthorityTests
{
    [Fact]
    public void PolicyBytesOwnExactControlTextAndPolicyDigest()
    {
        var trustedA = Trusted() with { TrustedPolicyBytes = "trusted policy A"u8.ToArray() };
        var trustedB = trustedA with
        {
            TrustedPolicyBytes = "trusted policy B"u8.ToArray(),
        };
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(
            trustedA,
            priorSessionSha256: null,
            out var materializedA));
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(
            trustedB,
            priorSessionSha256: null,
            out var materializedB));
        Assert.Equal(
            "trusted policy A",
            Assert.IsType<ProjectTextContent>(
                Assert.Single(
                    Assert.Single(materializedA!.ControlMessages).Contents))
                .Text);
        Assert.Equal(
            "trusted policy B",
            Assert.IsType<ProjectTextContent>(
                Assert.Single(
                    Assert.Single(materializedB!.ControlMessages).Contents))
                .Text);
        Assert.NotEqual(
            materializedA.StablePlan.PolicySha256,
            materializedB.StablePlan.PolicySha256);
    }

    [Fact]
    public void SelectedLimitAuthoritiesRejectCrossedAdapterPairsBeforeMaterialization()
    {
        var currentAuthority = DeepSeekAdapterContext.LimitAuthorityFor(DeepSeekRequestProfile.Current);
        var candidateAuthority = DeepSeekAdapterContext.LimitAuthorityFor(DeepSeekRequestProfile.Output8192);
        var output65536Authority = DeepSeekAdapterContext.LimitAuthorityFor(DeepSeekRequestProfile.Output65536);
        var template = Trusted() with
        {
            ProviderId = DeepSeekAdapterContext.Provider,
            ModelId = DeepSeekAdapterContext.Model,
        };
        var current = template with
        {
            AdapterId = DeepSeekAdapterContext.Adapter,
            LimitAuthority = currentAuthority,
        };
        var candidate = template with
        {
            AdapterId = DeepSeekAdapterContext.CandidateAdapter,
            LimitAuthority = candidateAuthority,
        };
        var output65536 = template with
        {
            AdapterId = DeepSeekAdapterContext.Output65536Adapter,
            LimitAuthority = output65536Authority,
        };
        var crossedOld = current with { LimitAuthority = candidateAuthority };
        var crossedCandidate = candidate with { LimitAuthority = currentAuthority };
        var crossedOutput65536 = output65536 with { LimitAuthority = currentAuthority };
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(current, null, out var currentStable));
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(candidate, null, out var candidateStable));
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(output65536, null, out var output65536Stable));
        Assert.Equal(AgentCanonical.LimitsSha256(), currentStable!.StablePlan.LimitsSha256);
        Assert.Equal(AgentCanonical.LimitsSha256(AgentLimitProfile.Output8192),
            candidateStable!.StablePlan.LimitsSha256);
        Assert.Equal(AgentCanonical.LimitsSha256(AgentLimitProfile.Output65536),
            output65536Stable!.StablePlan.LimitsSha256);
        Assert.NotEqual(AgentCanonical.StablePlanSha256(currentStable.StablePlan),
            AgentCanonical.StablePlanSha256(candidateStable.StablePlan));
        Assert.NotEqual(AgentCanonical.StablePlanSha256(candidateStable.StablePlan),
            AgentCanonical.StablePlanSha256(output65536Stable.StablePlan));
        Assert.False(AgentStableRequestMaterializer.TryMaterialize(crossedOld, null, out _));
        Assert.False(AgentStableRequestMaterializer.TryMaterialize(crossedCandidate, null, out _));
        Assert.False(AgentStableRequestMaterializer.TryMaterialize(crossedOutput65536, null, out _));
    }

    private static AgentTrustedRequest Trusted() => new(
        "owner/repository", 109, "trusted-r3-workflow",
        Encoding.UTF8.GetBytes("Use the immutable reviewed snapshot for all evidence."),
        "build-109", DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model,
        DeepSeekAdapterContext.Adapter);
}
