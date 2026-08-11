using System.Runtime.Versioning;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.ActionHost.Snapshot.Materialization;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Snapshot.Materialization;

public sealed class ReviewedRootMaterializerTests
{
    [Fact]
    public void RootByteMeterAcceptsTheCapAndRejectsCapPlusOne()
    {
        var meter = new ReviewedMaterializedRootByteMeter();

        Assert.True(meter.TryAdd(
            ReviewedContentLimits.MaterializedRootBytes));
        Assert.Equal(
            ReviewedContentLimits.MaterializedRootBytes,
            meter.Bytes);
        Assert.False(meter.TryAdd(1));
    }

    [Fact]
    public async Task FileDirectoryPrefixCollisionFailsBeforeRootCreation()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            new H5HeadEntry(
                "conflict",
                "100644",
                ReviewedTreeEntryKind.Regular,
                []),
            new H5HeadEntry(
                "conflict/child",
                "100644",
                ReviewedTreeEntryKind.Regular,
                []));
        try
        {
            var result = await ReviewedRootMaterializer.MaterializeAsync(
                tree,
                parent,
                CancellationToken.None);

            Assert.Null(result.Lease);
            Assert.Equal(ReviewedRootFailure.UnsafeRoot, result.Failure);
            Assert.Empty(Directory.EnumerateDirectories(
                parent,
                "apr-tool-root-*"));
        }
        finally
        {
            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task ReparseStagingParentCannotRedirectTheToolRoot()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var staging = H5SnapshotTestSupport.TemporaryDirectory();
        var target = H5SnapshotTestSupport.TemporaryDirectory();
        var link = Path.Join(
            Path.GetTempPath(),
            "apr-h5-parent-link-" + Guid.NewGuid().ToString("N"));
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            staging,
            new H5HeadEntry(
                "file.txt",
                "100644",
                ReviewedTreeEntryKind.Regular,
                []));
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch (Exception exception) when (exception is IOException or
                UnauthorizedAccessException or
                PlatformNotSupportedException)
            {
                return;
            }

            var result = await ReviewedRootMaterializer.MaterializeAsync(
                tree,
                link,
                CancellationToken.None);

            Assert.Null(result.Lease);
            Assert.Equal(ReviewedRootFailure.UnsafeRoot, result.Failure);
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        finally
        {
            await tree.DisposeAsync();
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            Directory.Delete(staging, recursive: true);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public async Task RootContainsOnlyRegularHeadBlobsAndIdentityIgnoresTempPath()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            new H5HeadEntry(
                "src/file.txt",
                "100644",
                ReviewedTreeEntryKind.Regular,
                "reviewed\n"u8.ToArray()),
            new H5HeadEntry(
                "link",
                "120000",
                ReviewedTreeEntryKind.Symlink,
                null,
                new string('7', 40)),
            new H5HeadEntry(
                "module",
                "160000",
                ReviewedTreeEntryKind.Submodule,
                null,
                new string('6', 40)));
        ReviewedRootLease? first = null;
        ReviewedRootLease? second = null;
        try
        {
            first = Assert.IsType<ReviewedRootLease>(
                (await ReviewedRootMaterializer.MaterializeAsync(
                    tree,
                    parent,
                    CancellationToken.None)).Lease);
            second = Assert.IsType<ReviewedRootLease>(
                (await ReviewedRootMaterializer.MaterializeAsync(
                    tree,
                    parent,
                    CancellationToken.None)).Lease);

            Assert.NotEqual(first.AbsoluteRoot, second.AbsoluteRoot);
            Assert.Equal(first.Identity.Sha256, second.Identity.Sha256);
            Assert.Equal<string>(["src/file.txt"], first.RegularPaths);
            Assert.Equal(
                "reviewed\n",
                await File.ReadAllTextAsync(Path.Join(
                    first.AbsoluteRoot,
                    "src",
                    "file.txt")));
            Assert.False(File.Exists(Path.Join(first.AbsoluteRoot, "link")));
            Assert.False(Directory.Exists(Path.Join(first.AbsoluteRoot, "module")));
            Assert.True((File.GetAttributes(Path.Join(
                    first.AbsoluteRoot,
                    "src",
                    "file.txt")) & FileAttributes.ReadOnly) != 0 ||
                OperatingSystem.IsLinux());

            if (OperatingSystem.IsLinux())
            {
                Assert.Throws<UnauthorizedAccessException>(() =>
                    File.OpenWrite(Path.Join(
                        first.AbsoluteRoot,
                        "src",
                        "file.txt")));
                Assert.Throws<UnauthorizedAccessException>(() =>
                    File.WriteAllText(
                        Path.Join(first.AbsoluteRoot, "new.txt"),
                        "forbidden"));
                Assert.Throws<UnauthorizedAccessException>(() =>
                    File.Move(
                        Path.Join(first.AbsoluteRoot, "src", "file.txt"),
                        Path.Join(first.AbsoluteRoot, "src", "renamed.txt")));
            }

            var firstPath = first.AbsoluteRoot;
            await first.DisposeAsync();
            first = null;
            Assert.False(Directory.Exists(firstPath));
        }
        finally
        {
            if (first is not null)
            {
                await first.DisposeAsync();
            }

            if (second is not null)
            {
                await second.DisposeAsync();
            }

            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task StagedIdentityMismatchExposesNoPartialToolRoot()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            new H5HeadEntry(
                "file.txt",
                "100644",
                ReviewedTreeEntryKind.Regular,
                "expected"u8.ToArray()));
        try
        {
            var record = Assert.Single(tree.Records);
            var stream = ReviewedSnapshotTestAccess.StagedStream(
                record.StagedBlob!);
            RandomAccess.Write(
                stream.SafeFileHandle,
                "tampered"u8,
                0);

            var result = await ReviewedRootMaterializer.MaterializeAsync(
                tree,
                parent,
                CancellationToken.None);

            Assert.Null(result.Lease);
            Assert.Equal(
                ReviewedRootFailure.IdentityMismatch,
                result.Failure);
            Assert.Empty(Directory.EnumerateDirectories(
                parent,
                "apr-tool-root-*"));
        }
        finally
        {
            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task HeadBlobCopyUsesTheFixedStreamingBuffer()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var bytes = new byte[checked((int)ReviewedContentLimits.HeadBlobBytes)];
        Array.Fill<byte>(bytes, 0x5a);
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            new H5HeadEntry(
                "maximum.bin",
                "100644",
                ReviewedTreeEntryKind.Regular,
                bytes));
        try
        {
            var destination = new MeasuringStream();
            Assert.True(await Assert.Single(tree.Records).StagedBlob!
                .CopyVerifiedToAsync(destination, CancellationToken.None));
            Assert.Equal(bytes.LongLength, destination.Length);
            Assert.InRange(
                destination.MaximumWrite,
                1,
                ReviewedContentLimits.StreamBufferBytes);
        }
        finally
        {
            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task ExpiredEmptyRootIsRejectedBeforeCreation()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var time = new H5ManualTimeProvider();
        var budget = ReviewedSnapshotTestAccess.Budget(
            ReviewedContentLimits.GitObjectRequests,
            ReviewedContentLimits.GitObjectResponseBytes,
            ReviewedContentLimits.AggregateResponseBytes,
            ReviewedContentLimits.AcquisitionAndMaterializationTimeout,
            time);
        var tree = await H5SnapshotTestSupport.TreeWithBudgetAsync(
            invocation,
            parent,
            budget);
        try
        {
            time.Advance(
                ReviewedContentLimits.AcquisitionAndMaterializationTimeout);

            var result = await ReviewedRootMaterializer.MaterializeAsync(
                tree,
                parent,
                CancellationToken.None);

            Assert.Null(result.Lease);
            Assert.Equal(ReviewedRootFailure.UnsupportedSize, result.Failure);
            Assert.Empty(Directory.EnumerateDirectories(
                parent,
                "apr-tool-root-*"));
        }
        finally
        {
            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task LinuxCancellationAfterParentOpenDisposesRetainedHandle()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var tree = await H5SnapshotTestSupport.TreeAsync(invocation, parent);
        using var cancellation = new CancellationTokenSource();
        var baseline = CountLinuxHandlesTo(parent);
        try
        {
            var result = await ReviewedRootMaterializer.MaterializeAsync(
                tree,
                parent,
                cancellation.Token,
                new ReviewedRootMaterializationHooks
                {
                    AfterParentOpen = openedParent =>
                    {
                        Assert.Equal(parent, openedParent);
                        Assert.Equal(
                            baseline + 1,
                            CountLinuxHandlesTo(parent));
                        cancellation.Cancel();
                    },
                });

            Assert.Null(result.Lease);
            Assert.Equal(ReviewedRootFailure.Cancelled, result.Failure);
            Assert.Equal(baseline, CountLinuxHandlesTo(parent));
            Assert.Empty(Directory.EnumerateDirectories(
                parent,
                "apr-tool-root-*"));
        }
        finally
        {
            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task RootCapOverflowFailsBeforeRootCreation()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var rootCreateCalled = false;
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            new H5HeadEntry(
                "file.txt",
                "100644",
                ReviewedTreeEntryKind.Regular,
                "123"u8.ToArray()));
        try
        {
            var result = await ReviewedRootMaterializer.MaterializeAsync(
                tree,
                parent,
                CancellationToken.None,
                new ReviewedRootMaterializationHooks
                {
                    BeforeRootCreate = _ => rootCreateCalled = true,
                },
                maximumRootBytes: 2);

            Assert.Null(result.Lease);
            Assert.Equal(ReviewedRootFailure.UnsupportedSize, result.Failure);
            Assert.False(rootCreateCalled);
            Assert.Empty(Directory.EnumerateDirectories(
                parent,
                "apr-tool-root-*"));
        }
        finally
        {
            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task LinuxParentReplacementCannotRedirectRootCreation()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var moved = parent + "-moved";
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            new H5HeadEntry(
                "file.txt",
                "100644",
                ReviewedTreeEntryKind.Regular,
                "private"u8.ToArray()));
        try
        {
            var result = await ReviewedRootMaterializer.MaterializeAsync(
                tree,
                parent,
                CancellationToken.None,
                new ReviewedRootMaterializationHooks
                {
                    BeforeRootCreate = _ =>
                    {
                        Directory.Move(parent, moved);
                        Directory.CreateDirectory(parent);
                    },
                });

            Assert.Null(result.Lease);
            Assert.Equal(ReviewedRootFailure.UnsafeRoot, result.Failure);
            Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
            Assert.Empty(Directory.EnumerateDirectories(
                moved,
                "apr-tool-root-*"));
        }
        finally
        {
            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }

            if (Directory.Exists(moved))
            {
                Directory.Move(moved, parent);
            }

            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task LinuxRootReplacementReportsIncompleteWithoutDeletingIt()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        string? moved = null;
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            new H5HeadEntry(
                "file.txt",
                "100644",
                ReviewedTreeEntryKind.Regular,
                "private"u8.ToArray()));
        var result = await ReviewedRootMaterializer.MaterializeAsync(
            tree,
            parent,
            CancellationToken.None,
            new ReviewedRootMaterializationHooks
            {
                BeforeRootCleanup = root =>
                {
                    moved = root + "-renamed";
                    Directory.Move(root, moved);
                    File.WriteAllText(root, "outside-sentinel");
                },
            });
        var lease = Assert.IsType<ReviewedRootLease>(result.Lease);
        try
        {
            var root = lease.AbsoluteRoot;
            await lease.DisposeAsync();

            Assert.True(lease.CleanupIncomplete);
            Assert.Equal("outside-sentinel", File.ReadAllText(root));
            Assert.NotNull(moved);
            Assert.Equal(
                "private",
                File.ReadAllText(Path.Join(moved!, "file.txt")));
        }
        finally
        {
            var root = lease.AbsoluteRoot;
            if (File.Exists(root))
            {
                File.Delete(root);
            }

            if (moved is not null && Directory.Exists(moved))
            {
                MakeWritableRecursive(moved);
                Directory.Delete(moved, recursive: true);
            }

            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task LinuxDanglingRootLinkReportsIncompleteWithoutFollowingIt()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        string? moved = null;
        string? missing = null;
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            new H5HeadEntry(
                "file.txt",
                "100644",
                ReviewedTreeEntryKind.Regular,
                "private"u8.ToArray()));
        var result = await ReviewedRootMaterializer.MaterializeAsync(
            tree,
            parent,
            CancellationToken.None,
            new ReviewedRootMaterializationHooks
            {
                BeforeRootCleanup = root =>
                {
                    moved = root + "-renamed";
                    missing = root + "-missing";
                    Directory.Move(root, moved);
                    Directory.CreateSymbolicLink(root, missing);
                },
            });
        var lease = Assert.IsType<ReviewedRootLease>(result.Lease);
        try
        {
            await lease.DisposeAsync();

            Assert.True(lease.CleanupIncomplete);
            Assert.Equal(
                missing,
                new DirectoryInfo(lease.AbsoluteRoot).LinkTarget);
            Assert.NotNull(moved);
            Assert.Equal(
                "private",
                File.ReadAllText(Path.Join(moved!, "file.txt")));
        }
        finally
        {
            if (new DirectoryInfo(lease.AbsoluteRoot).LinkTarget is not null)
            {
                File.Delete(lease.AbsoluteRoot);
            }

            if (moved is not null && Directory.Exists(moved))
            {
                MakeWritableRecursive(moved);
                Directory.Delete(moved, recursive: true);
            }

            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task LinuxChildReplacementDoesNotTouchOutsideSentinel()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var outside = H5SnapshotTestSupport.TemporaryDirectory();
        var sentinel = Path.Join(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "outside");
        var replaced = false;
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            new H5HeadEntry(
                "src/file.txt",
                "100644",
                ReviewedTreeEntryKind.Regular,
                "private"u8.ToArray()));
        var result = await ReviewedRootMaterializer.MaterializeAsync(
            tree,
            parent,
            CancellationToken.None,
            new ReviewedRootMaterializationHooks
            {
                BeforeChildCleanup = child =>
                {
                    if (replaced || !child.EndsWith("src", StringComparison.Ordinal))
                    {
                        return;
                    }

                    replaced = true;
                    Directory.Move(child, child + "-away");
                    Directory.CreateSymbolicLink(child, outside);
                },
            });
        var lease = Assert.IsType<ReviewedRootLease>(result.Lease);
        try
        {
            await lease.DisposeAsync();

            Assert.True(lease.CleanupIncomplete);
            Assert.Equal("outside", File.ReadAllText(sentinel));
        }
        finally
        {
            if (Directory.Exists(lease.AbsoluteRoot))
            {
                MakeWritableRecursive(lease.AbsoluteRoot);
                Directory.Delete(lease.AbsoluteRoot, recursive: true);
            }

            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    private static int CountLinuxHandlesTo(string target)
    {
        var canonical = Path.GetFullPath(target);
        var count = 0;
        foreach (var descriptor in Directory.EnumerateFileSystemEntries(
                     "/proc/self/fd"))
        {
            try
            {
                var resolved = File.ResolveLinkTarget(
                    descriptor,
                    returnFinalTarget: false);
                if (resolved is not null &&
                    StringComparer.Ordinal.Equals(
                        Path.GetFullPath(resolved.FullName),
                        canonical))
                {
                    count++;
                }
            }
            catch (Exception exception) when (exception is IOException or
                UnauthorizedAccessException)
            {
            }
        }

        return count;
    }

    [SupportedOSPlatform("linux")]
    private static void MakeWritableRecursive(string root)
    {
        File.SetUnixFileMode(
            root,
            UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        foreach (var directory in Directory.EnumerateDirectories(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
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
            get => Length;
            set => throw new NotSupportedException();
        }

        internal int MaximumWrite { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            MaximumWrite = Math.Max(MaximumWrite, count);
            _length += count;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            MaximumWrite = Math.Max(MaximumWrite, buffer.Length);
            _length += buffer.Length;
            return ValueTask.CompletedTask;
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
}
