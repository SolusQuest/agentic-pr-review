using System.Formats.Tar;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Serialization;
using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using AgenticPrReview.Runtime.ActionHostVerifierFixture;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.TrustedProof;

public sealed class TrustedProofVerifierFixtureTests
{
    [Fact]
    public void RequestBudgetTailsUseTheLargestFutureChargedSuffixPerDomain()
    {
        var bootstrap = new[]
        {
            Event("dispatch-bootstrap", 1, 1, "node_artifact_rest", "success"),
            Event("dispatch-bootstrap", 1, 2, "host_head_source_rest", "not_modified"),
            Event("dispatch-bootstrap", 1, 3, "trusted_control_rest", "permission_denied"),
            Event("dispatch-bootstrap", 1, 4, "host_other_github_rest", "success"),
        };
        var continuation = new[]
        {
            Event("dispatch-continuation", 2, 5, "node_artifact_rest", "success"),
            Event("dispatch-continuation", 2, 6, "host_head_source_rest", "success"),
        };
        var stale = new[]
        {
            Event("stale-head", 3, 7, "host_other_github_rest", "success"),
        };

        var tails = FrameworkSupervisor.DomainFuturePrimaryTails(
            [bootstrap, continuation, stale]);

        Assert.Equal(5, tails["node_artifact_rest"]);
        Assert.Equal(5, tails["host_head_source_rest"]);
        Assert.Equal(4, tails["trusted_control_rest"]);
        Assert.Equal(3, tails["host_other_github_rest"]);
        Assert.Equal(4, tails.Count);
    }

