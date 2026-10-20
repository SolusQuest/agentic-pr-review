using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.Tests.Agent.Core;

public sealed class AgentStableRequestMaterializerTests
{
    // Captured from the unmodified eb2d4d0 implementation before SESSION retirement.
    // These identities bind the existing policy, tool registry, limits and private wire keys.
    [Theory]
    [InlineData(null, "65dc3672423bef9d7ee0d1a807943e7f113ed57273fb2f004b361c733b6d345f")]
    [InlineData("cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", "3e9a0890e209c9c8acab7351acafdb02f6c55a3b50160c2146954e0418e06aeb")]
    public void PreservesBaselineStableIdentityAndControlMessages(string? prior, string expected)
    {
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(Trusted(), prior, out var request));
        var bytes = AgentCanonical.StablePlanBytes(request!.StablePlan);
        Assert.Equal(expected, AgentCanonical.HashDomain(AgentCanonical.StablePlanDomain, bytes));
        using var plan = JsonDocument.Parse(bytes);
        Assert.Equal(prior, plan.RootElement.GetProperty("prior_session_sha256").GetString());
        var message = Assert.Single(request.ControlMessages);
        Assert.Equal("system", message.Role);
        Assert.Equal("Use the immutable reviewed snapshot for all evidence.",
            Assert.IsType<ProjectTextContent>(Assert.Single(message.Contents)).Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC")]
    public void RejectsInvalidPriorIdentity(string prior)
    {
        Assert.False(AgentStableRequestMaterializer.TryMaterialize(Trusted(), prior, out var request));
        Assert.Null(request);
    }

    [Fact]
    public void RejectsInvalidUtf8Policy()
    {
        Assert.False(AgentStableRequestMaterializer.TryMaterialize(
            Trusted() with { TrustedPolicyBytes = [255] }, null, out var request));
        Assert.Null(request);
    }

    private static AgentTrustedRequest Trusted() => new(
        "owner/repository", 109, "trusted-r3-workflow",
        Encoding.UTF8.GetBytes("Use the immutable reviewed snapshot for all evidence."),
        "build-109", DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model,
        DeepSeekAdapterContext.Adapter);
}
