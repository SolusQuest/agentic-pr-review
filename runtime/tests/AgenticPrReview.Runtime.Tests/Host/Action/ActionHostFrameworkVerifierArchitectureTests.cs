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