    [Fact]
    public void RequestWitnessJoinsPlatformGithubAndControlWithoutForwardingDuplicates()
    {
        var root = CreateWitnessScenario();
        try
        {
            WriteWitnessSources(root,
                [
                    "node_artifact_rest\tgithub_rest\tGET\t1\t20\tsuccess",
                ],
                ["verifier_github\t1\thost_other_github_rest\tgithub_rest\tGET\t1\t10\tother_failure"],
                ["verifier_control\t1\ttrusted_control_rest\tgithub_rest\tPOST\t5\t10\tsuccess"]);

            Assert.True(FrameworkSupervisor.MaterializeScenarioRequestEventsForTest(root));
            Assert.Equal(
                [
                    "host_other_github_rest\tgithub_rest\tGET\t1\t10\tother_failure",
                    "trusted_control_rest\tgithub_rest\tPOST\t5\t10\tsuccess",
                    "node_artifact_rest\tgithub_rest\tGET\t1\t20\tsuccess",
                ],
                File.ReadAllLines(Path.Join(root,
                    "trusted-proof-request-events.tsv")));
            Assert.Contains("host_head_source_rest\t0", File.ReadAllLines(
                Path.Join(root, "trusted-proof-request-domains.tsv")));
            Assert.Contains("host_other_github_rest\t1", File.ReadAllLines(
                Path.Join(root, "trusted-proof-request-domains.tsv")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RequestWitnessFailsClosedWhenPlatformClaimsHostOrControlTraffic()
    {
        var host = CreateWitnessScenario();
        var control = CreateWitnessScenario();
        try
        {
            WriteWitnessSources(host,
                ["host_head_source_rest\tgithub_rest\tGET\t1\t1\tsuccess"],
                ["verifier_github\t1\thost_head_source_rest\tgithub_rest\tGET\t1\t2\tsuccess"],
                ["verifier_control\t1\ttrusted_control_rest\tgithub_rest\tGET\t1\t3\tsuccess"]);
            Assert.False(FrameworkSupervisor
                .MaterializeScenarioRequestEventsForTest(host));

            WriteWitnessSources(control,
                ["trusted_control_rest\tgithub_rest\tGET\t1\t1\tsuccess"],
                ["verifier_github\t1\thost_other_github_rest\tgithub_rest\tGET\t1\t2\tsuccess"],
                ["verifier_control\t1\ttrusted_control_rest\tgithub_rest\tGET\t1\t3\tsuccess"]);
            Assert.False(FrameworkSupervisor
                .MaterializeScenarioRequestEventsForTest(control));
        }
        finally
        {
            Directory.Delete(host, recursive: true);
            Directory.Delete(control, recursive: true);
        }
    }

    [Fact]
    public void RequestWitnessFailsClosedForMissingMalformedOrDuplicateSources()
    {
        var missing = CreateWitnessScenario();
        var malformed = CreateWitnessScenario();
        var duplicate = CreateWitnessScenario();
        try
        {
            File.WriteAllText(Path.Join(missing,
                "trusted-proof-request-events.tsv"),
                "node_artifact_rest\tgithub_rest\tGET\t1\t1\tsuccess\n");
            Assert.False(FrameworkSupervisor.MaterializeScenarioRequestEventsForTest(missing));

            WriteWitnessSources(malformed,
                ["node_artifact_rest\tgithub_rest\tGET\t1\t1\tsuccess"],
                ["verifier_github\t1\thost_other_github_rest\tgithub_rest\tGET\t1\t2\tother_failure"],
                ["verifier_control\t1\ttrusted_control_rest\tgithub_rest\tPOST\t5\t3\tsecret"]);
            Assert.False(FrameworkSupervisor.MaterializeScenarioRequestEventsForTest(malformed));

            WriteWitnessSources(duplicate,
                ["node_artifact_rest\tgithub_rest\tGET\t1\t1\tsuccess"],
                [
                    "verifier_github\t1\thost_other_github_rest\tgithub_rest\tGET\t1\t2\tsuccess",
                    "verifier_github\t1\thost_other_github_rest\tgithub_rest\tGET\t1\t3\tsuccess",
                ],
                ["verifier_control\t1\ttrusted_control_rest\tgithub_rest\tPOST\t5\t4\tsuccess"]);
            Assert.False(FrameworkSupervisor.MaterializeScenarioRequestEventsForTest(duplicate));
        }
        finally
        {
            Directory.Delete(missing, recursive: true);
            Directory.Delete(malformed, recursive: true);
            Directory.Delete(duplicate, recursive: true);
        }
    }

    [Fact]
    public void RequestWitnessOrderingIsDeterministicWhenSourceInputIsReordered()
    {
        var first = CreateWitnessScenario();
        var second = CreateWitnessScenario();
        try
        {
            var platform = new[]
            {
                "node_artifact_rest\tgithub_rest\tGET\t1\t20\tsuccess",
                "actions_results_service\tactions_results_twirp\tPOST\t5\t20\tsuccess",
            };
            var github = new[]
            {
                "verifier_github\t2\thost_head_source_rest\tgithub_rest\tGET\t1\t20\tsuccess",
                "verifier_github\t1\thost_other_github_rest\tgithub_rest\tGET\t1\t20\tsuccess",
            };
            var control = new[]
            {
                "verifier_control\t2\ttrusted_control_rest\tgithub_rest\tPOST\t5\t20\tsuccess",
                "verifier_control\t1\ttrusted_control_rest\tgithub_rest\tPOST\t5\t20\tsuccess",
            };
            WriteWitnessSources(first, platform, github, control);
            WriteWitnessSources(second, platform.Reverse().ToArray(),
                github.Reverse().ToArray(), control);
            Assert.True(FrameworkSupervisor.MaterializeScenarioRequestEventsForTest(first));
            Assert.True(FrameworkSupervisor.MaterializeScenarioRequestEventsForTest(second));
            Assert.Equal(File.ReadAllText(Path.Join(first,
                    "trusted-proof-request-events.tsv")),
                File.ReadAllText(Path.Join(second,
                    "trusted-proof-request-events.tsv")));
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }

    [Fact]
    public void RequestBudgetTailsAreDeterministicAfterWitnessReordering()
    {
        var ordered = new[]
        {
            Event("dispatch-bootstrap", 1, 1, "host_other_github_rest", "success"),
            Event("dispatch-bootstrap", 1, 2, "node_artifact_rest", "success"),
            Event("dispatch-continuation", 2, 3, "trusted_control_rest", "success"),
            Event("stale-head", 3, 4, "host_head_source_rest", "success"),
        };
        var reordered = ordered.Reverse().ToArray();

        var first = FrameworkSupervisor.DomainFuturePrimaryTails(
            [ordered.Where(value => value.ScenarioOrdinal == 1),
                ordered.Where(value => value.ScenarioOrdinal == 2),
                ordered.Where(value => value.ScenarioOrdinal == 3)]);
        var second = FrameworkSupervisor.DomainFuturePrimaryTails(
            [reordered.Where(value => value.ScenarioOrdinal == 1),
                reordered.Where(value => value.ScenarioOrdinal == 2),
                reordered.Where(value => value.ScenarioOrdinal == 3)]);

        Assert.Equal(first.OrderBy(pair => pair.Key), second.OrderBy(pair => pair.Key));
        Assert.Equal(3, first["host_other_github_rest"]);
        Assert.Equal(2, first["node_artifact_rest"]);
        Assert.Equal(1, first["trusted_control_rest"]);
        Assert.Equal(0, first["host_head_source_rest"]);
    }

    [Fact]
    public void RequestBudgetTailsTreatSameTimestampAsAConcurrentCrossSourceBucket()
    {
        // These represent a platform Node request, a verifier-owned Host
        // request, and a verifier-owned control request that all began on the
        // same monotonic timer tick. Their append order must not change any
        // profile tail: every response retains the other primary work in its
        // concurrent bucket.
        var first = new[]
        {
            Event("dispatch-bootstrap", 1, 1, "node_artifact_rest", "success",
                timestamp: 10),
            Event("dispatch-bootstrap", 1, 2, "host_head_source_rest", "success",
                timestamp: 10),
            Event("dispatch-bootstrap", 1, 3, "trusted_control_rest", "success",
                timestamp: 10),
            Event("dispatch-continuation", 2, 4, "host_other_github_rest", "success",
                timestamp: 20),
        };
        var reordered = first.Reverse().ToArray();

        var expected = FrameworkSupervisor.DomainFuturePrimaryTails(
            [first.Where(value => value.ScenarioOrdinal == 1),
                first.Where(value => value.ScenarioOrdinal == 2)]);
        var actual = FrameworkSupervisor.DomainFuturePrimaryTails(
            [reordered.Where(value => value.ScenarioOrdinal == 1),
                reordered.Where(value => value.ScenarioOrdinal == 2)]);

        Assert.Equal(expected.OrderBy(pair => pair.Key), actual.OrderBy(pair => pair.Key));
        Assert.Equal(3, expected["node_artifact_rest"]);
        Assert.Equal(3, expected["host_head_source_rest"]);
        Assert.Equal(3, expected["trusted_control_rest"]);
        Assert.Equal(0, expected["host_other_github_rest"]);
    }

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

            var stale = Path.Join(root, "stale-head");
            await PrepareProofScenarioAsync(stale, "stale", 902);
            Assert.Equal(
                TrustedProofStaleWindowCoordinator.StaleOperationId,
                FrameworkCanaries.StaleProofOperationId);
            Assert.Equal(1, await RunControlAsync(
                stale,
                902,
                "hold",
                FrameworkCanaries.ProofOperationId));
            Assert.Equal(0, await RunControlAsync(
                stale,
                902,
                "hold",
                FrameworkCanaries.StaleProofOperationId));

            Assert.Equal(
                TrustedProofControlCoordinates.FrozenRepository,
                FrameworkCanaries.ProofControlRepository);
            var staleCoordinates = new TrustedProofControlCoordinates(
                TrustedProofControlCoordinates.FrozenRepository,
                FrameworkGitHubHandler.RepositoryId,
                FrameworkGitHubHandler.PullRequestNumber,
                FrameworkGitHubHandler.HeadSha,
                FrameworkCanaries.StaleProofOperationId,
                FrameworkGitHubHandler.WorkflowSha,
                FrameworkGitHubHandler.ActionSha,
                new string('f', 64),
                902,
                1);
            using var control = TrustedProofControlTransport.Create(
                staleCoordinates,
                FrameworkCanaries.GitHubToken,
                new FrameworkGitHubHandler(stale, new string('f', 64)));
            var beforeStaleSignal = await control.ListAsync(
                CancellationToken.None);
            Assert.NotNull(beforeStaleSignal);
            Assert.Equal(2, beforeStaleSignal.Count);
            var staleReady = TrustedProofControlMarker.CreateBody(
                "stale-ready",
                staleCoordinates,
                predecessorCommentId: null);
            var creation = await control.CreateAsync(
                staleReady, CancellationToken.None);
            Assert.Equal(TrustedProofMutationOutcome.Committed, creation.Outcome);
            var controlComments = await control.ListAsync(CancellationToken.None);
            Assert.NotNull(controlComments);
            Assert.Equal(4, controlComments.Count);
            var kinds = new List<string>();
            Assert.All(controlComments, comment =>
            {
                Assert.True(TrustedProofControlMarker.TryParse(
                    comment.Body, out var marker));
                Assert.Equal(
                    TrustedProofControlCoordinates.FrozenRepository,
                    marker!.Repository);
                kinds.Add(marker.Kind);
            });
            Assert.Equal(
                ["ready", "release", "stale-ready", "stale-release"],
                kinds.Order(StringComparer.Ordinal));
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
    public async Task TrustedProofGitFactsShareTheWorkflowBaseAndPolicyTree()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "apr-r4-e2p-base-facts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await PrepareProofScenarioAsync(root, "continuation-seed", 900);
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

            Assert.Equal(
                FrameworkGitHubHandler.WorkflowSha,
                FrameworkGitHubHandler.PullRequestBaseSha(
                    trustedProofPayload: true));
            Assert.Equal(
                FrameworkGitHubHandler.BaseSha,
                FrameworkGitHubHandler.PullRequestBaseSha(
                    trustedProofPayload: false));

            using var triggerResponse = await client.GetAsync(
                "repos/" + FrameworkCanaries.Repository +
                "/actions/runs/800/attempts/1");
            triggerResponse.EnsureSuccessStatusCode();
            using var trigger = JsonDocument.Parse(
                await triggerResponse.Content.ReadAsStringAsync());
            Assert.Equal(
                FrameworkGitHubHandler.WorkflowSha,
                trigger.RootElement.GetProperty("pull_requests")[0]
                    .GetProperty("base").GetProperty("sha").GetString());

            using var associatedResponse = await client.GetAsync(
                "repos/" + FrameworkCanaries.Repository + "/commits/" +
                FrameworkGitHubHandler.TriggerSha + "/pulls");
            associatedResponse.EnsureSuccessStatusCode();
            using var associated = JsonDocument.Parse(
                await associatedResponse.Content.ReadAsStringAsync());
            Assert.Equal(
                FrameworkGitHubHandler.WorkflowSha,
                associated.RootElement[0].GetProperty("base")
                    .GetProperty("sha").GetString());

            using var pullResponse = await client.GetAsync(
                "repos/" + FrameworkCanaries.Repository + "/pulls/147");
            pullResponse.EnsureSuccessStatusCode();
            using var pull = JsonDocument.Parse(
                await pullResponse.Content.ReadAsStringAsync());
            Assert.Equal(
                FrameworkGitHubHandler.WorkflowSha,
                pull.RootElement.GetProperty("base")
                    .GetProperty("sha").GetString());

            using var commitResponse = await client.GetAsync(
                "repos/" + FrameworkCanaries.Repository + "/git/commits/" +
                FrameworkGitHubHandler.WorkflowSha);
            commitResponse.EnsureSuccessStatusCode();
            using var commit = JsonDocument.Parse(
                await commitResponse.Content.ReadAsStringAsync());
            var workflowTree = await GetTreeAsync(
                client,
                commit.RootElement.GetProperty("tree")
                    .GetProperty("sha").GetString()!);
            Assert.Contains(workflowTree, entry =>
                entry.GetProperty("path").GetString() == ".github" &&
                entry.GetProperty("type").GetString() == "tree");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TrustedProofCurrentHeadAuthorityBindsTheV2WorkflowAndGitFacts()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "apr-r4-e2p-current-head-authority-" + Guid.NewGuid().ToString("N"));
        const string sourceCommit = "6666666666666666666666666666666666666666";
        var payloadSha256 = new string('f', 64);
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Join(root, "mode"),
                "continuation-seed");
            await File.WriteAllTextAsync(Path.Join(root,
                "trusted-proof-payload"), "1");
            await File.WriteAllTextAsync(Path.Join(root,
                "trusted-proof-authority.json"),
                "{\"source_commit\":\"" + sourceCommit + "\"}");
            await File.WriteAllTextAsync(Path.Join(root, "run-id"), "900");
            await File.WriteAllTextAsync(Path.Join(root, "run-attempt"), "1");
            using var handler = new FrameworkGitHubHandler(
                root,
                payloadSha256,
                TrustedProofV2WorkflowAdmission.Render);
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.github.com/"),
            };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    FrameworkCanaries.GitHubToken);

            using var runResponse = await client.GetAsync(
                "repos/" + FrameworkCanaries.Repository +
                "/actions/runs/900/attempts/1");
            runResponse.EnsureSuccessStatusCode();
            using var run = JsonDocument.Parse(
                await runResponse.Content.ReadAsStringAsync());
            Assert.Equal(sourceCommit,
                run.RootElement.GetProperty("head_sha").GetString());

            using var pullResponse = await client.GetAsync(
                "repos/" + FrameworkCanaries.Repository + "/pulls/147");
            pullResponse.EnsureSuccessStatusCode();
            using var pull = JsonDocument.Parse(
                await pullResponse.Content.ReadAsStringAsync());
            Assert.Equal(sourceCommit,
                pull.RootElement.GetProperty("base").GetProperty("sha")
                    .GetString());

            using var commitResponse = await client.GetAsync(
                "repos/" + FrameworkCanaries.Repository + "/git/commits/" +
                sourceCommit);
            commitResponse.EnsureSuccessStatusCode();
            using var commit = JsonDocument.Parse(
                await commitResponse.Content.ReadAsStringAsync());
            Assert.Equal(new string('1', 40),
                commit.RootElement.GetProperty("tree").GetProperty("sha")
                    .GetString());

            using var workflowResponse = await client.GetAsync(
                "repos/" + FrameworkCanaries.Repository +
                "/contents/.github/workflows/r4-trusted-proof.yml");
            workflowResponse.EnsureSuccessStatusCode();
            using var workflow = JsonDocument.Parse(
                await workflowResponse.Content.ReadAsStringAsync());
            var source = Encoding.UTF8.GetString(Convert.FromBase64String(
                workflow.RootElement.GetProperty("content").GetString()!));
            Assert.Equal(TrustedProofV2WorkflowAdmission.Render(
                    sourceCommit,
                    payloadSha256),
                source);
            Assert.NotEqual(ActionHostTrustedWorkflowContract.Render(
                sourceCommit,
                payloadSha256), source);

            await File.WriteAllTextAsync(Path.Join(root, "mode"),
                "wrong-action");
            using var wrongActionResponse = await client.GetAsync(
                "repos/" + FrameworkCanaries.Repository +
                "/contents/.github/workflows/r4-trusted-proof.yml");
            wrongActionResponse.EnsureSuccessStatusCode();
            using var wrongAction = JsonDocument.Parse(
                await wrongActionResponse.Content.ReadAsStringAsync());
            var wrongActionSource = Encoding.UTF8.GetString(
                Convert.FromBase64String(wrongAction.RootElement
                    .GetProperty("content").GetString()!));
            Assert.NotEqual(source, wrongActionSource);
            Assert.EndsWith("# trusted-proof-wrong-action\n",
                wrongActionSource, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OrdinaryFixtureKeepsItsStaticV1IdentityWithoutAuthority()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "apr-r4-e2p-ordinary-static-identity-" + Guid.NewGuid().ToString("N"));
        var payloadSha256 = new string('f', 64);
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Join(root, "mode"), "sticky");
            await File.WriteAllTextAsync(Path.Join(root, "run-id"), "900");
            await File.WriteAllTextAsync(Path.Join(root, "run-attempt"), "1");
            using var handler = new FrameworkGitHubHandler(root, payloadSha256);
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.github.com/"),
            };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    FrameworkCanaries.GitHubToken);

            using var runResponse = await client.GetAsync(
                "repos/" + FrameworkCanaries.Repository +
                "/actions/runs/900/attempts/1");
            runResponse.EnsureSuccessStatusCode();
            using var run = JsonDocument.Parse(
                await runResponse.Content.ReadAsStringAsync());
            Assert.Equal(FrameworkGitHubHandler.WorkflowSha,
                run.RootElement.GetProperty("head_sha").GetString());

            using var workflowResponse = await client.GetAsync(
                "repos/" + FrameworkCanaries.Repository +
                "/contents/.github/workflows/r4-trusted-proof.yml");
            workflowResponse.EnsureSuccessStatusCode();
            using var workflow = JsonDocument.Parse(
                await workflowResponse.Content.ReadAsStringAsync());
            var source = Encoding.UTF8.GetString(Convert.FromBase64String(
                workflow.RootElement.GetProperty("content").GetString()!));
            Assert.Equal(ActionHostTrustedWorkflowContract.Render(
                FrameworkGitHubHandler.ActionSha,
                payloadSha256), source);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidTrustedProofCurrentHeadAuthorityFailsClosed()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "apr-r4-e2p-invalid-current-head-authority-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Join(root, "mode"), "sticky");
            await File.WriteAllTextAsync(Path.Join(root,
                "trusted-proof-payload"), "1");
            await File.WriteAllTextAsync(Path.Join(root,
                "trusted-proof-authority.json"),
                "{\"source_commit\":\"ABCDEF\"}");
            await File.WriteAllTextAsync(Path.Join(root, "run-id"), "900");
            await File.WriteAllTextAsync(Path.Join(root, "run-attempt"), "1");
            using var handler = new FrameworkGitHubHandler(
                root,
                new string('f', 64),
                TrustedProofV2WorkflowAdmission.Render);
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.github.com/"),
            };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    FrameworkCanaries.GitHubToken);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.GetAsync("repos/" + FrameworkCanaries.Repository +
                    "/actions/runs/900/attempts/1"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TrustedProofCurrentHeadAuthorityRejectsTheImplicitV1Renderer()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "apr-r4-e2p-implicit-v1-current-head-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Join(root, "mode"), "sticky");
            await File.WriteAllTextAsync(Path.Join(root,
                "trusted-proof-payload"), "1");
            await File.WriteAllTextAsync(Path.Join(root,
                "trusted-proof-authority.json"),
                "{\"source_commit\":\"" + new string('6', 40) + "\"}");
            await File.WriteAllTextAsync(Path.Join(root, "run-id"), "900");
            await File.WriteAllTextAsync(Path.Join(root, "run-attempt"), "1");
            using var handler = new FrameworkGitHubHandler(root,
                new string('f', 64));
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.github.com/"),
            };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    FrameworkCanaries.GitHubToken);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.GetAsync("repos/" + FrameworkCanaries.Repository +
                    "/contents/.github/workflows/r4-trusted-proof.yml"));
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

    [Fact]
    public async Task ExactHeadArchiveUsesAnonymousCodeloadAnd178TreeTopology()
    {
        var root = Path.Join(Path.GetTempPath(),
            "apr-r4-e2p-archive-topology-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var handler = new FrameworkGitHubHandler(root,
                new string('f', 64));
            using var api = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.github.com/"),
            };
            api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", FrameworkCanaries.GitHubToken);
            using var redirect = await api.GetAsync("repos/" +
                FrameworkCanaries.Repository + "/tarball/" +
                FrameworkGitHubHandler.HeadSha);
            Assert.Equal(System.Net.HttpStatusCode.Found, redirect.StatusCode);
            using var codeload = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://codeload.github.com/"),
            };
            using var archiveResponse = await codeload.GetAsync(
                redirect.Headers.Location);
            archiveResponse.EnsureSuccessStatusCode();
            using var archive = new GZipStream(
                new MemoryStream(await archiveResponse.Content.ReadAsByteArrayAsync()),
                CompressionMode.Decompress);
            using var reader = new TarReader(archive);
            var entries = new Dictionary<string, TarEntry>(StringComparer.Ordinal);
            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) is not null) entries.Add(
                entry.Name, entry);
            var bundle = entries["agentic-pr-review-fixture/.github/actions/" +
                "agentic-pr-review/dist/index.js"];
            Assert.Equal((UnixFileMode)0x1b4, bundle.Mode);
            Assert.Equal(FrameworkGitHubHandler.ProductionShapedLargeBlobByteCount,
                bundle.Length);
            Assert.Equal("0", await File.ReadAllTextAsync(Path.Join(root,
                "head-blob-api-count")));

            using var commitResponse = await api.GetAsync("repos/" +
                FrameworkCanaries.Repository + "/git/commits/" +
                FrameworkGitHubHandler.HeadSha);
            using var commit = JsonDocument.Parse(
                await commitResponse.Content.ReadAsStringAsync());
            var pending = new Stack<string>([
                commit.RootElement.GetProperty("tree").GetProperty("sha")
                    .GetString()!,
            ]);
            var visited = 0;
            while (pending.TryPop(out var treeSha))
            {
                visited++;
                foreach (var treeEntry in await GetTreeAsync(api, treeSha))
                {
                    if (treeEntry.GetProperty("type").GetString() == "tree")
                    {
                        pending.Push(treeEntry.GetProperty("sha").GetString()!);
                    }
                }
            }

            Assert.Equal(FrameworkGitHubHandler.ProductionShapedHeadTreeObjectCount,
                visited);
            Assert.Equal("1", await File.ReadAllTextAsync(Path.Join(root,
                "head-commit-api-count")));
            Assert.Equal(FrameworkGitHubHandler.ProductionShapedHeadTreeObjectCount
                    .ToString(),
                await File.ReadAllTextAsync(Path.Join(root,
                    "head-tree-api-count")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StaleHostFailureCancelsCoordinatorBeforeAwaitingRelease()
    {
        var root = Path.Join(Path.GetTempPath(),
            "apr-r4-e2p-stale-coordinator-cancel-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Join(root, "mode"), "stale");
            await File.WriteAllTextAsync(Path.Join(root,
                "trusted-proof-payload"), "1");
            var eventPath = Path.Join(root, "event.json");
            var eventBytes = Encoding.UTF8.GetBytes("{}");
            await File.WriteAllBytesAsync(eventPath, eventBytes);
            var launch = StaleCancelledLaunch(eventPath, eventBytes);
            var input = FramedLaunch(launch);
            await using var output = new MemoryStream();
            var run = TrustedProofPayloadHost.RunCoreAsync(
                input,
                output,
                _ => Task.FromResult(new TrustedProofPayloadRuntimePorts(
                    () => new FrameworkGitHubHandler(root,
                        launch.PayloadSha256),
                    github => new FrameworkStateDependencies(root, github))),
                CancellationToken.None);

            Assert.Equal(1, await run.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal("1", await File.ReadAllTextAsync(Path.Join(root,
                "pull-request-revalidation-count")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SyntheticOfficialPlatformConditionalArtifactAndAttemptGetsAreFreeOfPrimaryCharge()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "apr-r4-e2p-conditional-rest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var platform = SyntheticOfficialPlatform.Start(root);
            using var client = new HttpClient
            {
                BaseAddress = new Uri(platform.BaseUrl + "/"),
            };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", FrameworkCanaries.GitHubToken);
            var artifactPath = "repos/" + FrameworkCanaries.Repository +
                "/actions/artifacts?name=missing&per_page=100&page=1";

            using var initial = await client.GetAsync(artifactPath);
            initial.EnsureSuccessStatusCode();
            var artifactEtag = initial.Headers.ETag;
            Assert.NotNull(artifactEtag);
            var artifactTag = artifactEtag.Tag;
            using var conditional = new HttpRequestMessage(
                HttpMethod.Get, artifactPath);
            conditional.Headers.TryAddWithoutValidation("If-None-Match", artifactTag);
            using var notModified = await client.SendAsync(conditional);
            Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
            Assert.Equal(artifactTag, notModified.Headers.ETag?.Tag);

            const string attemptPath = "repos/" + FrameworkCanaries.Repository +
                "/actions/runs/900/attempts/1";
            using var attempt = await client.GetAsync(attemptPath);
            attempt.EnsureSuccessStatusCode();
            var attemptEtag = attempt.Headers.ETag;
            Assert.NotNull(attemptEtag);
            var attemptTag = attemptEtag.Tag;
            using var conditionalAttempt = new HttpRequestMessage(
                HttpMethod.Get, attemptPath);
            conditionalAttempt.Headers.TryAddWithoutValidation("If-None-Match", attemptTag);
            using var notModifiedAttempt = await client.SendAsync(conditionalAttempt);
            Assert.Equal(HttpStatusCode.NotModified, notModifiedAttempt.StatusCode);

            Assert.Equal("4", await File.ReadAllTextAsync(
                Path.Join(root, "official-rest-count")));
            Assert.Equal("2", await File.ReadAllTextAsync(
                Path.Join(root, "official-rest-not-modified-count")));
            Assert.Equal("2", await File.ReadAllTextAsync(
                Path.Join(root, "official-rest-primary-count")));
            Assert.Equal("4", await File.ReadAllTextAsync(
                Path.Join(root, "official-rest-secondary-points")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateWitnessScenario()
    {
        var root = Path.Join(Path.GetTempPath(),
            "apr-r4-e2p-request-witness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteWitnessSources(
        string root,
        IReadOnlyList<string> platform,
        IReadOnlyList<string> github,
        IReadOnlyList<string> control)
    {
        File.WriteAllLines(Path.Join(root, "trusted-proof-request-events.tsv"),
            platform);
        File.WriteAllLines(Path.Join(root, "verifier-github-requests.tsv"),
            github);
        File.WriteAllLines(Path.Join(root, "verifier-control-requests.tsv"),
            control);
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

    private static ActionHostLaunchContract StaleCancelledLaunch(
        string eventPath,
        byte[] eventBytes)
    {
        Assert.True(ActionHostGitHubToken.TryCreate(
            FrameworkCanaries.GitHubToken, out var githubToken));
        Assert.True(ActionHostProviderApiKey.TryCreate(
            "provider-canary", out var providerKey));
        Assert.True(ActionHostStateKey.TryCreate(
            Convert.ToBase64String(new byte[32]), out var stateKey));
        Assert.True(ActionHostInputs.TryCreate(
            githubToken,
            providerKey,
            stateKey,
            previousStateKey: null,
            configPath: null,
            FrameworkGitHubHandler.PullRequestNumber,
            ActionHostStateMode.Auto,
            out var inputs));
        Assert.True(ActionHostLaunchContract.TryCreate(
            inputs,
            eventPath,
            Convert.ToHexString(SHA256.HashData(eventBytes)).ToLowerInvariant(),
            FrameworkCanaries.Repository,
            FrameworkGitHubHandler.RepositoryId,
            runId: 900,
            runAttempt: 1,
            workflowPath: ".github/workflows/r4-trusted-proof.yml",
            workflowRef: FrameworkCanaries.Repository +
                "/.github/workflows/r4-trusted-proof.yml@refs/heads/main",
            workflowSha: FrameworkGitHubHandler.WorkflowSha,
            actionSourceSha: FrameworkGitHubHandler.ActionSha,
            payloadSha256: new string('f', 64),
            buildDiscriminator: TrustedProofPayloadHost.PayloadBuildDiscriminator,
            cancellation: ActionHostCancellationState.Requested,
            artifactBridgeEndpoint: Path.Join(Path.GetDirectoryName(eventPath)!,
                "bridge.sock"),
            out var launch));
        return launch!;
    }

    private static MemoryStream FramedLaunch(ActionHostLaunchContract launch)
    {
        Assert.True(ActionHostJsonCodec.TryWriteLaunch(launch, out var document));
        var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)document.Length));
        stream.Write(length);
        stream.Write(document);
        stream.Position = 0;
        return stream;
    }

    private static FrameworkSupervisor.OperationRequestEvent Event(
        string scenario,
        int scenarioOrdinal,
        int ordinal,
        string domain,
        string responseClass,
        long? timestamp = null) => new(
        scenario,
        scenarioOrdinal,
        ordinal,
        domain,
        "github_rest",
        "GET",
        1,
        timestamp ?? ordinal,
        responseClass);

    private static Task<int> RunControlAsync(
        string scenario,
        long runId,
        string command,
        string? operationId = null)
    {
        var payloadSha256 = new string('f', 64);
        var coordinates = new TrustedProofControlCoordinates(
            FrameworkCanaries.ProofControlRepository,
            FrameworkGitHubHandler.RepositoryId,
            FrameworkGitHubHandler.PullRequestNumber,
            FrameworkGitHubHandler.HeadSha,
            operationId ?? FrameworkCanaries.ProofOperationId,
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
