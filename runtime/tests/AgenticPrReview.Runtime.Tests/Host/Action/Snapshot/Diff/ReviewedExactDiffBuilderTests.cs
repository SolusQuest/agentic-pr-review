using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.ActionHost.Snapshot.ChangedFiles;
using AgenticPrReview.Runtime.ActionHost.Snapshot.Diff;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Snapshot.Diff;

public sealed class ReviewedExactDiffBuilderTests
{
    [Fact]
    public void BaseLogicalByteMeterAcceptsTheCapAndRejectsCapPlusOne()
    {
        var meter = new ReviewedBaseLogicalByteMeter();

        Assert.True(meter.TryAdd(
            ReviewedContentLimits.AggregateBaseBlobBytes));
        Assert.Equal(
            ReviewedContentLimits.AggregateBaseBlobBytes,
            meter.Bytes);
        Assert.False(meter.TryAdd(1));
    }

    [Fact]
    public void FinalChangedIdentityBindsAgentVisibleCounts()
    {
        var endpointFiles = ImmutableArray<ReviewedPullRequestFileFact>.Empty;
        var endpoint = ReviewedChangedFileIdentityWriter.Write(endpointFiles);
        var first = ImmutableArray.Create(new ReviewedBuiltChange(
            new(
                "file.txt",
                null,
                "modified",
                1,
                0,
                1,
                "unavailable",
                null,
                false),
            null,
            ReviewedUnavailableReason.NonText));
        var second = ImmutableArray.Create(first[0] with
        {
            Change = first[0].Change with
            {
                Additions = 2,
                Changes = 2,
            },
        });

        var firstIdentity = ReviewedFinalChangedFileIdentityWriter.Write(
            endpoint,
            first);
        var secondIdentity = ReviewedFinalChangedFileIdentityWriter.Write(
            endpoint,
            second);

        Assert.NotEqual(firstIdentity.Sha256, secondIdentity.Sha256);
    }

