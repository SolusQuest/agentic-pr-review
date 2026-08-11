using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.ActionHost.Snapshot.ChangedFiles;
using AgenticPrReview.Runtime.ActionHost.Snapshot.Diff;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Snapshot.ChangedFiles;

public sealed class ReviewedChangedFileReaderTests
{
    [Fact]
    public async Task ExactCapRequiresAndAcceptsAnEmptyTerminalProbe()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var entries = Enumerable.Range(0, ReviewedContentLimits.ChangedFiles)
            .Select(index => new H5HeadEntry(
                $"file-{index:D3}.txt",
                "100644",
                ReviewedTreeEntryKind.Regular,
                []))
            .ToArray();
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            entries);
        try
        {
            var files = entries.Select(entry => new ActionHostPullRequestFileObject(
                    H5SnapshotTestSupport.BlobSha([]),
                    entry.Path,
                    null,
                    "modified",
                    0,
                    0,
                    0,
                    null))
                .ToArray();
            var transport = new ScriptedTransport(
                H5SnapshotTestSupport.PullRequest(invocation.PullRequest),
                new Dictionary<int, ActionHostPullRequestFilePageObject>
                {
                    [1] = new(files[..100], false),
                    [2] = new(files[100..], false),
                    [3] = new([], true),
                });

            var result = await new ReviewedChangedFileReader(
                    new ScriptedFactory(transport))
                .ReadAsync(
                    invocation,
                    H5SnapshotTestSupport.Token(),
                    tree,
                    CancellationToken.None);

            Assert.Equal(ReviewedSnapshotReadFailure.None, result.Failure);
            Assert.Equal(ReviewedContentLimits.ChangedFiles,
                Assert.IsType<ReviewedChangedFileSet>(result.Value).Files.Length);
            Assert.Equal([1, 2, 3], transport.RequestedPages);
        }
        finally
        {
            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task CapPlusOneReturnsNoPrefix()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var entries = Enumerable.Range(
                0,
                ReviewedContentLimits.ChangedFiles + 1)
            .Select(index => new H5HeadEntry(
                $"file-{index:D3}.txt",
                "100644",
                ReviewedTreeEntryKind.Regular,
                []))
            .ToArray();
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            entries);
        try
        {
            var files = entries.Select(entry => new ActionHostPullRequestFileObject(
                    H5SnapshotTestSupport.BlobSha([]),
                    entry.Path,
                    null,
                    "modified",
                    0,
                    0,
                    0,
                    null))
                .ToArray();
            var transport = new ScriptedTransport(
                H5SnapshotTestSupport.PullRequest(invocation.PullRequest),
                new Dictionary<int, ActionHostPullRequestFilePageObject>
                {
                    [1] = new(files[..100], false),
                    [2] = new(files[100..200], false),
                    [3] = new(files[200..], true),
                });

            var result = await new ReviewedChangedFileReader(
                    new ScriptedFactory(transport))
                .ReadAsync(
                    invocation,
                    H5SnapshotTestSupport.Token(),
                    tree,
                    CancellationToken.None);

            Assert.Null(result.Value);
            Assert.Equal(
                ReviewedSnapshotReadFailure.UnsupportedSize,
                result.Failure);
            Assert.Equal([1, 2, 3], transport.RequestedPages);
        }
        finally
        {
            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task PostPaginationTupleDriftFailsClosed()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent);
        try
        {
            var exact = H5SnapshotTestSupport.PullRequest(
                invocation.PullRequest);
            var drifted = exact with { HeadSha = new string('8', 40) };
            var transport = new ScriptedTransport(
                [exact, drifted],
                new Dictionary<int, ActionHostPullRequestFilePageObject>
                {
                    [1] = new([], true),
                });

            var result = await new ReviewedChangedFileReader(
                    new ScriptedFactory(transport))
                .ReadAsync(
                    invocation,
                    H5SnapshotTestSupport.Token(),
                    tree,
                    CancellationToken.None);

            Assert.Null(result.Value);
            Assert.Equal(
                ReviewedSnapshotReadFailure.IdentityMismatch,
                result.Failure);
        }
        finally
        {
            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    public async Task PaginationAcceptsZeroOneAndFullFirstPage(int count)
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var entries = Enumerable.Range(0, count)
            .Select(index => new H5HeadEntry(
                $"file-{index:D3}.txt",
                "100644",
                ReviewedTreeEntryKind.Regular,
                []))
            .ToArray();
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            entries);
        try
        {
            var files = entries.Select(entry =>
                    new ActionHostPullRequestFileObject(
                        H5SnapshotTestSupport.BlobSha([]),
                        entry.Path,
                        null,
                        "modified",
                        0,
                        0,
                        0,
                        null))
                .ToArray();
            var pages = new Dictionary<
                int,
                ActionHostPullRequestFilePageObject>
            {
                [1] = new(files, count < 100),
            };
            if (count == 100)
            {
                pages[2] = new([], true);
            }

            var transport = new ScriptedTransport(
                H5SnapshotTestSupport.PullRequest(invocation.PullRequest),
                pages);
            var result = await new ReviewedChangedFileReader(
                    new ScriptedFactory(transport))
                .ReadAsync(
                    invocation,
                    H5SnapshotTestSupport.Token(),
                    tree,
                    CancellationToken.None);

            Assert.Equal(count, Assert.IsType<ReviewedChangedFileSet>(
                result.Value).Files.Length);
            Assert.Equal(
                count == 100 ? [1, 2] : [1],
                transport.RequestedPages);
        }
        finally
        {
            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task RepeatedPageIsRejectedAsDuplicateEvidence()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var bytes = "value"u8.ToArray();
        var entry = new H5HeadEntry(
            "file.txt",
            "100644",
            ReviewedTreeEntryKind.Regular,
            bytes);
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            entry);
        try
        {
            var file = new ActionHostPullRequestFileObject(
                H5SnapshotTestSupport.BlobSha(bytes),
                entry.Path,
                null,
                "modified",
                0,
                0,
                0,
                null);
            var transport = new ScriptedTransport(
                H5SnapshotTestSupport.PullRequest(invocation.PullRequest),
                new Dictionary<int, ActionHostPullRequestFilePageObject>
                {
                    [1] = new([file], false),
                    [2] = new([file], true),
                });

            var result = await new ReviewedChangedFileReader(
                    new ScriptedFactory(transport))
                .ReadAsync(
                    invocation,
                    H5SnapshotTestSupport.Token(),
                    tree,
                    CancellationToken.None);

            Assert.Null(result.Value);
            Assert.Equal(
                ReviewedSnapshotReadFailure.IdentityMismatch,
                result.Failure);
        }
        finally
        {
            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task FrozenBaseDriftBeforeOrAfterPaginationFailsClosed()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent);
        try
        {
            var exact = H5SnapshotTestSupport.PullRequest(
                invocation.PullRequest);
            var drifted = exact with { BaseSha = new string('7', 40) };
            foreach (var pullRequests in new[]
                     {
                         new[] { drifted },
                         new[] { exact, drifted },
                     })
            {
                var result = await new ReviewedChangedFileReader(
                        new ScriptedFactory(new ScriptedTransport(
                            pullRequests,
                            new Dictionary<
                                int,
                                ActionHostPullRequestFilePageObject>
                            {
                                [1] = new([], true),
                            })))
                    .ReadAsync(
                        invocation,
                        H5SnapshotTestSupport.Token(),
                        tree,
                        CancellationToken.None);

                Assert.Null(result.Value);
                Assert.Equal(
                    ReviewedSnapshotReadFailure.IdentityMismatch,
                    result.Failure);
            }
        }
        finally
        {
            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task AllLifecyclesAndOversizedPatchEvidenceRemainBounded()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var bytes = "value"u8.ToArray();
        var sha = H5SnapshotTestSupport.BlobSha(bytes);
        var entries = new[]
        {
            "added.txt",
            "modified.txt",
            "changed.txt",
            "renamed.txt",
            "copied.txt",
        }.Select(path => new H5HeadEntry(
            path,
            "100644",
            ReviewedTreeEntryKind.Regular,
            bytes)).ToArray();
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            entries);
        try
        {
            var oversizedPatch = new string('p', 512 * 1024 + 1);
            var files = new[]
            {
                new ActionHostPullRequestFileObject(
                    sha, "added.txt", null, "added", 1, 0, 1,
                    oversizedPatch),
                new ActionHostPullRequestFileObject(
                    sha, "removed.txt", null, "removed", 0, 1, 1, null),
                new ActionHostPullRequestFileObject(
                    sha, "modified.txt", null, "modified", 1, 1, 2, null),
                new ActionHostPullRequestFileObject(
                    sha, "changed.txt", null, "changed", 1, 1, 2, null),
                new ActionHostPullRequestFileObject(
                    sha, "renamed.txt", "old.txt", "renamed", 0, 0, 0, null),
                new ActionHostPullRequestFileObject(
                    sha, "copied.txt", "source.txt", "copied", 0, 0, 0, null),
            };
            var result = await new ReviewedChangedFileReader(
                    new ScriptedFactory(new ScriptedTransport(
                        H5SnapshotTestSupport.PullRequest(
                            invocation.PullRequest),
                        new Dictionary<
                            int,
                            ActionHostPullRequestFilePageObject>
                        {
                            [1] = new(files, true),
                        })))
                .ReadAsync(
                    invocation,
                    H5SnapshotTestSupport.Token(),
                    tree,
                    CancellationToken.None);

            var read = Assert.IsType<ReviewedChangedFileSet>(result.Value);
            Assert.Equal(
                ["added", "changed", "copied", "modified", "removed", "renamed"],
                read.Files.Select(file => file.Status).Order());
            var added = read.Files.Single(file => file.Status == "added");
            Assert.True(added.PatchIncomplete);
            Assert.Null(added.Patch);
        }
        finally
        {
            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    private sealed class ScriptedFactory : IReviewedSnapshotTransportFactory
    {
        private readonly ScriptedTransport _transport;

        internal ScriptedFactory(ScriptedTransport transport)
        {
            _transport = transport;
        }

        public IReviewedSnapshotTransport Create(
            ActionHostAuthorizer.AuthorizedInvocation invocation,
            ActionHostGitHubToken token,
            ReviewedContentBudget budget) => _transport;
    }

    private sealed class ScriptedTransport : IReviewedSnapshotTransport
    {
        private readonly IReadOnlyList<ActionHostGitHubPullRequestFact>
            _pullRequests;
        private readonly IReadOnlyDictionary<
            int,
            ActionHostPullRequestFilePageObject> _pages;
        private int _pullRequestIndex;

        internal ScriptedTransport(
            ActionHostGitHubPullRequestFact pullRequest,
            IReadOnlyDictionary<int, ActionHostPullRequestFilePageObject> pages)
            : this([pullRequest], pages)
        {
        }

        internal ScriptedTransport(
            IReadOnlyList<ActionHostGitHubPullRequestFact> pullRequests,
            IReadOnlyDictionary<int, ActionHostPullRequestFilePageObject> pages)
        {
            _pullRequests = pullRequests;
            _pages = pages;
        }

        internal List<int> RequestedPages { get; } = [];

        public Task<ReviewedSnapshotReadResult<ActionHostGitHubPullRequestFact>>
            GetCurrentPullRequestAsync(CancellationToken cancellationToken)
        {
            var index = Math.Min(
                _pullRequestIndex++,
                _pullRequests.Count - 1);
            return Task.FromResult(ReviewedSnapshotReadResult<
                ActionHostGitHubPullRequestFact>.Success(
                    _pullRequests[index]));
        }

        public Task<ReviewedSnapshotReadResult<
            ActionHostPullRequestFilePageObject>> GetPullRequestFilesAsync(
            int page,
            CancellationToken cancellationToken)
        {
            RequestedPages.Add(page);
            return Task.FromResult(_pages.TryGetValue(page, out var value)
                ? ReviewedSnapshotReadResult<
                    ActionHostPullRequestFilePageObject>.Success(value)
                : ReviewedSnapshotReadResult<
                    ActionHostPullRequestFilePageObject>.Failed(
                        ReviewedSnapshotReadFailure.InvalidResponse));
        }

        public Task<ReviewedSnapshotReadResult<ActionHostGitCommitObject>>
            GetBaseCommitAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Base commit must not be read.");

        public Task<ReviewedSnapshotReadResult<ActionHostGitTreeObject>>
            GetTreeAsync(
                string treeSha,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Base tree must not be read.");

        public Task<ReviewedSnapshotReadResult<ReviewedBaseStagedBlob>>
            StageBaseBlobAsync(
                string blobSha,
                long declaredSize,
                ReviewedBaseBlobStagingLease staging,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Base blob must not be read.");

        public void Dispose()
        {
        }
    }
}
