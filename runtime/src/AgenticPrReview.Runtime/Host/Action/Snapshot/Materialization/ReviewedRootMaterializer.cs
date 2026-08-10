using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgenticPrReview.Runtime.ActionHost.Snapshot.Materialization;

internal enum ReviewedRootFailure
{
    None = 0,
    UnsupportedSize,
    UnsafeRoot,
    IdentityMismatch,
    IoFailure,
    Cancelled,
}

internal sealed class ReviewedMaterializationIdentity
{
    internal ReviewedMaterializationIdentity(
        string sha256,
        ImmutableArray<byte> canonicalPreimage)
    {
        Sha256 = sha256;
        CanonicalPreimage = canonicalPreimage;
    }

    internal string Sha256 { get; }

    internal ImmutableArray<byte> CanonicalPreimage { get; }
}

internal sealed class ReviewedRootMaterializationResult
{
    private ReviewedRootMaterializationResult(
        ReviewedRootLease? lease,
        ReviewedRootFailure failure,
        bool cleanupIncomplete)
    {
        Lease = lease;
        Failure = failure;
        CleanupIncomplete = cleanupIncomplete;
    }

    internal ReviewedRootLease? Lease { get; }

    internal ReviewedRootFailure Failure { get; }

    internal bool CleanupIncomplete { get; }

    internal static ReviewedRootMaterializationResult Success(
        ReviewedRootLease lease) => new(lease, ReviewedRootFailure.None, false);

    internal static ReviewedRootMaterializationResult Failed(
        ReviewedRootFailure failure,
        bool cleanupIncomplete = false) => new(null, failure, cleanupIncomplete);
}

internal sealed class ReviewedRootLease : IAsyncDisposable
{
    private readonly SafeFileHandle _parentHandle;
    private readonly ReviewedStagedFileIdentity _parentIdentity;
    private readonly string _rootName;
    private readonly SafeFileHandle _rootHandle;
    private readonly ReviewedStagedFileIdentity _identity;
    private readonly ReviewedRootMaterializationHooks? _hooks;
    private bool _disposed;

    internal ReviewedRootLease(
        string absoluteRoot,
        SafeFileHandle parentHandle,
        ReviewedStagedFileIdentity parentIdentity,
        string rootName,
        SafeFileHandle rootHandle,
        ReviewedStagedFileIdentity identity,
        IEnumerable<string> regularPaths,
        ReviewedMaterializationIdentity materializationIdentity,
        ReviewedRootMaterializationHooks? hooks)
    {
        AbsoluteRoot = absoluteRoot;
        _parentHandle = parentHandle;
        _parentIdentity = parentIdentity;
        _rootName = rootName;
        _rootHandle = rootHandle;
        _identity = identity;
        _hooks = hooks;
        RegularPaths = regularPaths
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        Identity = materializationIdentity;
    }

    internal string AbsoluteRoot { get; }

    internal ImmutableArray<string> RegularPaths { get; }

    internal ReviewedMaterializationIdentity Identity { get; }

