using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using AgenticPrReview.Runtime.Agent.Core;

namespace AgenticPrReview.Runtime.Agent.Tools;

internal sealed class ReviewedSnapshot
{
    private readonly ImmutableHashSet<string> _trackedFiles;
    private readonly ImmutableHashSet<string> _changedFiles;

    internal ReviewedSnapshot(
        ReviewedIdentity identity,
        string absoluteRoot,
        IEnumerable<string> trackedFiles)
        : this(identity, absoluteRoot, trackedFiles, [], [])
    {
    }

    internal ReviewedSnapshot(
        ReviewedIdentity identity,
        string absoluteRoot,
        IEnumerable<string> trackedFiles,
        IEnumerable<ReviewedChangedFile> changedFiles,
        IEnumerable<ReviewedDiffSource> diffSources)
    {
        ArgumentNullException.ThrowIfNull(trackedFiles);
        ArgumentNullException.ThrowIfNull(changedFiles);
        ArgumentNullException.ThrowIfNull(diffSources);
        if (!identity.IsValid())
        {
            throw new ArgumentException("Reviewed identity is invalid.", nameof(identity));
        }

        var root = Path.GetFullPath(absoluteRoot);
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root))
        {
            throw new ArgumentException("Snapshot root must be an existing absolute directory.", nameof(absoluteRoot));
        }

        var builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var strictUtf8 = new UTF8Encoding(false, true);
        var trackedFileCount = 0;
        long trackedFilesMetadataBytes = 0;
        foreach (var trackedFile in trackedFiles)
        {
            trackedFileCount = checked(trackedFileCount + 1);
            if (trackedFileCount > AgentLimits.TrackedFiles)
            {
                throw new ArgumentException(
                    "Tracked file count exceeds the stable snapshot limit.",
                    nameof(trackedFiles));
            }

            if (!RepositoryPath.IsValid(trackedFile))
            {
                throw new ArgumentException("Tracked path is not canonical.", nameof(trackedFiles));
            }

            trackedFilesMetadataBytes = checked(
                trackedFilesMetadataBytes + strictUtf8.GetByteCount(trackedFile));
            if (trackedFilesMetadataBytes > AgentLimits.TrackedFilesMetadataBytes)
            {
                throw new ArgumentException(
                    "Tracked file metadata exceeds the stable snapshot limit.",
                    nameof(trackedFiles));
            }

            builder.Add(trackedFile);
        }

        var trackedSet = builder.ToImmutable();
        var changedByPath = new Dictionary<string, ReviewedChangedFile>(
            StringComparer.Ordinal);
        var changedFileCount = 0;
        long changedFilesMetadataBytes = 0;
        foreach (var changedFile in changedFiles)
        {
            changedFileCount = checked(changedFileCount + 1);
            if (changedFileCount > AgentLimits.ChangedFiles)
            {
                throw new ArgumentException(
                    "Changed file count exceeds the stable snapshot limit.",
                    nameof(changedFiles));
            }

            if (!ReviewedChangedFileValidation.IsShapeValid(changedFile) ||
                !ReviewedChangedFileValidation.MembershipIsValid(
                    changedFile,
                    trackedSet) ||
                !changedByPath.TryAdd(changedFile.Path, changedFile))
            {
                throw new ArgumentException(
                    "Changed file metadata is invalid or incoherent.",
                    nameof(changedFiles));
            }

            changedFilesMetadataBytes = checked(
                changedFilesMetadataBytes +
                ReviewedChangedFileWriter.Write(changedFile).Length);
            if (changedFilesMetadataBytes > AgentLimits.ChangedFilesMetadataBytes)
            {
                throw new ArgumentException(
                    "Changed file metadata exceeds the stable snapshot limit.",
                    nameof(changedFiles));
            }
        }

        var sourceByPath = new Dictionary<string, ReviewedDiffSource>(
            StringComparer.Ordinal);
        var sourceCount = 0;
        long diffSnapshotBytes = 0;
        foreach (var source in diffSources)
        {
            sourceCount = checked(sourceCount + 1);
            if (sourceCount > AgentLimits.ChangedFiles ||
                source is null ||
                source.ReviewedIdentity != identity ||
                !sourceByPath.TryAdd(source.Path, source))
            {
                throw new ArgumentException(
                    "Diff source membership or identity is invalid.",
                    nameof(diffSources));
            }

            diffSnapshotBytes = checked(
                diffSnapshotBytes + source.CanonicalBytes.Length);
            if (diffSnapshotBytes > AgentLimits.DiffSnapshotBytes)
            {
                throw new ArgumentException(
                    "Diff source aggregate exceeds the stable snapshot limit.",
                    nameof(diffSources));
            }
        }

        var availableCount = 0;
        foreach (var change in changedByPath.Values)
        {
            if (change.PatchStatus != "available")
            {
                if (sourceByPath.ContainsKey(change.Path))
                {
                    throw new ArgumentException(
                        "A non-available change has a diff source.",
                        nameof(diffSources));
                }

                continue;
            }

            availableCount = checked(availableCount + 1);
            if (!sourceByPath.TryGetValue(change.Path, out var source) ||
                !StringComparer.Ordinal.Equals(source.Path, change.Path) ||
                !StringComparer.Ordinal.Equals(
                    source.PreviousPath,
                    change.PreviousPath) ||
                !StringComparer.Ordinal.Equals(source.Status, change.Status) ||
                source.SourceTruncated != change.SourceTruncated ||
                !StringComparer.Ordinal.Equals(
                    source.PatchSha256,
                    change.PatchSha256) ||
                !CountsAreCoherent(change, source))
            {
                throw new ArgumentException(
                    "Available change and diff source are incoherent.",
                    nameof(diffSources));
            }
        }

        if (availableCount != sourceByPath.Count)
        {
            throw new ArgumentException(
                "Diff source membership is not one-to-one.",
                nameof(diffSources));
        }

        Identity = identity;
        AbsoluteRoot = Path.TrimEndingDirectorySeparator(root);
        _trackedFiles = trackedSet;
        OrderedTrackedFiles = _trackedFiles.Order(StringComparer.Ordinal).ToImmutableArray();
        OrderedChangedFiles = changedByPath.Values
            .OrderBy(change => change.Path, StringComparer.Ordinal)
            .ToImmutableArray();
        _changedFiles = OrderedChangedFiles
            .Select(change => change.Path)
            .ToImmutableHashSet(StringComparer.Ordinal);
        DiffByChangedPath = sourceByPath.ToImmutableDictionary(StringComparer.Ordinal);
    }

    internal ReviewedIdentity Identity { get; }

    internal string AbsoluteRoot { get; }

    internal ImmutableArray<string> OrderedTrackedFiles { get; }

    internal ImmutableArray<ReviewedChangedFile> OrderedChangedFiles { get; }

    internal ImmutableDictionary<string, ReviewedDiffSource> DiffByChangedPath { get; }

    internal bool Contains(string path) => _trackedFiles.Contains(path);

    internal bool ContainsChangedPath(string path) => _changedFiles.Contains(path);

    internal bool TryGetChangedFile(
        string path,
        out ReviewedChangedFile change)
    {
        var low = 0;
        var high = OrderedChangedFiles.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            var comparison = StringComparer.Ordinal.Compare(
                OrderedChangedFiles[middle].Path,
                path);
            if (comparison < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        if (low < OrderedChangedFiles.Length &&
            StringComparer.Ordinal.Equals(OrderedChangedFiles[low].Path, path))
        {
            change = OrderedChangedFiles[low];
            return true;
        }

        change = null!;
        return false;
    }

    internal bool TryGetDiffSource(
        string path,
        out ReviewedDiffSource source) =>
        DiffByChangedPath.TryGetValue(path, out source!);

    private static bool CountsAreCoherent(
        ReviewedChangedFile change,
        ReviewedDiffSource source) =>
        source.SourceTruncated
            ? source.RepresentedAdditions <= change.Additions &&
                source.RepresentedDeletions <= change.Deletions
            : source.RepresentedAdditions == change.Additions &&
                source.RepresentedDeletions == change.Deletions;
}

