using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.Agent;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Snapshot;

public sealed class ReviewedTreeSnapshotTests
{
    [Fact]
    public void ProductionLimitsMatchTheFrozenH4AndRetainedAgentContract()
    {
        Assert.Equal(20_000, ReviewedContentLimits.TrackedPaths);
        Assert.Equal(8L * 1024 * 1024,
            ReviewedContentLimits.TreeMetadataBytes);
        Assert.Equal(1_024, ReviewedContentLimits.PathBytes);
        Assert.Equal(64, ReviewedContentLimits.TreeDepth);
        Assert.Equal(4_000,
            ReviewedContentLimits.UniqueTreeAndBlobObjects);
        Assert.Equal(4_096, ReviewedContentLimits.GitObjectRequests);
        Assert.Equal(1024L * 1024, ReviewedContentLimits.HeadBlobBytes);
        Assert.Equal(256L * 1024 * 1024,
            ReviewedContentLimits.AggregateHeadBlobBytes);
        Assert.Equal(2L * 1024 * 1024,
            ReviewedContentLimits.GitObjectResponseBytes);
        Assert.Equal(512L * 1024 * 1024,
            ReviewedContentLimits.AggregateResponseBytes);
        Assert.Equal(TimeSpan.FromSeconds(300),
            ReviewedContentLimits.AcquisitionAndMaterializationTimeout);
        Assert.Equal(AgentLimits.TrackedFiles,
            ReviewedContentLimits.TrackedPaths);
        Assert.Equal(AgentLimits.TrackedFilesMetadataBytes,
            ReviewedContentLimits.TreeMetadataBytes);
        Assert.Equal(AgentLimits.PathBytes, ReviewedContentLimits.PathBytes);
    }

    [Fact]
    public void TraversalMetersAcceptEachCapAndRejectCapPlusOne()
    {
        var metadata = new ReviewedTreeTraversalMeter();
        Assert.True(metadata.TryAddLogicalEntry(
            checked((int)ReviewedContentLimits.TreeMetadataBytes)));
        Assert.False(metadata.TryAddLogicalEntry(1));

        var leaves = new ReviewedTreeTraversalMeter();
        for (var index = 0; index < ReviewedContentLimits.TrackedPaths; index++)
        {
            Assert.True(leaves.TryAddLeafPath());
        }

        Assert.False(leaves.TryAddLeafPath());

        var objects = new ReviewedTreeTraversalMeter();
        for (var index = 0;
             index < ReviewedContentLimits.UniqueTreeAndBlobObjects;
             index++)
        {
            Assert.True(objects.TryAddUniqueObject());
        }

        Assert.False(objects.TryAddUniqueObject());

        var bytes = new ReviewedTreeTraversalMeter();
        Assert.True(bytes.TryAddLogicalHeadBlobBytes(
            ReviewedContentLimits.AggregateHeadBlobBytes));
        Assert.False(bytes.TryAddLogicalHeadBlobBytes(1));
    }

    [Fact]
    public void SharedBudgetCountsRequestsResponsesAndAbsoluteDeadline()
    {
        var time = new ManualTimeProvider();
        var budget = ReviewedContentBudget.Create(
            new ReviewedContentLimitProfile(
                MaximumRequests: 2,
                MaximumResponseBytes: 3,
                MaximumAggregateResponseBytes: 5,
                Timeout: TimeSpan.FromSeconds(10)),
            time);

        Assert.True(budget.TryReserveRequest(CancellationToken.None));
        Assert.True(budget.TryReserveRequest(CancellationToken.None));
        Assert.False(budget.TryReserveRequest(CancellationToken.None));

        long first = 0;
        Assert.True(budget.TryConsumeResponseBytes(
            ref first,
            3,
            CancellationToken.None));
        Assert.False(budget.TryConsumeResponseBytes(
            ref first,
            1,
            CancellationToken.None));

        long second = 0;
        Assert.True(budget.TryConsumeResponseBytes(
            ref second,
            2,
            CancellationToken.None));
        Assert.False(budget.TryConsumeResponseBytes(
            ref second,
            1,
            CancellationToken.None));

        Assert.True(budget.TryGetRemaining(out var remaining));
        Assert.Equal(0, remaining!.Requests);
        Assert.Equal(0, remaining.ResponseBytes);

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.False(budget.TryGetRemaining(out _));
    }

