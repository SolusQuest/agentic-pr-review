using System.Text;
using AgenticPrReview.Runtime.ActionHost.Policy;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Tests.Agent.Loop;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Policy;

public sealed partial class ActionHostTrustedPolicyTests
{
    internal static byte[] ReviewConfig(string? review)
    {
        var original = Encoding.UTF8.GetString(Config("sticky", null));
        return Encoding.UTF8.GetBytes(review is null ? original : original[..^1] + ",\"review\":" + review + "}");
    }

    internal static async Task<ActionHostTrustedPolicyMaterialization> MaterializeReview(string? review)
    {
        var (request, _) = await Request();
        return await ActionHostTrustedPolicy.MaterializeAsync(request,
            ScriptedObjectTransport.Valid(ReviewConfig(review), "instructions"u8.ToArray()), default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"maxModelCalls\":64,\"maxUncachedInputTokens\":2000000,\"maxCachedInputTokens\":38000000,\"maxOutputTokens\":524288,\"timeoutSeconds\":900}")]
    public async Task OmittedEmptyAndExplicitDefaultsPreserveEffectiveIdentity(string? review)
    {
        var result = await MaterializeReview(review);
        Assert.True(result.Succeeded);
        var authority = result.Policy!.LimitAuthority;
        Assert.Equal(64, authority.ModelCalls);
        Assert.Equal(900, authority.TimeoutSeconds);
        Assert.Equal(new ReviewTokenBudget(2_000_000, 38_000_000, 524_288), authority.TokenBudget);
        Assert.Equal(AgentCanonical.LimitsSha256(), result.Policy.LimitsSha256);
        Assert.Equal(AgentCanonical.LimitsBytes(), AgentCanonical.LimitsBytes(authority.Profile,
            authority.TokenBudget, authority.ModelCalls, authority.TimeoutSeconds));
    }

    [Theory]
    [InlineData("maxModelCalls", 1, "model_calls")]
    [InlineData("maxModelCalls", 128, "model_calls")]
    [InlineData("maxUncachedInputTokens", 1, "uncached_input_tokens")]
    [InlineData("maxCachedInputTokens", 1, "cached_input_tokens")]
    [InlineData("maxOutputTokens", 1, "output_tokens")]
    [InlineData("timeoutSeconds", 1, "deadline_seconds")]
    public async Task EachEffectiveOverrideChangesOnlyItsCanonicalRow(string field, int value, string row)
    {
        var baseline = (await MaterializeReview(null)).Policy!;
        var result = await MaterializeReview("{\"" + field + "\":" + value + "}");
        Assert.True(result.Succeeded);
        var policy = result.Policy!;
        var authority = policy.LimitAuthority;
        var registry = AgentLimits.RegistryFor(authority.Profile, authority.TokenBudget, authority.ModelCalls, authority.TimeoutSeconds);
        Assert.Equal(value, registry.Single(item => item.Name == row).Value);
        Assert.Equal(AgentLimits.Registry.Where(item => item.Name != row), registry.Where(item => item.Name != row));
        Assert.NotEqual(baseline.LimitsSha256, policy.LimitsSha256);
        Assert.NotEqual(baseline.PolicySha256, policy.PolicySha256);
        Assert.Equal(baseline.AdapterId, policy.AdapterId);
        Assert.Equal(baseline.Endpoint, policy.Endpoint);
        Assert.Equal(baseline.ToolsetSha256, policy.ToolsetSha256);
    }

    [Fact]
    public async Task AllMinimumFieldsMaterializeTogetherAndRawConfigIdentityStillMatters()
    {
        var minimum = await MaterializeReview("{\"maxModelCalls\":1,\"maxUncachedInputTokens\":1,\"maxCachedInputTokens\":1,\"maxOutputTokens\":1,\"timeoutSeconds\":1}");
        Assert.True(minimum.Succeeded);
        Assert.Equal(new ReviewTokenBudget(1, 1, 1), minimum.Policy!.LimitAuthority.TokenBudget);
        var omitted = (await MaterializeReview(null)).Policy!;
        var empty = (await MaterializeReview("{}")).Policy!;
        Assert.Equal(omitted.LimitsSha256, empty.LimitsSha256);
        Assert.NotEqual(omitted.PolicySha256, empty.PolicySha256);
    }

