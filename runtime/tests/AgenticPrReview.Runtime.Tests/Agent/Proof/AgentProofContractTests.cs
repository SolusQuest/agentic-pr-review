using System.Reflection;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.Tests.Agent.Proof;

public sealed class AgentProofContractTests
{
    private static readonly string FixtureRoot = Path.Combine(
        AppContext.BaseDirectory,
        "fixtures",
        "agent",
        "agent-loop");

    [Fact]
    public void ProofGoldensAreByteExactPublicMetadata()
    {
        var bootstrap = ReadGolden("bootstrap.json.golden");
        var continuation = ReadGolden("continue.json.golden");

        AssertGolden(
            bootstrap,
            "bootstrap",
            expectedGeneration: 0,
            expectedModelCalls: 2,
            expectedToolCalls: 5);
        AssertGolden(
            continuation,
            "continue",
            expectedGeneration: 1,
            expectedModelCalls: 1,
            expectedToolCalls: 1);
        Assert.Contains(
            "\"transition\":\"automatic_absent\"",
            Encoding.UTF8.GetString(bootstrap),
            StringComparison.Ordinal);
        Assert.Contains(
            "\"transition\":\"verified_ahead\"",
            Encoding.UTF8.GetString(continuation),
            StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAndLimitManifestsFreezeTheCompleteProofSurface()
    {
        var cases = File.ReadAllLines(
                Path.Combine(FixtureRoot, "negative-cases.txt"))
            .Where(line => line.Length > 0)
            .ToArray();

        Assert.Equal(cases.Length, cases.Distinct(StringComparer.Ordinal).Count());
        Assert.Subset(
            cases.ToHashSet(StringComparer.Ordinal),
            new[]
            {
                "missing-state-automatic",
                "bootstrap-non-current",
                "explicit-incompatible",
                "authorization-untrusted",
                "authorization-fork",
                "authorization-cross-repository",
                "head-same",
                "head-unknown",
                "head-diverged",
                "head-unrelated",
                "state-no-lineage",
                "state-tamper",
                "state-truncation",
                "state-oversize",
                "state-classification-invalid",
                "state-association-invalid",
                "state-cross-scope",
                "state-stale-replay",
                "state-header-magic",
                "state-header-namespace",
                "state-header-discriminator",
                "state-header-algorithm",
                "state-header-key-id",
                "state-header-length",
                "state-old-format-disguise",
                "state-current-downgrade",
                "state-accepted-newer-present",
                "state-accepted-newer-hidden",
                "state-outcome-unknown-retry",
                "state-randomized-envelope-conflict",
                "state-cleanup-failure",
                "path-rejection",
                "tool-rejection",
                "caller-cancellation",
                "invalid-terminal",
                "post-tool-chat-failure",
                "session-construction-limit",
                "continuation-limit",
                "state-capacity-limit",
                "redirect",
                "redirect-cross-host",
                "method-get",
                "header-default",
                "header-cookie",
                "header-proxy",
                "header-trace",
                "header-github",
                "header-ambient",
                "endpoint-abbreviated-host",
                "endpoint-integer-host",
                "endpoint-hex-host",
                "endpoint-octal-host",
                "endpoint-leading-zero-host",
                "endpoint-trailing-dot-host",
                "canary-leak-model",
                "canary-leak-durable",
                "canary-leak-transport",
                "canary-leak-diagnostic",
                "canary-leak-environment",
            }.ToHashSet(StringComparer.Ordinal));

        var limitLines = File.ReadAllLines(
            Path.Combine(FixtureRoot, "limit-cases.tsv"));
        Assert.Equal(
            "case\tstatus\taction\tcode\tmodel_calls\ttool_calls\tprovider_requests\tstore_calls\tsession_admissions\tstate_mutation\tlineage_mutation",
            limitLines[0]);
        var limitCases = new HashSet<string>(StringComparer.Ordinal);
        Assert.All(
            limitLines.Skip(1),
            line =>
            {
                var fields = line.Split('\t');
                Assert.Equal(11, fields.Length);
                Assert.True(limitCases.Add(fields[0]));
                Assert.Contains(fields[0], cases);
                Assert.Contains(fields[1], new[] { "passed", "failed" });
                Assert.True(int.TryParse(fields[4], out _));
                Assert.True(int.TryParse(fields[5], out _));
                Assert.True(int.TryParse(fields[6], out _));
                Assert.True(int.TryParse(fields[7], out _));
                Assert.True(int.TryParse(fields[8], out _));
                Assert.Contains(fields[9], new[] { "true", "false" });
                Assert.Contains(fields[10], new[] { "true", "false" });
            });
    }

    [Fact]
    public void ProductProofModulesComeFromTheRuntimeAssembly()
    {
        var runtime = typeof(AgentLoop).Assembly;
        Assert.Same(runtime, typeof(MinimalChatClient).Assembly);
        Assert.Same(runtime, typeof(AgentSessionBuilder).Assembly);
        Assert.Same(runtime, typeof(RestrictedStateService).Assembly);

        var namespaces = runtime.GetTypes()
            .Select(type => type.Namespace)
            .Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("AgenticPrReview.Runtime.Agent.Loop", namespaces);
        Assert.Contains("AgenticPrReview.Runtime.Agent.Session", namespaces);
        Assert.Contains("AgenticPrReview.Runtime.Host.State", namespaces);
        Assert.DoesNotContain(
            runtime.GetTypes(),
            type => type.Name.Contains(
                "ProviderResponseDto",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(AgentSessionDocument)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => property.Name.Contains(
                "Wire",
                StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] ReadGolden(string name)
    {
        var bytes = File.ReadAllBytes(
            Path.Combine(FixtureRoot, "expected", name));
        Assert.NotEmpty(bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.DoesNotContain((byte)'\r', bytes);
        return bytes;
    }

    private static void AssertGolden(
        byte[] bytes,
        string phase,
        long expectedGeneration,
        int expectedModelCalls,
        int expectedToolCalls)
    {
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("NEBULA-", text, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-r2-", text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Synthetic public reasoning marker",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("reviewed/fact.txt", text, StringComparison.Ordinal);
        Assert.DoesNotContain("apr-canary-", text, StringComparison.Ordinal);
        Assert.DoesNotContain(":\\", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/", text, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Assert.Equal("apr-agent-loop-proof", root.GetProperty("kind").GetString());
        Assert.Equal(phase, root.GetProperty("phase").GetString());
        Assert.Equal("passed", root.GetProperty("status").GetString());
        Assert.Equal(expectedGeneration, root.GetProperty("generation").GetInt64());
        Assert.Equal(expectedModelCalls, root.GetProperty("model_calls").GetInt32());
        Assert.Equal(expectedToolCalls, root.GetProperty("tool_calls").GetInt32());
        Assert.True(root.GetProperty("thinking_required").GetBoolean());

        foreach (var name in new[]
        {
            "session_identity_sha256",
            "stable_plan_sha256",
            "limits_sha256",
            "toolset_sha256",
        })
        {
            AssertLowerHexSha256(root.GetProperty(name).GetString());
        }

        Assert.All(
            root.GetProperty("request_sha256").EnumerateArray(),
            value => AssertLowerHexSha256(value.GetString()));
    }

    private static void AssertLowerHexSha256(string? value)
    {
        Assert.NotNull(value);
        Assert.Equal(64, value.Length);
        Assert.All(
            value,
            character => Assert.True(
                character is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }
}
