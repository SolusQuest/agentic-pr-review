using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.TrustedProof;

public sealed class TrustedProofArchitectureTests
{
    [Fact]
    public void ProofAssemblyHasNoSyntheticFixtureReference()
    {
        var assembly = typeof(TrustedProofPayloadHost).Assembly;
        var references = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();

        Assert.Contains("AgenticPrReview.Runtime", references);
        Assert.DoesNotContain(
            "AgenticPrReview.Runtime.ActionHostVerifierFixture",
            references);
        Assert.DoesNotContain(
            "AgenticPrReview.Runtime.ActionHostTrustedProofVerifier",
            references);
        Assert.DoesNotContain(
            "AgenticPrReview.Runtime.LiveAgentVerifierFixture",
            references);
        Assert.NotNull(assembly.GetType(
            "AgenticPrReview.Runtime.ActionHostTrustedProofPayload." +
            "TrustedProofDeterministicDeepSeekHandler"));
        Assert.NotNull(assembly.GetType(
            "AgenticPrReview.Runtime.ActionHostTrustedProofPayload." +
            "TrustedProofStaleWindowCoordinator"));
        Assert.NotNull(assembly.GetType(
            "AgenticPrReview.Runtime.ActionHostTrustedProofPayload." +
            "TrustedProofControlTransport"));
    }

