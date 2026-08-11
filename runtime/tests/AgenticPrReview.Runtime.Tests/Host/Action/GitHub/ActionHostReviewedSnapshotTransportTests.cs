using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.GitHub;

public sealed class ActionHostReviewedSnapshotTransportTests
{
    [Fact]
    public async Task PullRequestFilesUseBoundedPaginationAndGeneratedDto()
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        var handler = new CapturingHandler(_ => Json(
            $$"""
            [{"sha":"{{sha}}","filename":"src/file.cs","status":"modified","additions":1,"deletions":2,"changes":3,"patch":"@@ -1 +1 @@"}]
            """));
        using var transport = ActionHostReviewedSnapshotTransport.CreateForTesting(
            "token-canary",
            handler,
            new FailIfCalledObjectTransport());

        var result = await transport.GetPullRequestFilesAsync(
            "owner/repository",
            17,
            2,
            ReviewedContentLimits.ChangedFilesPerPage,
            CancellationToken.None);

        var page = Assert.IsType<ActionHostPullRequestFilePageObject>(
            result.Value);
        var file = Assert.Single(page.Files);
        Assert.True(page.IsComplete);
        Assert.Equal("src/file.cs", file.Path);
        Assert.Equal(3, file.Changes);
        Assert.Equal(
            "https://api.github.com/repos/owner/repository/pulls/17/files" +
                "?per_page=100&page=2",
            handler.Uri);
        Assert.Equal("Bearer token-canary", handler.Authorization);
        Assert.Equal(
            ActionHostGitHubAuthorizationPolicy.Accept,
            handler.Accept);
        Assert.Equal(
            ActionHostGitHubAuthorizationPolicy.ApiVersion,
            handler.ApiVersion);
    }

    [Fact]
    public async Task OversizedOptionalPatchStillMapsWithinThePageByteCap()
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        var patch = new string('a', 1024 * 1024 + 1);
        var handler = new CapturingHandler(_ => Json(
            $$"""
            [{"sha":"{{sha}}","filename":"src/file.cs","status":"modified","additions":1,"deletions":0,"changes":1,"patch":"{{patch}}"}]
            """));
        using var transport = ActionHostReviewedSnapshotTransport.CreateForTesting(
            "token-canary",
            handler,
            new FailIfCalledObjectTransport());

        var result = await transport.GetPullRequestFilesAsync(
            "owner/repository",
            17,
            1,
            ReviewedContentLimits.ChangedFilesPerPage,
            CancellationToken.None);

        var page = Assert.IsType<ActionHostPullRequestFilePageObject>(
            result.Value);
        Assert.Equal(patch.Length, Assert.Single(page.Files).Patch!.Length);
    }

    [Fact]
    public async Task RawBlobStreamsFixedChunksAndRejectsTrailingBytes()
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        var bytes = new byte[ReviewedContentLimits.StreamBufferBytes * 2 + 7];
        Array.Fill<byte>(bytes, 0x5a);
        var handler = new CapturingHandler(_ => new HttpResponseMessage(
            HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(bytes),
        });
        using var transport = ActionHostReviewedSnapshotTransport.CreateForTesting(
            "token-canary",
            handler,
            new FailIfCalledObjectTransport());
        var destination = new MeasuringStream();

        var result = await transport.CopyBlobObjectAsync(
            "owner/repository",
            sha,
            bytes.LongLength - 1,
            destination,
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(
            ActionHostGitObjectFailure.ResponseTooLarge,
            result.Failure);
        Assert.Equal(bytes.LongLength, result.CapturedResponseBytes);
        Assert.Equal(bytes.LongLength - 1, destination.Length);
        Assert.InRange(
            destination.MaximumWrite,
            1,
            ReviewedContentLimits.StreamBufferBytes);
        Assert.Equal("application/vnd.github.raw+json", handler.Accept);
    }

    [Fact]
    public async Task RawBlobShortReadCannotBecomeAnExactObject()
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        var handler = new CapturingHandler(_ => new HttpResponseMessage(
            HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent("short"u8.ToArray()),
        });
        using var transport = ActionHostReviewedSnapshotTransport.CreateForTesting(
            "token-canary",
            handler,
            new FailIfCalledObjectTransport());
        using var destination = new MemoryStream();

        var result = await transport.CopyBlobObjectAsync(
            "owner/repository",
            sha,
            6,
            destination,
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(
            ActionHostGitObjectFailure.InvalidResponse,
            result.Failure);
        Assert.Equal(5, result.CapturedResponseBytes);
    }

    [Fact]
    public async Task MalformedJsonPreservesEveryCapturedResponseByte()
    {
        const string body = "{\"malformed\":";
        var handler = new CapturingHandler(_ => Json(body));
        using var transport = ActionHostReviewedSnapshotTransport.CreateForTesting(
            "token-canary",
            handler,
            new FailIfCalledObjectTransport());

        var result = await transport.GetCurrentPullRequestAsync(
            "owner/repository",
            17,
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ActionHostGitObjectFailure.InvalidResponse, result.Failure);
        Assert.Equal(
            Encoding.UTF8.GetByteCount(body),
            result.CapturedResponseBytes);
    }

    [Fact]
    public async Task UnknownLengthJsonCapPlusOneReportsConsumedBytes()
    {
        const int maximumBytes = 2 * 1024 * 1024;
        var body = new byte[maximumBytes + 1];
        Array.Fill<byte>(body, 0x20);
        var handler = new CapturingHandler(_ => new HttpResponseMessage(
            HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(body)
            {
                Headers =
                {
                    ContentType = new MediaTypeHeaderValue("application/json"),
                },
            },
        });
        using var transport = ActionHostReviewedSnapshotTransport.CreateForTesting(
            "token-canary",
            handler,
            new FailIfCalledObjectTransport());

        var result = await transport.GetCurrentPullRequestAsync(
            "owner/repository",
            17,
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ActionHostGitObjectFailure.ResponseTooLarge, result.Failure);
        Assert.Equal(body.Length, result.CapturedResponseBytes);
    }

    [Fact]
    public async Task MapperRejectionKeepsCapturedBodyChargedOnce()
    {
        const string body = "[{\"status\":\"modified\"}]";
        var handler = new CapturingHandler(_ => Json(body));
        using var transport = ActionHostReviewedSnapshotTransport.CreateForTesting(
            "token-canary",
            handler,
            new FailIfCalledObjectTransport());

        var result = await transport.GetPullRequestFilesAsync(
            "owner/repository",
            17,
            1,
            ReviewedContentLimits.ChangedFilesPerPage,
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ActionHostGitObjectFailure.InvalidResponse, result.Failure);
        Assert.Equal(
            Encoding.UTF8.GetByteCount(body),
            result.CapturedResponseBytes);
    }

    private static HttpResponseMessage Json(string body) => new(
        HttpStatusCode.OK)
    {
        Content = new StringContent(
            body,
            new MediaTypeHeaderValue("application/json")),
    };

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _reply;

        internal CapturingHandler(
            Func<HttpRequestMessage, HttpResponseMessage> reply)
        {
            _reply = reply;
        }

        internal string? Uri { get; private set; }
        internal string? Authorization { get; private set; }
        internal string? Accept { get; private set; }
        internal string? ApiVersion { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri = request.RequestUri?.AbsoluteUri;
            Authorization = request.Headers.Authorization?.ToString();
            Accept = request.Headers.GetValues("Accept").Single();
            ApiVersion = request.Headers.GetValues(
                "X-GitHub-Api-Version").Single();
            return Task.FromResult(_reply(request));
        }
    }

    private sealed class FailIfCalledObjectTransport :
        IActionHostGitObjectTransport
    {
        public Task<ActionHostGitObjectResult<ActionHostGitCommitObject>>
            GetCommitObjectAsync(
                string repositoryName,
                string commitSha,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Commit transport was called.");

        public Task<ActionHostGitObjectResult<ActionHostGitTreeObject>>
            GetTreeObjectAsync(
                string repositoryName,
                string treeSha,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Tree transport was called.");

        public Task<ActionHostGitObjectResult<ActionHostGitBlobObject>>
            GetBlobObjectAsync(
                string repositoryName,
                string blobSha,
                ActionHostGitBlobReadBudget budget,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Blob transport was called.");

        public void Dispose()
        {
        }
    }

    private sealed class MeasuringStream : Stream
    {
        private long _length;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        internal int MaximumWrite { get; private set; }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            MaximumWrite = Math.Max(MaximumWrite, buffer.Length);
            _length += buffer.Length;
            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            MaximumWrite = Math.Max(MaximumWrite, count);
            _length += count;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] _bytes;

        internal UnknownLengthContent(byte[] bytes)
        {
            _bytes = bytes;
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            stream.WriteAsync(_bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(
                new MemoryStream(_bytes, writable: false));
    }
}