    [Fact]
    public async Task ExactBytesBuildAddedRemovedModifiedAndRenamedSources()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var added = "added\n"u8.ToArray();
        var modifiedHead = "new\n"u8.ToArray();
        var modifiedBase = "old\n"u8.ToArray();
        var removed = "gone\n"u8.ToArray();
        var renamed = "same\n"u8.ToArray();
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            Regular("added.txt", added),
            Regular("modified.txt", modifiedHead),
            Regular("renamed.txt", renamed));
        var baseRoot = new string('a', 40);
        var transport = new ScriptedTransport(
            invocation.PullRequest.BaseSha,
            baseRoot,
            [
                BaseEntry("modified.txt", modifiedBase),
                BaseEntry("removed.txt", removed),
                BaseEntry("old-name.txt", renamed),
            ],
            [modifiedBase, removed, renamed]);
        using var staging = ReviewedBaseBlobStagingLease.TryCreate(
            parent,
            tree.Budget);
        Assert.NotNull(staging);
        try
        {
            var resolver = new ReviewedBaseObjectResolver(
                transport,
                staging!);
            Assert.Equal(
                ReviewedSnapshotReadFailure.None,
                await resolver.InitializeAsync(CancellationToken.None));
            var facts = new[]
            {
                Fact("added.txt", added, "added", additions: 99),
                Fact("modified.txt", modifiedHead, "modified", additions: 99),
                Fact(
                    "removed.txt",
                    removed,
                    "removed",
                    deletions: 99),
                Fact(
                    "renamed.txt",
                    renamed,
                    "renamed",
                    previousPath: "old-name.txt"),
            }.ToImmutableArray();
            var changed = new ReviewedChangedFileSet(
                facts,
                ReviewedChangedFileIdentityWriter.Write(facts));
            var result = await new ReviewedExactDiffBuilder(tree.Budget)
                .BuildAsync(
                    Identity(invocation),
                    changed,
                    tree,
                    resolver,
                    CancellationToken.None);

            var built = Assert.IsType<ReviewedDiffBuildSet>(result.Value);
            Assert.Equal(ReviewedSnapshotReadFailure.None, result.Failure);
            Assert.Equal(4, built.Changes.Length);
            Assert.All(built.Changes, static item =>
                Assert.Equal("available", item.Change.PatchStatus));
            Assert.Equal(
                (1, 0),
                Counts(built, "added.txt"));
            Assert.Equal(
                (1, 1),
                Counts(built, "modified.txt"));
            Assert.Equal(
                (0, 1),
                Counts(built, "removed.txt"));
            Assert.Equal(
                (0, 0),
                Counts(built, "renamed.txt"));
            Assert.Equal(64, built.Identity.Sha256.Length);
        }
        finally
        {
            staging?.Dispose();
            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task BinaryAndContradictoryPatchEvidenceBecomeUnavailable()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var binary = new byte[] { 0x41, 0x00, 0x42 };
        var text = "actual\n"u8.ToArray();
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            Regular("binary.bin", binary),
            Regular("text.txt", text));
        var baseRoot = new string('b', 40);
        var transport = new ScriptedTransport(
            invocation.PullRequest.BaseSha,
            baseRoot,
            [],
            []);
        using var staging = ReviewedBaseBlobStagingLease.TryCreate(
            parent,
            tree.Budget);
        Assert.NotNull(staging);
        try
        {
            var resolver = new ReviewedBaseObjectResolver(
                transport,
                staging!);
            Assert.Equal(
                ReviewedSnapshotReadFailure.None,
                await resolver.InitializeAsync(CancellationToken.None));
            var facts = new[]
            {
                Fact("binary.bin", binary, "added"),
                Fact(
                    "text.txt",
                    text,
                    "added",
                    patch: "@@ -0,0 +1 @@\n+different"),
            }.ToImmutableArray();
            var result = await new ReviewedExactDiffBuilder(tree.Budget)
                .BuildAsync(
                    Identity(invocation),
                    new ReviewedChangedFileSet(
                        facts,
                        ReviewedChangedFileIdentityWriter.Write(facts)),
                    tree,
                    resolver,
                    CancellationToken.None);

            var built = Assert.IsType<ReviewedDiffBuildSet>(result.Value);
            Assert.Equal(
                "binary",
                built.Changes.Single(item =>
                    item.Change.Path == "binary.bin").Change.PatchStatus);
            var contradiction = built.Changes.Single(item =>
                item.Change.Path == "text.txt");
            Assert.Equal("unavailable", contradiction.Change.PatchStatus);
            Assert.Equal(
                ReviewedUnavailableReason.PatchContradiction,
                contradiction.UnavailableReason);
            Assert.Null(contradiction.Source);
        }
        finally
        {
            staging?.Dispose();
            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task TextMatrixPreservesBomCrLfNoNewlineAndLineBoundary()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var bomCrLf = "\uFEFFline\r\n"u8.ToArray();
        var noNewline = "tail"u8.ToArray();
        var maximumLine = Encoding.UTF8.GetBytes(
            new string('a', AgentLimits.DiffLineTextBytes));
        var overlongLine = Encoding.UTF8.GetBytes(
            new string('b', AgentLimits.DiffLineTextBytes + 1));
        var loneCr = "bad\r"u8.ToArray();
        var entries = new[]
        {
            Regular("bom.txt", bomCrLf),
            Regular("no-newline.txt", noNewline),
            Regular("maximum.txt", maximumLine),
            Regular("overlong.txt", overlongLine),
            Regular("lone-cr.txt", loneCr),
        };
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            entries);
        var transport = new ScriptedTransport(
            invocation.PullRequest.BaseSha,
            new string('e', 40),
            [],
            []);
        using var staging = ReviewedBaseBlobStagingLease.TryCreate(
            parent,
            tree.Budget);
        Assert.NotNull(staging);
        try
        {
            var resolver = new ReviewedBaseObjectResolver(
                transport,
                staging!);
            Assert.Equal(
                ReviewedSnapshotReadFailure.None,
                await resolver.InitializeAsync(CancellationToken.None));
            var facts = new[]
            {
                Fact("bom.txt", bomCrLf, "added"),
                Fact("no-newline.txt", noNewline, "added"),
                Fact("maximum.txt", maximumLine, "added"),
                Fact("overlong.txt", overlongLine, "added", additions: 1),
                Fact("lone-cr.txt", loneCr, "added", additions: 1),
            }.ToImmutableArray();

            var result = await new ReviewedExactDiffBuilder(tree.Budget)
                .BuildAsync(
                    Identity(invocation),
                    new ReviewedChangedFileSet(
                        facts,
                        ReviewedChangedFileIdentityWriter.Write(facts)),
                    tree,
                    resolver,
                    CancellationToken.None);

            var built = Assert.IsType<ReviewedDiffBuildSet>(result.Value);
            var bom = built.Changes.Single(item =>
                item.Change.Path == "bom.txt");
            Assert.Equal("line", Assert.Single(
                Assert.Single(bom.Source!.Hunks).Lines).Text);
            var tailLines = Assert.Single(built.Changes.Single(item =>
                    item.Change.Path == "no-newline.txt").Source!.Hunks)
                .Lines;
            Assert.Equal(["addition", "no_newline"],
                tailLines.Select(static line => line.Kind));
            Assert.Equal("available", built.Changes.Single(item =>
                item.Change.Path == "maximum.txt").Change.PatchStatus);
            Assert.Equal(
                ReviewedUnavailableReason.LineTooLong,
                built.Changes.Single(item =>
                    item.Change.Path == "overlong.txt").UnavailableReason);
            Assert.Equal(
                ReviewedUnavailableReason.NonText,
                built.Changes.Single(item =>
                    item.Change.Path == "lone-cr.txt").UnavailableReason);
        }
        finally
        {
            staging?.Dispose();
            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task BaseBlobCacheDoesNotDeduplicateLogicalOperandBytes()
    {
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var bytes = new byte[checked((int)ReviewedContentLimits.BaseBlobBytes)];
        Array.Fill<byte>(bytes, 0x3c);
        var baseRoot = new string('c', 40);
        var transport = new ScriptedTransport(
            new string('d', 40),
            baseRoot,
            [BaseEntry("first.txt", bytes), BaseEntry("second.txt", bytes)],
            [bytes]);
        var budget = ReviewedSnapshotTestAccess.ProductionBudget();
        using var staging = ReviewedBaseBlobStagingLease.TryCreate(
            parent,
            budget);
        Assert.NotNull(staging);
        try
        {
            var resolver = new ReviewedBaseObjectResolver(
                transport,
                staging!);
            Assert.Equal(
                ReviewedSnapshotReadFailure.None,
                await resolver.InitializeAsync(CancellationToken.None));

            Assert.NotNull((await resolver.ResolveAsync(
                "first.txt",
                CancellationToken.None)).Value);
            Assert.NotNull((await resolver.ResolveAsync(
                "second.txt",
                CancellationToken.None)).Value);
            Assert.Equal(bytes.LongLength * 2, resolver.LogicalBytes);
            Assert.Equal(1, transport.StageCalls);
        }
        finally
        {
            budget.Invalidate();
            staging?.Dispose();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task BaseBlobCapPlusOneIsRejectedBeforeTransportRead()
    {
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var baseRoot = new string('f', 40);
        var transport = new ScriptedTransport(
            new string('d', 40),
            baseRoot,
            [
                new ActionHostGitTreeEntryObject(
                    "oversized.bin",
                    "100644",
                    "blob",
                    new string('a', 40),
                    ReviewedContentLimits.BaseBlobBytes + 1),
            ],
            []);
        var budget = ReviewedSnapshotTestAccess.ProductionBudget();
        using var staging = ReviewedBaseBlobStagingLease.TryCreate(
            parent,
            budget);
        Assert.NotNull(staging);
        try
        {
            var resolver = new ReviewedBaseObjectResolver(
                transport,
                staging!);
            Assert.Equal(
                ReviewedSnapshotReadFailure.None,
                await resolver.InitializeAsync(CancellationToken.None));

            var result = await resolver.ResolveAsync(
                "oversized.bin",
                CancellationToken.None);

            Assert.Null(result.Value);
            Assert.Equal(
                ReviewedSnapshotReadFailure.UnsupportedSize,
                result.Failure);
            Assert.Equal(0, transport.StageCalls);
        }
        finally
        {
            budget.Invalidate();
            staging?.Dispose();
            Directory.Delete(parent, recursive: true);
        }
    }

    private static (int Additions, int Deletions) Counts(
        ReviewedDiffBuildSet set,
        string path)
    {
        var change = set.Changes.Single(item => item.Change.Path == path);
        return (change.Change.Additions, change.Change.Deletions);
    }

    private static H5HeadEntry Regular(string path, byte[] bytes) => new(
        path,
        "100644",
        ReviewedTreeEntryKind.Regular,
        bytes);

    private static ActionHostGitTreeEntryObject BaseEntry(
        string path,
        byte[] bytes) => new(
            path,
            "100644",
            "blob",
            H5SnapshotTestSupport.BlobSha(bytes),
            bytes.LongLength);

    private static ReviewedPullRequestFileFact Fact(
        string path,
        byte[] bytes,
        string status,
        int additions = 0,
        int deletions = 0,
        string? previousPath = null,
        string? patch = null) => new(
            H5SnapshotTestSupport.BlobSha(bytes),
            path,
            previousPath,
            status,
            additions,
            deletions,
            checked(additions + deletions),
            patch,
            false);

    private static ReviewedIdentity Identity(
        ActionHostAuthorizer.AuthorizedInvocation invocation) =>
        new(
            invocation.PullRequest.RepositoryId.ToString(),
            invocation.PullRequest.Number,
            invocation.PullRequest.BaseSha,
            invocation.PullRequest.HeadSha);

    private sealed class ScriptedTransport : IReviewedSnapshotTransport
    {
        private readonly string _baseSha;
        private readonly string _rootTreeSha;
        private readonly IReadOnlyList<ActionHostGitTreeEntryObject> _entries;
        private readonly IReadOnlyDictionary<string, byte[]> _blobs;

        internal ScriptedTransport(
            string baseSha,
            string rootTreeSha,
            IReadOnlyList<ActionHostGitTreeEntryObject> entries,
            IEnumerable<byte[]> blobs)
        {
            _baseSha = baseSha;
            _rootTreeSha = rootTreeSha;
            _entries = entries;
            _blobs = blobs.ToDictionary(
                H5SnapshotTestSupport.BlobSha,
                static bytes => bytes,
                StringComparer.Ordinal);
        }

        internal int StageCalls { get; private set; }

        public Task<ReviewedSnapshotReadResult<ActionHostGitCommitObject>>
            GetBaseCommitAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ReviewedSnapshotReadResult<
                ActionHostGitCommitObject>.Success(
                    new(_baseSha, _rootTreeSha)));

        public Task<ReviewedSnapshotReadResult<ActionHostGitTreeObject>>
            GetTreeAsync(
                string treeSha,
                CancellationToken cancellationToken) =>
            Task.FromResult(StringComparer.Ordinal.Equals(
                    treeSha,
                    _rootTreeSha)
                ? ReviewedSnapshotReadResult<ActionHostGitTreeObject>.Success(
                    new(treeSha, _entries))
                : ReviewedSnapshotReadResult<ActionHostGitTreeObject>.Failed(
                    ReviewedSnapshotReadFailure.NotFound));

        public async Task<ReviewedSnapshotReadResult<ReviewedBaseStagedBlob>>
            StageBaseBlobAsync(
                string blobSha,
                long declaredSize,
                ReviewedBaseBlobStagingLease staging,
                CancellationToken cancellationToken)
        {
            StageCalls++;
            if (!_blobs.TryGetValue(blobSha, out var bytes))
            {
                return ReviewedSnapshotReadResult<ReviewedBaseStagedBlob>.Failed(
                    ReviewedSnapshotReadFailure.NotFound);
            }

            await using var writer = staging.TryCreateWriter(
                blobSha,
                declaredSize);
            Assert.NotNull(writer);
            await writer!.WriteAsync(bytes, cancellationToken);
            var blob = await writer.CompleteAsync(cancellationToken);
            return blob is null
                ? ReviewedSnapshotReadResult<ReviewedBaseStagedBlob>.Failed(
                    ReviewedSnapshotReadFailure.IdentityMismatch)
                : ReviewedSnapshotReadResult<ReviewedBaseStagedBlob>.Success(
                    blob);
        }

        public Task<ReviewedSnapshotReadResult<ActionHostGitHubPullRequestFact>>
            GetCurrentPullRequestAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("PR reads are not expected.");

        public Task<ReviewedSnapshotReadResult<
            ActionHostPullRequestFilePageObject>> GetPullRequestFilesAsync(
            int page,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("PR files are not expected.");

        public void Dispose()
        {
        }
    }
}
