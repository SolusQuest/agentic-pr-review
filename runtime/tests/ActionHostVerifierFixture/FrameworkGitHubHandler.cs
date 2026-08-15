using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

internal sealed class FrameworkGitHubHandler(string scenarioRoot) :
    HttpMessageHandler
{
    internal const long RepositoryId = 42;
    internal const long PullRequestId = 1000;
    internal const long PullRequestNumber = 147;
    internal const long TriggerRunId = 800;
    internal const int TriggerAttempt = 1;
    internal static readonly string WorkflowSha = new('a', 40);
    internal static readonly string ActionSha = new('b', 40);
    internal static readonly string TriggerSha = new('c', 40);
    internal static readonly string BaseSha = new('d', 40);
    internal static readonly string HeadSha = new('e', 40);
    internal static readonly string ContinuedHeadSha = new('f', 40);

    private static readonly string WorkflowRoot = new('1', 40);
    private static readonly string GitHubRoot = new('2', 40);
    private static readonly string InstructionsRoot = new('3', 40);
    private static readonly string BaseRoot = new('4', 40);
    private static readonly string HeadRoot = new('5', 40);
    private static readonly string ProofRoot = new('6', 40);
    private static readonly byte[] FileBytes = Encoding.UTF8.GetBytes(
        FrameworkCanaries.ToolData + "\n");
    private static readonly byte[] InstructionsBytes = Encoding.UTF8.GetBytes(
        FrameworkCanaries.Prompt + "\n");

    private readonly string scenarioRoot = scenarioRoot;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Headers.Authorization?.Scheme != "Bearer" ||
            request.Headers.Authorization.Parameter !=
                FrameworkCanaries.GitHubToken ||
            request.RequestUri is null)
        {
            return Json(HttpStatusCode.Unauthorized, "{}");
        }

        Increment("github-request-count");
        RecordObservation("github-token", "github.authorization");
        var path = request.RequestUri.AbsolutePath;
        var query = request.RequestUri.Query;
        var mode = ReadMode();
        var prefix = "/repos/" + FrameworkCanaries.Repository;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.NotFound, "{}");
        }

        var suffix = path[prefix.Length..];
        if (request.Method == HttpMethod.Get && suffix.Length == 0)
        {
            RecordObservation("repository", "github.repository");
            return Json(HttpStatusCode.OK, $$"""
                {"id":{{RepositoryId}},"full_name":"{{FrameworkCanaries.Repository}}","default_branch":"main"}
                """);
        }

        if (request.Method == HttpMethod.Get &&
            suffix.StartsWith("/actions/runs/", StringComparison.Ordinal) &&
            suffix.Contains("/attempts/", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK,
                suffix.Contains("/" + ReadCurrentRunId() + "/",
                    StringComparison.Ordinal)
                    ? CurrentRun(mode)
                    : TriggerRun(mode));
        }

        if (request.Method == HttpMethod.Get &&
            suffix == "/contents/.github/workflows/r4-trusted-proof.yml")
        {
            RecordObservation("workflow-source", "github.workflow-source");
            var workflow = Encoding.UTF8.GetBytes(Workflow(mode));
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                type = "file",
                encoding = "base64",
                size = workflow.Length,
                name = "r4-trusted-proof.yml",
                path = ".github/workflows/r4-trusted-proof.yml",
                sha = GitBlobSha(workflow),
                content = Convert.ToBase64String(workflow),
            }));
        }

        if (request.Method == HttpMethod.Get &&
            suffix.StartsWith("/commits/", StringComparison.Ordinal) &&
            suffix.EndsWith("/pulls", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, "[" + PullRequest(mode) + "]");
        }

        if (request.Method == HttpMethod.Get &&
            suffix == "/collaborators/maintainer/permission")
        {
            return Json(HttpStatusCode.OK,
                mode == "permission" ? "{\"permission\":\"read\"}" :
                    "{\"permission\":\"write\"}");
        }

        if (request.Method == HttpMethod.Get &&
            suffix == "/pulls/147")
        {
            Increment("pull-request-revalidation-count");
            return Json(HttpStatusCode.OK, PullRequest(mode));
        }

        if (request.Method == HttpMethod.Get &&
            suffix.StartsWith("/git/commits/", StringComparison.Ordinal))
        {
            var sha = suffix["/git/commits/".Length..];
            return Json(HttpStatusCode.OK, Commit(sha));
        }

        if (request.Method == HttpMethod.Get &&
            suffix.StartsWith("/git/trees/", StringComparison.Ordinal))
        {
            var sha = suffix["/git/trees/".Length..];
            return Json(HttpStatusCode.OK, Tree(sha, mode));
        }

        if (request.Method == HttpMethod.Get &&
            suffix.StartsWith("/git/blobs/", StringComparison.Ordinal))
        {
            var sha = suffix["/git/blobs/".Length..];
            var bytes = Blob(sha, mode);
            return bytes is null
                ? Json(HttpStatusCode.NotFound, "{}")
                : Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    sha,
                    size = bytes.Length,
                    encoding = "base64",
                    content = Convert.ToBase64String(bytes),
                }));
        }

        if (request.Method == HttpMethod.Get &&
            suffix == "/pulls/147/files")
        {
            RecordObservation("reviewed-path", "github.changed-files");
            return Json(HttpStatusCode.OK, "[" + JsonSerializer.Serialize(new
            {
                sha = GitBlobSha(FileBytes),
                filename = FrameworkCanaries.ReviewedPath,
                previous_filename = (string?)null,
                status = "added",
                additions = 1,
                deletions = 0,
                changes = 1,
                patch = "@@ -0,0 +1 @@\n+" + FrameworkCanaries.ToolData,
            }) + "]");
        }

        if (suffix == "/issues/147/comments" && request.Method == HttpMethod.Get)
        {
            if (mode == "cancel-before-dispatch")
            {
                File.WriteAllText(Path.Join(scenarioRoot,
                    "cancel-before-dispatch-ready"), "1");
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                    .ConfigureAwait(false);
            }
            Increment("sticky-list-count");
            var stored = ReadOptional(mode, "sticky-comment.json");
            return Json(HttpStatusCode.OK,
                stored is null ? "[]" : "[" + stored + "]");
        }

        if (suffix == "/issues/147/comments" && request.Method == HttpMethod.Post)
        {
            Increment("sticky-create-count");
            var body = await request.Content!.ReadAsStringAsync(
                cancellationToken).ConfigureAwait(false);
            var document = IssueComment(701, ExtractString(body, "body"));
            WriteStored(mode, "sticky-comment.json", document);
            if (mode is "mutation-crash" or "cancel-outcome-unknown")
            {
                File.WriteAllText(
                    Path.Join(scenarioRoot, mode == "mutation-crash"
                        ? "mutation-committed"
                        : "cancel-outcome-unknown-committed"), "1");
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                    .ConfigureAwait(false);
            }
            return Json(HttpStatusCode.Created, document);
        }

        if (suffix == "/issues/comments/701" && request.Method == HttpMethod.Get)
        {
            Increment("sticky-readback-count");
            if (mode == "cancel-known-commit")
            {
                File.WriteAllText(Path.Join(scenarioRoot,
                    "cancel-known-commit-ready"), "1");
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                    .ConfigureAwait(false);
            }
            return Json(HttpStatusCode.OK,
                ReadOptional(mode, "sticky-comment.json") ??
                IssueComment(701, ""));
        }

        if (suffix == "/issues/comments/701" && request.Method == HttpMethod.Patch)
        {
            Increment("sticky-update-count");
            var body = await request.Content!.ReadAsStringAsync(
                cancellationToken).ConfigureAwait(false);
            var document = IssueComment(701, ExtractString(body, "body"));
            WriteStored(mode, "sticky-comment.json", document);
            return Json(HttpStatusCode.OK, document);
        }

        if (suffix == "/pulls/147/comments" && request.Method == HttpMethod.Get)
        {
            Increment("inline-list-count");
            return Json(HttpStatusCode.OK,
                ReadOptional(mode, "inline-comments.json") ?? "[]");
        }

        if (suffix == "/pulls/147/reviews" && request.Method == HttpMethod.Post)
        {
            Increment("inline-batch-count");
            if (mode == "inline-warning")
            {
                return Json(HttpStatusCode.UnprocessableEntity,
                    "{\"message\":\"Validation Failed\"," +
                    "\"errors\":[{\"resource\":\"PullRequestReview\"," +
                    "\"field\":\"comments\",\"code\":\"invalid\"}]}");
            }

            var body = await request.Content!.ReadAsStringAsync(
                cancellationToken).ConfigureAwait(false);
            StoreInlineComments(body);
            return Json(HttpStatusCode.OK, Review(801));
        }

        if (suffix.StartsWith("/pulls/comments/", StringComparison.Ordinal) &&
            request.Method == HttpMethod.Get)
        {
            Increment("inline-readback-count");
            var comments = ReadOptional(mode, "inline-comments.json") ?? "[]";
            using var parsed = JsonDocument.Parse(comments);
            return Json(HttpStatusCode.OK,
                parsed.RootElement.GetArrayLength() == 0
                    ? "{}"
                    : parsed.RootElement[0].GetRawText());
        }

        File.WriteAllText(Path.Join(scenarioRoot, "unexpected-github-request"),
            request.Method + " " + suffix + query);
        return Json(HttpStatusCode.NotFound, "{}");
    }

    private void StoreInlineComments(string batchBody)
    {
        using var batch = JsonDocument.Parse(batchBody);
        var comments = new List<object>();
        var id = 901L;
        foreach (var source in batch.RootElement.GetProperty("comments")
                     .EnumerateArray())
        {
            comments.Add(new
            {
                id = id++,
                pull_request_review_id = 801,
                url = "https://api.github.com/repos/" +
                    FrameworkCanaries.Repository + "/pulls/comments/901",
                pull_request_url = "https://api.github.com/repos/" +
                    FrameworkCanaries.Repository + "/pulls/147",
                html_url = "https://github.com/" +
                    FrameworkCanaries.Repository + "/pull/147#discussion_r901",
                body = source.GetProperty("body").GetString(),
                path = source.GetProperty("path").GetString(),
                line = source.GetProperty("line").GetInt32(),
                side = source.GetProperty("side").GetString(),
                commit_id = batch.RootElement.GetProperty("commit_id")
                    .GetString(),
            });
        }

        File.WriteAllText(Path.Join(scenarioRoot, "inline-comments.json"),
            JsonSerializer.Serialize(comments));
    }

    private string CurrentRun(string mode) => JsonSerializer.Serialize(
        new
        {
            id = ReadCurrentRunId(),
            run_attempt = ReadCurrentRunAttempt(),
            workflow_id = 72,
            name = "R4 trusted proof",
            path = ".github/workflows/r4-trusted-proof.yml",
            head_branch = "main",
            head_sha = WorkflowSha,
            @event = mode == "workflow-run" ? "workflow_run" :
                "workflow_dispatch",
            conclusion = (string?)null,
            repository = Identity(RepositoryId),
            head_repository = Identity(RepositoryId),
            actor = Actor(),
            triggering_actor = Actor(),
            pull_requests = Array.Empty<object>(),
        });

    private string TriggerRun(string mode) => JsonSerializer.Serialize(
        new
        {
            id = TriggerRunId,
            run_attempt = TriggerAttempt,
            workflow_id = 71,
            name = "CI",
            path = ".github/workflows/ci.yml",
            head_branch = "feature",
            head_sha = TriggerSha,
            @event = "pull_request",
            conclusion = "success",
            repository = Identity(RepositoryId),
            head_repository = Identity(RepositoryId),
            actor = Actor(),
            triggering_actor = Actor(),
            pull_requests = new[] { PullReference(mode) },
        });

    private static object Identity(long id) => new
    {
        id,
        full_name = FrameworkCanaries.Repository,
    };

    private static object Actor() => new { id = 7, login = "maintainer" };

    private object PullReference(string mode) => new
    {
        id = PullRequestId,
        number = PullRequestNumber,
        @base = new
        {
            sha = BaseSha,
            repo = new
            {
                id = RepositoryId,
                url = "https://api.github.com/repos/" +
                    FrameworkCanaries.Repository,
                name = "apr178-repository-canary",
            },
        },
        head = new
        {
            sha = CurrentHead(mode),
            repo = new
            {
                id = mode == "fork" ? RepositoryId + 1 : RepositoryId,
                url = "https://api.github.com/repos/" +
                    FrameworkCanaries.Repository,
                name = "apr178-repository-canary",
            },
        },
    };

    private string PullRequest(string mode) => JsonSerializer.Serialize(
        new
        {
            id = PullRequestId,
            number = PullRequestNumber,
            state = "open",
            draft = false,
            merged_at = (string?)null,
            @base = new { sha = BaseSha, repo = Identity(RepositoryId) },
            head = new
            {
                sha = CurrentHead(mode),
                repo = Identity(mode == "fork" ? RepositoryId + 1 :
                    RepositoryId),
            },
        });

    private static string Workflow(string mode)
    {
        var action = mode == "wrong-action" ? new string('9', 40) : ActionSha;
        var cancel = mode == "concurrency" ? "true" : "false";
        return $$$"""
            name: R4 trusted proof
            on:
              workflow_run:
                workflows:
                  - CI
                types:
                  - completed
              workflow_dispatch:
                inputs:
                  pr-number:
                    description: Pull request number
                    required: true
                    type: number
            permissions: {}
            concurrency:
              group: agentic-pr-review-r4-${{ github.repository_id }}-pr-${{ github.event.workflow_run.pull_requests[0].number || inputs.pr-number }}
              cancel-in-progress: {{{cancel}}}
            jobs:
              authorization-preflight:
                permissions: {}
                runs-on: ubuntu-latest
                outputs:
                  authorized: ${{ steps.authorization.outputs.authorized }}
                steps:
                  - id: authorization
                    run: |
                      echo "authorized=false" >> "$GITHUB_OUTPUT"
              workflow-run-review:
                needs: authorization-preflight
                if: ${{ github.event_name == 'workflow_run' && needs.authorization-preflight.outputs.authorized == 'true' }}
                permissions:
                  actions: write
                  contents: read
                  pull-requests: write
                runs-on: ubuntu-latest
                steps:
                  - uses: SolusQuest/agentic-pr-review/.github/actions/agentic-pr-review@{{{action}}}
              workflow-dispatch-review:
                needs: authorization-preflight
                if: ${{ github.event_name == 'workflow_dispatch' && needs.authorization-preflight.outputs.authorized == 'true' }}
                permissions:
                  actions: write
                  contents: read
                  pull-requests: write
                runs-on: ubuntu-latest
                steps:
                  - uses: SolusQuest/agentic-pr-review/.github/actions/agentic-pr-review@{{{action}}}
            # {{{FrameworkCanaries.Workflow}}}
            """;
    }

    private string CurrentHead(string mode) =>
        mode == "continuation" ||
            mode == "stale" && ReadCounter("provider-sequence") >= 6
            ? ContinuedHeadSha
            : HeadSha;

    private long ReadCurrentRunId() => long.Parse(
        File.ReadAllText(Path.Join(scenarioRoot, "run-id")),
        CultureInfo.InvariantCulture);

    private int ReadCurrentRunAttempt() => int.Parse(
        File.ReadAllText(Path.Join(scenarioRoot, "run-attempt")),
        CultureInfo.InvariantCulture);

    private int ReadCounter(string name)
    {
        var path = Path.Join(scenarioRoot, name);
        return File.Exists(path) && int.TryParse(File.ReadAllText(path),
            NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static string Commit(string sha)
    {
        var tree = sha == WorkflowSha ? WorkflowRoot :
            sha == BaseSha ? BaseRoot : HeadRoot;
        var parents = sha == HeadSha ? new[] { BaseSha } :
            sha == ContinuedHeadSha ? new[] { HeadSha } : Array.Empty<string>();
        return JsonSerializer.Serialize(new
        {
            sha,
            tree = new { sha = tree },
            parents = parents.Select(parent => new { sha = parent }),
        });
    }

    private static string Tree(string sha, string mode)
    {
        object[] entries = sha switch
        {
            var value when value == WorkflowRoot =>
                [TreeEntry(".github", "040000", "tree", GitHubRoot)],
            var value when value == GitHubRoot =>
                [
                    TreeEntry("agentic-pr-review.json", "100644", "blob",
                        GitBlobSha(ConfigBytes(mode)), ConfigBytes(mode).Length),
                    TreeEntry("agentic-pr-review", "040000", "tree",
                        InstructionsRoot),
                ],
            var value when value == InstructionsRoot =>
                [TreeEntry("instructions.md", "100644", "blob",
                    GitBlobSha(InstructionsBytes), InstructionsBytes.Length)],
            var value when value == BaseRoot => [],
            var value when value == HeadRoot =>
                [TreeEntry("proof", "040000", "tree", ProofRoot)],
            var value when value == ProofRoot =>
                [TreeEntry("apr178-path-canary.txt", "100644", "blob",
                    GitBlobSha(FileBytes), FileBytes.Length)],
            _ => [],
        };
        return JsonSerializer.Serialize(new
        {
            sha,
            truncated = false,
            tree = entries,
        });
    }

    private static object TreeEntry(
        string path,
        string mode,
        string type,
        string sha,
        int? size = null) => new { path, mode, type, sha, size };

    private static byte[]? Blob(string sha, string mode)
    {
        var config = ConfigBytes(mode);
        if (sha == GitBlobSha(config)) return config;
        if (sha == GitBlobSha(InstructionsBytes)) return InstructionsBytes;
        return sha == GitBlobSha(FileBytes) ? FileBytes : null;
    }

    private static byte[] ConfigBytes(string mode) => Encoding.UTF8.GetBytes(
        "{\"schema\":\"agentic-pr-review.config.v1\"," +
        "\"instructionsPath\":\".github/agentic-pr-review/" +
        "instructions.md\",\"publication\":{\"mode\":\"" +
        (mode is "inline" or "inline-warning" ? "sticky_and_inline" :
            "sticky") + "\"" +
        (mode is "inline" or "inline-warning"
            ? ",\"inlineMinSeverity\":\"high\""
            : string.Empty) + "}}");

    private static string IssueComment(long id, string body) =>
        JsonSerializer.Serialize(new
        {
            id,
            url = "https://api.github.com/repos/" +
                FrameworkCanaries.Repository + "/issues/comments/" + id,
            html_url = "https://github.com/" + FrameworkCanaries.Repository +
                "/pull/147#issuecomment-" + id,
            body,
        });

    private static string Review(long id) => JsonSerializer.Serialize(new
    {
        id,
        url = "https://api.github.com/repos/" + FrameworkCanaries.Repository +
            "/pulls/147/reviews/" + id,
        pull_request_url = "https://api.github.com/repos/" +
            FrameworkCanaries.Repository + "/pulls/147",
        html_url = "https://github.com/" + FrameworkCanaries.Repository +
            "/pull/147#pullrequestreview-" + id,
        commit_id = HeadSha,
    });

    private static string ExtractString(string json, string property)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(property).GetString() ?? "";
    }

    private string ReadMode()
    {
        var path = Path.Join(scenarioRoot, "mode");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : "sticky";
    }

    private string? ReadOptional(string mode, string name)
    {
        var path = Path.Join(StorageRoot(mode), name);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private void WriteStored(string mode, string name, string value)
    {
        var root = StorageRoot(mode);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Join(root, name), value);
    }

    private string StorageRoot(string mode) =>
        mode is "mutation-crash" or "mutation-recovery"
            ? Path.Join(Directory.GetParent(scenarioRoot)!.FullName,
                "shared-github")
            : mode is "continuation-seed" or "continuation"
                ? Path.Join(Directory.GetParent(scenarioRoot)!.FullName,
                    "shared-continuation-github")
            : scenarioRoot;

    private void Increment(string name)
    {
        var path = Path.Join(scenarioRoot, name);
        var value = File.Exists(path) && int.TryParse(
            File.ReadAllText(path),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed + 1
            : 1;
        File.WriteAllText(path, value.ToString(CultureInfo.InvariantCulture));
    }

    private void RecordObservation(string canaryClass, string sink) =>
        File.AppendAllText(
            Path.Join(scenarioRoot, "canary-observations.tsv"),
            canaryClass + "\t" + sink + "\n");

    private static string GitBlobSha(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes(
            "blob " + bytes.Length.ToString(CultureInfo.InvariantCulture) +
            "\0");
        return Convert.ToHexString(SHA1.HashData([.. header, .. bytes]))
            .ToLowerInvariant();
    }

    private static HttpResponseMessage Json(
        HttpStatusCode status,
        string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8,
                "application/json"),
        };
}
