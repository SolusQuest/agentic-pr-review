using System.Net.Http.Headers;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using AgenticPrReview.Runtime.ActionHostVerifierFixture;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.TrustedProof;

public sealed class TrustedProofVerifierFixtureTests
{
    [Fact]
    public async Task ProofControlBarrierCompletesAcrossFreshProofScenarios()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "apr-r4-e2p-control-barrier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var seed = Path.Join(root, "dispatch-bootstrap");
            await PrepareProofScenarioAsync(seed, "continuation-seed", 900);
            Assert.Equal(0, await RunControlAsync(seed, 900, "hold"));
            var stickyRoot = Path.Join(
                root,
                "shared-continuation-github");
            Directory.CreateDirectory(stickyRoot);
            await File.WriteAllTextAsync(
                Path.Join(stickyRoot, "sticky-comment.json"),
                "{\"id\":701,\"body\":\"ordinary sticky\"}");

            var continuation = Path.Join(root, "dispatch-continuation");
            await PrepareProofScenarioAsync(continuation, "continuation", 901);
            Assert.Equal(0, await RunControlAsync(
                continuation,
                901,
                "verify-completed"));
            Assert.Equal(0, await RunControlAsync(
                continuation,
                901,
                "cleanup"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProofControlUsesItsFrozenRepositoryCoordinateOnlyInProofCases()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "apr-r4-e2p-control-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Join(root, "mode"),
                "continuation-seed");
            await File.WriteAllTextAsync(
                Path.Join(root, "trusted-proof-payload"), "1");
            await File.WriteAllTextAsync(Path.Join(root, "run-id"), "900");
            await File.WriteAllTextAsync(Path.Join(root, "run-attempt"), "1");
            using var handler = new FrameworkGitHubHandler(
                root,
                new string('f', 64));
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.github.com/"),
            };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    FrameworkCanaries.GitHubToken);

            using var proofResponse = await client.GetAsync(
                "repos/" + FrameworkCanaries.ProofControlRepository +
                "/issues/147/comments");
            proofResponse.EnsureSuccessStatusCode();
            Assert.Equal("[]", await proofResponse.Content.ReadAsStringAsync());

            using var productResponse = await client.GetAsync(
                "repos/" + FrameworkCanaries.Repository);
            productResponse.EnsureSuccessStatusCode();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TrustedProofContinuationAdvancesTheReviewedHead()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "apr-r4-e2p-continuation-head-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await PrepareProofScenarioAsync(root, "continuation", 901);
            using var handler = new FrameworkGitHubHandler(
                root,
                new string('f', 64));
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.github.com/"),
            };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    FrameworkCanaries.GitHubToken);

            using var response = await client.GetAsync(
                "repos/" + FrameworkCanaries.Repository + "/pulls/147");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());
            Assert.Equal(
                FrameworkGitHubHandler.ContinuedHeadSha,
                document.RootElement.GetProperty("head")
                    .GetProperty("sha").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FinalWorkflowAndNestedTrustedPolicyShareOneSyntheticTree()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "apr-r4-e2p-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var handler = new FrameworkGitHubHandler(
                root,
                new string('f', 64));
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.github.com/"),
            };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    FrameworkCanaries.GitHubToken);

            var githubTree = await GetTreeAsync(client, new string('2', 40));
            Assert.DoesNotContain(githubTree, entry =>
                entry.GetProperty("path").GetString() == "trusted-proof.json");
            Assert.Contains(githubTree, entry =>
                entry.GetProperty("path").GetString() == "agentic-pr-review" &&
                entry.GetProperty("type").GetString() == "tree");

            var policyTree = await GetTreeAsync(client, new string('3', 40));
            Assert.Contains(policyTree, entry =>
                entry.GetProperty("path").GetString() == "trusted-proof.json" &&
                entry.GetProperty("type").GetString() == "blob");
            Assert.Contains(policyTree, entry =>
                entry.GetProperty("path").GetString() ==
                    "trusted-proof-instructions.md" &&
                entry.GetProperty("type").GetString() == "blob");

            using var workflowResponse = await client.GetAsync(
                "repos/" + FrameworkCanaries.Repository +
                "/contents/.github/workflows/r4-trusted-proof.yml");
            workflowResponse.EnsureSuccessStatusCode();
            var workflowJson = await workflowResponse.Content.ReadAsStringAsync();
            using var workflowDocument = JsonDocument.Parse(workflowJson);
            var workflowSource = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(
                    workflowDocument.RootElement.GetProperty("content")
                        .GetString()!));
            Assert.Contains(FrameworkCanaries.Workflow, workflowSource,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task PrepareProofScenarioAsync(
        string scenario,
        string mode,
        long runId)
    {
        Directory.CreateDirectory(scenario);
        await File.WriteAllTextAsync(Path.Join(scenario, "mode"), mode);
        await File.WriteAllTextAsync(
            Path.Join(scenario, "trusted-proof-payload"), "1");
        await File.WriteAllTextAsync(
            Path.Join(scenario, "run-id"), runId.ToString());
        await File.WriteAllTextAsync(Path.Join(scenario, "run-attempt"), "1");
    }

    private static Task<int> RunControlAsync(
        string scenario,
        long runId,
        string command)
    {
        var payloadSha256 = new string('f', 64);
        var coordinates = new TrustedProofControlCoordinates(
            FrameworkCanaries.ProofControlRepository,
            FrameworkGitHubHandler.RepositoryId,
            FrameworkGitHubHandler.PullRequestNumber,
            FrameworkGitHubHandler.HeadSha,
            new string('1', 64),
            FrameworkGitHubHandler.WorkflowSha,
            FrameworkGitHubHandler.ActionSha,
            payloadSha256,
            runId,
            1);
        return TrustedProofControlService.RunAsync(
            [command],
            coordinates,
            TrustedProofControlTransport.Create(
                coordinates,
                FrameworkCanaries.GitHubToken,
                new FrameworkGitHubHandler(scenario, payloadSha256)),
            CancellationToken.None);
    }

    private static async Task<JsonElement[]> GetTreeAsync(
        HttpClient client,
        string treeSha)
    {
        using var response = await client.GetAsync(
            "repos/" + FrameworkCanaries.Repository + "/git/trees/" + treeSha);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("tree")
            .EnumerateArray()
            .Select(element => element.Clone())
            .ToArray();
    }
}
