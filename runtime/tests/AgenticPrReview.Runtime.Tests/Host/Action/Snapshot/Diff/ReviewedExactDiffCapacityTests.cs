using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.ActionHost.Snapshot.ChangedFiles;
using AgenticPrReview.Runtime.ActionHost.Snapshot.Diff;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Snapshot.Diff;

public sealed partial class ReviewedExactDiffBuilderTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public async Task CanonicalSourceByteBoundariesAreExactAndNeverReturnAPartialSet(
        bool aggregate, int excess)
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var identity = Identity(invocation);
        var target = (aggregate ? AgentLimits.DiffSnapshotBytes : AgentLimits.DiffSourceBytesPerFile) + excess;
        var count = aggregate ? 17 : 1;
        var files = Enumerable.Range(0, count).Select(index =>
        {
            var path = $"capacity-{index:D2}.txt";
            var size = target / count + (index < target % count ? 1 : 0);
            return (Path: path, Bytes: AddedBytesWithCanonicalSize(identity, path, size));
        }).ToArray();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var tree = await H5SnapshotTestSupport.TreeAsync(invocation, parent,
            files.Select(file => Regular(file.Path, file.Bytes)).ToArray());
        using var transport = new ScriptedTransport(invocation.PullRequest.BaseSha,
            new string('e', 40), [], []);
        using var staging = ReviewedBaseBlobStagingLease.TryCreate(parent, tree.Budget);
        Assert.NotNull(staging);
        try
        {
            var resolver = new ReviewedBaseObjectResolver(transport, staging!);
            Assert.Equal(ReviewedSnapshotReadFailure.None,
                await resolver.InitializeAsync(CancellationToken.None));
            var facts = files.Select(file => Fact(file.Path, file.Bytes, "added", additions: 600))
                .ToImmutableArray();
            var result = await new ReviewedExactDiffBuilder(tree.Budget).BuildAsync(identity,
                new ReviewedChangedFileSet(facts, ReviewedChangedFileIdentityWriter.Write(facts)),
                tree, resolver, CancellationToken.None);
            Assert.Equal(0, transport.StageCalls);
            if (excess != 0)
            {
                Assert.Equal(ReviewedSnapshotReadFailure.UnsupportedSize, result.Failure);
                Assert.Null(result.Value);
                return;
            }

            Assert.Equal(ReviewedSnapshotReadFailure.None, result.Failure);
            var built = Assert.IsType<ReviewedDiffBuildSet>(result.Value);
            Assert.Equal(count, built.Changes.Length);
            Assert.Equal(target, built.Changes.Sum(change => change.Source!.CanonicalBytes.Length));
            Assert.All(built.Changes, change =>
            {
                Assert.Equal("available", change.Change.PatchStatus);
                Assert.False(change.Change.SourceTruncated);
                Assert.False(change.Source!.SourceTruncated);
                Assert.Equal(600, change.Change.Additions);
                Assert.Equal(0, change.Change.Deletions);
                Assert.Equal(change.Source.PatchSha256, change.Change.PatchSha256);
                Assert.Contains("é\"\\\u0001", change.Source.Hunks[0].Lines[0].Text);
                Assert.Contains("\\u0001", Encoding.UTF8.GetString(change.Source.CanonicalBytes.AsSpan()));
            });
        }
        finally
        {
            staging?.Dispose();
            await tree.DisposeAsync();
            Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
            Directory.Delete(parent);
        }
    }

    private static byte[] AddedBytesWithCanonicalSize(ReviewedIdentity identity, string path, int target)
    {
        // Every padding byte is ASCII and adds exactly one canonical byte. The
        // prefix exercises UTF-8 plus JSON quote, slash and control expansion.
        var lines = Enumerable.Repeat("é\"\\\u0001", 600).ToArray();
        var source = new ReviewedDiffSource(identity, path, null, "added", false,
            [new ReviewedDiffHunk(0, 0, 1, lines.Length,
                lines.Select((text, index) => new ReviewedDiffLine("addition", null, index + 1, text)))]);
        var remaining = target - source.CanonicalBytes.Length;
        Assert.True(remaining >= 0);
        for (var index = 0; index < lines.Length && remaining > 0; index++)
        {
            var padding = Math.Min(remaining, AgentLimits.DiffLineTextBytes - Encoding.UTF8.GetByteCount(lines[index]));
            lines[index] += new string('x', padding);
            remaining -= padding;
        }
        Assert.Equal(0, remaining);
        return Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
    }
}
