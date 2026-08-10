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
    public async Task ChangedCopiedAndRenameSwapUseExactHistoricalOperands()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var changedBase = "changed-old\n"u8.ToArray();
        var changedHead = "changed-new\n"u8.ToArray();
        var copiedBase = "copied-old\n"u8.ToArray();
        var copiedHead = "copied-new\n"u8.ToArray();
        var swapA = "swap-a\n"u8.ToArray();
        var swapB = "swap-b\n"u8.ToArray();
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            Regular("changed.txt", changedHead),
            Regular("copied.txt", copiedHead),
            Regular("a.txt", swapB),
            Regular("b.txt", swapA));
        var transport = new ScriptedTransport(
            invocation.PullRequest.BaseSha,
            new string('9', 40),
            [
                BaseEntry("changed.txt", changedBase),
                BaseEntry("copy-source.txt", copiedBase),
                BaseEntry("a.txt", swapA),
                BaseEntry("b.txt", swapB),
            ],
            [changedBase, copiedBase, swapA, swapB]);
        using var staging = ReviewedBaseBlobStagingLease.TryCreate(
            parent,
            tree.Budget);
        Assert.NotNull(staging);
        try
        {
            var resolver = new ReviewedBaseObjectResolver(transport, staging!);
            Assert.Equal(
                ReviewedSnapshotReadFailure.None,
                await resolver.InitializeAsync(CancellationToken.None));
            var facts = new[]
            {
                Fact("changed.txt", changedHead, "changed"),
                Fact(
                    "copied.txt",
                    copiedHead,
                    "copied",
                    previousPath: "copy-source.txt"),
                Fact(
                    "a.txt",
                    swapB,
                    "renamed",
                    previousPath: "b.txt"),
                Fact(
                    "b.txt",
                    swapA,
                    "renamed",
                    previousPath: "a.txt"),
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
            Assert.All(built.Changes, static change =>
                Assert.Equal("available", change.Change.PatchStatus));
            Assert.Equal((1, 1), Counts(built, "changed.txt"));
            Assert.Equal((1, 1), Counts(built, "copied.txt"));
            Assert.Equal((0, 0), Counts(built, "a.txt"));
            Assert.Equal((0, 0), Counts(built, "b.txt"));
            Assert.Equal(4, transport.StageCalls);
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
                    patch: "@@ -0,0 +1 @@\n+different\n"),
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
    public async Task RegularSymlinkAndSubmoduleTransitionsStayUnavailable()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var fromSymlink = "from-symlink\n"u8.ToArray();
        var fromSubmodule = "from-submodule\n"u8.ToArray();
        var oldForSymlink = "old-regular-one\n"u8.ToArray();
        var oldForSubmodule = "old-regular-two\n"u8.ToArray();
        var symlinkSha = new string('c', 40);
        var submoduleSha = new string('d', 40);
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            Regular("regular-from-symlink.txt", fromSymlink),
            Regular("regular-from-submodule.txt", fromSubmodule),
            new H5HeadEntry(
                "symlink-from-regular.txt",
                "120000",
                ReviewedTreeEntryKind.Symlink,
                null,
                symlinkSha),
            new H5HeadEntry(
                "submodule-from-regular.txt",
                "160000",
                ReviewedTreeEntryKind.Submodule,
                null,
                submoduleSha));
        var transport = new ScriptedTransport(
            invocation.PullRequest.BaseSha,
            new string('8', 40),
            [
                new(
                    "regular-from-symlink.txt",
                    "120000",
                    "blob",
                    new string('e', 40),
                    8),
                new(
                    "regular-from-submodule.txt",
                    "160000",
                    "commit",
                    new string('f', 40),
                    null),
                BaseEntry("symlink-from-regular.txt", oldForSymlink),
                BaseEntry("submodule-from-regular.txt", oldForSubmodule),
            ],
            [oldForSymlink, oldForSubmodule]);
        using var staging = ReviewedBaseBlobStagingLease.TryCreate(
            parent,
            tree.Budget);
        Assert.NotNull(staging);
        try
        {
            var resolver = new ReviewedBaseObjectResolver(transport, staging!);
            Assert.Equal(
                ReviewedSnapshotReadFailure.None,
                await resolver.InitializeAsync(CancellationToken.None));
            var facts = new[]
            {
                Fact(
                    "regular-from-symlink.txt",
                    fromSymlink,
                    "modified"),
                Fact(
                    "regular-from-submodule.txt",
                    fromSubmodule,
                    "modified"),
                new ReviewedPullRequestFileFact(
                    symlinkSha,
                    "symlink-from-regular.txt",
                    null,
                    "modified",
                    0,
                    0,
                    0,
                    null,
                    false),
                new ReviewedPullRequestFileFact(
                    submoduleSha,
                    "submodule-from-regular.txt",
                    null,
                    "modified",
                    0,
                    0,
                    0,
                    null,
                    false),
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
            Assert.Equal(4, built.Changes.Length);
            Assert.All(built.Changes, static change =>
            {
                Assert.Equal("unavailable", change.Change.PatchStatus);
                Assert.Equal(
                    ReviewedUnavailableReason.NonRegular,
                    change.UnavailableReason);
                Assert.Null(change.Source);
            });
        }
        finally
        {
            staging?.Dispose();
            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task PatchEvidenceMustBeCompleteBeforeItCanVetoExactBytes()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var actual = "actual\n"u8.ToArray();
        var partial = "partial\n"u8.ToArray();
        var multiple = "one\ntwo\n"u8.ToArray();
        var bom = "\uFEFFactual\n"u8.ToArray();
        var unterminated = "actual"u8.ToArray();
        var removed = "removed"u8.ToArray();
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            Regular("truncated.txt", actual),
            Regular("terminal-truncated.txt", partial),
            Regular("malformed.txt", actual),
            Regular("overflow.txt", actual),
            Regular("consistent.txt", actual),
            Regular("multiple.txt", multiple),
            Regular("bom.txt", bom),
            Regular("old-zero-positive.txt", actual),
            Regular("new-zero-positive.txt", actual),
            Regular("false-no-newline.txt", actual),
            Regular("true-no-newline.txt", unterminated),
            Regular("contradictory.txt", actual));
        var transport = new ScriptedTransport(
            invocation.PullRequest.BaseSha,
            new string('b', 40),
            [BaseEntry("removed.txt", removed)],
            [removed]);
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
                Fact(
                    "truncated.txt",
                    actual,
                    "added",
                    additions: 1,
                    patch: "@@ -0,0 +1,2 @@\n+actual"),
                Fact(
                    "terminal-truncated.txt",
                    partial,
                    "added",
                    additions: 1,
                    patch: "@@ -0,0 +1 @@\n+part"),
                Fact(
                    "malformed.txt",
                    actual,
                    "added",
                    additions: 1,
                    patch: "@@ -0,0 +1 @@\n?actual"),
                Fact(
                    "overflow.txt",
                    actual,
                    "added",
                    additions: 1,
                    patch: "@@ -0,0 +2147483647 @@\n+actual"),
                Fact(
                    "consistent.txt",
                    actual,
                    "added",
                    additions: 1,
                    patch: "@@ -0,0 +1 @@\n+actual\n"),
                Fact(
                    "multiple.txt",
                    multiple,
                    "added",
                    additions: 2,
                    patch: "@@ -0,0 +1 @@\n+one\n@@ -0,0 +2 @@\n+two\n"),
                Fact(
                    "bom.txt",
                    bom,
                    "added",
                    additions: 1,
                    patch: "@@ -0,0 +1 @@\n+\uFEFFactual\n"),
                Fact(
                    "old-zero-positive.txt",
                    actual,
                    "added",
                    additions: 1,
                    patch: "@@ -0,1 +1 @@\n-wrong\n+different\n"),
                Fact(
                    "new-zero-positive.txt",
                    actual,
                    "added",
                    additions: 1,
                    patch: "@@ -0,0 +0,1 @@\n+different\n"),
                Fact(
                    "false-no-newline.txt",
                    actual,
                    "added",
                    additions: 1,
                    patch: "@@ -0,0 +1 @@\n+different\n\\ No newline at end of file\n"),
                Fact(
                    "true-no-newline.txt",
                    unterminated,
                    "added",
                    additions: 1,
                    patch: "@@ -0,0 +1 @@\n+different\n\\ No newline at end of file\n"),
                Fact(
                    "removed.txt",
                    removed,
                    "removed",
                    deletions: 1,
                    patch: "@@ -1 +0,0 @@\n-different\n\\ No newline at end of file\n"),
                Fact(
                    "contradictory.txt",
                    actual,
                    "added",
                    additions: 1,
                    patch: "@@ -0,0 +1 @@\n+different\n"),
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
                "available",
                built.Changes.Single(change =>
                    change.Change.Path == "truncated.txt").Change.PatchStatus);
            Assert.Equal(
                "available",
                built.Changes.Single(change =>
                    change.Change.Path == "terminal-truncated.txt")
                    .Change.PatchStatus);
            Assert.Equal(
                "available",
                built.Changes.Single(change =>
                    change.Change.Path == "malformed.txt").Change.PatchStatus);
            Assert.Equal(
                "available",
                built.Changes.Single(change =>
                    change.Change.Path == "overflow.txt").Change.PatchStatus);
            Assert.Equal(
                "available",
                built.Changes.Single(change =>
                    change.Change.Path == "consistent.txt").Change.PatchStatus);
            Assert.Equal(
                "available",
                built.Changes.Single(change =>
                    change.Change.Path == "multiple.txt").Change.PatchStatus);
            Assert.Equal(
                "available",
                built.Changes.Single(change =>
                    change.Change.Path == "bom.txt").Change.PatchStatus);
            Assert.Equal(
                "available",
                built.Changes.Single(change =>
                    change.Change.Path == "old-zero-positive.txt")
                    .Change.PatchStatus);
            Assert.Equal(
                "available",
                built.Changes.Single(change =>
                    change.Change.Path == "new-zero-positive.txt")
                    .Change.PatchStatus);
            Assert.Equal(
                "available",
                built.Changes.Single(change =>
                    change.Change.Path == "false-no-newline.txt")
                    .Change.PatchStatus);
            Assert.Equal(
                ReviewedUnavailableReason.PatchContradiction,
                built.Changes.Single(change =>
                    change.Change.Path == "true-no-newline.txt")
                    .UnavailableReason);
            Assert.Equal(
                ReviewedUnavailableReason.PatchContradiction,
                built.Changes.Single(change =>
                    change.Change.Path == "removed.txt")
                    .UnavailableReason);
            Assert.Equal(
                ReviewedUnavailableReason.PatchContradiction,
                built.Changes.Single(change =>
                    change.Change.Path == "contradictory.txt")
                    .UnavailableReason);
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
    public async Task HunkLineLimitSplitsOneThousandAndOneRecords()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var bytes = Encoding.UTF8.GetBytes(string.Concat(
            Enumerable.Range(0, AgentLimits.DiffLinesPerHunk + 1)
                .Select(index => $"line-{index:D4}\n")));
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            Regular("many-lines.txt", bytes));
        var transport = new ScriptedTransport(
            invocation.PullRequest.BaseSha,
            new string('7', 40),
            [],
            []);
        using var staging = ReviewedBaseBlobStagingLease.TryCreate(
            parent,
            tree.Budget);
        Assert.NotNull(staging);
        try
        {
            var resolver = new ReviewedBaseObjectResolver(transport, staging!);
            Assert.Equal(
                ReviewedSnapshotReadFailure.None,
                await resolver.InitializeAsync(CancellationToken.None));
            var facts = ImmutableArray.Create(Fact(
                "many-lines.txt",
                bytes,
                "added"));

            var result = await new ReviewedExactDiffBuilder(tree.Budget)
                .BuildAsync(
                    Identity(invocation),
                    new ReviewedChangedFileSet(
                        facts,
                        ReviewedChangedFileIdentityWriter.Write(facts)),
                    tree,
                    resolver,
                    CancellationToken.None);

            var source = Assert.Single(
                Assert.IsType<ReviewedDiffBuildSet>(result.Value).Changes)
                .Source;
            Assert.NotNull(source);
            Assert.Equal(2, source.Hunks.Length);
            Assert.Equal(
                AgentLimits.DiffLinesPerHunk,
                source.Hunks[0].Lines.Length);
            Assert.Single(source.Hunks[1].Lines);
        }
        finally
        {
            staging?.Dispose();
            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public async Task SeparatedHunksAcceptTheCapAndRejectCapPlusOne(
        int changedLines,
        bool accepted)
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var baseText = new StringBuilder();
        var headText = new StringBuilder();
        for (var change = 0; change < changedLines; change++)
        {
            baseText.AppendLine($"old-{change:D3}");
            headText.AppendLine($"new-{change:D3}");
            for (var context = 0; context < 7; context++)
            {
                var line = $"same-{change:D3}-{context}";
                baseText.AppendLine(line);
                headText.AppendLine(line);
            }
        }

        var baseBytes = Encoding.UTF8.GetBytes(baseText.ToString());
        var headBytes = Encoding.UTF8.GetBytes(headText.ToString());
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            Regular("many-hunks.txt", headBytes));
        var transport = new ScriptedTransport(
            invocation.PullRequest.BaseSha,
            new string('6', 40),
            [BaseEntry("many-hunks.txt", baseBytes)],
            [baseBytes]);
        using var staging = ReviewedBaseBlobStagingLease.TryCreate(
            parent,
            tree.Budget);
        Assert.NotNull(staging);
        try
        {
            var resolver = new ReviewedBaseObjectResolver(transport, staging!);
            Assert.Equal(
                ReviewedSnapshotReadFailure.None,
                await resolver.InitializeAsync(CancellationToken.None));
            var facts = ImmutableArray.Create(Fact(
                "many-hunks.txt",
                headBytes,
                "modified"));

            var result = await new ReviewedExactDiffBuilder(tree.Budget)
                .BuildAsync(
                    Identity(invocation),
                    new ReviewedChangedFileSet(
                        facts,
                        ReviewedChangedFileIdentityWriter.Write(facts)),
                    tree,
                    resolver,
                    CancellationToken.None);

            if (accepted)
            {
                var source = Assert.Single(
                    Assert.IsType<ReviewedDiffBuildSet>(result.Value).Changes)
                    .Source;
                Assert.NotNull(source);
                Assert.Equal(changedLines, source.Hunks.Length);
            }
            else
            {
                Assert.Null(result.Value);
                Assert.Equal(
                    ReviewedSnapshotReadFailure.UnsupportedSize,
                    result.Failure);
            }
        }
        finally
        {
            staging?.Dispose();
            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task OversizedCanonicalDiffSourceReturnsNoPartialSet()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var line = new string('x', AgentLimits.DiffLineTextBytes);
        var bytes = Encoding.UTF8.GetBytes(string.Concat(
            Enumerable.Repeat(line + "\n", 128)));
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            Regular("oversized-source.txt", bytes));
        var transport = new ScriptedTransport(
            invocation.PullRequest.BaseSha,
            new string('5', 40),
            [],
            []);
        using var staging = ReviewedBaseBlobStagingLease.TryCreate(
            parent,
            tree.Budget);
        Assert.NotNull(staging);
        try
        {
            var resolver = new ReviewedBaseObjectResolver(transport, staging!);
            Assert.Equal(
                ReviewedSnapshotReadFailure.None,
                await resolver.InitializeAsync(CancellationToken.None));
            var facts = ImmutableArray.Create(Fact(
                "oversized-source.txt",
                bytes,
                "added"));

            var result = await new ReviewedExactDiffBuilder(tree.Budget)
                .BuildAsync(
                    Identity(invocation),
                    new ReviewedChangedFileSet(
                        facts,
                        ReviewedChangedFileIdentityWriter.Write(facts)),
                    tree,
                    resolver,
                    CancellationToken.None);

            Assert.Null(result.Value);
            Assert.Equal(
                ReviewedSnapshotReadFailure.UnsupportedSize,
                result.Failure);
        }
        finally
        {
            staging?.Dispose();
            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task SharedDeadlineStopsTheLargeAllChangedLoop()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var time = new H5SteppedTimeProvider(TimeSpan.FromSeconds(1));
        var budget = ReviewedSnapshotTestAccess.Budget(
            ReviewedContentLimits.GitObjectRequests,
            ReviewedContentLimits.GitObjectResponseBytes,
            ReviewedContentLimits.AggregateResponseBytes,
            TimeSpan.FromSeconds(100),
            time);
        var bytes = Encoding.UTF8.GetBytes(string.Concat(
            Enumerable.Repeat("new\n", 100_000)));
        var tree = await H5SnapshotTestSupport.TreeWithBudgetAsync(
            invocation,
            parent,
            budget,
            Regular("all-changed.txt", bytes));
        var transport = new ScriptedTransport(
            invocation.PullRequest.BaseSha,
            new string('4', 40),
            [],
            []);
        using var staging = ReviewedBaseBlobStagingLease.TryCreate(
            parent,
            tree.Budget);
        Assert.NotNull(staging);
        try
        {
            var resolver = new ReviewedBaseObjectResolver(transport, staging!);
            Assert.Equal(
                ReviewedSnapshotReadFailure.None,
                await resolver.InitializeAsync(CancellationToken.None));
            var facts = ImmutableArray.Create(Fact(
                "all-changed.txt",
                bytes,
                "added"));
            time.AdvanceOnRead = true;

            var result = await new ReviewedExactDiffBuilder(tree.Budget)
                .BuildAsync(
                    Identity(invocation),
                    new ReviewedChangedFileSet(
                        facts,
                        ReviewedChangedFileIdentityWriter.Write(facts)),
                    tree,
                    resolver,
                    CancellationToken.None);

            Assert.Null(result.Value);
            Assert.Equal(
                ReviewedSnapshotReadFailure.UnsupportedSize,
                result.Failure);
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

    [Fact]
    public async Task ExactBaseDepthAccepts64AndRejects65BeforeTreeRead()
    {
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var budget = ReviewedSnapshotTestAccess.ProductionBudget();
        using var staging = ReviewedBaseBlobStagingLease.TryCreate(
            parent,
            budget);
        Assert.NotNull(staging);
        try
        {
            var acceptedTransport = new DepthTransport(64, "value"u8.ToArray());
            var accepted = new ReviewedBaseObjectResolver(
                acceptedTransport,
                staging!);
            Assert.Equal(
                ReviewedSnapshotReadFailure.None,
                await accepted.InitializeAsync(CancellationToken.None));

            var acceptedResult = await accepted.ResolveAsync(
                DepthTransport.Path(64),
                CancellationToken.None);

            Assert.NotNull(acceptedResult.Value);
            Assert.Equal(
                ReviewedBaseOperandKind.Regular,
                acceptedResult.Value!.Kind);
            Assert.Equal(64, acceptedTransport.TreeCalls);

            var rejectedTransport = new DepthTransport(65, "value"u8.ToArray());
            var rejected = new ReviewedBaseObjectResolver(
                rejectedTransport,
                staging!);
            Assert.Equal(
                ReviewedSnapshotReadFailure.None,
                await rejected.InitializeAsync(CancellationToken.None));

            var rejectedResult = await rejected.ResolveAsync(
                DepthTransport.Path(65),
                CancellationToken.None);

            Assert.Null(rejectedResult.Value);
            Assert.Equal(
                ReviewedSnapshotReadFailure.UnsupportedSize,
                rejectedResult.Failure);
            Assert.Equal(0, rejectedTransport.TreeCalls);
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

    private sealed class DepthTransport : IReviewedSnapshotTransport
    {
        private readonly byte[] _bytes;
        private readonly string _blobSha;
        private readonly Dictionary<string, ActionHostGitTreeObject> _trees;

        internal DepthTransport(int depth, byte[] bytes)
        {
            _bytes = bytes;
            _blobSha = H5SnapshotTestSupport.BlobSha(bytes);
            RootTreeSha = Sha(0);
            _trees = new(StringComparer.Ordinal);
            for (var index = 0; index < depth; index++)
            {
                var current = Sha(index);
                var last = index == depth - 1;
                _trees.Add(
                    current,
                    new(
                        current,
                        [
                            new(
                                "a",
                                last ? "100644" : "040000",
                                last ? "blob" : "tree",
                                last ? _blobSha : Sha(index + 1),
                                last ? bytes.LongLength : null),
                        ]));
            }
        }

        internal string RootTreeSha { get; }
        internal int TreeCalls { get; private set; }

        internal static string Path(int depth) =>
            string.Join('/', Enumerable.Repeat("a", depth));

        public Task<ReviewedSnapshotReadResult<ActionHostGitCommitObject>>
            GetBaseCommitAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ReviewedSnapshotReadResult<
                ActionHostGitCommitObject>.Success(
                new(new string('b', 40), RootTreeSha)));

        public Task<ReviewedSnapshotReadResult<ActionHostGitTreeObject>>
            GetTreeAsync(
                string treeSha,
                CancellationToken cancellationToken)
        {
            TreeCalls++;
            return Task.FromResult(_trees.TryGetValue(treeSha, out var tree)
                ? ReviewedSnapshotReadResult<ActionHostGitTreeObject>.Success(tree)
                : ReviewedSnapshotReadResult<ActionHostGitTreeObject>.Failed(
                    ReviewedSnapshotReadFailure.NotFound));
        }

        public async Task<ReviewedSnapshotReadResult<ReviewedBaseStagedBlob>>
            StageBaseBlobAsync(
                string blobSha,
                long declaredSize,
                ReviewedBaseBlobStagingLease staging,
                CancellationToken cancellationToken)
        {
            if (!StringComparer.Ordinal.Equals(blobSha, _blobSha))
            {
                return ReviewedSnapshotReadResult<ReviewedBaseStagedBlob>.Failed(
                    ReviewedSnapshotReadFailure.NotFound);
            }

            await using var writer = staging.TryCreateWriter(
                blobSha,
                declaredSize);
            Assert.NotNull(writer);
            await writer!.WriteAsync(_bytes, cancellationToken);
            var blob = await writer.CompleteAsync(cancellationToken);
            return ReviewedSnapshotReadResult<ReviewedBaseStagedBlob>.Success(
                Assert.IsType<ReviewedBaseStagedBlob>(blob));
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

        private static string Sha(int value) =>
            Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                    BitConverter.GetBytes(value)))
                .ToLowerInvariant();
    }
}
