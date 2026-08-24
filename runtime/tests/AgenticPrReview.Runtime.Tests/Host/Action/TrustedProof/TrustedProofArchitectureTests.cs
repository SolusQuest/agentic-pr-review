using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.TrustedProof;

public sealed class TrustedProofArchitectureTests
{
    [Fact]
    public void StagedV2IsCompileTimeOnlyAndLeavesTheActiveV1SurfacePinned()
    {
        var root = FindRepositoryRoot();
        var payloadRoot = Path.Join(
            root,
            "runtime",
            "tests",
            "ActionHostTrustedProofPayload");
        var project = File.ReadAllText(Path.Join(
            payloadRoot,
            "AgenticPrReview.Runtime.ActionHostTrustedProofPayload.csproj"));
        var composition = File.ReadAllText(Path.Join(
            payloadRoot,
            "TrustedProofPayloadComposition.cs"));
        var admission = File.ReadAllText(Path.Join(
            payloadRoot,
            "TrustedProofV2WorkflowAdmission.cs"));
        var workflow = File.ReadAllText(Path.Join(
            root,
            ".github",
            "workflows",
            "runtime-ci.yml"));
        var preparation = File.ReadAllText(Path.Join(
            root,
            "runtime",
            "scripts",
            "prepare-r4-trusted-proof-payload-v2.sh"));
        var launchSources = string.Join('\n',
            Directory.EnumerateFiles(
                    Path.Join(
                        root,
                        "runtime",
                        "src",
                        "AgenticPrReview.Runtime",
                        "Host",
                        "Action",
                        "Contracts"),
                    "*.cs")
                .Select(File.ReadAllText));

        Assert.Contains("PayloadSourceCommit", project,
            StringComparison.Ordinal);
        Assert.Contains("PayloadSourceTree", project,
            StringComparison.Ordinal);
        Assert.Contains("TrustedProofWorkflowTemplateV2", project,
            StringComparison.Ordinal);
        Assert.Contains("r4-trusted-proof-v2.yml.template", project,
            StringComparison.Ordinal);
        Assert.Contains("TrustedProofPayloadBuildIdentity.StagedV2",
            composition, StringComparison.Ordinal);
        Assert.Contains("ActionHostV1TrustedWorkflowAdmission.Instance",
            composition, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable",
            composition + admission, StringComparison.Ordinal);
        Assert.DoesNotContain("payload_source_sha", launchSources,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "-p:PayloadSourceCommit=$source_commit",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains(
            "action_source_sha=%s\\npayload_source_sha=%s",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"5b5769753653bb3fd3e68cf8b7bb88a1bd350613\"",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains("trusted-proof-payload:\n", workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "ref: 5b5769753653bb3fd3e68cf8b7bb88a1bd350613",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("trusted-proof-payload-v2:\n", workflow,
            StringComparison.Ordinal);
        Assert.Contains("verify-r4-trusted-proof-payload-v2.sh", workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "node \"$control_checker\"",
            preparation,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "node \"$source_root/scripts/check-r4-e2p-receipt-v2.mjs\"",
            preparation,
            StringComparison.Ordinal);

        using var preflightV2 = JsonDocument.Parse(File.ReadAllBytes(Path.Join(
            root,
            "runtime",
            "tests",
            "fixtures",
            "action-host",
            "trusted-proof-payload",
            "workflow",
            "preflight-contract-v2.json")));
        Assert.Equal("main",
            preflightV2.RootElement.GetProperty("base_ref").GetString());
        Assert.Equal("exact-workflow-sha",
            preflightV2.RootElement.GetProperty("base_sha").GetString());
        Assert.Equal("exact-compiled-payload-source-commit",
            preflightV2.RootElement.GetProperty("payload_source_identity")
                .GetString());

        using var v2Contract = JsonDocument.Parse(File.ReadAllBytes(Path.Join(
            root,
            "runtime",
            "tests",
            "fixtures",
            "action-host",
            "trusted-proof-payload",
            "aot",
            "receipt-contract-v2.json")));
        Assert.Equal("apr-r4-e2p-receipt-contract-v2",
            v2Contract.RootElement.GetProperty("kind").GetString());
        var ordered = v2Contract.RootElement.GetProperty("ordered_fields")
            .EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Contains("compiled_payload_source_commit", ordered);
        Assert.Contains("compiled_payload_source_tree", ordered);
        Assert.Contains("transaction_partition", ordered);
    }

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
        const string receiptRelative =
            "runtime/tests/fixtures/action-host/trusted-proof/" +
            "trusted-proof-payload-receipt.json";
        var receiptBytes = File.ReadAllBytes(Path.Join(root, receiptRelative));
        Assert.NotEmpty(receiptBytes);
        Assert.Equal((byte)0x0a, receiptBytes[^1]);
        Assert.DoesNotContain((byte)0x0d, receiptBytes);
        AssertDigest(
            root,
            receiptRelative,
            "9b95a87e5f40d7b506e25426e3905aaa" +
            "f0510ad28d79c8a7ca3737a3952a7b34");
        var receiptLineBytes = Encoding.UTF8.GetBytes(
                "APR_R4_E2P_RECEIPT ")
            .Concat(receiptBytes)
            .ToArray();
        Assert.Equal(
            "3fa55211baa43da955a2eb083b2188a1f" +
            "de193e6684cb129ec99f5f35374ad49",
            Convert.ToHexString(SHA256.HashData(receiptLineBytes))
                .ToLowerInvariant());
        using var receiptDocument = JsonDocument.Parse(receiptBytes);
        var receipt = receiptDocument.RootElement;
        Assert.Equal(
            "5b5769753653bb3fd3e68cf8b7bb88a1bd350613",
            receipt.GetProperty("source_commit").GetString());
        Assert.Equal(
            "5b5769753653bb3fd3e68cf8b7bb88a1bd350613",
            receipt.GetProperty("action_source_sha").GetString());
        Assert.Equal(
            "97af2b7b0160e333862e74e5e421b2e8" +
            "02f3962d1bb6405c909301971a0130fc",
            receipt.GetProperty("payload_sha256").GetString());
        using var receiptContract = JsonDocument.Parse(File.ReadAllBytes(
            Path.Join(
                root,
                "runtime/tests/fixtures/action-host/trusted-proof-payload/" +
                "aot/receipt-contract.json")));
        Assert.Equal(
            receiptContract.RootElement.GetProperty("ordered_fields")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray(),
            receipt.EnumerateObject()
                .Select(property => property.Name)
                .ToArray());

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
