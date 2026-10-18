using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.ActionHost.Snapshot.Materialization;
using AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;
using AgenticPrReview.Runtime.ActionHostVerifierFixture;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.Tests.Host.Action.Snapshot;

namespace AgenticPrReview.Runtime.Tests.Storage;

public sealed class DarwinFactAttribute : FactAttribute
{
    public DarwinFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "Darwin descriptor/ABI control; Linux and Windows retain their own native controls.";
    }
}

public sealed class NativeFileOperationsTests
{
    [Fact]
    public void DarwinImportsRemainRestrictedToFilesystemPrimitives()
    {
        var imports = typeof(DarwinFiles).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Select(method => method.GetCustomAttribute<LibraryImportAttribute>())
            .OfType<LibraryImportAttribute>().ToArray();
        Assert.All(imports, import => Assert.Equal("libc", import.LibraryName));
        Assert.Equal(new[] { "__openat", "__openat", "closedir", "fdopendir", "fdopendir$INODE64", "fstat64", "readdir", "readdir$INODE64", "renameatx_np", "unlinkat" },
            imports.Select(import => import.EntryPoint).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task CommitPublishesCompleteBytesAndConsumesOnlyItsStage()
    {
        using var root = new Root();
        var files = new PhysicalRuntimeFileSystem();
        var bytes = Encoding.UTF8.GetBytes("complete staged bytes");
        var staged = await files.StageAsync(Path.Join(root.Path, "result"), bytes);
        await files.CommitNoReplaceAsync(staged);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(staged.FinalPath));
        Assert.False(File.Exists(staged.TempPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingFileOrDirectoryPreservesDestinationAndSource(bool directory)
    {
        using var root = new Root();
        var files = new PhysicalRuntimeFileSystem();
        var staged = await files.StageAsync(Path.Join(root.Path, "result"), [1, 2, 3]);
        if (directory) Directory.CreateDirectory(staged.FinalPath);
        else await File.WriteAllTextAsync(staged.FinalPath, "competitor");
        await Assert.ThrowsAsync<IOException>(() => files.CommitNoReplaceAsync(staged));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(staged.TempPath));
        if (directory) Assert.Empty(Directory.EnumerateFileSystemEntries(staged.FinalPath));
        else Assert.Equal("competitor", await File.ReadAllTextAsync(staged.FinalPath));
    }

    [Fact]
    public async Task ConcurrentPhysicalPublishersHaveExactlyOneWinnerAndRetainLosingSources()
    {
        using var root = new Root();
        var files = new PhysicalRuntimeFileSystem();
        var stages = await Task.WhenAll(Enumerable.Range(0, 12).Select(index =>
            files.StageAsync(Path.Join(root.Path, "winner"), Encoding.UTF8.GetBytes("candidate-" + index))));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishers = stages.Select((stage, index) => Task.Run(async () =>
        {
            await start.Task;
            try { await files.CommitNoReplaceAsync(stage); return index; }
            catch (IOException) { return -1; }
        })).ToArray();
        start.SetResult();
        var outcomes = await Task.WhenAll(publishers);
        var winner = Assert.Single(outcomes, index => index >= 0);
        Assert.Equal("candidate-" + winner, await File.ReadAllTextAsync(stages[winner].FinalPath));
        for (var index = 0; index < stages.Length; index++)
        {
            if (index == winner) Assert.False(File.Exists(stages[index].TempPath));
            else Assert.Equal("candidate-" + index, await File.ReadAllTextAsync(stages[index].TempPath));
        }
    }

    [Fact]
    public async Task FailedNativeRenamePreservesItsSourceWithoutCreatingTheDestination()
    {
        using var root = new Root();
        var files = new PhysicalRuntimeFileSystem();
        var staged = await files.StageAsync(Path.Join(root.Path, "unused"), [7, 8]);
        var missingParent = staged with { FinalPath = Path.Join(root.Path, "missing", "result") };
        await Assert.ThrowsAsync<IOException>(() => files.CommitNoReplaceAsync(missingParent));
        Assert.Equal(new byte[] { 7, 8 }, await File.ReadAllBytesAsync(staged.TempPath));
        Assert.False(Directory.Exists(Path.Join(root.Path, "missing")));
    }

    [Theory]
    [InlineData("case", "CASE")]
    [InlineData("\u00e9", "e\u0301")]
    public async Task ActualFilesystemAliasesCannotOverwriteAnEarlierCommit(string first, string second)
    {
        using var root = new Root();
        var files = new PhysicalRuntimeFileSystem();
        var original = await files.StageAsync(Path.Join(root.Path, first), [1]);
        await files.CommitNoReplaceAsync(original);
        var other = await files.StageAsync(Path.Join(root.Path, second), [2]);
        if (File.Exists(other.FinalPath))
        {
            await Assert.ThrowsAsync<IOException>(() => files.CommitNoReplaceAsync(other));
            Assert.Equal(new byte[] { 2 }, await File.ReadAllBytesAsync(other.TempPath));
        }
        else
        {
            await files.CommitNoReplaceAsync(other);
            Assert.Equal(new byte[] { 2 }, await File.ReadAllBytesAsync(other.FinalPath));
        }
        Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(original.FinalPath));
    }

    [DarwinFact]
    public async Task SymlinkAndDanglingDestinationsNeverReplaceTheirEntriesOrTargets()
    {
        using var root = new Root();
        var files = new PhysicalRuntimeFileSystem();
        foreach (var exists in new[] { false, true })
        {
            var target = Path.Join(root.Path, "target-" + exists);
            if (exists) await File.WriteAllTextAsync(target, "target");
            var staged = await files.StageAsync(Path.Join(root.Path, "link-" + exists), [3]);
            File.CreateSymbolicLink(staged.FinalPath, target);
            await Assert.ThrowsAsync<IOException>(() => files.CommitNoReplaceAsync(staged));
            Assert.Equal(target, new FileInfo(staged.FinalPath).LinkTarget);
            Assert.Equal(new byte[] { 3 }, await File.ReadAllBytesAsync(staged.TempPath));
            if (exists) Assert.Equal("target", await File.ReadAllTextAsync(target));
            else Assert.False(File.Exists(target));
        }
    }

    [DarwinFact]
    public void DarwinExclusiveCreationUsesTheRequestedPrivateMode()
    {
        using var root = new Root();
        Assert.True(ReviewedStagedFileAccess.TryOpenDirectory(root.Path, out var directory, out _));
        using (directory)
        {
            for (var index = 0; index < 16; index++)
            {
                using var file = DarwinFiles.OpenChild(directory!, "private-" + index, create: true);
                Assert.Equal(0, DarwinFiles.Stat(checked((int)file.DangerousGetHandle()), out var info));
                Assert.Equal(0x180, info.Mode & 0x1ff);
                Assert.Equal(1, info.Links);
            }
        }
    }

    [DarwinFact]
    public void DarwinMetadataTracksActualLengthIdentityAndHardLinks()
    {
        using var root = new Root();
        var file = Path.Join(root.Path, "source");
        File.WriteAllText(file, "12345");
        Assert.Equal(RestrictedStateOpenResult.Success, NativeRestrictedStateFiles.OpenFileNoFollow(file, out var handle));
        using (handle)
        {
            Assert.Equal(0, DarwinFiles.Stat(checked((int)handle!.DangerousGetHandle()), out var before));
            Assert.Equal(5, before.Size);
            Assert.Equal(0x8000, before.Mode & 0xf000);
            Assert.Equal(1, before.Links);
            Assert.True(ReplayFileLinks.IsSingleLink(handle));
            var link = Path.Join(root.Path, "hardlink");
            Assert.Equal(0, Link(file, link));
            Assert.Equal(0, DarwinFiles.Stat(checked((int)handle.DangerousGetHandle()), out var after));
            Assert.Equal(before.Device, after.Device);
            Assert.Equal(before.Inode, after.Inode);
            Assert.Equal(2, after.Links);
            Assert.False(ReplayFileLinks.IsSingleLink(handle));
            Assert.Equal((ulong)2, EvidenceFileHandle.Identity(handle).Links);
        }
    }

    [DarwinFact]
    public async Task DarwinNoFollowRejectsLeafAndAncestorLinksAndNeverBlocksOnFifo()
    {
        using var root = new Root();
        var directory = Path.Join(root.Path, "directory");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Join(directory, "file"), "safe");
        File.CreateSymbolicLink(Path.Join(root.Path, "leaf"), Path.Join(directory, "file"));
        Directory.CreateSymbolicLink(Path.Join(root.Path, "ancestor"), directory);
        foreach (var path in new[] { Path.Join(root.Path, "leaf"), Path.Join(root.Path, "ancestor", "file") })
        {
            Assert.Equal(RestrictedStateOpenResult.Unsafe, NativeRestrictedStateFiles.OpenFileNoFollow(path, out var rejected));
            Assert.Null(rejected);
        }
        var fifo = Path.Join(root.Path, "fifo");
        Assert.Equal(0, MakeFifo(fifo, 0x180));
        await Task.Run(() =>
        {
            Assert.Equal(RestrictedStateOpenResult.Success, NativeRestrictedStateFiles.OpenFileNoFollow(fifo, out var opened));
            using (opened) Assert.False(NativeRestrictedStateFiles.TryGetIdentity(opened!, false, out _));
            using var evidence = EvidenceFileHandle.OpenNoFollow(fifo);
            Assert.Throws<InvalidDataException>(() => EvidenceFileHandle.Identity(evidence));
        }).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [DarwinFact]
    public void RetainedDirectoryEnumerationAndRemovalCannotFollowAReplacementRoot()
    {
        using var parent = new Root();
        var root = Path.Join(parent.Path, "root");
        var displaced = Path.Join(parent.Path, "displaced");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Join(root, "original"), "owned");
        Assert.True(ReviewedStagedFileAccess.TryOpenDirectory(root, out var held, out var identity));
        using (held)
        {
            Directory.Move(root, displaced);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Join(root, "competitor"), "outside");
            Assert.False(ReviewedStagedFileAccess.DirectoryMatches(root, identity));
            Assert.Equal(new[] { "original" }, DarwinFiles.EnumerateNames(held!).ToArray());
            using (var original = DarwinFiles.OpenChild(held!, "original"))
                Assert.Equal(5, RandomAccess.GetLength(original));
            DarwinFiles.DeleteChild(held!, "original");
            Assert.Empty(DarwinFiles.EnumerateNames(held!));
            Assert.Equal("outside", File.ReadAllText(Path.Join(root, "competitor")));
        }
    }

