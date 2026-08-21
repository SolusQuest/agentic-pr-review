using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHostVerifierFixture;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed class ActionHostAotProofTests : IDisposable
{
    private const string Kind = "apr-r4-e2-action-host-build-pair-v1";
    private readonly string root = Path.Join(Path.GetTempPath(),
        "apr-r4-e2-aot-proof-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ManagedArchitecturePinsRequiredFixtureAndRuntimeTypes()
    {
        var audited = ManagedArchitectureAudit.TryAudit(FixtureAssembly,
            RuntimeAssembly, out var digest);
        Assert.True(audited, string.Join(Environment.NewLine,
            ManagedArchitectureAudit.CallSitesForTesting(FixtureAssembly,
            [
                "AgenticPrReview.Runtime.ActionHost.ActionHostDeepSeekProviderRunnerFactory",
                "AgenticPrReview.Runtime.ActionHost.GitHub.ActionHostGitHubAuthorizationTransportFactory",
                "AgenticPrReview.Runtime.Host.Publishing.GitHub.Common.BoundedGitHubPublisherTransportFactory",
                "AgenticPrReview.Runtime.Host.State.Restore.AcceptedStateProductionDependencies",
            ])));
        Assert.Matches("^[0-9a-f]{64}$", digest);
        Assert.False(ManagedArchitectureAudit.TryAudit(RuntimeAssembly,
            FixtureAssembly, out _));

        var invalid = Path.Join(root, "invalid.dll");
        Directory.CreateDirectory(root);
        File.WriteAllText(invalid, "not a managed assembly");
        Assert.False(ManagedArchitectureAudit.TryAudit(invalid,
            RuntimeAssembly, out _));
    }

    [Fact]
    public void ManagedArchitectureRejectsCallAndTypeSetDrift()
    {
        var assembly = typeof(ManagedAuditRequiredRoot).Assembly.Location;
        var required = new[] { typeof(ManagedAuditRequiredRoot).FullName! };

        Assert.True(ManagedArchitectureAudit.TryAuditForTesting(
            assembly, required, [], typeof(ManagedAuditTarget).FullName!, 1));
        Assert.False(ManagedArchitectureAudit.TryAuditForTesting(
            assembly, ["Missing.Required.Root"], [],
            typeof(ManagedAuditTarget).FullName!, 1));
        Assert.False(ManagedArchitectureAudit.TryAuditForTesting(
            assembly, required, [], typeof(ManagedAuditTarget).FullName!, 2));
        Assert.False(ManagedArchitectureAudit.TryAuditForTesting(
            assembly, required, [typeof(ManagedAuditForbiddenRoot).FullName!],
            typeof(ManagedAuditTarget).FullName!, 1));
        Assert.False(ManagedArchitectureAudit.TryAuditForTesting(
            assembly, required,
            [typeof(MethodImplAttribute).FullName!],
            typeof(ManagedAuditTarget).FullName!, 1));
        Assert.False(ManagedArchitectureAudit.TryAuditForTesting(
            assembly, required, [],
            typeof(ManagedAuditExtraTarget).FullName!, 1));
        Assert.False(ManagedArchitectureAudit.TryDecodeInstructionsForTesting(
            assembly, [0x29, 0, 0, 0, 0]));
    }

    [Fact]
    public void ValidBuildPairAdmitsTheExactProcessAndManagedInputs()
    {
        var fixture = CreateFixture();

        var result = AotProof.TryCreate(fixture.Arguments,
            fixture.Payload, fixture.Payload, false, false);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Context);
        Assert.Equal(fixture.PayloadSha256,
            result.Context.PayloadSha256);
        Assert.Equal(fixture.IntermediateSha256,
            result.Context.ManagedIntermediateSha256);
        Assert.Equal(fixture.RuntimeIntermediateSha256,
            result.Context.RuntimeIntermediateSha256);
        Assert.Matches("^[0-9a-f]{64}$",
            result.Context.ManagedArchitectureSha256);
        Assert.Equal(fixture.PairSha256,
            result.Context.BuildPairSha256);
        Assert.False(File.Exists(fixture.IdentityOutput));
    }

    [Fact]
    public void AdmissionRejectsMissingPartialAndMismatchedInputs()
    {
        var fixture = CreateFixture();
        Assert.True(AotProof.TryCreate(
            new Dictionary<string, string>(), fixture.Payload,
            fixture.Payload, false, false).IsValid);

        foreach (var name in fixture.Arguments.Keys)
        {
            var partial = fixture.Arguments
                .Where(pair => pair.Key != name)
                .ToDictionary(StringComparer.Ordinal);
            Assert.False(AotProof.TryCreate(partial, fixture.Payload,
                fixture.Payload, false, false).IsValid);
        }

        var wrongKind = new Dictionary<string, string>(fixture.Arguments,
            StringComparer.Ordinal)
        {
            ["execution-kind"] = "framework",
        };
        Assert.False(AotProof.TryCreate(wrongKind, fixture.Payload,
            fixture.Payload, false, false).IsValid);
        Assert.False(AotProof.TryCreate(fixture.Arguments, fixture.Payload,
            fixture.RuntimeIntermediate, false, false).IsValid);
        Assert.False(AotProof.TryCreate(fixture.Arguments, fixture.Payload,
            fixture.Payload, true, false).IsValid);
        Assert.False(AotProof.TryCreate(fixture.Arguments, fixture.Payload,
            fixture.Payload, false, true).IsValid);
        var invalidPath = new Dictionary<string, string>(fixture.Arguments,
            StringComparer.Ordinal)
        {
            ["intermediate"] = "\0",
        };
        Assert.False(AotProof.TryCreate(invalidPath, fixture.Payload,
            fixture.Payload, false, false).IsValid);

        File.WriteAllText(fixture.IdentityOutput, "pre-existing");
        Assert.False(AotProof.TryCreate(fixture.Arguments, fixture.Payload,
            fixture.Payload, false, false).IsValid);
    }

    [Fact]
    public void AdmissionRejectsMalformedAndTamperedBuildPairs()
    {
        var fixture = CreateFixture();
        var valid = File.ReadAllText(fixture.BuildPair);
        var cases = new[]
        {
            "{}",
            valid.Replace("\"kind\":", "\"extra\":\"x\",\"kind\":",
                StringComparison.Ordinal),
            valid.Replace("\"kind\":\"" + Kind + "\",",
                "\"kind\":\"" + Kind + "\",\"kind\":\"" + Kind +
                "\",", StringComparison.Ordinal),
            valid.Replace("\"kind\":\"" + Kind + "\",\"execution_kind\":",
                "\"execution_kind\":\"native-aot\",\"kind\":\"" +
                Kind + "\",\"ignored\":",
                StringComparison.Ordinal),
            valid.Replace(Kind, "apr-r4-e2-action-host-build-pair-v0",
                StringComparison.Ordinal),
            valid.Replace(fixture.PayloadSha256,
                Mutate(fixture.PayloadSha256), StringComparison.Ordinal),
            valid.Replace(fixture.IntermediateSha256,
                Mutate(fixture.IntermediateSha256), StringComparison.Ordinal),
            valid.Replace(fixture.RuntimeIntermediateSha256,
                Mutate(fixture.RuntimeIntermediateSha256),
                StringComparison.Ordinal),
            valid.Replace(fixture.PairSha256,
                Mutate(fixture.PairSha256), StringComparison.Ordinal),
            "{" + new string('x', 4096) + "}",
        };

        foreach (var manifest in cases)
        {
            File.WriteAllText(fixture.BuildPair, manifest);
            Assert.False(AotProof.TryCreate(fixture.Arguments,
                fixture.Payload, fixture.Payload, false, false).IsValid);
        }

        File.WriteAllText(fixture.BuildPair, valid);
        var mutations = new[]
        {
            fixture.Payload,
            fixture.Intermediate,
            fixture.RuntimeIntermediate,
        };
        foreach (var path in mutations)
        {
            var bytes = File.ReadAllBytes(path);
            File.AppendAllText(path, "tampered");
            Assert.False(AotProof.TryCreate(fixture.Arguments,
                fixture.Payload, fixture.Payload, false, false).IsValid);
            File.WriteAllBytes(path, bytes);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private BuildPairFixture CreateFixture()
    {
        Directory.CreateDirectory(root);
        var payload = Path.Join(root, "payload");
        var intermediate = Path.Join(root,
            "AgenticPrReview.Runtime.ActionHostVerifierFixture.dll");
        var runtimeIntermediate = Path.Join(root,
            "AgenticPrReview.Runtime.dll");
        var buildPair = Path.Join(root, "build-pair.json");
        var identityOutput = Path.Join(root, "identity.json");
        File.WriteAllText(payload, "native payload identity");
        File.Copy(FixtureAssembly, intermediate, true);
        File.Copy(RuntimeAssembly, runtimeIntermediate, true);
        if (File.Exists(identityOutput)) File.Delete(identityOutput);

        var payloadSha256 = Sha256(payload);
        var intermediateSha256 = Sha256(intermediate);
        var runtimeIntermediateSha256 = Sha256(runtimeIntermediate);
        var pairSha256 = Sha256Bytes(Encoding.UTF8.GetBytes(
            Kind + "\n" +
            "native-aot\n" +
            payloadSha256 + "\n" +
            intermediateSha256 + "\n" +
            runtimeIntermediateSha256 + "\n"));
        File.WriteAllText(buildPair,
            "{\"kind\":\"" + Kind +
            "\",\"execution_kind\":\"native-aot\",\"payload_sha256\":\"" +
            payloadSha256 +
            "\",\"managed_intermediate_sha256\":\"" +
            intermediateSha256 +
            "\",\"runtime_intermediate_sha256\":\"" +
            runtimeIntermediateSha256 +
            "\",\"build_pair_sha256\":\"" + pairSha256 + "\"}\n");
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["execution-kind"] = "native-aot",
            ["intermediate"] = intermediate,
            ["runtime-intermediate"] = runtimeIntermediate,
            ["build-pair"] = buildPair,
            ["identity-output"] = identityOutput,
        };
        return new BuildPairFixture(payload, intermediate,
            runtimeIntermediate, buildPair, identityOutput, payloadSha256,
            intermediateSha256, runtimeIntermediateSha256, pairSha256,
            arguments);
    }

    private static string FixtureAssembly =>
        typeof(global::AgenticPrReview.Runtime.ActionHostVerifierFixture.Program)
            .Assembly.Location;

    private static string RuntimeAssembly =>
        typeof(ActionHostComposition).Assembly.Location;

    private static string Sha256(string path) =>
        Sha256Bytes(File.ReadAllBytes(path));

    private static string Sha256Bytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Mutate(string value) =>
        (value[0] == '0' ? "1" : "0") + value[1..];

    private sealed record BuildPairFixture(
        string Payload,
        string Intermediate,
        string RuntimeIntermediate,
        string BuildPair,
        string IdentityOutput,
        string PayloadSha256,
        string IntermediateSha256,
        string RuntimeIntermediateSha256,
        string PairSha256,
        IReadOnlyDictionary<string, string> Arguments);
}

internal sealed class ManagedAuditRequiredRoot;

internal sealed class ManagedAuditForbiddenRoot;

internal sealed class ManagedAuditTarget;

internal sealed class ManagedAuditExtraTarget;

internal static class ManagedAuditRoutes
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static object OneCallSite() => new ManagedAuditTarget();

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static object[] TwoCallSites() =>
        [new ManagedAuditExtraTarget(), new ManagedAuditExtraTarget()];
}