    [Fact]
    public void ProductionRequestAndResponseBoundariesAcceptCapAndRejectNext()
    {
        var requests = ReviewedContentBudget.Create(
            ReviewedContentLimits.Production,
            TimeProvider.System);
        for (var index = 0;
             index < ReviewedContentLimits.GitObjectRequests;
             index++)
        {
            Assert.True(requests.TryReserveRequest(CancellationToken.None));
        }

        Assert.False(requests.TryReserveRequest(CancellationToken.None));

        var perResponse = ReviewedContentBudget.Create(
            ReviewedContentLimits.Production,
            TimeProvider.System);
        long responseBytes = 0;
        Assert.True(perResponse.TryConsumeResponseBytes(
            ref responseBytes,
            checked((int)ReviewedContentLimits.GitObjectResponseBytes),
            CancellationToken.None));
        Assert.False(perResponse.TryConsumeResponseBytes(
            ref responseBytes,
            1,
            CancellationToken.None));

        var aggregate = ReviewedContentBudget.Create(
            ReviewedContentLimits.Production,
            TimeProvider.System);
        var responseCount = checked((int)(
            ReviewedContentLimits.AggregateResponseBytes /
            ReviewedContentLimits.GitObjectResponseBytes));
        for (var index = 0; index < responseCount; index++)
        {
            long currentResponse = 0;
            Assert.True(aggregate.TryConsumeResponseBytes(
                ref currentResponse,
                checked((int)ReviewedContentLimits.GitObjectResponseBytes),
                CancellationToken.None));
        }

        long overflowResponse = 0;
        Assert.False(aggregate.TryConsumeResponseBytes(
            ref overflowResponse,
            1,
            CancellationToken.None));
        Assert.True(aggregate.TryGetRemaining(out var remaining));
        Assert.Equal(0, remaining!.ResponseBytes);
    }

    [Fact]
    public void ProductionDeadlineAcceptsLastTickAndRejectsExactTimeout()
    {
        var time = new ManualTimeProvider();
        var budget = ReviewedContentBudget.Create(
            ReviewedContentLimits.Production,
            time);
        time.Advance(
            ReviewedContentLimits.AcquisitionAndMaterializationTimeout -
            TimeSpan.FromTicks(1));
        Assert.True(budget.TryGetRemaining(out var remaining));
        Assert.Equal(TimeSpan.FromTicks(1), remaining!.Time);

        time.Advance(TimeSpan.FromTicks(1));
        Assert.False(budget.TryGetRemaining(out _));
    }

    [Fact]
    public void ExactOrdinalPathDomainAcceptsCaseAndNormalizationPairs()
    {
        const string nfc = "caf\u00e9.txt";
        const string nfd = "cafe\u0301.txt";
        Assert.True(ReviewedTreePath.IsValid("src/A.txt"));
        Assert.True(ReviewedTreePath.IsValid("src/a.txt"));
        Assert.True(ReviewedTreePath.IsValid(nfc));
        Assert.True(ReviewedTreePath.IsValid(nfd));
        Assert.NotEqual(nfc, nfd);
    }

    [Fact]
    public void RepositoryPathByteBoundaryAcceptsCapAndRejectsCapPlusOne()
    {
        Assert.True(ReviewedTreePath.IsValid(
            new string('a', ReviewedContentLimits.PathBytes)));
        Assert.False(ReviewedTreePath.IsValid(
            new string('a', ReviewedContentLimits.PathBytes + 1)));
    }

    public static TheoryData<string> InvalidPaths => new()
    {
        "",
        "/absolute",
        "../escape",
        "a/./b",
        "a//b",
        "a\\b",
        "C:/drive",
        "https://example.test/file",
        ".git/config",
        "src/.GIT/config",
        "trailing. ",
        "bad#name",
        "bad\0name",
        "bad\uD800name",
    };

    [Theory]
    [MemberData(nameof(InvalidPaths))]
    public void InvalidRepositoryPathsFailClosed(string path)
    {
        Assert.False(ReviewedTreePath.IsValid(path));
    }

    [Fact]
    public void ReviewedTreeIdentityIsOrderedStableAndMetadataSensitive()
    {
        var sha = new string('a', 40);
        var blob = new ReviewedStagedBlob(
            Path.GetFullPath("not-exposed"),
            sha,
            3);
        var first = new ReviewedTreePathRecord(
            "b.txt", "100644", ReviewedTreeEntryKind.Regular,
            sha, 3, blob);
        var second = new ReviewedTreePathRecord(
            "a.txt", "120000", ReviewedTreeEntryKind.Symlink,
            sha, null, null);
        var identity = ReviewedTreeIdentityWriter.Create(
            42, 149, new string('b', 40), new string('c', 40),
            ImmutableArray.Create(second, first));
        var reordered = ReviewedTreeIdentityWriter.Create(
            42, 149, new string('b', 40), new string('c', 40),
            ImmutableArray.Create(first, second));
        var changed = ReviewedTreeIdentityWriter.Create(
            42, 149, new string('b', 40), new string('c', 40),
            ImmutableArray.Create(
                second,
                new ReviewedTreePathRecord(
                    "b.txt", "100755", ReviewedTreeEntryKind.Regular,
                    sha, 3, blob)));

        Assert.Equal(64, identity.Sha256.Length);
        Assert.Equal(identity.Sha256, reordered.Sha256);
        Assert.True(identity.CanonicalPreimage.AsSpan().SequenceEqual(
            reordered.CanonicalPreimage.AsSpan()));
        Assert.NotEqual(identity.Sha256, changed.Sha256);
    }