    [Fact]
    public void ProviderAndCoordinatorKeepTheirCredentialBoundaries()
    {
        var root = FindRepositoryRoot();
        var proofRoot = Path.Join(
            root,
            "runtime",
            "tests",
            "ActionHostTrustedProofPayload");
        var handler = File.ReadAllText(Path.Join(
            proofRoot,
            "TrustedProofDeterministicDeepSeekHandler.cs"));
        var coordinator = File.ReadAllText(Path.Join(
            proofRoot,
            "TrustedProofStaleWindowCoordinator.cs"));
        var composition = File.ReadAllText(Path.Join(
            proofRoot,
            "TrustedProofPayloadComposition.cs"));
        var productionSources = string.Join(
            '\n',
            Directory.EnumerateFiles(proofRoot, "*.cs")
                .Order(StringComparer.Ordinal)
                .Select(File.ReadAllText));

        Assert.DoesNotContain("Task.Delay", handler, StringComparison.Ordinal);
        Assert.Contains("proof/apr178-path-canary.txt", handler,
            StringComparison.Ordinal);
        Assert.DoesNotContain("src/reviewed.ts", handler,
            StringComparison.Ordinal);
        Assert.DoesNotContain("GITHUB_TOKEN", handler, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TrustedProofControlCoordinates",
            handler,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ProviderApiKey", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("StateKey", coordinator, StringComparison.Ordinal);
        Assert.Contains("DeepSeekTransport.CreateForTesting", composition);
        Assert.DoesNotContain("DeepSeekTransport.Create(", composition);
        Assert.Contains("ActionHostGitHubAuthorizationTransportFactory", composition);
        Assert.Contains("AcceptedStateProductionDependencies", composition);
        Assert.Contains("BoundedGitHubPublisherTransportFactory", composition);
        Assert.Contains("TimeProvider.System", composition);
        Assert.DoesNotContain("Framework", composition, StringComparison.Ordinal);
        Assert.Contains("CreateForVerifier", coordinator,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ActionHostTrustedProofVerifier", composition,
            StringComparison.Ordinal);
        Assert.DoesNotContain("GITHUB_API_URL", productionSources,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\"http://", productionSources,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            productionSources.Split(
                "new Uri(\"https://api.github.com/\")",
                StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void NativeVerifierOwnsOnlyTestSyntheticOuterDependencies()
    {
        var root = FindRepositoryRoot();
        var verifierRoot = Path.Join(
            root,
            "runtime",
            "tests",
            "ActionHostTrustedProofVerifier");
        var project = File.ReadAllText(Path.Join(
            verifierRoot,
            "AgenticPrReview.Runtime.ActionHostTrustedProofVerifier.csproj"));
        var host = File.ReadAllText(Path.Join(
            verifierRoot,
            "TrustedProofVerifierHost.cs"));
        var control = File.ReadAllText(Path.Join(
            verifierRoot,
            "TrustedProofVerifierControl.cs"));
        var payloadAssemblyInfo = File.ReadAllText(Path.Join(
            root,
            "runtime",
            "tests",
            "ActionHostTrustedProofPayload",
            "AssemblyInfo.cs"));
        var preparation = File.ReadAllText(Path.Join(
            root,
            "runtime",
            "scripts",
            "prepare-r4-trusted-proof-payload.sh"));
        var verification = File.ReadAllText(Path.Join(
            root,
            "runtime",
            "scripts",
            "verify-r4-trusted-proof-payload.sh"));

        Assert.Contains("<PublishAot>true</PublishAot>", project,
            StringComparison.Ordinal);
        Assert.Contains("ActionHostTrustedProofPayload.csproj", project,
            StringComparison.Ordinal);
        Assert.Contains("FrameworkGitHubHandler.cs", project,
            StringComparison.Ordinal);
        Assert.Contains("FrameworkStateDependencies.cs", project,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ActionHostVerifierFixture.csproj", project,
            StringComparison.Ordinal);
        Assert.Contains("TrustedProofPayloadHost.RunAsync", host,
            StringComparison.Ordinal);
        Assert.Contains("TrustedProofDeterministicDeepSeekHandler", host,
            StringComparison.Ordinal);
        Assert.Contains("TrustedProofStaleWindowCoordinator.CreateForVerifier",
            host, StringComparison.Ordinal);
        Assert.Contains("FrameworkCanaries.ProofControlRepository",
            host, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new TrustedProofControlCoordinates(\n            launch.RepositoryName",
            host,
            StringComparison.Ordinal);
        Assert.Contains("TrustedProofControlService.RunAsync", control,
            StringComparison.Ordinal);
        Assert.Contains("new VerifierRecordingHandler", control,
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(
            verifierRoot,
            "VerifierRecordingHandler.cs")));
        Assert.Contains(
            "InternalsVisibleTo(\"AgenticPrReview.Runtime." +
            "ActionHostTrustedProofVerifier\")",
            payloadAssemblyInfo,
            StringComparison.Ordinal);
        Assert.DoesNotContain("GITHUB_API_URL", host,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SocketsHttpHandler", host,
            StringComparison.Ordinal);
        Assert.Contains(
            "-p:JsonSerializerIsReflectionEnabledByDefault=false",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains("-warnaserror -warnnotaserror:IL3058",
            preparation, StringComparison.Ordinal);
        Assert.Contains(
            "$artifacts_root=/_/apr-r4-e2p-artifacts",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains(
            "$artifacts_root=/_/apr-r4-e2p-artifacts",
            verification,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TrustedProofPayloadAotIntermediateDirectory",
            verification,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TrustedProofVerifierAotIntermediateDirectory",
            verification,
            StringComparison.Ordinal);
        Assert.DoesNotContain("| tee \"$proof_log\"", verification,
            StringComparison.Ordinal);
        Assert.DoesNotContain("tail -n 100", verification,
            StringComparison.Ordinal);
        Assert.Contains("project-r4-e2p-diagnostics.mjs", verification,
            StringComparison.Ordinal);
        Assert.Contains("> \"$proof_log\" 2>&1", verification,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TrustedPolicyAndHistoricalSurfacesAreExact()
    {
        var root = FindRepositoryRoot();
        Assert.Equal(
            "{\"schema\":\"agentic-pr-review.config.v1\"," +
            "\"instructionsPath\":\".github/agentic-pr-review/" +
            "trusted-proof-instructions.md\",\"publication\":{\"mode\":" +
            "\"sticky\"}}\n",
            File.ReadAllText(Path.Join(
                root,
                ".github",
                "agentic-pr-review",
                "trusted-proof.json")));
        Assert.False(File.Exists(Path.Join(
            root,
            "runtime",
            "tests",
            "fixtures",
            "action-host",
            "trusted-proof",
            "trusted-proof-payload-receipt.json")));

        using var immutableDocument = JsonDocument.Parse(File.ReadAllBytes(
            Path.Join(
                root,
                "runtime",
                "tests",
                "fixtures",
                "action-host",
                "trusted-proof-payload",
                "immutable-base-sha256.json")));
        var immutable = immutableDocument.RootElement;
        AssertDigest(
            root,
            ".github/actions/agentic-pr-review/action.yml",
            immutable.GetProperty("action_metadata_sha256").GetString()!);
        AssertDigest(
            root,
            ".github/actions/agentic-pr-review/dist/index.js",
            immutable.GetProperty("wrapper_bundle_sha256").GetString()!);
        AssertDigest(
            root,
            "runtime/tests/fixtures/action-host/aot/receipt-contract.json",
            immutable.GetProperty("e2_receipt_contract_sha256").GetString()!);
        AssertDigest(
            root,
            "runtime/tests/fixtures/action-host/aot/warning-policy.txt",
            immutable.GetProperty("e2_warning_policy_sha256").GetString()!);
    }

    private static void AssertDigest(
        string root,
        string relative,
        string expected)
    {
        var digest = Convert.ToHexString(SHA256.HashData(
                File.ReadAllBytes(Path.Join(root, relative))))
            .ToLowerInvariant();
        Assert.Equal(expected, digest);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Join(directory.FullName, "package.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new InvalidOperationException("Repository root not found.");
    }
}
