using System.Globalization;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

using AgenticPrReview.Runtime.ActionHost.Authorization;

internal sealed class FrameworkGitHubHandler(
    string scenarioRoot,
    string payloadSha256,
    Func<string, string, string>? workflowRenderer = null,
    Func<bool, CancellationToken, ValueTask<int>>? observePrimaryRemaining = null) :
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
    internal static readonly string ConflictHeadSha = new('7', 40);

    private static readonly string WorkflowRoot = new('1', 40);
    private static readonly string GitHubRoot = new('2', 40);
    private static readonly string InstructionsRoot = new('3', 40);
    private static readonly string BaseRoot = new('4', 40);
    private static readonly string HeadRoot = new('5', 40);
    private static readonly string ProofRoot = new('6', 40);
    private static readonly string HeadGitHubRoot = new('8', 40);
    private static readonly string ActionsRoot = new('9', 40);
    private static readonly string ActionRoot = new('0', 40);
    private static readonly string DistRoot = new('a', 40);
    internal const int ProductionShapedHeadTreeObjectCount = 178;
    private static readonly string[] BudgetTreeRoots = Enumerable.Range(0,
            ProductionShapedHeadTreeObjectCount - 6)
        .Select(index => (0x1000 + index).ToString("x40",
            CultureInfo.InvariantCulture))
        .ToArray();
    private const string ArchivePrefix = "agentic-pr-review-fixture/";
    private static readonly byte[] FileBytes = Encoding.UTF8.GetBytes(
        FrameworkCanaries.ToolData + "\n");
    private static readonly byte[] InstructionsBytes = Encoding.UTF8.GetBytes(
        FrameworkCanaries.Prompt + "\n");
    internal const int ProductionShapedLargeBlobByteCount = 3_585_824;
    private static readonly byte[] ProductionShapedLargeBlobBytes =
        CreateProductionShapedLargeBlob();

    private readonly string scenarioRoot = scenarioRoot;
    private readonly string payloadSha256 = payloadSha256;
    private readonly Func<string, string, string> workflowRenderer =
        workflowRenderer ?? ActionHostTrustedWorkflowContract.Render;
    private readonly bool hasExplicitWorkflowRenderer =
        workflowRenderer is not null;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await SendCoreAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (observePrimaryRemaining is not null &&
            request.RequestUri?.Host.Equals("api.github.com",
                StringComparison.OrdinalIgnoreCase) == true &&
            request.Headers.Authorization?.Parameter == FrameworkCanaries.GitHubToken)
        {
            var remaining = await observePrimaryRemaining(
                response.StatusCode != HttpStatusCode.NotModified,
                cancellationToken).ConfigureAwait(false);
            response.Headers.TryAddWithoutValidation("x-ratelimit-limit", "1000");
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining",
                remaining.ToString(CultureInfo.InvariantCulture));
            response.Headers.TryAddWithoutValidation("x-ratelimit-reset",
                "4102444800");
        }
        return response;
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.RequestUri is null)
        {
            return Json(HttpStatusCode.Unauthorized, "{}");
        }

        if (request.RequestUri.Host.Equals("codeload.github.com",
                StringComparison.OrdinalIgnoreCase))
        {
            return CodeloadArchive(request);
        }

        if (request.Headers.Authorization?.Scheme != "Bearer" ||
            request.Headers.Authorization.Parameter !=
                FrameworkCanaries.GitHubToken)
        {
            return Json(HttpStatusCode.Unauthorized, "{}");
        }

        Increment("github-request-count");
        FrameworkCanaryCapture.CaptureAll(scenarioRoot,
            "github.authorization", request.Headers.Authorization.ToString());
        var path = request.RequestUri.AbsolutePath;
        var query = request.RequestUri.Query;
        var mode = ReadMode();
        var prefix = "/repos/" + FrameworkCanaries.Repository;
        var proofControlPrefix =
            "/repos/" + FrameworkCanaries.ProofControlRepository;
        var proofControlRequest = IsTrustedProofPayload() &&
            path.StartsWith(proofControlPrefix, StringComparison.Ordinal);
        if (proofControlRequest)
        {
            prefix = proofControlPrefix;
        }
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.NotFound, "{}");
        }

        var suffix = path[prefix.Length..];
        if (IsPublisherRoute(suffix))
        {
            Increment("publisher-api-count");
        }

        if (request.Method == HttpMethod.Get && suffix.Length == 0)
        {
            Increment("authorization-api-count");
            var response = $$"""
                {"id":{{RepositoryId}},"full_name":"{{FrameworkCanaries.Repository}}","default_branch":"main"}
                """;
            FrameworkCanaryCapture.CaptureAll(scenarioRoot,
                "github.repository", response);
            return Json(HttpStatusCode.OK, response);
        }

        if (request.Method == HttpMethod.Get &&
            suffix.StartsWith("/actions/runs/", StringComparison.Ordinal) &&
            suffix.Contains("/attempts/", StringComparison.Ordinal))
        {
            Increment("authorization-api-count");
            return Json(HttpStatusCode.OK,
                suffix.Contains("/" + ReadCurrentRunId() + "/",
                    StringComparison.Ordinal)
                    ? CurrentRun(mode)
                    : TriggerRun(mode));
        }

        if (request.Method == HttpMethod.Get &&
            suffix == "/contents/.github/workflows/r4-trusted-proof.yml")
        {
            Increment("policy-api-count");
            var workflow = Encoding.UTF8.GetBytes(Workflow(mode));
            FrameworkCanaryCapture.CaptureAll(scenarioRoot,
                "github.workflow-source", workflow);
            return Json(HttpStatusCode.OK, FrameworkJson.Serialize(
                FrameworkJson.Object(
                    ("type", "file"),
                    ("encoding", "base64"),
                    ("size", workflow.Length),
                    ("name", "r4-trusted-proof.yml"),
                    ("path", ".github/workflows/r4-trusted-proof.yml"),
                    ("sha", GitBlobSha(workflow)),
                    ("content", Convert.ToBase64String(workflow)))));
        }

        if (request.Method == HttpMethod.Get &&
            suffix.StartsWith("/commits/", StringComparison.Ordinal) &&
            suffix.EndsWith("/pulls", StringComparison.Ordinal))
        {
            Increment("authorization-api-count");
            return Json(HttpStatusCode.OK,
                "[" + PullRequest(mode, proofControlRequest) + "]");
        }

        if (request.Method == HttpMethod.Get &&
            suffix == "/collaborators/maintainer/permission")
        {
            Increment("authorization-api-count");
            return Json(HttpStatusCode.OK,
                mode == "permission" ? "{\"permission\":\"read\"}" :
                    "{\"permission\":\"write\"}");
        }

        if (request.Method == HttpMethod.Get &&
            suffix == "/pulls/147")
        {
            Increment("pull-request-revalidation-count");
            Increment("revalidation-api-count");
            return Json(HttpStatusCode.OK,
                PullRequest(mode, proofControlRequest));
        }

        if (request.Method == HttpMethod.Get &&
            suffix.StartsWith("/git/commits/", StringComparison.Ordinal))
        {
            var sha = suffix["/git/commits/".Length..];
            Increment(IsSyntheticHeadSha(sha) ? "head-commit-api-count" :
                "base-api-count");
            return Json(HttpStatusCode.OK, Commit(sha));
        }

        if (request.Method == HttpMethod.Get &&
            suffix.StartsWith("/tarball/", StringComparison.Ordinal))
        {
            var sha = suffix["/tarball/".Length..];
            if (!IsSyntheticHeadSha(sha))
            {
                return Json(HttpStatusCode.NotFound, "{}");
            }

            Increment("head-archive-api-count");
            return new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers =
                {
                    Location = new Uri("https://codeload.github.com/" +
                        FrameworkCanaries.Repository + "/legacy.tar.gz/" + sha),
                },
            };
        }

        if (request.Method == HttpMethod.Get &&
            suffix.StartsWith("/git/trees/", StringComparison.Ordinal))
        {
            var sha = suffix["/git/trees/".Length..];
            Increment(IsHeadTreeSha(sha) ? "head-tree-api-count" :
                "base-api-count");
            return Json(HttpStatusCode.OK, Tree(sha, mode));
        }

        if (request.Method == HttpMethod.Get &&
            suffix.StartsWith("/git/blobs/", StringComparison.Ordinal))
        {
            var sha = suffix["/git/blobs/".Length..];
            if (IsHeadBlobSha(sha))
            {
                Increment("head-blob-api-count");
            }
            else
            {
                Increment("base-api-count");
            }
            var bytes = Blob(sha, mode);
            if (bytes is null)
            {
                return Json(HttpStatusCode.NotFound, "{}");
            }

            return Json(HttpStatusCode.OK, FrameworkJson.Serialize(FrameworkJson.Object(
                ("sha", sha),
                ("size", bytes.Length),
                ("encoding", "base64"),
                ("content", Convert.ToBase64String(bytes)))));
        }

        if (request.Method == HttpMethod.Get &&
            suffix == "/pulls/147/files")
        {
            if (IsTrustedProofPayload())
            {
                throw new InvalidOperationException(
                    "Trusted-v2 must not read the historical PR-files view.");
            }
            Increment("diff-api-count");
            var response = FrameworkJson.Serialize(FrameworkJson.Array([
                FrameworkJson.Object(
                    ("sha", GitBlobSha(FileBytes)),
                    ("filename", FrameworkCanaries.ReviewedPath),
                    ("previous_filename", null),
                    ("status", "added"),
                    ("additions", 1),
                    ("deletions", 0),
                    ("changes", 1),
                    ("patch", "@@ -0,0 +1 @@\n+" +
                        FrameworkCanaries.ToolData)),
            ]));
            FrameworkCanaryCapture.CaptureAll(scenarioRoot,
                "github.changed-files", response);
            return Json(HttpStatusCode.OK, response);
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
            var comments = new List<string>();
            if (IsTrustedProofPayload())
            {
                comments.AddRange(ReadProofControlComments(mode));
            }

            if (stored is not null)
            {
                comments.Add(proofControlRequest
                    ? ProofControlCompatibleIssueComment(stored)
                    : stored);
            }

            return Json(HttpStatusCode.OK, "[" +
                string.Join(',', comments) + "]");
        }

        if (suffix == "/issues/147/comments" && request.Method == HttpMethod.Post)
        {
            var body = await request.Content!.ReadAsStringAsync(
                cancellationToken).ConfigureAwait(false);
            var commentBody = ExtractString(body, "body");
            if (IsTrustedProofPayload() && commentBody.StartsWith(
                    "<!-- apr-r4-e2p-control ",
                    StringComparison.Ordinal))
            {
                return Json(
                    HttpStatusCode.Created,
                    CreateProofControlPair(mode, commentBody));
            }

            Increment("sticky-create-count");
            var document = IssueComment(701, commentBody);
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
            File.WriteAllText(
                Path.Join(scenarioRoot, "sticky-readback-comment-id"), "701");
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
            File.WriteAllText(
                Path.Join(scenarioRoot, "sticky-update-comment-id"), "701");
            var body = await request.Content!.ReadAsStringAsync(
                cancellationToken).ConfigureAwait(false);
            var document = IssueComment(701, ExtractString(body, "body"));
            if (mode == "continuation")
            {
                var predecessor = ReadOptional(mode, "sticky-comment.json");
                if (predecessor is not null)
                {
                    File.WriteAllText(Path.Join(scenarioRoot,
                        "sticky-predecessor-comment.json"), predecessor);
                }
            }
            WriteStored(mode, "sticky-comment.json", document);
            if (mode == "continuation")
            {
                File.WriteAllText(Path.Join(scenarioRoot,
                    "sticky-successor-comment.json"), document);
            }
            return Json(HttpStatusCode.OK, document);
        }

        if (IsTrustedProofPayload() &&
            suffix.StartsWith("/issues/comments/", StringComparison.Ordinal) &&
            long.TryParse(
                suffix["/issues/comments/".Length..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var proofCommentId))
        {
            var proofComments = ReadProofControlComments(mode);
            var proofComment = proofComments.SingleOrDefault(value =>
                JsonPropertyLong(value, "id") == proofCommentId);
            if (request.Method == HttpMethod.Get)
            {
                return proofComment is null
                    ? Json(HttpStatusCode.NotFound, "{}")
                    : Json(HttpStatusCode.OK, proofComment);
            }

            if (request.Method == HttpMethod.Delete)
            {
                if (proofComment is null)
                {
                    return Json(HttpStatusCode.NotFound, "{}");
                }

                WriteProofControlComments(
                    mode,
                    proofComments.Where(value =>
                        JsonPropertyLong(value, "id") != proofCommentId));
                return Json(HttpStatusCode.NoContent, "{}");
            }
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
        var comments = new List<JsonNode>();
        var id = 901L;
        foreach (var source in batch.RootElement.GetProperty("comments")
                     .EnumerateArray())
        {
            comments.Add(FrameworkJson.Object(
                ("id", id++),
                ("pull_request_review_id", 801),
                ("url", "https://api.github.com/repos/" +
                    FrameworkCanaries.Repository + "/pulls/comments/901"),
                ("pull_request_url", "https://api.github.com/repos/" +
                    FrameworkCanaries.Repository + "/pulls/147"),
                ("html_url", "https://github.com/" +
                    FrameworkCanaries.Repository +
                    "/pull/147#discussion_r901"),
                ("body", source.GetProperty("body").GetString()),
                ("path", source.GetProperty("path").GetString()),
                ("line", source.GetProperty("line").GetInt32()),
                ("side", source.GetProperty("side").GetString()),
                ("commit_id", batch.RootElement.GetProperty("commit_id")
                    .GetString())));
        }

        File.WriteAllText(Path.Join(scenarioRoot, "inline-comments.json"),
            FrameworkJson.Serialize(FrameworkJson.Array(comments)));
    }

    private string CurrentRun(string mode) => FrameworkJson.Serialize(
        FrameworkJson.Object(
            ("id", ReadCurrentRunId()),
            ("run_attempt", ReadCurrentRunAttempt()),
            ("workflow_id", 72),
            ("name", "R4 trusted proof"),
            ("path", ".github/workflows/r4-trusted-proof.yml"),
            ("head_branch", "main"),
            ("head_sha", CurrentWorkflowSha()),
            ("event", mode == "workflow-run" ||
                IsTrustedProofPayload() && mode == "continuation-seed"
                    ? "workflow_run"
                    : "workflow_dispatch"),
            ("conclusion", null),
            ("repository", Identity(RepositoryId)),
            ("head_repository", Identity(RepositoryId)),
            ("actor", Actor()),
            ("triggering_actor", Actor()),
            ("pull_requests", FrameworkJson.Array([]))));

    private string TriggerRun(string mode) => FrameworkJson.Serialize(
        FrameworkJson.Object(
            ("id", TriggerRunId),
            ("run_attempt", TriggerAttempt),
            ("workflow_id", 71),
            ("name", "CI"),
            ("path", ".github/workflows/ci.yml"),
            ("head_branch", "feature"),
            ("head_sha", TriggerSha),
            ("event", "pull_request"),
            ("conclusion", "success"),
            ("repository", Identity(RepositoryId)),
            ("head_repository", Identity(RepositoryId)),
            ("actor", Actor()),
            ("triggering_actor", Actor()),
            ("pull_requests", FrameworkJson.Array([PullReference(mode)]))));

    private static JsonObject Identity(long id, string? fullName = null) =>
        FrameworkJson.Object(
        ("id", id),
        ("full_name", fullName ?? FrameworkCanaries.Repository));

    private static JsonObject Actor() => FrameworkJson.Object(
        ("id", 7),
        ("login", "maintainer"));

    private JsonObject PullReference(string mode) => FrameworkJson.Object(
        ("id", PullRequestId),
        ("number", PullRequestNumber),
        ("base", FrameworkJson.Object(
            ("sha", CurrentBaseSha()),
            ("repo", RepositoryIdentity(RepositoryId)))),
        ("head", FrameworkJson.Object(
            ("sha", CurrentHead(mode)),
            ("repo", RepositoryIdentity(mode == "fork"
                ? RepositoryId + 1
                : RepositoryId)))));

    private static JsonObject RepositoryIdentity(long id) =>
        FrameworkJson.Object(
            ("id", id),
            ("url", "https://api.github.com/repos/" +
                FrameworkCanaries.Repository),
            ("name", "apr178-repository-canary"));

    private string PullRequest(string mode, bool proofControlRequest = false)
    {
        var repository = proofControlRequest
            ? FrameworkCanaries.ProofControlRepository
            : FrameworkCanaries.Repository;
        var head = FrameworkJson.Object(
            ("sha", proofControlRequest ? HeadSha : CurrentHead(mode)),
            ("repo", Identity(mode == "fork"
                ? RepositoryId + 1
                : RepositoryId, repository)));
        if (IsTrustedProofPayload())
        {
            head.Add("ref", "r4-trusted-proof/" +
                (mode == "stale" ? FrameworkCanaries.StaleProofOperationId :
                    FrameworkCanaries.ProofOperationId));
        }

        return FrameworkJson.Serialize(FrameworkJson.Object(
            ("id", PullRequestId),
            ("number", PullRequestNumber),
            ("state", "open"),
            ("draft", false),
            ("merged_at", null),
            ("base", FrameworkJson.Object(
                ("ref", "main"),
                ("sha", CurrentBaseSha()),
                ("repo", Identity(RepositoryId, repository)))),
            ("head", head)));
    }

    private string Workflow(string mode)
    {
        if (HasTrustedProofCurrentHeadAuthority() &&
            !hasExplicitWorkflowRenderer)
        {
            throw new InvalidOperationException(
                "Trusted proof current-head scenarios require the v2 renderer.");
        }

        var workflow = workflowRenderer(
            CurrentActionSourceSha(),
            payloadSha256);
        return mode switch
        {
            "wrong-action" when HasTrustedProofCurrentHeadAuthority() =>
                workflow + "# trusted-proof-wrong-action\n",
            "wrong-action" => workflow.Replace(
                CurrentActionSourceSha(),
                new string('9', 40),
                StringComparison.Ordinal),
            "concurrency" => workflow.Replace(
                "cancel-in-progress: false",
                "cancel-in-progress: true",
                StringComparison.Ordinal),
            _ => workflow,
        };
    }

    private string CurrentHead(string mode) =>
        mode == "cross-head-conflict" ? ConflictHeadSha :
        mode == "continuation" ||
            mode == "stale" &&
                (ReadCounter("provider-sequence") >= 6 ||
                    File.Exists(Path.Join(scenarioRoot, "stale-released")))
            ? ContinuedHeadSha
            : HeadSha;

    internal static string PullRequestBaseSha(bool trustedProofPayload) =>
        BaseSha;

    private string CurrentBaseSha() =>
        BaseSha;

    private string CurrentWorkflowSha() =>
        HasTrustedProofCurrentHeadAuthority()
            ? ReadTrustedProofSourceCommit()
            : WorkflowSha;

    private string CurrentActionSourceSha() =>
        HasTrustedProofCurrentHeadAuthority()
            ? ReadTrustedProofSourceCommit()
            : ActionSha;

    private bool HasTrustedProofCurrentHeadAuthority() =>
        IsTrustedProofPayload() && File.Exists(Path.Join(scenarioRoot,
            "trusted-proof-authority.json"));

    private string ReadTrustedProofSourceCommit()
    {
        var path = Path.Join(scenarioRoot, "trusted-proof-authority.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "The trusted proof authority is invalid.");
            }

            var properties = document.RootElement.EnumerateObject().ToArray();
            if (properties.Length != 1 ||
                !StringComparer.Ordinal.Equals(properties[0].Name,
                    "source_commit") ||
                properties[0].Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException(
                    "The trusted proof authority is invalid.");
            }

            var sourceCommit = properties[0].Value.GetString();
            if (sourceCommit is null || !IsLowerHex(sourceCommit, 40))
            {
                throw new InvalidOperationException(
                    "The trusted proof authority is invalid.");
            }

            return sourceCommit;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The trusted proof authority is invalid.", exception);
        }
    }

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

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

    private string Commit(string sha)
    {
        var tree = sha == CurrentWorkflowSha() ? WorkflowRoot :
            sha == BaseSha ? BaseRoot : HeadRoot;
        var effectiveBaseSha = IsTrustedProofPayload()
            ? CurrentWorkflowSha()
            : CurrentBaseSha();
        var parents = sha == HeadSha
            ? IsTrustedProofPayload()
                ? new[] { effectiveBaseSha, TriggerSha }
                : new[] { effectiveBaseSha }
            :
            sha == ContinuedHeadSha ? new[] { HeadSha } :
            sha == ConflictHeadSha
                ? new[] { effectiveBaseSha }
                : Array.Empty<string>();
        return FrameworkJson.Serialize(FrameworkJson.Object(
            ("sha", sha),
            ("tree", FrameworkJson.Object(("sha", tree))),
            ("parents", FrameworkJson.Array(parents.Select(parent =>
                FrameworkJson.Object(("sha", parent)))))));
    }

    private static string Tree(string sha, string mode)
    {
        JsonObject[] entries = sha switch
        {
            var value when value == WorkflowRoot =>
                [TreeEntry(".github", "040000", "tree", GitHubRoot)],
            var value when value == GitHubRoot =>
                [
                    TreeEntry("agentic-pr-review", "040000", "tree",
                        InstructionsRoot),
                ],
            var value when value == InstructionsRoot =>
                [
                    TreeEntry("trusted-proof.json", "100644", "blob",
                        GitBlobSha(ConfigBytes(mode)), ConfigBytes(mode).Length),
                    TreeEntry("trusted-proof-instructions.md", "100644", "blob",
                        GitBlobSha(InstructionsBytes), InstructionsBytes.Length),
                ],
            var value when value == BaseRoot => [],
            var value when value == HeadRoot => HeadRootEntries(),
            var value when value == ProofRoot =>
                [TreeEntry("apr178-path-canary.txt", "100644", "blob",
                    GitBlobSha(FileBytes), FileBytes.Length)],
            var value when value == HeadGitHubRoot =>
                [TreeEntry("actions", "040000", "tree", ActionsRoot)],
            var value when value == ActionsRoot =>
                [TreeEntry("agentic-pr-review", "040000", "tree",
                    ActionRoot)],
            var value when value == ActionRoot =>
                [TreeEntry("dist", "040000", "tree", DistRoot)],
            var value when value == DistRoot =>
                [TreeEntry("index.js", "100644", "blob",
                    GitBlobSha(ProductionShapedLargeBlobBytes),
                    ProductionShapedLargeBlobBytes.Length)],
            var value when BudgetTreeRoots.Contains(value,
                StringComparer.Ordinal) => [],
            _ => [],
        };
        return FrameworkJson.Serialize(FrameworkJson.Object(
            ("sha", sha),
            ("truncated", false),
            ("tree", FrameworkJson.Array(entries))));
    }

    private static JsonObject[] HeadRootEntries()
    {
        var entries = new List<JsonObject>(BudgetTreeRoots.Length + 2)
        {
            TreeEntry(".github", "040000", "tree", HeadGitHubRoot),
            TreeEntry("proof", "040000", "tree", ProofRoot),
        };
        entries.AddRange(BudgetTreeRoots.Select((sha, index) => TreeEntry(
            "request-budget-" + index.ToString("D3",
                CultureInfo.InvariantCulture), "040000", "tree", sha)));
        return [.. entries];
    }

    private static JsonObject TreeEntry(
        string path,
        string mode,
        string type,
        string sha,
        int? size = null) => FrameworkJson.Object(
            ("path", path),
            ("mode", mode),
            ("type", type),
            ("sha", sha),
            ("size", size));

    private static byte[]? Blob(string sha, string mode)
    {
        var config = ConfigBytes(mode);
        if (sha == GitBlobSha(config)) return config;
        if (sha == GitBlobSha(InstructionsBytes)) return InstructionsBytes;
        if (sha == GitBlobSha(ProductionShapedLargeBlobBytes))
        {
            return ProductionShapedLargeBlobBytes;
        }
        return sha == GitBlobSha(FileBytes) ? FileBytes : null;
    }

    private static byte[] CreateProductionShapedLargeBlob()
    {
        var bytes = new byte[ProductionShapedLargeBlobByteCount];
        var segment = Encoding.ASCII.GetBytes(
            "const aprR4ProductionBundleSegment = \"trusted-proof-fixture\";\n");
        for (var offset = 0; offset < bytes.Length; offset += segment.Length)
        {
            Array.Copy(segment, 0, bytes, offset,
                Math.Min(segment.Length, bytes.Length - offset));
        }

        return bytes;
    }

    private HttpResponseMessage CodeloadArchive(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Get ||
            request.Headers.Authorization is not null ||
            request.RequestUri!.Scheme != Uri.UriSchemeHttps ||
            request.RequestUri.Port != 443 ||
            !TryCodeloadHeadSha(request.RequestUri.AbsolutePath, out var sha))
        {
            return Json(HttpStatusCode.Unauthorized, "{}");
        }

        var archive = HeadArchive(sha);
        EnsureCounter("head-blob-api-count");
        Increment("head-archive-anonymous-codeload-count");
        Increment("head-archive-credential-not-forwarded-count");
        File.WriteAllText(Path.Join(scenarioRoot, "head-archive-served"),
            ProductionShapedLargeBlobByteCount.ToString(
                CultureInfo.InvariantCulture) + "\t" +
            archive.Length.ToString(CultureInfo.InvariantCulture) + "\t" +
            (ProductionShapedLargeBlobByteCount + FileBytes.Length).ToString(
                CultureInfo.InvariantCulture) + "\t" + ArchivePrefix + "\n");
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(archive),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/x-gzip");
        return response;
    }

    private static bool TryCodeloadHeadSha(string path, out string sha)
    {
        const string prefix = "/" + FrameworkCanaries.Repository +
            "/legacy.tar.gz/";
        sha = path.StartsWith(prefix, StringComparison.Ordinal)
            ? path[prefix.Length..]
            : string.Empty;
        return IsSyntheticHeadSha(sha);
    }

    private static bool IsSyntheticHeadSha(string sha) => sha == HeadSha ||
        sha == ContinuedHeadSha || sha == ConflictHeadSha;

    private static bool IsHeadBlobSha(string sha) => sha ==
        GitBlobSha(ProductionShapedLargeBlobBytes) || sha == GitBlobSha(FileBytes);

    private static bool IsHeadTreeSha(string sha) => sha == HeadRoot ||
        sha == HeadGitHubRoot || sha == ActionsRoot || sha == ActionRoot ||
        sha == DistRoot || sha == ProofRoot || BudgetTreeRoots.Contains(sha,
            StringComparer.Ordinal);

    private static bool IsPublisherRoute(string suffix) =>
        suffix.StartsWith("/issues/", StringComparison.Ordinal) ||
        suffix.StartsWith("/pulls/147/comments", StringComparison.Ordinal) ||
        suffix.StartsWith("/pulls/147/reviews", StringComparison.Ordinal) ||
        suffix.StartsWith("/pulls/comments/", StringComparison.Ordinal);

    private static byte[] HeadArchive(string sha)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize,
                   leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
        {
            writer.WriteEntry(DirectoryEntry(ArchivePrefix));
            writer.WriteEntry(DirectoryEntry(ArchivePrefix + ".github/"));
            writer.WriteEntry(DirectoryEntry(ArchivePrefix +
                ".github/actions/"));
            writer.WriteEntry(DirectoryEntry(ArchivePrefix +
                ".github/actions/agentic-pr-review/"));
            writer.WriteEntry(DirectoryEntry(ArchivePrefix +
                ".github/actions/agentic-pr-review/dist/"));
            writer.WriteEntry(FileEntry(ArchivePrefix +
                ".github/actions/agentic-pr-review/dist/index.js",
                ProductionShapedLargeBlobBytes));
            writer.WriteEntry(DirectoryEntry(ArchivePrefix + "proof/"));
            writer.WriteEntry(FileEntry(ArchivePrefix +
                "proof/apr178-path-canary.txt", FileBytes));
        }

        return output.ToArray();
    }

    private static TarEntry DirectoryEntry(string name) =>
        new PaxTarEntry(TarEntryType.Directory, name)
        {
            Mode = (UnixFileMode)0x1fd,
        };

    private static TarEntry FileEntry(string name, byte[] bytes) =>
        new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(bytes, writable: false),
            Mode = (UnixFileMode)0x1b4,
        };

    private static byte[] ConfigBytes(string mode) => Encoding.UTF8.GetBytes(
        "{\"schema\":\"agentic-pr-review.config.v1\"," +
        "\"instructionsPath\":\".github/agentic-pr-review/" +
        "trusted-proof-instructions.md\",\"publication\":{\"mode\":\"" +
        (mode is "inline" or "inline-warning" ? "sticky_and_inline" :
            "sticky") + "\"" +
        (mode is "inline" or "inline-warning"
            ? ",\"inlineMinSeverity\":\"high\""
            : string.Empty) + "}}");

    private bool IsTrustedProofPayload() => File.Exists(
        Path.Join(scenarioRoot, "trusted-proof-payload"));

    private string CreateProofControlPair(string mode, string readyBody)
    {
        var stale = readyBody.Contains(
            "\"kind\":\"stale-ready\"",
            StringComparison.Ordinal);
        var readyId = stale ? 820L : 810L;
        var releaseId = readyId + 1;
        var ready = ProofControlComment(readyId, readyBody, "proof-bot");
        var releaseBody = CreateReleaseBody(
            readyBody,
            stale ? "stale-release" : "release",
            readyId);
        var release = ProofControlComment(
            releaseId,
            releaseBody,
            "maintainer");
        WriteProofControlComments(
            mode,
            [.. ReadProofControlComments(mode), ready, release]);
        if (stale)
        {
            File.WriteAllText(Path.Join(scenarioRoot, "stale-released"), "1");
        }

        return ready;
    }

    private static string CreateReleaseBody(
        string readyBody,
        string kind,
        long predecessorCommentId)
    {
        const string prefix = "<!-- apr-r4-e2p-control ";
        const string suffix = " -->";
        var value = JsonNode.Parse(
            readyBody[prefix.Length..^suffix.Length])!.AsObject();
        value["kind"] = kind;
        value["predecessor_comment_id"] = predecessorCommentId;
        value["body_sha256"] = string.Empty;
        var preimage = value.ToJsonString();
        value["body_sha256"] = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(preimage)))
            .ToLowerInvariant();
        return prefix + value.ToJsonString() + suffix;
    }

    private IReadOnlyList<string> ReadProofControlComments(string mode)
    {
        var path = ProofControlPath(mode);
        if (!File.Exists(path))
        {
            return [];
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.EnumerateArray()
            .Select(value => value.GetRawText())
            .ToArray();
    }

    private void WriteProofControlComments(
        string mode,
        IEnumerable<string> comments)
    {
        var path = ProofControlPath(mode);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "[" + string.Join(',', comments) + "]");
    }

    private string ProofControlPath(string mode) => Path.Join(
        mode is "continuation-seed" or "continuation"
            ? Directory.GetParent(scenarioRoot)!.FullName
            : scenarioRoot,
        mode is "continuation-seed" or "continuation"
            ? "shared-proof-control-comments.json"
            : "proof-control-comments.json");

    private static long JsonPropertyLong(string value, string property)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.GetProperty(property).GetInt64();
    }

    private static string IssueComment(long id, string body) =>
        FrameworkJson.Serialize(FrameworkJson.Object(
            ("id", id),
            ("url", "https://api.github.com/repos/" +
                FrameworkCanaries.Repository + "/issues/comments/" + id),
            ("html_url", "https://github.com/" +
                FrameworkCanaries.Repository + "/pull/147#issuecomment-" + id),
            ("body", body)));

    private static string ProofControlCompatibleIssueComment(string source)
    {
        var value = JsonNode.Parse(source)!.AsObject();
        value["user"] = FrameworkJson.Object(
            ("login", "proof-bot"),
            ("id", 8));
        value["created_at"] = Timestamp(701);
        value["updated_at"] = Timestamp(701);
        value["author_association"] = "MEMBER";
        return value.ToJsonString();
    }

    private static string ProofControlComment(
        long id,
        string body,
        string login) =>
        FrameworkJson.Serialize(FrameworkJson.Object(
            ("id", id),
            ("url", "https://api.github.com/repos/" +
                FrameworkCanaries.Repository + "/issues/comments/" + id),
            ("html_url", "https://github.com/" +
                FrameworkCanaries.Repository + "/pull/147#issuecomment-" + id),
            ("body", body),
            ("user", FrameworkJson.Object(
                ("login", login),
                ("id", login == "maintainer" ? 7 : 8))),
            ("created_at", Timestamp(id)),
            ("updated_at", Timestamp(id)),
            ("author_association", login == "maintainer" ? "MEMBER" : "NONE")));

    private static string Timestamp(long id) =>
        DateTimeOffset.Parse(
                "2026-08-21T00:00:00+00:00",
                CultureInfo.InvariantCulture)
            .AddSeconds(id)
            .ToString("O", CultureInfo.InvariantCulture);

    private static string Review(long id) => FrameworkJson.Serialize(
        FrameworkJson.Object(
            ("id", id),
            ("url", "https://api.github.com/repos/" +
                FrameworkCanaries.Repository + "/pulls/147/reviews/" + id),
            ("pull_request_url", "https://api.github.com/repos/" +
                FrameworkCanaries.Repository + "/pulls/147"),
            ("html_url", "https://github.com/" +
                FrameworkCanaries.Repository +
                "/pull/147#pullrequestreview-" + id),
            ("commit_id", HeadSha)));

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
            : mode is "continuation-seed" or "continuation" or
                "cross-head-conflict"
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

    private void EnsureCounter(string name)
    {
        var path = Path.Join(scenarioRoot, name);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "0");
        }
    }

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