    [Fact]
    public async Task StagingStreamsVerifiesCopiesAndCleansOwnedFiles()
    {
        var parent = CreateTemporaryDirectory();
        try
        {
            var bytes = "raw\0bytes"u8.ToArray();
            var sha = GitBlobSha(bytes);
            var staging = Assert.IsType<ReviewedBlobStagingLease>(
                ReviewedBlobStagingLease.TryCreate(parent));
            await using (var writer = Assert.IsType<ReviewedBlobStageWriter>(
                staging.TryCreateWriter(sha, bytes.Length)))
            {
                Assert.True(await writer.WriteAsync(
                    bytes,
                    CancellationToken.None));
                var blob = Assert.IsType<ReviewedStagedBlob>(
                    await writer.CompleteAsync(CancellationToken.None));
                using var copied = new MemoryStream();
                Assert.True(await blob.CopyVerifiedToAsync(
                    copied,
                    CancellationToken.None));
                Assert.Equal(bytes, copied.ToArray());
            }

            Assert.True(staging.Cleanup());
            Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task WrongDeclaredSizeNeverFinalizesAStagedBlob()
    {
        var parent = CreateTemporaryDirectory();
        try
        {
            var bytes = "content"u8.ToArray();
            var sha = GitBlobSha(bytes);
            var staging = Assert.IsType<ReviewedBlobStagingLease>(
                ReviewedBlobStagingLease.TryCreate(parent));
            await using (var writer = Assert.IsType<ReviewedBlobStageWriter>(
                staging.TryCreateWriter(sha, bytes.Length + 1)))
            {
                Assert.True(await writer.WriteAsync(
                    bytes,
                    CancellationToken.None));
                Assert.Null(await writer.CompleteAsync(
                    CancellationToken.None));
            }

            Assert.True(staging.Cleanup());
            Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task EmptyBlobFinalizesAndExtraByteNeverEntersTheStage()
    {
        var parent = CreateTemporaryDirectory();
        try
        {
            var staging = Assert.IsType<ReviewedBlobStagingLease>(
                ReviewedBlobStagingLease.TryCreate(parent));
            var emptySha = GitBlobSha([]);
            await using (var emptyWriter =
                         Assert.IsType<ReviewedBlobStageWriter>(
                             staging.TryCreateWriter(emptySha, 0)))
            {
                var emptyBlob = Assert.IsType<ReviewedStagedBlob>(
                    await emptyWriter.CompleteAsync(CancellationToken.None));
                using var copied = new MemoryStream();
                Assert.True(await emptyBlob.CopyVerifiedToAsync(
                    copied,
                    CancellationToken.None));
                Assert.Empty(copied.ToArray());
            }

            var content = "two"u8.ToArray();
            await using (var shortWriter =
                         Assert.IsType<ReviewedBlobStageWriter>(
                             staging.TryCreateWriter(
                                 GitBlobSha(content),
                                 content.Length - 1)))
            {
                Assert.False(await shortWriter.WriteAsync(
                    content,
                    CancellationToken.None));
                Assert.Null(await shortWriter.CompleteAsync(
                    CancellationToken.None));
            }

            Assert.True(staging.Cleanup());
            Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task PostStageTamperReturnsNoReviewedBytes()
    {
        var parent = CreateTemporaryDirectory();
        try
        {
            var bytes = "trusted"u8.ToArray();
            var sha = GitBlobSha(bytes);
            var staging = Assert.IsType<ReviewedBlobStagingLease>(
                ReviewedBlobStagingLease.TryCreate(parent));
            ReviewedStagedBlob blob;
            await using (var writer = Assert.IsType<ReviewedBlobStageWriter>(
                staging.TryCreateWriter(sha, bytes.Length)))
            {
                Assert.True(await writer.WriteAsync(
                    bytes,
                    CancellationToken.None));
                blob = Assert.IsType<ReviewedStagedBlob>(
                    await writer.CompleteAsync(CancellationToken.None));
            }

            var stagedPath = Assert.Single(
                Directory.EnumerateFiles(parent, "*.blob",
                    SearchOption.AllDirectories));
            File.SetAttributes(stagedPath, FileAttributes.Normal);
            await File.WriteAllBytesAsync(
                stagedPath,
                "changed"u8.ToArray());
            using var copied = new MemoryStream();
            Assert.False(await blob.CopyVerifiedToAsync(
                copied,
                CancellationToken.None));
            Assert.Empty(copied.ToArray());

            Assert.True(staging.Cleanup());
            Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
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

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "apr-h4-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan time) =>
            _timestamp = checked(_timestamp + time.Ticks);
    }
}
