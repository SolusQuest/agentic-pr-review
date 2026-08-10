using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.Agent;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Snapshot;

public sealed class ReviewedTreeSnapshotTests
{
    [Fact]
    public void UnknownAuthorityCannotMintBudgetOrStagingLease()
    {
        var authority = new object();

        Assert.Throws<InvalidOperationException>(() =>
            ReviewedContentBudget.Mint(authority, TimeProvider.System));
        Assert.Null(ReviewedBlobStagingLease.TryCreate(
            authority,
            Path.GetFullPath(Path.GetTempPath()),
            ReviewedSnapshotTestAccess.ProductionBudget()));
    }

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
        var budget = ReviewedSnapshotTestAccess.Budget(
            2,
            3,
            5,
            TimeSpan.FromSeconds(10),
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
        var requests = ReviewedSnapshotTestAccess.ProductionBudget();
        for (var index = 0;
             index < ReviewedContentLimits.GitObjectRequests;
             index++)
        {
            Assert.True(requests.TryReserveRequest(CancellationToken.None));
        }

        Assert.False(requests.TryReserveRequest(CancellationToken.None));

        var perResponse = ReviewedSnapshotTestAccess.ProductionBudget();
        long responseBytes = 0;
        Assert.True(perResponse.TryConsumeResponseBytes(
            ref responseBytes,
            checked((int)ReviewedContentLimits.GitObjectResponseBytes),
            CancellationToken.None));
        Assert.False(perResponse.TryConsumeResponseBytes(
            ref responseBytes,
            1,
            CancellationToken.None));

        var aggregate = ReviewedSnapshotTestAccess.ProductionBudget();
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
        var budget = ReviewedSnapshotTestAccess.Budget(
            ReviewedContentLimits.GitObjectRequests,
            ReviewedContentLimits.GitObjectResponseBytes,
            ReviewedContentLimits.AggregateResponseBytes,
            ReviewedContentLimits.AcquisitionAndMaterializationTimeout,
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

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan time) =>
            _timestamp = checked(_timestamp + time.Ticks);
    }
}