internal static class RepositoryPath
{
    internal static bool IsValid(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] == '/')
        {
            return false;
        }

        try
        {
            if (new System.Text.UTF8Encoding(false, true).GetByteCount(path) >
                AgentLimits.PathBytes)
            {
                return false;
            }
        }
        catch (System.Text.EncoderFallbackException)
        {
            return false;
        }

        foreach (var character in path)
        {
            if (character is <= '\u001f' or '\u007f' ||
                character is '\\' or ':' or '?' or '#' or '*' or '"' or
                    '<' or '>' or '|')
            {
                return false;
            }
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 ||
                segment is "." or ".." ||
                segment[^1] is '.' or ' ')
            {
                return false;
            }
        }

        return true;
    }
}

internal enum ReviewedFileAccessStatus
{
    Success,
    Unsafe,
    IoFailure,
}

internal readonly record struct ReviewedFileIdentity(
    ulong Device,
    ulong File);

internal sealed record ReviewedFileProbe(
    ReviewedFileAccessStatus Status,
    long Length,
    ReviewedFileIdentity Identity)
{
    internal static ReviewedFileProbe Unsafe() =>
        new(ReviewedFileAccessStatus.Unsafe, 0, default);

    internal static ReviewedFileProbe IoFailure() =>
        new(ReviewedFileAccessStatus.IoFailure, 0, default);
}

