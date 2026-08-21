using System.Net.Http.Headers;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHostVerifierFixture;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.TrustedProof;

public sealed class TrustedProofVerifierFixtureTests
{
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
