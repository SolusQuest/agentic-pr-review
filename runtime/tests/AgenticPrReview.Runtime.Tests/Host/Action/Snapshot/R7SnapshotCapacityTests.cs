using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.ActionHost.Snapshot.GitObjects;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Snapshot;

public sealed class R7SnapshotCapacityTests
{
    [Fact]
    public async Task FiveHundredFilesAreAcquiredAndInspectedThroughProductionTransports()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var script = new SnapshotScript(invocation.PullRequest);
        var factory = new ActionHostGitHubAuthorizationTransportFactory(
            () => new ScriptedHandler(script));
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        try
        {
            var acquired = await new ReviewedTreeReader(
                    new ReviewedGitObjectTransportFactory(factory), TimeProvider.System)
                .MaterializeAsync(invocation, H5SnapshotTestSupport.Token(), parent,
                    CancellationToken.None);
            Assert.True(acquired.Snapshot is not null, acquired.FailureCode);
            await using var tree = Assert.IsType<ReviewedTreeSnapshot>(acquired.Snapshot);
            Assert.Equal(500, tree.Records.Length);
            Assert.True(tree.Budget.TryGetRemaining(out var remaining));
            Assert.Equal(ReviewedContentLimits.GitObjectRequests - 4, remaining!.Requests);

            var built = await new BoundedReviewedSnapshotBuilder(factory).BuildAsync(
                invocation, H5SnapshotTestSupport.Token(), tree, parent, CancellationToken.None);
            Assert.True(built.Lease is not null, built.Failure.ToString());
            await using var lease = Assert.IsType<BoundedReviewedSnapshotLease>(built.Lease);
            var snapshot = lease.Snapshot;
            Assert.Equal(invocation.PullRequest.BaseSha, snapshot.Identity.BaseSha);
            Assert.Equal(invocation.PullRequest.HeadSha, snapshot.Identity.HeadSha);
            Assert.Equal(snapshot.Identity.BaseSha, lease.Identities.BaseSha);
            Assert.Equal(snapshot.Identity.HeadSha, lease.Identities.HeadSha);
            Assert.Equal(tree.Identity.Sha256, lease.Identities.ReviewedTreeSha256);
            Assert.All(new[] { lease.Identities.ReviewedTreeSha256,
                lease.Identities.ChangedFilesSha256, lease.Identities.DiffSha256,
                lease.Identities.MaterializationSha256 }, hash => Assert.Equal(64, hash.Length));
            Assert.Equal([1, 2, 3, 4, 5, 6], script.Pages);
            Assert.InRange(script.Requests, 1, ReviewedContentLimits.GitObjectRequests);
            Assert.Equal(514, script.Requests);
            Assert.Equal(500, script.BaseBlobReads);
            Assert.Equal(2, script.PullRequestReads);
            Assert.True(script.BaseBytes < ReviewedContentLimits.AggregateBaseBlobBytes);
            Assert.True(script.HeadBytes < ReviewedContentLimits.MaterializedRootBytes);
            var sources = snapshot.OrderedChangedFiles.Select(change =>
            {
                Assert.True(snapshot.TryGetDiffSource(change.Path, out var source));
                Assert.False(source.SourceTruncated);
                Assert.Equal(change.PatchSha256, source.PatchSha256);
                return source;
            }).ToArray();
            Assert.InRange(sources.Sum(source => (long)source.CanonicalBytes.Length),
                8L * 1024 * 1024 + 1, AgentLimits.DiffSnapshotBytes);
            Assert.InRange(sources[0].CanonicalBytes.Length,
                512 * 1024 + 1, AgentLimits.DiffSourceBytesPerFile);
            Assert.Equal(140, sources[0].Hunks.Length);

            var tools = new SnapshotToolExecutor(snapshot, new VerifiedReviewedFileAccess());
            var listed = new List<string>();
            string? after = null;
            var totalResultBytes = 0;
            do
            {
                Assert.True(AgentToolArguments.TryListChangedFiles(
                    after is null ? "{}" : JsonSerializer.Serialize(new { after }), out var arguments));
                var call = new PreparedListChangedFilesCall("list", arguments!);
                var result = await tools.ExecuteAsync(call, CancellationToken.None);
                Assert.True(result.Succeeded);
                Assert.True(AgentToolResultAdmission.TryAdmit(call, snapshot.Identity, result, out _, out _));
                Assert.InRange(result.CanonicalResult!.Length, 1, AgentLimits.ToolResultBytes);
                totalResultBytes += result.CanonicalResult!.Length;
                using var json = JsonDocument.Parse(result.ResultJson!);
                var root = json.RootElement;
                listed.AddRange(root.GetProperty("changes").EnumerateArray()
                    .Select(item => item.GetProperty("path").GetString()!));
                after = root.GetProperty("next_after").GetString();
                Assert.Equal(after is not null, root.GetProperty("truncated").GetBoolean());
            } while (after is not null);
            Assert.Equal(500, listed.Count);
            Assert.Equal(500, listed.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal("file-499.txt", listed[^1]);
            Assert.Equal(snapshot.OrderedChangedFiles.Select(file => file.Path), listed);

            var first = await ReadDiff(tools, snapshot.Identity, "file-000.txt", 1);
            using (var json = JsonDocument.Parse(first.ResultJson!))
            {
                Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
                Assert.False(json.RootElement.GetProperty("source_truncated").GetBoolean());
                var next = json.RootElement.GetProperty("next_start_hunk").GetInt32();
                var continued = await ReadDiff(tools, snapshot.Identity, "file-000.txt", next);
                totalResultBytes += continued.CanonicalResult!.Length;
                using var continuedJson = JsonDocument.Parse(continued.ResultJson!);
                Assert.Equal(next, continuedJson.RootElement.GetProperty("returned_start_hunk").GetInt32());
                Assert.Equal(sources[0].PatchSha256,
                    continuedJson.RootElement.GetProperty("patch_sha256").GetString());
            }
            var lateHunk = await ReadDiff(tools, snapshot.Identity, "file-000.txt", 140);
            Assert.Contains("APR332_LATE_HUNK", lateHunk.ResultJson);
            Assert.Contains(sources[0].PatchSha256, lateHunk.ResultJson);
            var lateFile = await ReadDiff(tools, snapshot.Identity, "file-499.txt", 1);
            Assert.Contains("APR332_LATE_FILE", lateFile.ResultJson);
            Assert.Contains(sources[^1].PatchSha256, lateFile.ResultJson);
            totalResultBytes += first.CanonicalResult!.Length +
                lateHunk.CanonicalResult!.Length + lateFile.CanonicalResult!.Length;
            Assert.True(AgentToolArguments.TryReadFile(
                "{\"path\":\"file-499.txt\",\"start_line\":1,\"line_count\":1}", out var read));
            var readCall = new PreparedReadFileCall("read", read!);
            var content = await tools.ExecuteAsync(readCall, CancellationToken.None);
            Assert.True(content.Succeeded);
            Assert.True(AgentToolResultAdmission.TryAdmit(readCall, snapshot.Identity, content, out _, out _));
            Assert.Contains("APR332_LATE_FILE", content.ResultJson);
            Assert.Equal(snapshot.Identity, content.Observation!.Identity);
            Assert.Contains(1, content.Observation.ReturnedLines["file-499.txt"]);
            totalResultBytes += content.CanonicalResult!.Length;
            Assert.True(totalResultBytes <= AgentLimits.ToolResultsTotalBytes);
        }
        finally
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
            Directory.Delete(parent);
        }
    }

    private static async Task<AgentToolExecution> ReadDiff(
        SnapshotToolExecutor tools, ReviewedIdentity identity, string path, int start)
    {
        Assert.True(AgentToolArguments.TryReadDiff(JsonSerializer.Serialize(new
        {
            path, start_hunk = start, hunk_count = 20,
        }), out var arguments));
        var call = new PreparedReadDiffCall("diff", arguments!);
        var result = await tools.ExecuteAsync(call, CancellationToken.None);
        Assert.True(result.Succeeded, result.FailureCode);
        Assert.True(AgentToolResultAdmission.TryAdmit(call, identity, result, out _, out _));
        Assert.InRange(result.CanonicalResult!.Length, 1, AgentLimits.ToolResultBytes);
        return result;
    }

    private sealed class ScriptedHandler(SnapshotScript script) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(script.Respond(request));
    }

    private sealed class SnapshotScript
    {
        private readonly ActionHostAuthorizer.FrozenPullRequest _pr;
        private readonly (string Path, byte[] Before, byte[] After)[] _files;
        private readonly Dictionary<string, byte[]> _baseBlobs;
        private readonly byte[] _archive;
        private readonly string _headTree = new('c', 40);
        private readonly string _baseTree = new('8', 40);

        internal SnapshotScript(ActionHostAuthorizer.FrozenPullRequest pr)
        {
            _pr = pr;
            _files = Enumerable.Range(0, 500).Select(index =>
            {
                var before = index == 0 ? Large(false) : index == 499 ? "previous\n" : Moderate(index, false);
                var after = index == 0 ? Large(true) : index == 499 ? "APR332_LATE_FILE\n" : Moderate(index, true);
                return ($"file-{index:D3}.txt", Encoding.UTF8.GetBytes(before), Encoding.UTF8.GetBytes(after));
            }).ToArray();
            _baseBlobs = _files.ToDictionary(file => H5SnapshotTestSupport.BlobSha(file.Before), file => file.Before);
            using var stream = new MemoryStream();
            using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
            using (var tar = new TarWriter(gzip, leaveOpen: true))
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory,
                    "repository-" + pr.HeadSha + "/")
                {
                    Mode = (UnixFileMode)0x1fd,
                });
                foreach (var file in _files)
                {
                    using var bytes = new MemoryStream(file.After);
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile,
                        "repository-" + pr.HeadSha + "/" + file.Path)
                    {
                        DataStream = bytes,
                        Mode = (UnixFileMode)0x1b4,
                    });
                }
            }
            _archive = stream.ToArray();
        }

        internal int Requests { get; private set; }
        internal int BaseBlobReads { get; private set; }
        internal int PullRequestReads { get; private set; }
        internal List<int> Pages { get; } = [];
        internal long BaseBytes => _files.Sum(file => (long)file.Before.Length);
        internal long HeadBytes => _files.Sum(file => (long)file.After.Length);

        internal HttpResponseMessage Respond(HttpRequestMessage request)
        {
            Requests++;
            var uri = request.RequestUri!;
            if (uri.Host == "codeload.github.com")
            {
                Assert.Null(request.Headers.Authorization);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_archive) };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-gzip");
                return response;
            }
            Assert.Equal("h5-token-canary", request.Headers.Authorization!.Parameter);
            var path = uri.AbsolutePath;
            if (path.Contains("/tarball/", StringComparison.Ordinal))
            {
                Assert.EndsWith(_pr.HeadSha, path);
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri("https://codeload.github.com/" +
                    _pr.BaseRepositoryName + "/legacy.tar.gz/" + _pr.HeadSha);
                return response;
            }
            if (path.Contains("/git/commits/", StringComparison.Ordinal))
            {
                var head = path.EndsWith(_pr.HeadSha, StringComparison.Ordinal);
                Assert.EndsWith(head ? _pr.HeadSha : _pr.BaseSha, path);
                return Json(new { sha = head ? _pr.HeadSha : _pr.BaseSha, tree = new { sha = head ? _headTree : _baseTree } });
            }
            if (path.Contains("/git/trees/", StringComparison.Ordinal))
            {
                var head = path.EndsWith(_headTree, StringComparison.Ordinal);
                Assert.EndsWith(head ? _headTree : _baseTree, path);
                return Json(new { sha = head ? _headTree : _baseTree, truncated = false,
                    tree = _files.Select(file => new { path = file.Path, mode = "100644", type = "blob",
                        sha = H5SnapshotTestSupport.BlobSha(head ? file.After : file.Before),
                        size = (head ? file.After : file.Before).Length }) });
            }
            if (path.Contains("/git/blobs/", StringComparison.Ordinal))
            {
                BaseBlobReads++;
                Assert.Equal("application/vnd.github.raw+json", request.Headers.Accept.Single().MediaType);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(_baseBlobs[path.Split('/')[^1]]),
                };
            }
            if (path.EndsWith("/files", StringComparison.Ordinal))
            {
                Assert.StartsWith("?per_page=100&page=", uri.Query);
                var page = int.Parse(uri.Query.Split('=')[^1]);
                Pages.Add(page);
                return Json(_files.Skip((page - 1) * 100).Take(100).Select(file => new
                {
                    sha = H5SnapshotTestSupport.BlobSha(file.After), filename = file.Path,
                    status = "modified", additions = file.Path == "file-000.txt" ? 140 : file.Path == "file-499.txt" ? 1 : 20,
                    deletions = file.Path == "file-000.txt" ? 140 : file.Path == "file-499.txt" ? 1 : 20,
                    changes = file.Path == "file-000.txt" ? 280 : file.Path == "file-499.txt" ? 2 : 40,
                }));
            }
            Assert.EndsWith("/pulls/" + _pr.Number, path);
            PullRequestReads++;
            return Json(new { id = 12345, number = _pr.Number, state = "open", draft = false, merged_at = (string?)null,
                @base = new { sha = _pr.ReportedBaseSha, @ref = "main", repo = new { id = _pr.BaseRepositoryId, full_name = _pr.BaseRepositoryName } },
                head = new { sha = _pr.HeadSha, repo = new { id = _pr.HeadRepositoryId, full_name = _pr.HeadRepositoryName } } });
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };

        private static string Moderate(int file, bool head) => string.Concat(Enumerable.Range(0, 20)
            .Select(line => $"{file:D3}-{line:D2}-" + new string(head ? 'h' : 'b', 600) + "\n"));

        private static string Large(bool head)
        {
            var text = new StringBuilder();
            for (var line = 0; line < 140; line++)
            {
                text.Append(head && line == 139 ? "APR332_LATE_HUNK" : $"{(head ? "new" : "old")}-{line:D3}")
                    .Append(new string(head ? 'h' : 'b', 2048)).Append('\n');
                for (var context = 0; context < 7; context++) text.Append($"context-{line:D3}-{context}\n");
            }
            return text.ToString();
        }
    }
}
