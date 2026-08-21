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

        Assert.DoesNotContain("Task.Delay", handler, StringComparison.Ordinal);
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