    [DarwinFact]
    public async Task MaterializationRejectsPhysicalDirectoryAliasesWithoutMergingLogicalTrees()
    {
        foreach (var pair in new[] { ("case", "CASE"), ("\u00e9", "e\u0301") })
        {
            using var root = new Root();
            var probe = Path.Join(root.Path, "probe");
            Directory.CreateDirectory(Path.Join(probe, pair.Item1));
            var aliases = Directory.Exists(Path.Join(probe, pair.Item2));
            Directory.Delete(probe, recursive: true);
            var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
            var tree = await H5SnapshotTestSupport.TreeAsync(invocation, root.Path,
                new H5HeadEntry(pair.Item1 + "/first", "100644", ReviewedTreeEntryKind.Regular, "first"u8.ToArray()),
                new H5HeadEntry(pair.Item2 + "/second", "100644", ReviewedTreeEntryKind.Regular, "second"u8.ToArray()));
            try
            {
                var result = await ReviewedRootMaterializer.MaterializeAsync(tree, root.Path, CancellationToken.None);
                if (aliases)
                {
                    if (result.Lease is not null) await result.Lease.DisposeAsync();
                    Assert.Equal(ReviewedRootFailure.UnsafeRoot, result.Failure);
                    Assert.Null(result.Lease);
                }
                else
                {
                    var lease = Assert.IsType<ReviewedRootLease>(result.Lease);
                    await lease.DisposeAsync();
                    Assert.False(lease.CleanupIncomplete);
                }
            }
            finally { await tree.DisposeAsync(); }
            Assert.Empty(Directory.EnumerateDirectories(root.Path, "apr-tool-root-*"));
        }
    }

