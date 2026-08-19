using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Inline;
using AgenticPrReview.Runtime.Host.State.Restore;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed class ActionHostFrameworkVerifierArchitectureTests
{
    [Fact]
    public void ProductionCompositionWiresTheRealPostAcceptanceInlineHook()
    {
        var dependencies = ActionHostCompositionDependencies.Production();

        Assert.IsType<PostAcceptanceInlinePublisherHook>(
            dependencies.InlineHook);
        Assert.IsType<BoundedGitHubPublisherTransportFactory>(
            dependencies.PublisherFactory);
        Assert.IsType<AcceptedStateProductionDependencies>(
            dependencies.StateDependencies);
    }

    [Fact]
    public void ProofFactoriesAreNarrowInternalConstructorSeams()
    {
        var provider = typeof(ActionHostDeepSeekProviderRunnerFactory)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic);
        var github = typeof(ActionHostGitHubAuthorizationTransportFactory)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic);
        var publisher = typeof(BoundedGitHubPublisherTransportFactory)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic);
        var state = typeof(AcceptedStateProductionDependencies)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.Contains(provider, constructor =>
            constructor.GetParameters().Length == 1);
        Assert.Contains(github, constructor =>
            constructor.GetParameters().Length == 1);
        Assert.Contains(publisher, constructor =>
            constructor.GetParameters().Length == 1);
        Assert.Contains(state, constructor =>
            constructor.GetParameters().Length == 1);
    }

    [Fact]
    public void FrameworkProofAddsNoPublicSelectorOrActionInput()
    {
        var root = FindRepositoryRoot();
        var action = File.ReadAllText(Path.Join(root,
            ".github", "actions", "agentic-pr-review", "action.yml"));
        var contracts = File.ReadAllText(Path.Join(root,
            "src", "action-wrapper", "launcher", "contracts.ts"));

        Assert.DoesNotContain("proof-mode", action,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("verifier-mode", action,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("proof_mode", contracts,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("verifier_mode", contracts,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReplacementAndInventoryArtifactsAreClosedAndPinned()
    {
        var root = FindRepositoryRoot();
        var fixture = Path.Join(root, "runtime", "tests", "fixtures",
            "action-host", "framework");
        using var replacement = JsonDocument.Parse(File.ReadAllBytes(
            Path.Join(fixture, "replacement-record.json")));
        using var inventory = JsonDocument.Parse(File.ReadAllBytes(
            Path.Join(fixture, "e1-base-inventory.json")));

        var packages = replacement.RootElement.GetProperty("entries")
            .EnumerateArray()
            .Select(value => value.GetProperty("leaf_id").GetString())
            .ToArray();
        Assert.Equal(new[]
        {
            "W3", "W4", "W5", "W6", "W7", "W8", "W9", "W10",
            "W11", "W12", "W14", "W15",
        }, packages);
        Assert.DoesNotContain("W13", packages);
        var w4 = replacement.RootElement.GetProperty("entries")
            .EnumerateArray().Single(value =>
                value.GetProperty("leaf_id").GetString() == "W4");
        Assert.Equal("removed",
            w4.GetProperty("disposition").GetString());
        Assert.Equal(new[] { "src/live-provider/" },
            w4.GetProperty("removed_paths").EnumerateArray()
                .Select(value => value.GetString()).ToArray());
        Assert.False(Directory.Exists(Path.Join(root, "src", "live-provider")));
        Assert.Contains(
            "runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Core/LiveAgentVerifierRetirementArchitectureTests.cs",
            w4.GetProperty("retained_evidence_paths").EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Contains(
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/Policy/ActionHostTrustedPolicyArchitectureTests.cs",
            w4.GetProperty("retained_evidence_paths").EnumerateArray()
                .Select(value => value.GetString()));
        var w3 = replacement.RootElement.GetProperty("entries")
            .EnumerateArray().Single(value =>
                value.GetProperty("leaf_id").GetString() == "W3");
        Assert.Equal(new[]
        {
            ".github/workflows/ci.yml",
            "src/state-v2/import-boundary.test.ts",
            "src/residual-reference-allowlist.ts",
            "docs/20_architecture/agent-runtime-rebaseline.md",
            "docs/20_architecture/r1-legacy-removal-handoff.md",
            "docs/20_architecture/r3-single-shot-removal-handoff.md",
            "docs/20_architecture/r4-actionhost-wrapper-plan.md",
            "docs/50_ai/agent-context.md",
        }, w3.GetProperty("referenced_tests_and_docs").EnumerateArray()
                .Select(value => value.GetString()).ToArray());
        var w6 = replacement.RootElement.GetProperty("entries")
            .EnumerateArray().Single(value =>
                value.GetProperty("leaf_id").GetString() == "W6");
        Assert.Equal("removed", w6.GetProperty("disposition").GetString());
        Assert.Equal(new[]
        {
            "src/state-acceptance/",
            "protocol/schemas/candidate-registration.v1.json",
            "protocol/schemas/accepted-state-marker.v1.json",
            "protocol/schemas/state-selector.v1.json",
            "protocol/schemas/state-publication-receipt.v1.json",
        }, w6.GetProperty("removed_paths").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        Assert.False(Directory.Exists(Path.Join(root, "src", "state-acceptance")));
        Assert.Contains(
            "runtime/src/AgenticPrReview.Runtime/Host/Publishing/GitHub/Sticky/StickyCommentPublisher.cs#StickyPublicationReceipt",
            w6.GetProperty("owner_members").EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Contains(
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Publishing/GitHub/Sticky/StickyPublicationContractsTests.cs",
            w6.GetProperty("retained_evidence_paths").EnumerateArray()
                .Select(value => value.GetString()));
        var w6Cases = w6.GetProperty("legacy_test_cases")
            .EnumerateArray().ToArray();
        var w6CaseIds = w6Cases.Select(value => value.GetProperty("id")
            .GetString()).ToArray();
        Assert.Equal(47, w6CaseIds.Length);
        Assert.Equal(47, w6CaseIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("contract.test.ts::contract-version-legacy-v1", w6CaseIds);
        Assert.Contains("contract.test.ts::contract-version-unknown-version", w6CaseIds);
        Assert.Contains("contract.test.ts::kernel-lock", w6CaseIds);
        Assert.Contains("github-state-store.test.ts::counter-transaction", w6CaseIds);
        Assert.All(w6Cases, value =>
        {
            var retained = value.GetProperty("disposition").GetString() == "retained";
            Assert.True(retained || value.GetProperty("disposition").GetString() ==
                "reviewed_obsolete");
            Assert.Equal(retained, value.TryGetProperty("evidence_path", out _));
            Assert.Equal(retained, value.TryGetProperty("owner", out _));
            Assert.Equal(!retained, value.TryGetProperty("reason", out _));
        });
        var w6Helpers = w6.GetProperty("legacy_helper_cases")
            .EnumerateArray().ToArray();
        Assert.Equal(new[] { "lock-child.mjs::unix-socket-lock",
                "store-child.mjs::reference-store-child" },
            w6Helpers.Select(value => value.GetProperty("id").GetString())
                .Order(StringComparer.Ordinal).ToArray());
        var receipt = w6.GetProperty("receipt_disposition");
        Assert.Equal("protocol/schemas/state-publication-receipt.v1.json",
            receipt.GetProperty("legacy_schema").GetString());
        Assert.Equal(new[] { "P2", "P5", "P6", "S5", "S6" },
            receipt.GetProperty("owners").EnumerateArray()
                .Select(value => value.GetString()).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(
            "runtime/src/AgenticPrReview.Runtime/Host/Publishing/GitHub/Sticky/StickyCommentPublisher.cs#StickyPublicationReceipt",
            receipt.GetProperty("owner_members").EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Contains(
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Publishing/Recovery/PublicationRecoveryServiceTests.cs",
            receipt.GetProperty("evidence_paths").EnumerateArray()
                .Select(value => value.GetString()));
        var residual = w6.GetProperty("w6_residual_scan");
        var forbiddenTokens = residual.GetProperty("forbidden_tokens")
            .EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Equal(22, forbiddenTokens.Length);
        Assert.Contains("StateAcceptanceStore", forbiddenTokens);
        Assert.Contains("ReferenceStateStore", forbiddenTokens);
        Assert.Contains("OctokitGitDataClient", forbiddenTokens);
        Assert.Contains("acceptLocalCandidate", forbiddenTokens);
        Assert.Contains("candidate-registration.v1.json", forbiddenTokens);
        Assert.Contains("state-publication-receipt.v1.json", forbiddenTokens);
        Assert.Contains("@actions/cache", forbiddenTokens);
        Assert.Contains("actions/cache", forbiddenTokens);
        Assert.Contains("src/comments.ts", residual.GetProperty("w8_marker_paths")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("runtime/tests/fixtures/action-host/framework/e1-base-inventory.json",
            residual.GetProperty("immutable_provenance_paths").EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Equal(new[] { "scripts/check-r3-live-proof.mjs" },
            residual.GetProperty("retained_unrelated_policy_paths").EnumerateArray()
                .Select(value => value.GetString()).ToArray());
        var w11 = replacement.RootElement.GetProperty("entries")
            .EnumerateArray().Single(value =>
                value.GetProperty("leaf_id").GetString() == "W11");
        Assert.Equal("removed", w11.GetProperty("disposition").GetString());
        Assert.Equal(new[]
        {
            "src/prefix-contract/",
            "scripts/regenerate-prefix-contract-fixtures.mjs",
        }, w11.GetProperty("removed_paths").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        Assert.False(Directory.Exists(Path.Join(root, "src", "prefix-contract")));
        Assert.False(File.Exists(Path.Join(root, "scripts",
            "regenerate-prefix-contract-fixtures.mjs")));

        var w12 = replacement.RootElement.GetProperty("entries")
            .EnumerateArray().Single(value =>
                value.GetProperty("leaf_id").GetString() == "W12");
        Assert.Equal("removed",
            w12.GetProperty("disposition").GetString());
        Assert.Equal(new[]
        {
            "src/provider-metadata/",
            "protocol/schemas/provider-run-metadata.v1.json",
            "protocol/fixtures/provider-run-metadata/",
        }, w12.GetProperty("removed_paths").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        Assert.False(Directory.Exists(Path.Join(root, "src", "provider-metadata")));
        Assert.False(File.Exists(Path.Join(root, "protocol", "schemas",
            "provider-run-metadata.v1.json")));
        Assert.False(Directory.Exists(Path.Join(root, "protocol", "fixtures",
            "provider-run-metadata")));
        Assert.Contains(
            "runtime/tests/AgenticPrReview.Runtime.Tests/Execution/DeepSeek/DeepSeekChatBackendTests.cs",
            w12.GetProperty("retained_evidence_paths").EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Contains(
            "W5 opaque sidecar bytes descriptors hashes and fixtures remain under its current owner",
            w12.GetProperty("retained_owner_groups").EnumerateArray()
                .Select(value => value.GetString()));

        const string prefixCorpus = "protocol/fixtures/prefix-contract/";
        var expectedCorpus = inventory.RootElement.GetProperty("files")
            .EnumerateArray()
            .Where(value => value.GetProperty("path").GetString()!
                .StartsWith(prefixCorpus, StringComparison.Ordinal))
            .ToDictionary(
                value => value.GetProperty("path").GetString()!,
                value => value.GetProperty("sha256").GetString()!,
                StringComparer.Ordinal);
        var corpusRoot = Path.Join(root, "protocol", "fixtures", "prefix-contract");
        var currentCorpus = Directory.GetFiles(corpusRoot, "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expectedCorpus.Keys.Order(StringComparer.Ordinal),
            currentCorpus.Order(StringComparer.Ordinal));
        foreach (var (path, digest) in expectedCorpus)
        {
            Assert.Equal(digest, Convert.ToHexString(SHA256.HashData(
                File.ReadAllBytes(Path.Join(root,
                    path.Replace('/', Path.DirectorySeparatorChar)))))
                .ToLowerInvariant());
        }
        Assert.Equal("e698fb1df6daf49f393e87fac4f00e3a2ec2c716",
            inventory.RootElement.GetProperty("base_sha").GetString());
        Assert.Equal("apr.action-host.replacement-record.v2",
            replacement.RootElement.GetProperty("schema").GetString());
        Assert.Equal(349, inventory.RootElement.GetProperty("files")
            .GetArrayLength());

        var framing = new StringBuilder();
        foreach (var file in inventory.RootElement.GetProperty("files")
                     .EnumerateArray())
        {
            var path = file.GetProperty("path").GetString()!;
            var digest = file.GetProperty("sha256").GetString()!;
            Assert.Equal(digest, BaseBlobDigest(root,
                inventory.RootElement.GetProperty("base_sha").GetString()!,
                path));
            framing.Append(path).Append('\0').Append(digest).Append('\n');
        }

        Assert.Equal(
            inventory.RootElement.GetProperty("aggregate_sha256").GetString(),
            Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(framing.ToString())))
                .ToLowerInvariant());
    }

    [Fact]
    public void RuntimeCiRunsTheCheckedFrameworkProofTwiceWithoutCredentials()
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Join(root,
            ".github", "workflows", "runtime-ci.yml"));

        Assert.True(Count(workflow,
            "bash runtime/scripts/verify-action-host.sh framework") >= 2);
        Assert.True(Count(workflow, "persist-credentials: false") >= 2);
        Assert.DoesNotContain("secrets.", workflow,
            StringComparison.Ordinal);
    }

    private static int Count(string value, string searched)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(searched, offset,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += searched.Length;
        }

        return count;
    }

    private static string BaseBlobDigest(
        string repository,
        string revision,
        string path)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("show");
        info.ArgumentList.Add(revision + ":" + path);
        using var process = Process.Start(info);
        Assert.NotNull(process);
        using var bytes = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(bytes);
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()))
            .ToLowerInvariant();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "package.json")) &&
                Directory.Exists(Path.Join(directory.FullName, "runtime")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("repository_root_not_found");
    }
}
