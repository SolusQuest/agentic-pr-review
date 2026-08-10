using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.ActionHost.Snapshot.GitObjects;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.Action.Snapshot;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Snapshot.GitObjects;

public sealed partial class GitObjectTransportTests
{
    [Fact]
    public async Task ExactCommitAndNonRecursiveTreeRequestsUseFrozenAuthority()
    {
        var invocation = await AuthorizedInvocation();
        var treeSha = new string('1', 40);
        var handler = new CapturingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path.EndsWith(
                "/git/commits/" + ActionHostAuthorizationScenario.HeadSha,
                StringComparison.Ordinal)
                ? JsonResponse($$"""
                    {
                      "sha": "{{ActionHostAuthorizationScenario.HeadSha}}",
                      "tree": { "sha": "{{treeSha}}" },
                      "forward_compatible": true
                    }
                    """)
                : JsonResponse($$"""
                    {
                      "sha": "{{treeSha}}",
                      "truncated": false,
                      "tree": []
                    }
                    """);
        });
        var budget = ProductionBudget();
        using var transport = ReviewedSnapshotTestAccess.Transport(
            invocation,
            Token(),
            budget,
            handler);

        var commit = await transport.GetCommitAsync(CancellationToken.None);
        var tree = await transport.GetTreeAsync(
            treeSha,
            CancellationToken.None);

        Assert.Equal(treeSha, commit.Value!.TreeSha);
        Assert.Empty(tree.Value!.Entries);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.github.com", request.Origin);
            Assert.Equal(string.Empty, request.Query);
            Assert.Equal("Bearer token-canary",
                request.Header("Authorization"));
            Assert.Equal("2026-03-10",
                request.Header("X-GitHub-Api-Version"));
        });
        Assert.DoesNotContain(
            "recursive",
            handler.Requests[1].Uri,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SharedBlobEnvelopeStagesExactDecodedBytes()
    {
        var invocation = await AuthorizedInvocation();
        var bytes = "text\0and-binary"u8.ToArray();
        var sha = GitBlobSha(bytes);
        var handler = new CapturingHandler(_ => JsonResponse(
            BlobResponse(bytes)));
        var budget = ProductionBudget();
        using var transport = ReviewedSnapshotTestAccess.Transport(
            invocation,
            Token(),
            budget,
            handler);
        var parent = CreateTemporaryDirectory();
        try
        {
            var staging = ReviewedSnapshotTestAccess.Staging(parent, budget);
            var result = await transport.StageBlobAsync(
                sha,
                bytes.Length,
                staging,
                CancellationToken.None);

            var staged = Assert.IsType<ReviewedStagedBlob>(result.Value);
            using var copied = new MemoryStream();
            Assert.True(await staged.CopyVerifiedToAsync(
                copied,
                CancellationToken.None));
            Assert.Equal(bytes, copied.ToArray());
            Assert.Equal(ActionHostGitHubAuthorizationPolicy.Accept,
                Assert.Single(handler.Requests).Header("Accept"));
            Assert.True(staging.Cleanup());
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task SameSizePostStageTamperProducesNoBytes()
    {
        var staged = await Stage("trusted"u8.ToArray());
        try
        {
            var stream = ReviewedSnapshotTestAccess.StagedStream(staged.Blob);
            await RandomAccess.WriteAsync(
                stream.SafeFileHandle,
                "altered"u8.ToArray(),
                0,
                CancellationToken.None);
            using var destination = new MemoryStream();

            Assert.False(await staged.Blob.CopyVerifiedToAsync(
                destination,
                CancellationToken.None));
            Assert.Empty(destination.ToArray());
            Assert.True(staged.Lease.Cleanup());
        }
        finally
        {
            Directory.Delete(staged.Parent, recursive: true);
        }
    }

    [Fact]
    public async Task ExpiredSharedDeadlinePreventsStagedCopy()
    {
        var time = new ManualTimeProvider();
        var budget = ReviewedSnapshotTestAccess.Budget(
            ReviewedContentLimits.GitObjectRequests,
            ReviewedContentLimits.GitObjectResponseBytes,
            ReviewedContentLimits.AggregateResponseBytes,
            ReviewedContentLimits.AcquisitionAndMaterializationTimeout,
            time);
        var staged = await Stage("trusted"u8.ToArray(), budget);
        try
        {
            time.Advance(
                ReviewedContentLimits.AcquisitionAndMaterializationTimeout);
            using var destination = new MemoryStream();

            Assert.False(await staged.Blob.CopyVerifiedToAsync(
                destination,
                CancellationToken.None));
            Assert.Empty(destination.ToArray());
        }
        finally
        {
            staged.Lease.Cleanup();
            Directory.Delete(staged.Parent, recursive: true);
        }
    }

    [Fact]
    public async Task LinuxSymlinkPathCannotRedirectHandleBackedBlob()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var bytes = "trusted"u8.ToArray();
        var staged = await Stage(bytes);
        try
        {
            var path = ReviewedSnapshotTestAccess.StagedPath(staged.Blob);
            var target = Path.Join(staged.Parent, "symlink-target");
            await File.WriteAllBytesAsync(target, bytes);
            Assert.False(File.Exists(path));
            File.CreateSymbolicLink(path, target);

            using var destination = new MemoryStream();
            Assert.True(await staged.Blob.CopyVerifiedToAsync(
                destination,
                CancellationToken.None));
            Assert.Equal(bytes, destination.ToArray());
            Assert.True(staged.Lease.Cleanup());
            Assert.True(File.Exists(path));
        }
        finally
        {
            staged.Lease.Cleanup();
            Directory.Delete(staged.Parent, recursive: true);
        }
    }

    [Fact]
    public async Task LinuxFifoPathCannotBlockOrRedirectHandleBackedBlob()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var staged = await Stage("trusted"u8.ToArray());
        try
        {
            var path = ReviewedSnapshotTestAccess.StagedPath(staged.Blob);
            Assert.False(File.Exists(path));
            Assert.Equal(0, MakeFifo(path, Convert.ToUInt32("600", 8)));
            using var destination = new MemoryStream();

            var copied = await staged.Blob.CopyVerifiedToAsync(
                    destination,
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(copied);
            Assert.Equal("trusted"u8.ToArray(), destination.ToArray());
            Assert.True(staged.Lease.Cleanup());
            Assert.True(File.Exists(path));
        }
        finally
        {
            staged.Lease.Cleanup();
            Directory.Delete(staged.Parent, recursive: true);
        }
    }

    [Fact]
    public async Task MissingTruncatedAndDuplicatePropertiesFailClosed()
    {
        var invocation = await AuthorizedInvocation();
        var sha = new string('2', 40);
        foreach (var body in new[]
        {
            $$"""{"sha":"{{sha}}","tree":[]}""",
            $$"""{"sha":"{{sha}}","truncated":false,"truncated":false,"tree":[]}""",
            $$"""{"sha":"{{sha}}","truncated":true,"tree":[]}""",
        })
        {
        using var transport = ReviewedSnapshotTestAccess.Transport(
                invocation,
                Token(),
                ProductionBudget(),
                new CapturingHandler(_ => JsonResponse(body)));
            var result = await transport.GetTreeAsync(
                sha,
                CancellationToken.None);
            Assert.Null(result.Value);
            Assert.Equal(ReviewedGitObjectFailure.InvalidResponse,
                result.Failure);
        }
    }

    [Fact]
    public async Task CommitAndTreeResponsesMustMatchTheRequestedObjectSha()
    {
        var invocation = await AuthorizedInvocation();
        var treeSha = new string('4', 40);
        using (var commitTransport =
               ReviewedSnapshotTestAccess.Transport(
                   invocation,
                   Token(),
                   ProductionBudget(),
                   new CapturingHandler(_ => JsonResponse($$"""
                       {
                         "sha": "{{new string('5', 40)}}",
                         "tree": { "sha": "{{treeSha}}" }
                       }
                       """))))
        {
            var commit = await commitTransport.GetCommitAsync(
                CancellationToken.None);
            Assert.Null(commit.Value);
            Assert.Equal(ReviewedGitObjectFailure.InvalidResponse,
                commit.Failure);
        }

        using var treeTransport =
            ReviewedSnapshotTestAccess.Transport(
                invocation,
                Token(),
                ProductionBudget(),
                new CapturingHandler(_ => JsonResponse($$"""
                    {
                      "sha": "{{new string('6', 40)}}",
                      "truncated": false,
                      "tree": []
                    }
                    """)));
        var tree = await treeTransport.GetTreeAsync(
            treeSha,
            CancellationToken.None);
        Assert.Null(tree.Value);
        Assert.Equal(ReviewedGitObjectFailure.InvalidResponse, tree.Failure);
    }

    [Fact]
    public async Task ObjectResponseByteBoundaryAcceptsCapAndRejectsCapPlusOne()
    {
        var invocation = await AuthorizedInvocation();
        var treeSha = new string('7', 40);
        var json = Encoding.UTF8.GetBytes($$$"""
            {"sha":"{{{ActionHostAuthorizationScenario.HeadSha}}}","tree":{"sha":"{{{treeSha}}}"}}
            """);
        var atCap = new byte[ReviewedContentLimits.GitObjectResponseBytes];
        json.CopyTo(atCap, 0);
        Array.Fill(atCap, (byte)' ', json.Length,
            atCap.Length - json.Length);
        using (var accepted = ReviewedSnapshotTestAccess.Transport(
                   invocation,
                   Token(),
                   ProductionBudget(),
                   new CapturingHandler(_ => JsonBytesResponse(atCap))))
        {
            var result = await accepted.GetCommitAsync(
                CancellationToken.None);
            Assert.NotNull(result.Value);
        }

        var overCap = new byte[ReviewedContentLimits.GitObjectResponseBytes + 1];
        using var rejected = ReviewedSnapshotTestAccess.Transport(
            invocation,
            Token(),
            ProductionBudget(),
            new CapturingHandler(_ => JsonBytesResponse(overCap)));
        var overflow = await rejected.GetCommitAsync(CancellationToken.None);
        Assert.Null(overflow.Value);
        Assert.Equal(ReviewedGitObjectFailure.UnsupportedSize,
            overflow.Failure);
    }

    [Fact]
    public async Task RequestOverflowIsRejectedBeforeASecondSend()
    {
        var invocation = await AuthorizedInvocation();
        var treeSha = new string('3', 40);
        var handler = new CapturingHandler(_ => JsonResponse($$"""
            {
              "sha": "{{ActionHostAuthorizationScenario.HeadSha}}",
              "tree": { "sha": "{{treeSha}}" }
            }
            """));
        var budget = ReviewedSnapshotTestAccess.Budget(
            1,
            ReviewedContentLimits.GitObjectResponseBytes,
            ReviewedContentLimits.AggregateResponseBytes,
            TimeSpan.FromMinutes(5),
            TimeProvider.System);
        using var transport = ReviewedSnapshotTestAccess.Transport(
            invocation,
            Token(),
            budget,
            handler);

        Assert.NotNull((await transport.GetCommitAsync(
            CancellationToken.None)).Value);
        var overflow = await transport.GetTreeAsync(
            treeSha,
            CancellationToken.None);

        Assert.Equal(ReviewedGitObjectFailure.UnsupportedSize,
            overflow.Failure);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task BudgetDeadlineCancelsStalledResponseHeaders()
    {
        var invocation = await AuthorizedInvocation();
        using var transport = ReviewedSnapshotTestAccess.Transport(
            invocation,
            Token(),
            ShortDeadlineBudget(),
            new StallingHeadersHandler());
        var stopwatch = Stopwatch.StartNew();

        var result = await transport.GetCommitAsync(CancellationToken.None);

        Assert.Equal(ReviewedGitObjectFailure.UnsupportedSize, result.Failure);
        Assert.Null(result.Value);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task BudgetDeadlineCancelsStalledResponseBody()
    {
        var invocation = await AuthorizedInvocation();
        using var transport = ReviewedSnapshotTestAccess.Transport(
            invocation,
            Token(),
            ShortDeadlineBudget(),
            new CapturingHandler(_ => StallingJsonResponse()));
        var stopwatch = Stopwatch.StartNew();

        var result = await transport.GetCommitAsync(CancellationToken.None);

        Assert.Equal(ReviewedGitObjectFailure.UnsupportedSize, result.Failure);
        Assert.Null(result.Value);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RateLimitIsNotRetriedAndReturnsNoValue()
    {
        var invocation = await AuthorizedInvocation();
        var handler = new CapturingHandler(_ => new HttpResponseMessage(
            (HttpStatusCode)429));
        using var transport = ReviewedSnapshotTestAccess.Transport(
            invocation,
            Token(),
            ProductionBudget(),
            handler);

        var result = await transport.GetCommitAsync(CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ReviewedGitObjectFailure.RateLimited, result.Failure);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void SourceGeneratedJsonRootsExistWithoutReflectionFallback()
    {
        Assert.NotNull(ActionHostGitObjectJsonContext.Default
            .ActionHostGitCommitDocument);
        Assert.NotNull(ActionHostGitObjectJsonContext.Default
            .ActionHostGitTreeDocument);
        Assert.False(ActionHostGitObjectJsonContext.Default.Options
            .PropertyNameCaseInsensitive);
        Assert.False(ActionHostGitObjectJsonContext.Default.Options
            .AllowDuplicateProperties);
    }

    private static async Task<ActionHostAuthorizer.AuthorizedInvocation>
        AuthorizedInvocation()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var result = await scenario.CreateAuthorizer().AuthorizeAsync(
            scenario.Launch,
            CancellationToken.None);
        return Assert.IsType<ActionHostAuthorizer.AuthorizedInvocation>(
            result.Invocation);
    }

    private static ActionHostGitHubToken Token()
    {
        Assert.True(ActionHostGitHubToken.TryCreate(
            "token-canary",
            out var token));
        return token!;
    }

    private static ReviewedContentBudget ProductionBudget() =>
        ReviewedSnapshotTestAccess.ProductionBudget();

    private static ReviewedContentBudget ShortDeadlineBudget() =>
        ReviewedSnapshotTestAccess.Budget(
            ReviewedContentLimits.GitObjectRequests,
            ReviewedContentLimits.GitObjectResponseBytes,
            ReviewedContentLimits.AggregateResponseBytes,
            TimeSpan.FromMilliseconds(50),
            TimeProvider.System);

    private static async Task<(
        ReviewedStagedBlob Blob,
        ReviewedBlobStagingLease Lease,
        string Parent)> Stage(
            byte[] bytes,
            ReviewedContentBudget? suppliedBudget = null)
    {
        var invocation = await AuthorizedInvocation();
        var parent = CreateTemporaryDirectory();
        var budget = suppliedBudget ?? ProductionBudget();
        var lease = ReviewedSnapshotTestAccess.Staging(parent, budget);
        using var transport = ReviewedSnapshotTestAccess.Transport(
            invocation,
            Token(),
            budget,
            new CapturingHandler(_ => JsonResponse(BlobResponse(bytes))));
        var result = await transport.StageBlobAsync(
            GitBlobSha(bytes),
            bytes.Length,
            lease,
            CancellationToken.None);
        return (Assert.IsType<ReviewedStagedBlob>(result.Value), lease, parent);
    }

    private static HttpResponseMessage JsonResponse(string body) => new(
        HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage JsonBytesResponse(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/json")
        {
            CharSet = "utf-8",
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        };
    }

    private static HttpResponseMessage StallingJsonResponse()
    {
        var content = new StreamContent(new StallingReadStream());
        content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/json");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        };
    }

    private static string GitBlobSha(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes(
            "blob " + bytes.Length.ToString(CultureInfo.InvariantCulture) +
            "\0");
        return Convert.ToHexString(SHA1.HashData([.. header, .. bytes]))
            .ToLowerInvariant();
    }

    private static string BlobResponse(byte[] bytes) => $$"""
        {"sha":"{{GitBlobSha(bytes)}}","size":{{bytes.Length}},"encoding":"base64","content":"{{Convert.ToBase64String(bytes)}}"}
        """;

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Join(
            Path.GetTempPath(),
            "apr-h4-transport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

        internal CapturingHandler(
            Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _response = response;
        }

        internal List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(CapturedRequest.From(request));
            return Task.FromResult(_response(request));
        }
    }

    private sealed class StallingHeadersHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan time) =>
            _timestamp = checked(_timestamp + time.Ticks);
    }

    private sealed class StallingReadStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    [LibraryImport(
        "libc",
        EntryPoint = "mkfifo",
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MakeFifo(string path, uint mode);

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Uri,
        string Origin,
        string Query,
        IReadOnlyDictionary<string, string> Headers)
    {
        internal string Header(string name) => Headers[name];

        internal static CapturedRequest From(HttpRequestMessage request) => new(
            request.Method,
            request.RequestUri!.AbsoluteUri,
            request.RequestUri.GetLeftPart(UriPartial.Authority),
            request.RequestUri.Query,
            request.Headers.ToDictionary(
                static header => header.Key,
                static header => string.Join(' ', header.Value),
                StringComparer.OrdinalIgnoreCase));
    }
}