internal sealed record ReviewedFileMetadata(
    ReviewedFileAccessStatus Status,
    long Length)
{
    internal static ReviewedFileMetadata Unsafe() =>
        new(ReviewedFileAccessStatus.Unsafe, 0);

    internal static ReviewedFileMetadata IoFailure() =>
        new(ReviewedFileAccessStatus.IoFailure, 0);
}

internal sealed record ReviewedFileRead(
    ReviewedFileAccessStatus Status,
    byte[]? Bytes)
{
    internal static ReviewedFileRead Unsafe() =>
        new(ReviewedFileAccessStatus.Unsafe, null);

    internal static ReviewedFileRead IoFailure() =>
        new(ReviewedFileAccessStatus.IoFailure, null);
}

internal interface IReviewedFileAccess
{
    ReviewedFileMetadata InspectMetadata(ReviewedSnapshot snapshot, string path);

    ReviewedFileProbe Probe(ReviewedSnapshot snapshot, string path);

    ValueTask<ReviewedFileRead> ReadAsync(
        ReviewedSnapshot snapshot,
        string path,
        ReviewedFileProbe expected,
        CancellationToken cancellationToken);
}

internal sealed class VerifiedReviewedFileAccess : IReviewedFileAccess
{
    public ReviewedFileMetadata InspectMetadata(
        ReviewedSnapshot snapshot,
        string path)
    {
        if (!TryResolveSafePath(snapshot, path, out var fullPath))
        {
            return ReviewedFileMetadata.Unsafe();
        }

        try
        {
            var length = new FileInfo(fullPath).Length;
            return length >= 0
                ? new ReviewedFileMetadata(
                    ReviewedFileAccessStatus.Success,
                    length)
                : ReviewedFileMetadata.Unsafe();
        }
        catch (UnauthorizedAccessException)
        {
            return ReviewedFileMetadata.IoFailure();
        }
        catch (IOException)
        {
            return ReviewedFileMetadata.IoFailure();
        }
    }