    internal bool CleanupIncomplete { get; private set; }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        try
        {
            _hooks?.BeforeRootCleanup?.Invoke(AbsoluteRoot);
            CleanupIncomplete = !ReviewedRootCleanup.TryDelete(
                _parentHandle,
                Path.GetDirectoryName(AbsoluteRoot)!,
                _parentIdentity,
                _rootName,
                _rootHandle,
                AbsoluteRoot,
                _identity,
                _hooks);
        }
        finally
        {
            _rootHandle.Dispose();
            _parentHandle.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class ReviewedRootMaterializationHooks
{
    internal Action<string>? AfterParentOpen { get; init; }
    internal Action<string>? BeforeRootCreate { get; init; }
    internal Action<string>? BeforeRootCleanup { get; init; }
    internal Action<string>? BeforeChildCleanup { get; init; }
}

internal static class ReviewedRootMaterializer
{
    internal static async Task<ReviewedRootMaterializationResult> MaterializeAsync(
        ReviewedTreeSnapshot tree,
        string absoluteParent,
        CancellationToken cancellationToken,
        ReviewedRootMaterializationHooks? hooks = null,
        long maximumRootBytes = ReviewedContentLimits.MaterializedRootBytes)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (!TryResolveSafeParent(
                absoluteParent,
                out var parent,
                out var parentHandle,
                out var parentIdentity))
        {
            return ReviewedRootMaterializationResult.Failed(
                ReviewedRootFailure.UnsafeRoot);
        }

        try
        {
            hooks?.AfterParentOpen?.Invoke(parent);
            if (!tree.Budget.TryContinue(cancellationToken))
            {
                parentHandle!.Dispose();
                return ReviewedRootMaterializationResult.Failed(
                    ReviewedRootFailure.UnsupportedSize);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            parentHandle!.Dispose();
            return ReviewedRootMaterializationResult.Failed(
                ReviewedRootFailure.Cancelled);
        }

        var regular = tree.Records
            .Where(static record => record.Kind == ReviewedTreeEntryKind.Regular)
            .OrderBy(static record => record.Path, StringComparer.Ordinal)
            .ToImmutableArray();
        if (!TargetPathsAreCollisionFree(regular))
        {
            parentHandle!.Dispose();
            return ReviewedRootMaterializationResult.Failed(
                ReviewedRootFailure.UnsafeRoot);
        }

        var rootBytes = new ReviewedMaterializedRootByteMeter(
            maximumRootBytes);
        foreach (var record in regular)
        {
            if (!rootBytes.TryAdd(record.Size!.Value))
            {
                parentHandle!.Dispose();
                return ReviewedRootMaterializationResult.Failed(
                    ReviewedRootFailure.UnsupportedSize);
            }
        }

        var rootName = "apr-tool-root-" + Guid.NewGuid().ToString("N");
        var root = Path.Join(parent, rootName);
        SafeFileHandle? rootHandle = null;
        ReviewedStagedFileIdentity rootIdentity = default;
        try
        {
            hooks?.BeforeRootCreate?.Invoke(root);
            if (!ReviewedStagedFileAccess.TryCreateChildDirectoryExclusive(
                    parentHandle!,
                    parent,
                    parentIdentity,
                    rootName,
                    out rootHandle,
                    out rootIdentity,
                    out _))
            {
                parentHandle!.Dispose();
                return ReviewedRootMaterializationResult.Failed(
                    ReviewedRootFailure.UnsafeRoot);
            }

            var copied = await CopyFilesAsync(
                tree.Budget,
                root,
                rootHandle!,
                rootIdentity,
                regular,
                cancellationToken);
            if (copied != ReviewedRootFailure.None)
            {
                return FailedWithCleanup(
                    parentHandle!,
                    parent,
                    parentIdentity,
                    rootName,
                    rootHandle!,
                    root,
                    rootIdentity,
                    copied,
                    hooks);
            }

            var budgetContinues = tree.Budget.TryContinue(cancellationToken);
            if (!budgetContinues ||
                !ReviewedStagedFileAccess.DirectoryMatches(root, rootIdentity))
            {
                return FailedWithCleanup(
                    parentHandle!,
                    parent,
                    parentIdentity,
                    rootName,
                    rootHandle!,
                    root,
                    rootIdentity,
                    budgetContinues
                        ? ReviewedRootFailure.UnsafeRoot
                        : ReviewedRootFailure.UnsupportedSize,
                    hooks);
            }

            var materializationIdentity =
                ReviewedMaterializationIdentityWriter.Write(tree, regular);
            if (!tree.Budget.TryContinue(cancellationToken))
            {
                return FailedWithCleanup(
                    parentHandle!,
                    parent,
                    parentIdentity,
                    rootName,
                    rootHandle!,
                    root,
                    rootIdentity,
                    ReviewedRootFailure.UnsupportedSize,
                    hooks);
            }

            var lease = new ReviewedRootLease(
                root,
                parentHandle!,
                parentIdentity,
                rootName,
                rootHandle!,
                rootIdentity,
                regular.Select(static record => record.Path),
                materializationIdentity,
                hooks);
            parentHandle = null;
            rootHandle = null;
            return ReviewedRootMaterializationResult.Success(lease);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            var cleanupComplete = rootHandle is null ||
                ReviewedRootCleanup.TryDelete(
                    parentHandle!,
                    parent,
                    parentIdentity,
                    rootName,
                    rootHandle,
                    root,
                    rootIdentity,
                    hooks);
            rootHandle?.Dispose();
            parentHandle?.Dispose();
            return ReviewedRootMaterializationResult.Failed(
                ReviewedRootFailure.Cancelled,
                !cleanupComplete);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            var cleanupComplete = rootHandle is null ||
                ReviewedRootCleanup.TryDelete(
                    parentHandle!,
                    parent,
                    parentIdentity,
                    rootName,
                    rootHandle,
                    root,
                    rootIdentity,
                    hooks);
            rootHandle?.Dispose();
            parentHandle?.Dispose();
            return ReviewedRootMaterializationResult.Failed(
                ReviewedRootFailure.IoFailure,
                !cleanupComplete);
        }
    }

    private static bool TryResolveSafeParent(
        string candidate,
        out string parent,
        out SafeFileHandle? handle,
        out ReviewedStagedFileIdentity identity)
    {
        parent = string.Empty;
        handle = null;
        identity = default;
        if (string.IsNullOrWhiteSpace(candidate) ||
            !Path.IsPathFullyQualified(candidate))
        {
            return false;
        }

        try
        {
            parent = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(candidate));
            return ReviewedStagedFileAccess.TryOpenDirectory(
                parent,
                out handle,
                out identity);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            parent = string.Empty;
            handle?.Dispose();
            handle = null;
            identity = default;
            return false;
        }
    }

    private static bool TargetPathsAreCollisionFree(
        ImmutableArray<ReviewedTreePathRecord> regular)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var files = new HashSet<string>(comparer);
        var directories = new HashSet<string>(comparer);
        foreach (var record in regular)
        {
            var segments = record.Path.Split('/');
            var prefix = string.Empty;
            for (var index = 0; index < segments.Length - 1; index++)
            {
                prefix = prefix.Length == 0
                    ? segments[index]
                    : prefix + "/" + segments[index];
                if (files.Contains(prefix))
                {
                    return false;
                }

                directories.Add(prefix);
            }

            if (directories.Contains(record.Path) ||
                !files.Add(record.Path))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<ReviewedRootFailure> CopyFilesAsync(
        ReviewedContentBudget budget,
        string root,
        SafeFileHandle rootHandle,
        ReviewedStagedFileIdentity rootIdentity,
        ImmutableArray<ReviewedTreePathRecord> regular,
        CancellationToken cancellationToken)
    {
        var directories = new Dictionary<string, MaterializationDirectory>(
            StringComparer.Ordinal)
        {
            [string.Empty] = new(root, rootHandle, rootIdentity),
        };
        try
        {
            foreach (var record in regular)
            {
                if (!budget.TryContinue(cancellationToken))
                {
                    return ReviewedRootFailure.UnsupportedSize;
                }

                var segments = record.Path.Split('/');
                var relativeParent = string.Empty;
                var current = directories[string.Empty];
                for (var index = 0; index < segments.Length - 1; index++)
                {
                    relativeParent = relativeParent.Length == 0
                        ? segments[index]
                        : relativeParent + "/" + segments[index];
                    if (directories.TryGetValue(
                            relativeParent,
                            out var existing))
                    {
                        current = existing;
                        continue;
                    }

                    if (!ReviewedStagedFileAccess
                            .TryOpenOrCreateChildDirectory(
                                current.Handle,
                                current.Path,
                                current.Identity,
                                segments[index],
                                out var childHandle,
                                out var childIdentity,
                                out var childPath))
                    {
                        return ReviewedRootFailure.UnsafeRoot;
                    }

                    current = new(
                        childPath,
                        childHandle!,
                        childIdentity);
                    directories.Add(relativeParent, current);
                }

                if (!ReviewedStagedFileAccess.TryCreateVisibleFile(
                        current.Handle,
                        current.Path,
                        current.Identity,
                        segments[^1],
                        out var destination))
                {
                    return ReviewedRootFailure.UnsafeRoot;
                }

                var destinationPath = Path.Join(current.Path, segments[^1]);
                await using (destination)
                {
                    var copied = await record.StagedBlob!
                        .CopyVerifiedDetailedAsync(
                            destination!,
                            cancellationToken);
                    if (copied != ReviewedStagedBlobCopyFailure.None)
                    {
                        return copied switch
                        {
                            ReviewedStagedBlobCopyFailure.IdentityMismatch =>
                                ReviewedRootFailure.IdentityMismatch,
                            ReviewedStagedBlobCopyFailure.UnsupportedSize =>
                                ReviewedRootFailure.UnsupportedSize,
                            _ => ReviewedRootFailure.IoFailure,
                        };
                    }

                    await destination!.FlushAsync(cancellationToken);
                    if (!ReviewedStagedFileAccess.TryInspectRegular(
                            destination.SafeFileHandle,
                            out _,
                            out var length) ||
                        length != record.Size ||
                        !TryMakeFileReadOnly(destinationPath, destination))
                    {
                        return ReviewedRootFailure.UnsafeRoot;
                    }
                }
            }

            if (!budget.TryContinue(cancellationToken))
            {
                return ReviewedRootFailure.UnsupportedSize;
            }

            if (OperatingSystem.IsLinux())
            {
                foreach (var directory in directories.Values.OrderByDescending(
                             static value => value.Path.Length))
                {
                    if (!ReviewedStagedFileAccess.TrySetDirectoryWritable(
                            directory.Handle,
                            writable: false))
                    {
                        return ReviewedRootFailure.UnsafeRoot;
                    }
                }
            }
            else if (!TryMakeDirectoriesReadOnly(root))
            {
                return ReviewedRootFailure.UnsafeRoot;
            }

            return ReviewedStagedFileAccess.DirectoryMatches(
                    root,
                    rootIdentity)
                ? ReviewedRootFailure.None
                : ReviewedRootFailure.UnsafeRoot;
        }
        finally
        {
            foreach (var directory in directories.Where(
                         static pair => pair.Key.Length != 0))
            {
                directory.Value.Handle.Dispose();
            }
        }
    }

    private sealed record MaterializationDirectory(
        string Path,
        SafeFileHandle Handle,
        ReviewedStagedFileIdentity Identity);

    private static bool TryMakeFileReadOnly(
        string path,
        FileStream stream)
    {
        if (OperatingSystem.IsLinux())
        {
            return ReviewedStagedFileAccess.TryMakeReadOnly(
                stream.SafeFileHandle);
        }

        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, FileAttributes.ReadOnly);
            return true;
        }

        return false;
    }

