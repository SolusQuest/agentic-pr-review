using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.TrustedProof;

public sealed class TrustedProofArchitectureTests
{
    [Fact]
    public void FrameworkSourceInventoryGoldenMatchesThePortableInventory()
    {
        var root = FindRepositoryRoot();
        var sourceRoot = Path.Join(
            root,
            "runtime",
            "tests",
            "ActionHostVerifierFixture");
        var framing = new StringBuilder();
        foreach (var source in Directory.EnumerateFiles(
                     sourceRoot,
                     "*",
                     SearchOption.AllDirectories)
                     .Where(source => !source.Contains(
                         Path.DirectorySeparatorChar + "bin" +
                         Path.DirectorySeparatorChar,
                         StringComparison.Ordinal) &&
                         !source.Contains(
                         Path.DirectorySeparatorChar + "obj" +
                         Path.DirectorySeparatorChar,
                         StringComparison.Ordinal))
                     .OrderBy(source => Path.GetRelativePath(root, source)
                         .Replace(Path.DirectorySeparatorChar, '/'),
                         StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, source)
                .Replace(Path.DirectorySeparatorChar, '/');
            var sourceDigest = Convert.ToHexString(SHA256.HashData(
                    File.ReadAllBytes(source)))
                .ToLowerInvariant();
            framing.Append(relative).Append('\0').Append(sourceDigest)
                .Append('\n');
        }

        using var golden = JsonDocument.Parse(File.ReadAllBytes(Path.Join(
            root,
            "runtime",
            "tests",
            "fixtures",
            "action-host",
            "framework",
            "expected-evidence.json.golden")));
        var expected = golden.RootElement
            .GetProperty("source_inventory_digest")
            .GetString();
        var actual = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(framing.ToString())))
            .ToLowerInvariant();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CurrentHeadPayloadIsUnconditionallyV2WithCompiledIdentity()
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
        var host = File.ReadAllText(Path.Join(
            payloadRoot,
            "TrustedProofPayloadHost.cs"));
        var verifier = File.ReadAllText(Path.Join(
            root,
            "runtime",
            "tests",
            "ActionHostTrustedProofVerifier",
            "TrustedProofVerifierHost.cs"));
        var workflow = File.ReadAllText(Path.Join(
            root,
            ".github",
            "workflows",
            "runtime-ci.yml"));
        var preparation = File.ReadAllText(Path.Join(
            root,
            "runtime",
            "scripts",
            "prepare-r4-trusted-proof-payload-current.sh"));
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
        Assert.Contains(
            "PayloadSourceCommit and PayloadSourceTree are required",
            project,
            StringComparison.Ordinal);
        Assert.Contains("^[0-9a-f]{40}$", project,
            StringComparison.Ordinal);
        Assert.Contains("TrustedProofWorkflowTemplateV2", project,
            StringComparison.Ordinal);
        Assert.Contains("r4-trusted-proof-v2.yml.template", project,
            StringComparison.Ordinal);
        Assert.Contains("TrustedProofV2WorkflowAdmission.Instance",
            composition, StringComparison.Ordinal);
        Assert.DoesNotContain("ActionHostV1TrustedWorkflowAdmission.Instance",
            composition, StringComparison.Ordinal);
        Assert.Contains("apr-r4-e2p-trusted-proof-payload-v2",
            host, StringComparison.Ordinal);
        Assert.Contains("TrustedProofPayloadBuildIdentity.SourceCommit",
            verifier, StringComparison.Ordinal);
        Assert.Contains("TrustedProofPayloadBuildIdentity.SourceTree",
            verifier, StringComparison.Ordinal);
        Assert.Contains("TrustedProofPayloadHost.ProofKind",
            verifier, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable",
            composition + admission, StringComparison.Ordinal);
        Assert.DoesNotContain("payload_source_sha", launchSources,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "-p:PayloadSourceCommit=$expected_source_sha",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains(
            "action_source_sha=%s\\npayload_source_sha=%s\\npayload_source_tree=%s\\npayload_build_discriminator=r4-w2",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains("--expected-payload-sha256", preparation,
            StringComparison.Ordinal);
        Assert.Contains("payload_sha256\" == \"$expected_payload_sha256", preparation,
            StringComparison.Ordinal);
        Assert.Contains("trusted-proof-payload:\n", workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "ref: 5b5769753653bb3fd3e68cf8b7bb88a1bd350613",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("trusted-proof-payload-v2:\n", workflow,
            StringComparison.Ordinal);
        var v2JobStart = workflow.IndexOf("  trusted-proof-payload-v2:\n",
            StringComparison.Ordinal);
        var v2JobEnd = workflow.IndexOf("\n  integration:\n", v2JobStart,
            StringComparison.Ordinal);
        Assert.True(v2JobStart >= 0 && v2JobEnd > v2JobStart);
        var v2Job = workflow[v2JobStart..v2JobEnd];
        var checkout = v2Job.IndexOf("uses: actions/checkout@v6",
            StringComparison.Ordinal);
        var setupNode = v2Job.IndexOf("uses: actions/setup-node@v6",
            StringComparison.Ordinal);
        var installDependencies = v2Job.IndexOf("- run: npm ci",
            StringComparison.Ordinal);
        var firstProof = v2Job.IndexOf(
            "bash runtime/scripts/verify-r4-trusted-proof-payload-v2.sh > \"$RUNNER_TEMP/r4-e2p-v2-first.log\"",
            StringComparison.Ordinal);
        var secondProof = v2Job.IndexOf(
            "bash runtime/scripts/verify-r4-trusted-proof-payload-v2.sh > \"$RUNNER_TEMP/r4-e2p-v2-second.log\"",
            StringComparison.Ordinal);
        Assert.True(checkout >= 0 && checkout < setupNode &&
            setupNode < installDependencies && installDependencies < firstProof &&
            firstProof < secondProof);
        Assert.Equal(2, v2Job.Split(
            "bash runtime/scripts/verify-r4-trusted-proof-payload-v2.sh",
            StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "ref: ${{ github.event_name == 'pull_request' && " +
            "github.event.pull_request.head.sha || github.sha }}",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "receipt.source_commit !== sourceCommit",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "receipt.source_tree !== sourceTree",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "receipt.compiled_payload_source_commit !== sourceCommit",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "receipt.compiled_payload_source_tree !== sourceTree",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "cmp \"$RUNNER_TEMP/r4-e2p-v2-first.receipt\" " +
            "\"$RUNNER_TEMP/r4-e2p-v2-second.receipt\"",
            workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain("path: payload-source", workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain("git -C payload-source", workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain("r4-e2p-v2-checked.receipt", workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain("trusted-proof-payload-receipt-v2.json", workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "trusted-proof-payload-receipt-v2.json",
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
        Assert.Equal("exact-workflow-action-payload-source-commit",
            preflightV2.RootElement.GetProperty("payload_source_identity")
                .GetString());

        using var preparationV2 = JsonDocument.Parse(File.ReadAllBytes(Path.Join(
            root,
            "runtime",
            "tests",
            "fixtures",
            "action-host",
            "trusted-proof-payload",
            "preparation-contract-v2.json")));
        Assert.Equal(
            new[]
            {
                "prepared_root",
                "prepared_executable",
                "prepared_payload_sha256",
                "action_source_sha",
                "payload_source_sha",
                "payload_source_tree",
                "payload_build_discriminator",
            },
            preparationV2.RootElement.GetProperty("outputs")
                .EnumerateArray().Select(value => value.GetString()).ToArray());

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
        Assert.Contains("compiled_payload_proof_kind", ordered);
        Assert.DoesNotContain("transaction_partition", ordered);
    }

    [Fact]
    public void TrustedOnlyEvidenceNormalizerRequiresTheBudgetAndDeniedFollowOnBoundary()
    {
        var source = File.ReadAllText(Path.Join(
            FindRepositoryRoot(),
            "runtime",
            "scripts",
            "verify-r4-trusted-proof-payload-v2.sh"));

        Assert.Contains("'trusted_proof_request_budget_satisfied',", source,
            StringComparison.Ordinal);
        Assert.Contains(
            "input.trusted_proof_request_budget_satisfied !== true",
            source,
            StringComparison.Ordinal);
        Assert.Contains("'stale-unauthorized-follow-on',", source,
            StringComparison.Ordinal);
        Assert.Contains(
            "expectedName === 'stale-unauthorized-follow-on' ? 0 : null",
            source,
            StringComparison.Ordinal);
        Assert.Contains("item.HostPid !== expectedHostPid", source,
            StringComparison.Ordinal);
        Assert.Contains("AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE: final",
            File.ReadAllText(Path.Join(FindRepositoryRoot(), ".github",
                "workflows", "r4-trusted-proof.yml")), StringComparison.Ordinal);
        Assert.Contains("'launch_workflow_sha',", source,
            StringComparison.Ordinal);
        Assert.Contains("'launch_action_source_sha',", source,
            StringComparison.Ordinal);
        Assert.Contains("'fixture_base_sha',", source,
            StringComparison.Ordinal);
        Assert.Contains("input.launch_workflow_sha !== expectedSourceCommit",
            source, StringComparison.Ordinal);
        Assert.Contains("input.launch_action_source_sha !== expectedSourceCommit",
            source, StringComparison.Ordinal);
        Assert.Contains("input.fixture_base_sha !== expectedSourceCommit",
            source, StringComparison.Ordinal);
        Assert.Contains("input.trusted_authority_exact !== true", source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE=final",
            source, StringComparison.Ordinal);
        Assert.Contains(
            "env AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE=final",
            source, StringComparison.Ordinal);
        Assert.Contains("validate_missing_github_stderr", source,
            StringComparison.Ordinal);
        Assert.Contains(
            "github.host_head_source_rest.remaining_tail_required !== 863",
            source, StringComparison.Ordinal);
        Assert.Contains(
            "github.host_other_github_rest.remaining_tail_required !== 878",
            source, StringComparison.Ordinal);
        Assert.Contains("control.remaining_tail_required !== 888", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"rejected_requests\":0}\\nAPR_R4_E2P_CONTROL_REQUEST_BUDGET",
            source, StringComparison.Ordinal);
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
        var ports = File.ReadAllText(Path.Join(
            proofRoot,
            "TrustedProofPayloadRuntimePorts.cs"));
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
        Assert.Contains("AcceptedStateProductionDependencies", ports);
        Assert.Contains("BoundedGitHubPublisherTransportFactory", composition);
        Assert.Contains("TimeProvider.System", ports);
        Assert.DoesNotContain("Framework", composition, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateForVerifier", coordinator,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ActionHostTrustedProofVerifier", composition,
            StringComparison.Ordinal);
        Assert.DoesNotContain("GITHUB_API_URL", productionSources,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\"http://", productionSources,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
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
        Assert.Contains("TrustedProofPayloadHost.RunCoreAsync", host,
            StringComparison.Ordinal);
        Assert.Contains("TrustedProofPayloadRuntimePorts", host,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ActionHostCompositionDependencies", host,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CreateForVerifier", host,
            StringComparison.Ordinal);
        Assert.DoesNotContain("TrustedProofControlTransport.Create", host,
            StringComparison.Ordinal);
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
    public void ProductionAndVerifierShareTheMeteredPayloadCore()
    {
        var root = FindRepositoryRoot();
        var proofRoot = Path.Join(
            root,
            "runtime",
            "tests",
            "ActionHostTrustedProofPayload");
        var payload = File.ReadAllText(Path.Join(
            proofRoot,
            "TrustedProofPayloadHost.cs"));
        var coordinator = File.ReadAllText(Path.Join(
            proofRoot,
            "TrustedProofStaleWindowCoordinator.cs"));
        var transport = File.ReadAllText(Path.Join(
            proofRoot,
            "TrustedProofControlTransport.cs"));
        var verifier = File.ReadAllText(Path.Join(
            root,
            "runtime",
            "tests",
            "ActionHostTrustedProofVerifier",
            "TrustedProofVerifierHost.cs"));
        var architectureAudit = File.ReadAllText(Path.Join(
            root,
            "scripts",
            "check-r4-e2p-managed-architecture.mjs"));
        var requiredVerifierStart = architectureAudit.IndexOf(
            "const requiredVerifier = [", StringComparison.Ordinal);
        var forbiddenVerifierStart = architectureAudit.IndexOf(
            "const forbiddenVerifier = [", StringComparison.Ordinal);
        var verifierChecksStart = architectureAudit.IndexOf(
            "for (const name of requiredProof)", StringComparison.Ordinal);
        Assert.True(requiredVerifierStart >= 0 &&
            forbiddenVerifierStart > requiredVerifierStart &&
            verifierChecksStart > forbiddenVerifierStart);
        var requiredVerifier = architectureAudit[
            requiredVerifierStart..forbiddenVerifierStart];
        var forbiddenVerifier = architectureAudit[
            forbiddenVerifierStart..verifierChecksStart];

        Assert.Contains("RunCoreAsync", payload, StringComparison.Ordinal);
        Assert.Contains("TrustedProofPayloadRuntimePorts.Production", payload,
            StringComparison.Ordinal);
        Assert.Contains("new TrustedProofGitHubRequestBudget", payload,
            StringComparison.Ordinal);
        Assert.Contains("new TrustedProofControlRequestBudget", payload,
            StringComparison.Ordinal);
        Assert.Contains(
            "var primaryRemainingLedger = new TrustedProofPrimaryRemainingLedger();",
            payload, StringComparison.Ordinal);
        Assert.Contains("remainingLedger: primaryRemainingLedger", payload,
            StringComparison.Ordinal);
        Assert.Contains("ports.CreateGitHubInnerHandler", payload,
            StringComparison.Ordinal);
        Assert.Contains("controlBudget", payload, StringComparison.Ordinal);
        Assert.Contains("WriteReceipt(Console.Error)", payload,
            StringComparison.Ordinal);
        Assert.Contains("TrustedProofControlRequestBudget requestBudget",
            coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("new TrustedProofControlRequestBudget", coordinator,
            StringComparison.Ordinal);
        Assert.Contains("if (!requestBudget.TryClaim(out var lease))", transport,
            StringComparison.Ordinal);
        Assert.Contains("CloseOutcomeUnknown(lease)", transport,
            StringComparison.Ordinal);
        Assert.Contains("TrustedProofPayloadHost.RunCoreAsync", verifier,
            StringComparison.Ordinal);
        Assert.DoesNotContain("new ActionHostCompositionDependencies", verifier,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CreateForVerifier", verifier,
            StringComparison.Ordinal);
        Assert.Contains("'TrustedProofPayloadRuntimePorts',",
            requiredVerifier, StringComparison.Ordinal);
        Assert.DoesNotContain("'ActionHostCompositionDependencies',",
            requiredVerifier, StringComparison.Ordinal);
        Assert.DoesNotContain("'TrustedProofStaleWindowCoordinator',",
            requiredVerifier, StringComparison.Ordinal);
        Assert.Contains("'ActionHostCompositionDependencies',",
            forbiddenVerifier, StringComparison.Ordinal);
        Assert.Contains("'TrustedProofStaleWindowCoordinator',",
            forbiddenVerifier, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationRequestEvidenceSeparatesAnonymousRoutesAndSharesTheFreezeSource()
    {
        var root = FindRepositoryRoot();
        var synthetic = File.ReadAllText(Path.Join(root, "runtime", "tests",
            "ActionHostVerifierFixture", "SyntheticOfficialPlatform.cs"));
        var supervisor = File.ReadAllText(Path.Join(root, "runtime", "tests",
            "ActionHostVerifierFixture", "FrameworkSupervisor.cs"));
        var verifierProject = File.ReadAllText(Path.Join(root, "runtime", "tests",
            "ActionHostVerifierFixture",
            "AgenticPrReview.Runtime.ActionHostVerifierFixture.csproj"));
        var accounting = File.ReadAllText(Path.Join(root, "runtime", "tests",
            "ActionHostTrustedProofPayload",
            "TrustedProofOperationRequestAccounting.cs"));

        Assert.Contains("actions_results_signed_upload", synthetic,
            StringComparison.Ordinal);
        Assert.Contains("actions_results_signed_download", synthetic,
            StringComparison.Ordinal);
        Assert.Contains("string Route", synthetic, StringComparison.Ordinal);
        Assert.Contains("anonymous.SignedUploads == result.AnonymousSignedUploads",
            supervisor, StringComparison.Ordinal);
        Assert.Contains("anonymous.SignedDownloads == result.AnonymousSignedDownloads",
            supervisor, StringComparison.Ordinal);
        Assert.Contains("!IsExactEventRoute(domain, route)", supervisor,
            StringComparison.Ordinal);
        Assert.Contains("anonymous.Raw == anonymous.SignedUploads + anonymous.SignedDownloads",
            supervisor, StringComparison.Ordinal);
        Assert.Contains("declared.Length != domains.Length", supervisor,
            StringComparison.Ordinal);
        Assert.Contains("TrustedProofOperationRequestAccounting.cs", verifierProject,
            StringComparison.Ordinal);
        Assert.Contains("TryGetFrozenTailProfile", accounting,
            StringComparison.Ordinal);
        Assert.DoesNotContain("FrozenRemainingTailByDomain", supervisor,
            StringComparison.Ordinal);
        Assert.Equal(3, supervisor.Split(
            "TrustedProofOperationRequestAccounting.MeasurementPrimaryReserve",
            StringSplitOptions.None).Length - 1);
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
            "runtime/tests/fixtures/action-host/trusted-proof/historical/v1/" +
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

        var immutablePath = Path.Join(
            root,
            "runtime",
            "tests",
            "fixtures",
            "action-host",
            "trusted-proof-payload",
            "immutable-base-sha256.json");
        var immutableBytes = File.ReadAllBytes(immutablePath);
        Assert.Equal(
            "f42b7791276659b9df01b159d330ae8b" +
            "90d8d3e881ba7e2ed81fbb3aede2fc2f",
            Convert.ToHexString(SHA256.HashData(immutableBytes)).ToLowerInvariant());
        using var immutableDocument = JsonDocument.Parse(immutableBytes);
        var immutable = immutableDocument.RootElement;
        Assert.Equal("apr-r4-e2p-immutable-base-sha256-v1",
            immutable.GetProperty("kind").GetString());
        Assert.Equal("0b5c96a6fea12906024c68b3d8457ccb7b026ebe",
            immutable.GetProperty("base_commit").GetString());
        Assert.Equal("eb7095fbb055002637233be328aa3cc78" +
            "b229a670aaedd1d13ac04559d700771",
            immutable.GetProperty("action_metadata_sha256").GetString());
        Assert.Equal("7c62221e415d97e632b9cd79e0bfe069" +
            "d18c0900f5013a3cf2db309530153fd9",
            immutable.GetProperty("wrapper_bundle_sha256").GetString());
        Assert.Equal("0423f3085734d2bed14d659e2281397e" +
            "73dfc40fcbaf5fa47adc48d3d52bc70a",
            immutable.GetProperty("e2_receipt_contract_sha256").GetString());
        Assert.Equal("11801e05c616676bf9470fe16ad6751e" +
            "56fad0a7444d8f47bc12c7e5c92d712e",
            immutable.GetProperty("e2_warning_policy_sha256").GetString());
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