    public static IEnumerable<object[]> InvalidReviewValues()
    {
        foreach (var value in new[] { "null", "[]", "true", "1", "\"review\"", "{\"endpoint\":\"secret-canary\"}",
            "{\"provider\":\"other\"}", "{\"key\":\"secret-canary\"}", "{\"toolCalls\":513}", "{\"MaxModelCalls\":1}",
            "{\"maxModelCalls\":1,\"maxModelCalls\":2}", "{\"maxOutputTokens\":1,\"maxOutputTokens\":1}" })
            yield return [value];
        foreach (var (field, maximum) in new[] { ("maxModelCalls", 128), ("maxUncachedInputTokens", 2_000_000),
            ("maxCachedInputTokens", 38_000_000), ("maxOutputTokens", 524_288), ("timeoutSeconds", 900) })
        {
            foreach (var value in new[] { "0", "-1", (maximum + 1).ToString(), "null", "true", "\"1\"", "1.5", "{}", "[]", "9223372036854775808" })
                yield return ["{\"" + field + "\":" + value + "}"];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidReviewValues))]
    public async Task InvalidReviewFailsClosedWithoutLeakingValues(string review)
    {
        var result = await MaterializeReview(review);
        Assert.False(result.Succeeded);
        Assert.Null(result.Policy);
        Assert.Equal(ActionHostTrustedPolicyFailure.MalformedConfig, result.Failure);
        Assert.DoesNotContain("secret-canary", result.ToString());
    }

    [Fact]
    public async Task DuplicateReviewObjectIsRejected()
    {
        var (request, _) = await Request();
        var config = Encoding.UTF8.GetString(ReviewConfig("{}"));
        config = config[..^1] + ",\"review\":{}}";
        var result = await ActionHostTrustedPolicy.MaterializeAsync(request,
            ScriptedObjectTransport.Valid(Encoding.UTF8.GetBytes(config), "instructions"u8.ToArray()), default);
        Assert.Equal(ActionHostTrustedPolicyFailure.MalformedConfig, result.Failure);
    }

    [Fact]
    public async Task ConfiguredBudgetComesOnlyFromTheTrustedCommit()
    {
        var (request, scenario) = await Request();
        var transport = ScriptedObjectTransport.Valid(ReviewConfig("{\"maxModelCalls\":2}"), "instructions"u8.ToArray());
        // A conflicting reviewed-head blob exists, but it is not in the trusted tree.
        var hostile = new string('f', 40);
        transport.Blobs[hostile] = new(hostile, ReviewConfig("{\"maxModelCalls\":128}"));
        var result = await ActionHostTrustedPolicy.MaterializeAsync(request, transport, default);
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Policy!.LimitAuthority.ModelCalls);
        Assert.Equal("commit:" + request.WorkflowCommitSha, transport.Calls[0]);
        Assert.DoesNotContain(transport.Calls, call => call.Contains(scenario.Transport.PullRequest.HeadSha, StringComparison.Ordinal));
        Assert.DoesNotContain(transport.Calls, call => call.Contains(hostile, StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultAndRetainedProfilesDoNotInheritTheConfigurableCeiling()
    {
        Assert.Equal(64, AgentLimits.ModelCalls);
        Assert.Equal(128, AgentLimits.ModelCallsCeiling);
        foreach (var profile in Enum.GetValues<AgentLimitProfile>())
            Assert.Equal(64, AgentLimits.RegistryFor(profile).Single(row => row.Name == "model_calls").Value);
        foreach (var profile in new[] { AgentLimitProfile.Output8192, AgentLimitProfile.Output65536 })
        {
            Assert.False(AgentLimitAuthority.TryResolve("adapter", new("adapter", profile, ModelCalls: 128), out _));
            Assert.False(AgentLimitAuthority.TryResolve("adapter", new("adapter", profile, TimeoutSeconds: 1), out _));
            Assert.Equal(300, AgentLimits.RegistryFor(profile).Single(row => row.Name == "deadline_seconds").Value);
        }
        foreach (var limit in new[] { 1, 64, 128 })
        {
            var accounting = new ReviewAccounting(limit);
            for (var i = 0; i < limit; i++) accounting.BeginCall().BeginAttempt().Freeze(true, false);
            Assert.Equal(limit, accounting.Finish().ModelCalls);
            Assert.Throws<InvalidOperationException>(() => accounting.BeginCall());
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReviewAccounting(129));
        Assert.Throws<ArgumentException>(() => ProviderAccounting.Aggregate(Enumerable.Range(0, 129)
            .Select(i => new ProviderAttemptObservation(i, 0, true, false, true, false, ProviderUsageObservation.Unknown)).ToArray()));
    }

    [Theory]
    [InlineData(2, 10, 2)]
    [InlineData(10, 2, 2)]
    public async Task ConfiguredDeadlineAndSmallerHostHeadroomShareOneClock(int configured, int host, int expected)
    {
        var policy = (await MaterializeReview("{\"timeoutSeconds\":" + configured + "}")).Policy!;
        var hash = policy.LimitsSha256;
        var clock = new DeadlineTestClock();
        var deadline = new ReviewDeadline(clock, AgentLimitProfile.Current, TimeSpan.FromSeconds(host), policy.LimitAuthority.TimeoutSeconds);
        Assert.Equal(TimeSpan.FromSeconds(expected), deadline.Remaining);
        clock.Advance(TimeSpan.FromSeconds(expected));
        Assert.True(deadline.Expired);
        Assert.Equal(hash, AgentCanonical.LimitsSha256(policy.LimitAuthority));
    }
}