    private static bool TryMakeDirectoriesReadOnly(string root)
    {
        if (OperatingSystem.IsLinux())
        {
            foreach (var directory in Directory
                         .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(static path => path.Length))
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }

            File.SetUnixFileMode(
                root,
                UnixFileMode.UserRead | UnixFileMode.UserExecute);
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(root, FileAttributes.ReadOnly);
            return true;
        }

        return false;
    }

    private static ReviewedRootMaterializationResult FailedWithCleanup(
        SafeFileHandle parentHandle,
        string parentPath,
        ReviewedStagedFileIdentity parentIdentity,
        string rootName,
        SafeFileHandle rootHandle,
        string root,
        ReviewedStagedFileIdentity identity,
        ReviewedRootFailure failure,
        ReviewedRootMaterializationHooks? hooks)
    {
        var cleanupComplete = ReviewedRootCleanup.TryDelete(
            parentHandle,
            parentPath,
            parentIdentity,
            rootName,
            rootHandle,
            root,
            identity,
            hooks);
        rootHandle.Dispose();
        parentHandle.Dispose();
        return ReviewedRootMaterializationResult.Failed(
            failure,
            !cleanupComplete);
    }
}

internal sealed class ReviewedMaterializedRootByteMeter
{
    private readonly long _maximumBytes;

