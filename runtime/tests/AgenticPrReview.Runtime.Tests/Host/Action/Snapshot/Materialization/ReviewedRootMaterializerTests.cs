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