    public ReviewedFileProbe Probe(ReviewedSnapshot snapshot, string path)
    {
        if (!TryResolveSafePath(snapshot, path, out var fullPath))
        {
            return ReviewedFileProbe.Unsafe();
        }

        try
        {
            var openStatus = OpenRead(fullPath, out var openedHandle);
            if (openStatus != ReviewedFileAccessStatus.Success)
            {
                return openStatus == ReviewedFileAccessStatus.Unsafe
                    ? ReviewedFileProbe.Unsafe()
                    : ReviewedFileProbe.IoFailure();
            }

            using var handle = openedHandle!;
            if (!TryInspectRegular(handle, out var identity, out var length) ||
                !TryResolveSafePath(snapshot, path, out _))
            {
                return ReviewedFileProbe.Unsafe();
            }

            return new ReviewedFileProbe(
                ReviewedFileAccessStatus.Success,
                length,
                identity);
        }
        catch (UnauthorizedAccessException)
        {
            return ReviewedFileProbe.IoFailure();
        }
        catch (IOException)
        {
            return ReviewedFileProbe.IoFailure();
        }
    }

    public async ValueTask<ReviewedFileRead> ReadAsync(
        ReviewedSnapshot snapshot,
        string path,
        ReviewedFileProbe expected,
        CancellationToken cancellationToken)
    {
        if (expected.Status != ReviewedFileAccessStatus.Success ||
            !TryResolveSafePath(snapshot, path, out var fullPath))
        {
            return ReviewedFileRead.Unsafe();
        }

        try
        {
            var openStatus = OpenRead(fullPath, out var openedHandle);
            if (openStatus != ReviewedFileAccessStatus.Success)
            {
                return openStatus == ReviewedFileAccessStatus.Unsafe
                    ? ReviewedFileRead.Unsafe()
                    : ReviewedFileRead.IoFailure();
            }

            using var handle = openedHandle!;
            if (!TryInspectRegular(handle, out var openedIdentity, out var length) ||
                openedIdentity != expected.Identity ||
                length != expected.Length ||
                length > int.MaxValue)
            {
                return ReviewedFileRead.Unsafe();
            }

            var bytes = new byte[(int)length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = await RandomAccess.ReadAsync(
                    handle,
                    bytes.AsMemory(offset),
                    offset,
                    cancellationToken);
                if (read == 0)
                {
                    return ReviewedFileRead.Unsafe();
                }

                offset += read;
            }

            if (!TryResolveSafePath(snapshot, path, out var verifiedPath))
            {
                return ReviewedFileRead.Unsafe();
            }

            var verificationStatus = OpenRead(
                verifiedPath,
                out var reopenedHandle);
            if (verificationStatus != ReviewedFileAccessStatus.Success)
            {
                return verificationStatus == ReviewedFileAccessStatus.Unsafe
                    ? ReviewedFileRead.Unsafe()
                    : ReviewedFileRead.IoFailure();
            }

            using var verificationHandle = reopenedHandle!;
            if (!TryInspectRegular(
                    verificationHandle,
                    out var verifiedIdentity,
                    out var verifiedLength) ||
                verifiedIdentity != openedIdentity ||
                verifiedLength != length)
            {
                return ReviewedFileRead.Unsafe();
            }

            return new ReviewedFileRead(ReviewedFileAccessStatus.Success, bytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return ReviewedFileRead.IoFailure();
        }
        catch (IOException)
        {
            return ReviewedFileRead.IoFailure();
        }
    }

    private static ReviewedFileAccessStatus OpenRead(
        string fullPath,
        out SafeFileHandle? handle)
    {
        handle = null;
        if (OperatingSystem.IsLinux())
        {
            return NativeReviewedFileIdentity.TryOpenNonBlockingNoFollow(
                fullPath,
                out handle)
                ? ReviewedFileAccessStatus.Success
                : ReviewedFileAccessStatus.Unsafe;
        }

        try
        {
            handle = File.OpenHandle(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return ReviewedFileAccessStatus.Success;
        }
        catch (UnauthorizedAccessException)
        {
            return ReviewedFileAccessStatus.IoFailure;
        }
        catch (IOException)
        {
            return ReviewedFileAccessStatus.IoFailure;
        }
    }

    private static bool TryInspectRegular(
        SafeFileHandle handle,
        out ReviewedFileIdentity identity,
        out long length)
    {
        identity = default;
        length = 0;
        try
        {
            var attributes = File.GetAttributes(handle);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return false;
            }

            if (!NativeReviewedFileIdentity.TryGet(handle, out identity))
            {
                return false;
            }

            length = RandomAccess.GetLength(handle);
            return length >= 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryResolveSafePath(
        ReviewedSnapshot snapshot,
        string path,
        out string fullPath)
    {
        fullPath = string.Empty;
        if (!RepositoryPath.IsValid(path))
        {
            return false;
        }

        var segments = path.Split('/');
        var current = snapshot.AbsoluteRoot;
        if (!IsSafeDirectory(current))
        {
            return false;
        }

        for (var index = 0; index < segments.Length - 1; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (!IsSafeDirectory(current))
            {
                return false;
            }
        }

        fullPath = Path.GetFullPath(Path.Combine(current, segments[^1]));
        var rootPrefix = snapshot.AbsoluteRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(rootPrefix, comparison))
        {
            return false;
        }

        try
        {
            var file = new FileInfo(fullPath);
            return file.Exists &&
                (file.Attributes &
                    (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0 &&
                file.LinkTarget is null;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsSafeDirectory(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            return directory.Exists &&
                (directory.Attributes &
                    (FileAttributes.ReparsePoint | FileAttributes.Device)) == 0 &&
                directory.LinkTarget is null;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

internal static partial class NativeReviewedFileIdentity
{
    private const int OpenReadOnly = 0;
    private const int OpenNonBlocking = 0x800;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;

    internal static bool TryOpenNonBlockingNoFollow(
        string path,
        out SafeFileHandle? handle)
    {
        var descriptor = Open(
            path,
            OpenReadOnly | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec);
        if (descriptor < 0)
        {
            handle = null;
            return false;
        }

        handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        return true;
    }

    internal static bool TryGet(
        SafeFileHandle handle,
        out ReviewedFileIdentity identity)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var info))
            {
                identity = default;
                return false;
            }

            identity = new ReviewedFileIdentity(
                info.VolumeSerialNumber,
                ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
            return true;
        }

        if (OperatingSystem.IsLinux())
        {
            var descriptor = checked((int)handle.DangerousGetHandle());
            if (FStat(descriptor, out var info) != 0 ||
                (info.Mode & FileTypeMask) != RegularFile)
            {
                identity = default;
                return false;
            }

            identity = new ReviewedFileIdentity(info.Device, info.Inode);
            return true;
        }

        identity = default;
        return false;
    }

    private const uint FileTypeMask = 0xF000;
    private const uint RegularFile = 0x8000;

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandle",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out ByHandleFileInformation information);

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int FStat(
        int fileDescriptor,
        out LinuxFileInformation information);

    [LibraryImport(
        "libc",
        EntryPoint = "open",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal FileTime CreationTime;
        internal FileTime LastAccessTime;
        internal FileTime LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint Low;
        internal uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct LinuxFileInformation
    {
        internal ulong Device;
        internal ulong Inode;
        internal ulong HardLinkCount;
        internal uint Mode;
        internal uint UserId;
        internal uint GroupId;
        internal int Padding;
        internal ulong DeviceId;
        internal long Size;
        internal long BlockSize;
        internal long BlockCount;
        internal LinuxTimestamp AccessTime;
        internal LinuxTimestamp ModificationTime;
        internal LinuxTimestamp ChangeTime;
        internal fixed long Reserved[3];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxTimestamp
    {
        internal long Seconds;
        internal long Nanoseconds;
    }
}