    internal ReviewedMaterializedRootByteMeter(
        long maximumBytes = ReviewedContentLimits.MaterializedRootBytes)
    {
        if (maximumBytes < 0 ||
            maximumBytes > ReviewedContentLimits.MaterializedRootBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        _maximumBytes = maximumBytes;
    }

    internal long Bytes { get; private set; }

    internal bool TryAdd(long size)
    {
        if (size < 0 ||
            Bytes > _maximumBytes - size)
        {
            return false;
        }

        Bytes += size;
        return true;
    }
}

internal static class ReviewedMaterializationIdentityWriter
{
    private static readonly byte[] Domain =
        "agentic-pr-review.materialized-root.v1"u8.ToArray();

    internal static ReviewedMaterializationIdentity Write(
        ReviewedTreeSnapshot tree,
        ImmutableArray<ReviewedTreePathRecord> regular)
    {
        using var stream = new MemoryStream();
        WriteFrame(stream, Domain);
        WriteFrame(stream, Encoding.ASCII.GetBytes(tree.Identity.Sha256));
        WriteInt32(stream, regular.Length);
        foreach (var record in regular)
        {
            WriteFrame(stream, Encoding.UTF8.GetBytes(record.Path));
            WriteFrame(stream, Encoding.ASCII.GetBytes(record.Mode));
            WriteFrame(stream, Encoding.ASCII.GetBytes(record.ObjectSha));
            WriteInt64(stream, record.Size!.Value);
        }

        var preimage = stream.ToArray();
        return new ReviewedMaterializationIdentity(
            Convert.ToHexString(SHA256.HashData(preimage)).ToLowerInvariant(),
            ImmutableArray.CreateRange(preimage));
    }