    [DarwinFact]
    [SupportedOSPlatform("macos")]
    public void DarwinProtectionChangesTheRetainedFileAndDirectoryModes()
    {
        using var parent = new Root();
        Assert.True(ReviewedStagedFileAccess.TryOpenDirectory(parent.Path, out var held, out var identity));
        using (held)
        {
            Assert.True(ReviewedStagedFileAccess.TryCreateVisibleFile(held!, parent.Path, identity, "file", out var stream));
            using (stream) Assert.True(ReviewedStagedFileAccess.TryMakeReadOnly(stream!.SafeFileHandle));
            Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(Path.Join(parent.Path, "file")));
            Assert.True(ReviewedStagedFileAccess.TrySetDirectoryWritable(held!, false));
            try
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserExecute, File.GetUnixFileMode(parent.Path));
                Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(Path.Join(parent.Path, "new"), "denied"));
            }
            finally { Assert.True(ReviewedStagedFileAccess.TrySetDirectoryWritable(held!, true)); }
        }
    }

    [Fact]
    public async Task SyntheticPrimaryBucketAllocationsAreExclusiveAcrossProcesses()
    {
        using var parent = new Root();
        var evidence = Path.Join(parent.Path, "evidence");
        var scenario = Path.Join(evidence, "scenario");
        Directory.CreateDirectory(scenario);
        using var owner = FrameworkPrimaryRateLimitBucket.Initialize(evidence, 200);
        Process Start()
        {
            var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(typeof(FrameworkSupervisor).Assembly.Location);
            info.ArgumentList.Add("primary-bucket-probe");
            info.ArgumentList.Add(scenario);
            info.ArgumentList.Add("40");
            return Process.Start(info)!;
        }
        using var first = Start();
        using var second = Start();
        try
        {
            await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(first.ExitCode == 0, await first.StandardError.ReadToEndAsync());
            Assert.True(second.ExitCode == 0, await second.StandardError.ReadToEndAsync());
            Assert.Equal(120, owner.ReadRemaining());
        }
        finally
        {
            if (!first.HasExited) first.Kill(entireProcessTree: true);
            if (!second.HasExited) second.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task SyntheticPrimaryBucketReleasesLockAfterInvalidStateAndDoesNotRetryMissingFile()
    {
        using var parent = new Root();
        var evidence = Path.Join(parent.Path, "evidence");
        Directory.CreateDirectory(evidence);
        using var owner = FrameworkPrimaryRateLimitBucket.Initialize(evidence, 100);
        var path = Path.Join(parent.Path, ".apr-r4-synthetic-primary-bucket.bin");
        File.WriteAllBytes(path, [255, 255, 255, 255]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ObserveAsync(true, CancellationToken.None).AsTask());
        File.WriteAllBytes(path, [100, 0, 0, 0]);
        Assert.Equal(100, owner.ReadRemaining());
        File.Delete(path);
        await Assert.ThrowsAsync<IOException>(() => Task.Run(owner.ReadRemaining).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [DarwinFact]
    public async Task SyntheticPrimaryBucketCancellationWhileLockedDoesNotCharge()
    {
        using var parent = new Root();
        var evidence = Path.Join(parent.Path, "evidence");
        Directory.CreateDirectory(evidence);
        using var owner = FrameworkPrimaryRateLimitBucket.Initialize(evidence, 100);
        var path = Path.Join(parent.Path, ".apr-r4-synthetic-primary-bucket.bin");
        Assert.Equal(RestrictedStateOpenResult.Success, NativeRestrictedStateFiles.OpenFileNoFollow(path, out var held));
        using (held)
        {
            Assert.Equal(0, Flock(held!, 2 | 4));
            try
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.ObserveAsync(true, cancellation.Token).AsTask());
            }
            finally { Assert.Equal(0, Flock(held!, 8)); }
        }
        Assert.Equal(100, owner.ReadRemaining());
        Assert.Equal(99, await owner.ObserveAsync(true, CancellationToken.None));
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string existing, [MarshalAs(UnmanagedType.LPUTF8Str)] string link);
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int operation);

    private sealed class Root : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Join(OperatingSystem.IsMacOS() ? "/private/tmp" : System.IO.Path.GetTempPath(), "apr-native-" + Guid.NewGuid().ToString("N"));
        internal Root() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
