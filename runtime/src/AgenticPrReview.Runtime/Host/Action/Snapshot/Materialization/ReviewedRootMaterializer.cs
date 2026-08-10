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
    private readonly ReviewedStagedFileIdentity _identity;
    private bool _disposed;

    internal ReviewedRootLease(
        string absoluteRoot,
        ReviewedStagedFileIdentity identity,
        IEnumerable<string> regularPaths,
        ReviewedMaterializationIdentity materializationIdentity)
    {
        AbsoluteRoot = absoluteRoot;
        _identity = identity;
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
        CleanupIncomplete = !ReviewedRootCleanup.TryDelete(
            AbsoluteRoot,
            _identity);
        return ValueTask.CompletedTask;
    }
}

internal static class ReviewedRootMaterializer
{
    internal static async Task<ReviewedRootMaterializationResult> MaterializeAsync(
        ReviewedTreeSnapshot tree,
        string absoluteParent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (!TryResolveSafeParent(absoluteParent, out var parent))
        {
            return ReviewedRootMaterializationResult.Failed(
                ReviewedRootFailure.UnsafeRoot);
        }

        var regular = tree.Records
            .Where(static record => record.Kind == ReviewedTreeEntryKind.Regular)
            .OrderBy(static record => record.Path, StringComparer.Ordinal)
            .ToImmutableArray();
        if (!TargetPathsAreCollisionFree(regular))
        {
            return ReviewedRootMaterializationResult.Failed(
                ReviewedRootFailure.UnsafeRoot);
        }

        var rootBytes = new ReviewedMaterializedRootByteMeter();
        foreach (var record in regular)
        {
            if (!rootBytes.TryAdd(record.Size!.Value))
            {
                return ReviewedRootMaterializationResult.Failed(
                    ReviewedRootFailure.UnsupportedSize);
            }
        }

        var root = Path.Join(
            parent,
            "apr-tool-root-" + Guid.NewGuid().ToString("N"));
        ReviewedStagedFileIdentity rootIdentity = default;
        var created = false;
        try
        {
            Directory.CreateDirectory(root);
            created = true;
            if (!ReviewedStagedFileAccess.TryOpenDirectory(
                    root,
                    out var rootHandle,
                    out rootIdentity))
            {
                return FailedWithCleanup(
                    root,
                    rootIdentity,
                    ReviewedRootFailure.UnsafeRoot);
            }

            var copied = await CopyFilesAsync(
                root,
                rootHandle!,
                rootIdentity,
                regular,
                cancellationToken);
            if (copied != ReviewedRootFailure.None)
            {
                return FailedWithCleanup(root, rootIdentity, copied);
            }

            if (!TryMakeDirectoriesReadOnly(root) ||
                !ReviewedStagedFileAccess.DirectoryMatches(root, rootIdentity))
            {
                return FailedWithCleanup(
                    root,
                    rootIdentity,
                    ReviewedRootFailure.UnsafeRoot);
            }

            return ReviewedRootMaterializationResult.Success(
                new ReviewedRootLease(
                    root,
                    rootIdentity,
                    regular.Select(static record => record.Path),
                    ReviewedMaterializationIdentityWriter.Write(
                        tree,
                        regular)));
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            var cleanupComplete = !created ||
                ReviewedRootCleanup.TryDelete(root, rootIdentity);
            return ReviewedRootMaterializationResult.Failed(
                ReviewedRootFailure.Cancelled,
                !cleanupComplete);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            var cleanupComplete = !created ||
                ReviewedRootCleanup.TryDelete(root, rootIdentity);
            return ReviewedRootMaterializationResult.Failed(
                ReviewedRootFailure.IoFailure,
                !cleanupComplete);
        }
    }

    private static bool TryResolveSafeParent(
        string candidate,
        out string parent)
    {
        parent = string.Empty;
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
                out var handle,
                out _)
                ? DisposeAndTrue(handle!)
                : false;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            parent = string.Empty;
            return false;
        }
    }

    private static bool DisposeAndTrue(IDisposable value)
    {
        value.Dispose();
        return true;
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
                cancellationToken.ThrowIfCancellationRequested();
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
                    if (!await record.StagedBlob!.CopyVerifiedToAsync(
                            destination!,
                            cancellationToken))
                    {
                        return ReviewedRootFailure.IdentityMismatch;
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

            return ReviewedStagedFileAccess.DirectoryMatches(
                    root,
                    rootIdentity)
                ? ReviewedRootFailure.None
                : ReviewedRootFailure.UnsafeRoot;
        }
        finally
        {
            foreach (var directory in directories.Values)
            {
                directory.Handle.Dispose();
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
        string root,
        ReviewedStagedFileIdentity identity,
        ReviewedRootFailure failure)
    {
        var cleanupComplete = ReviewedRootCleanup.TryDelete(root, identity);
        return ReviewedRootMaterializationResult.Failed(
            failure,
            !cleanupComplete);
    }
}

internal sealed class ReviewedMaterializedRootByteMeter
{
    internal long Bytes { get; private set; }

    internal bool TryAdd(long size)
    {
        if (size < 0 ||
            Bytes > ReviewedContentLimits.MaterializedRootBytes - size)
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
        string root,
        ReviewedStagedFileIdentity expectedIdentity)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return true;
            }

            if (expectedIdentity == default ||
                !ReviewedStagedFileAccess.DirectoryMatches(
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
            MakeWritable(path);
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