    private static void WriteFrame(Stream stream, ReadOnlySpan<byte> value)
    {
        WriteInt32(stream, value.Length);
        stream.Write(value);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }
}

internal static class ReviewedRootCleanup
{
    internal static bool TryDelete(
        SafeFileHandle parentHandle,
        string parentPath,
        ReviewedStagedFileIdentity parentIdentity,
        string rootName,
        SafeFileHandle rootHandle,
        string rootPath,
        ReviewedStagedFileIdentity expectedIdentity,
        ReviewedRootMaterializationHooks? hooks)
    {
        try
        {
            if (expectedIdentity == default)
            {
                return false;
            }

            if (OperatingSystem.IsLinux())
            {
                if (!ReviewedStagedFileAccess.TryOpenChildDirectory(
                        parentHandle,
                        parentPath,
                        parentIdentity,
                        rootName,
                        out var currentRoot,
                        out var currentIdentity))
                {
                    return false;
                }

                using (currentRoot)
                {
                    if (currentIdentity != expectedIdentity)
                    {
                        return false;
                    }
                }

                if (!ReviewedStagedFileAccess.TrySetDirectoryWritable(
                        rootHandle,
                        writable: true) ||
                    !TryDeleteDirectoryContents(
                        rootHandle,
                        rootPath,
                        expectedIdentity,
                        hooks))
                {
                    return false;
                }

                if (!ReviewedStagedFileAccess.TryOpenChildDirectory(
                        parentHandle,
                        parentPath,
                        parentIdentity,
                        rootName,
                        out currentRoot,
                        out currentIdentity))
                {
                    return false;
                }

                using (currentRoot)
                {
                    if (currentIdentity != expectedIdentity)
                    {
                        return false;
                    }
                }

                return ReviewedStagedFileAccess.TryRemoveChild(
                    parentHandle,
                    parentPath,
                    parentIdentity,
                    rootName,
                    directory: true);
            }

            return TryDeletePath(rootPath, expectedIdentity);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryDeleteDirectoryContents(
        SafeFileHandle directoryHandle,
        string displayPath,
        ReviewedStagedFileIdentity identity,
        ReviewedRootMaterializationHooks? hooks)
    {
        var descriptor = checked((int)directoryHandle.DangerousGetHandle());
        var enumerationPath = "/proc/self/fd/" + descriptor;
        var names = Directory.EnumerateFileSystemEntries(
                enumerationPath,
                "*",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(static name => !string.IsNullOrEmpty(name))
            .ToArray();
        foreach (var name in names)
        {
            var childName = name!;
            var childDisplayPath = Path.Join(displayPath, childName);
            hooks?.BeforeChildCleanup?.Invoke(childDisplayPath);
            if (ReviewedStagedFileAccess.TryOpenChildDirectory(
                    directoryHandle,
                    enumerationPath,
                    identity,
                    childName,
                    out var childHandle,
                    out var childIdentity))
            {
                using (childHandle)
                {
                    if (!ReviewedStagedFileAccess.TrySetDirectoryWritable(
                            childHandle!,
                            writable: true) ||
                        !TryDeleteDirectoryContents(
                            childHandle!,
                            childDisplayPath,
                            childIdentity,
                            hooks))
                    {
                        return false;
                    }
                }

                if (!ReviewedStagedFileAccess.TryOpenChildDirectory(
                        directoryHandle,
                        enumerationPath,
                        identity,
                        childName,
                        out var currentChild,
                        out var currentIdentity))
                {
                    return false;
                }

                using (currentChild)
                {
                    if (currentIdentity != childIdentity)
                    {
                        return false;
                    }
                }

                if (!ReviewedStagedFileAccess.TryRemoveChild(
                        directoryHandle,
                        enumerationPath,
                        identity,
                        childName,
                        directory: true))
                {
                    return false;
                }

                continue;
            }

            if (!ReviewedStagedFileAccess.TryRemoveChild(
                    directoryHandle,
                    enumerationPath,
                    identity,
                    childName,
                    directory: false))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryDeletePath(
        string root,
        ReviewedStagedFileIdentity expectedIdentity)
    {
        try
        {
            if (!ReviewedStagedFileAccess.DirectoryMatches(
                    root,
                    expectedIdentity))
            {
                return false;
            }

            MakeWritable(root);
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         root,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (!TryDeleteEntry(entry))
                {
                    return false;
                }
            }

            Directory.Delete(root, recursive: false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryDeleteEntry(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.Delete(path, recursive: false);
            }
            else
            {
                File.Delete(path);
            }

            return true;
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            MakeWritable(path);
            foreach (var child in Directory.EnumerateFileSystemEntries(
                         path,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (!TryDeleteEntry(child))
                {
                    return false;
                }
            }

            Directory.Delete(path, recursive: false);
            return true;
        }

        MakeWritable(path);
        File.Delete(path);
        return true;
    }

    private static void MakeWritable(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            var attributes = File.GetAttributes(path);
            File.SetUnixFileMode(
                path,
                (attributes & FileAttributes.Directory) != 0
                    ? UnixFileMode.UserRead |
                        UnixFileMode.UserWrite |
                        UnixFileMode.UserExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        else if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(
                path,
                File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }
    }
}
