using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Snapshot.GitObjects;
using Microsoft.Win32.SafeHandles;

namespace AgenticPrReview.Runtime.ActionHost.Snapshot;

internal enum ReviewedTreeEntryKind
{
    Regular = 1,
    Symlink,
    Submodule,
}

internal enum ReviewedTreeFailure
{
    None = 0,
    UnsupportedSize,
    InvalidGraph,
    GitHubUnavailable,
    MissingObject,
    IdentityMismatch,
    Cancelled,
    InternalFailure,
}

internal static class ReviewedTreeFailureCodes
{
    internal const string UnsupportedSize = "snapshot_unsupported_size";
    internal const string InvalidGraph = "snapshot_invalid_graph";
    internal const string GitHubUnavailable = "snapshot_github_unavailable";
    internal const string MissingObject = "snapshot_object_missing";
    internal const string IdentityMismatch = "snapshot_identity_mismatch";
    internal const string Cancelled = "snapshot_cancelled";
    internal const string InternalFailure = "snapshot_internal_failure";

    internal static string From(ReviewedTreeFailure failure) => failure switch
    {
        ReviewedTreeFailure.UnsupportedSize => UnsupportedSize,
        ReviewedTreeFailure.InvalidGraph => InvalidGraph,
        ReviewedTreeFailure.GitHubUnavailable => GitHubUnavailable,
        ReviewedTreeFailure.MissingObject => MissingObject,
        ReviewedTreeFailure.IdentityMismatch => IdentityMismatch,
        ReviewedTreeFailure.Cancelled => Cancelled,
        ReviewedTreeFailure.InternalFailure => InternalFailure,
        _ => throw new ArgumentOutOfRangeException(nameof(failure)),
    };
}

internal sealed class ReviewedTreeMaterializationResult
{
    private ReviewedTreeMaterializationResult(
        ReviewedTreeSnapshot? snapshot,
        ReviewedTreeFailure failure,
        bool cleanupIncomplete)
    {
        Snapshot = snapshot;
        Failure = failure;
        CleanupIncomplete = cleanupIncomplete;
    }

    internal ReviewedTreeSnapshot? Snapshot { get; }

    internal ReviewedTreeFailure Failure { get; }

    internal string? FailureCode => Failure == ReviewedTreeFailure.None
        ? null
        : ReviewedTreeFailureCodes.From(Failure);

    internal bool CleanupIncomplete { get; }

    internal static ReviewedTreeMaterializationResult Success(
        ReviewedTreeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(snapshot, ReviewedTreeFailure.None, false);
    }

    internal static ReviewedTreeMaterializationResult Failed(
        ReviewedTreeFailure failure,
        bool cleanupIncomplete = false)
    {
        if (failure == ReviewedTreeFailure.None)
        {
            throw new ArgumentOutOfRangeException(nameof(failure));
        }

        return new(null, failure, cleanupIncomplete);
    }
}

internal sealed class ReviewedTreePathRecord
{
    private readonly object _authority;

    private ReviewedTreePathRecord(
        object authority,
        string path,
        string mode,
        ReviewedTreeEntryKind kind,
        string objectSha,
        long? size,
        ReviewedStagedBlob? stagedBlob)
    {
        if (!ReviewedTreeReader.HasMintAuthority(authority) ||
            !ReviewedTreePath.IsValid(path) ||
            !ReviewedGitObjectValidation.IsSha(objectSha) ||
            !ModeMatches(kind, mode) ||
            kind == ReviewedTreeEntryKind.Regular != (stagedBlob is not null) ||
            kind == ReviewedTreeEntryKind.Regular != (size is not null) ||
            size is < 0 ||
            stagedBlob is not null &&
            (!stagedBlob.WasMintedBy(authority) ||
                !StringComparer.Ordinal.Equals(stagedBlob.Sha, objectSha) ||
                stagedBlob.Size != size))
        {
            throw new ArgumentException("Reviewed-tree record is invalid.");
        }

        _authority = authority;
        Path = path;
        Mode = mode;
        Kind = kind;
        ObjectSha = objectSha;
        Size = size;
        StagedBlob = stagedBlob;
    }

    internal string Path { get; }

    internal string Mode { get; }

    internal ReviewedTreeEntryKind Kind { get; }

    internal string ObjectSha { get; }

    internal long? Size { get; }

    internal ReviewedStagedBlob? StagedBlob { get; }

    internal static ReviewedTreePathRecord Mint(
        object authority,
        string path,
        string mode,
        ReviewedTreeEntryKind kind,
        string objectSha,
        long? size,
        ReviewedStagedBlob? stagedBlob) => new(
            authority,
            path,
            mode,
            kind,
            objectSha,
            size,
            stagedBlob);

    internal bool WasMintedBy(object authority) =>
        ReferenceEquals(_authority, authority) &&
        ReviewedTreeReader.HasMintAuthority(authority);

    private static bool ModeMatches(
        ReviewedTreeEntryKind kind,
        string mode) => kind switch
        {
            ReviewedTreeEntryKind.Regular => mode is "100644" or "100755",
            ReviewedTreeEntryKind.Symlink => mode == "120000",
            ReviewedTreeEntryKind.Submodule => mode == "160000",
            _ => false,
        };
}

internal sealed record ReviewedTreeIdentity(
    string Sha256,
    ImmutableArray<byte> CanonicalPreimage);

internal sealed class ReviewedTreeSnapshot : IAsyncDisposable
{
    private readonly ReviewedBlobStagingLease _staging;
    private bool _disposed;

    private ReviewedTreeSnapshot(
        object authority,
        long repositoryId,
        long pullRequestNumber,
        string headSha,
        string rootTreeSha,
        IEnumerable<ReviewedTreePathRecord> records,
        ReviewedContentBudget budget,
        ReviewedBlobStagingLease staging)
    {
        ArgumentNullException.ThrowIfNull(records);
        Budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _staging = staging ?? throw new ArgumentNullException(nameof(staging));
        if (!ReviewedTreeReader.HasMintAuthority(authority) ||
            repositoryId <= 0 || pullRequestNumber <= 0 ||
            !ReviewedGitObjectValidation.IsSha(headSha) ||
            !ReviewedGitObjectValidation.IsSha(rootTreeSha))
        {
            throw new ArgumentException("Reviewed-tree root identity is invalid.");
        }

        var materialized = records.ToArray();
        if (materialized.Any(record =>
                record is null || !record.WasMintedBy(authority)))
        {
            throw new ArgumentException("Reviewed-tree records are invalid.");
        }

        var ordered = materialized
            .OrderBy(static record => record.Path, StringComparer.Ordinal)
            .ToImmutableArray();
        if (ordered.Select(static record => record.Path)
                .Distinct(StringComparer.Ordinal).Count() != ordered.Length)
        {
            throw new ArgumentException("Reviewed-tree records are invalid.");
        }

        RepositoryId = repositoryId;
        PullRequestNumber = pullRequestNumber;
        HeadSha = headSha;
        RootTreeSha = rootTreeSha;
        Records = ordered;
        Identity = ReviewedTreeIdentityWriter.Mint(
            authority,
            repositoryId,
            pullRequestNumber,
            headSha,
            rootTreeSha,
            ordered);
    }

    internal long RepositoryId { get; }

    internal long PullRequestNumber { get; }

    internal string HeadSha { get; }

    internal string RootTreeSha { get; }

    internal ImmutableArray<ReviewedTreePathRecord> Records { get; }

    internal ReviewedTreeIdentity Identity { get; }

    internal ReviewedContentBudget Budget { get; }

    internal static ReviewedTreeSnapshot Mint(
        object authority,
        long repositoryId,
        long pullRequestNumber,
        string headSha,
        string rootTreeSha,
        IEnumerable<ReviewedTreePathRecord> records,
        ReviewedContentBudget budget,
        ReviewedBlobStagingLease staging) => new(
            authority,
            repositoryId,
            pullRequestNumber,
            headSha,
            rootTreeSha,
            records,
            budget,
            staging);

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        Budget.Invalidate();
        _staging.Cleanup();
        return ValueTask.CompletedTask;
    }
}

internal static class ReviewedTreePath
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool TryAppend(
        string prefix,
        string segment,
        out string path,
        out int utf8Bytes)
    {
        path = string.Empty;
        utf8Bytes = 0;
        if (!IsValidSegment(segment))
        {
            return false;
        }

        path = prefix.Length == 0 ? segment : prefix + "/" + segment;
        if (!IsValid(path))
        {
            path = string.Empty;
            return false;
        }

        try
        {
            utf8Bytes = StrictUtf8.GetByteCount(path);
            return true;
        }
        catch (EncoderFallbackException)
        {
            path = string.Empty;
            utf8Bytes = 0;
            return false;
        }
    }

    internal static bool IsValid(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] == '/')
        {
            return false;
        }

        try
        {
            if (StrictUtf8.GetByteCount(path) > ReviewedContentLimits.PathBytes)
            {
                return false;
            }
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        return path.Split('/').All(IsValidSegment);
    }

    internal static bool IsValidSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment) || segment is "." or ".." ||
            segment[^1] is '.' or ' ' ||
            StringComparer.OrdinalIgnoreCase.Equals(segment, ".git"))
        {
            return false;
        }

        try
        {
            _ = StrictUtf8.GetByteCount(segment);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        return segment.All(static character =>
            character is > '\u001f' and not '\u007f' &&
            character is not '\ufffd' &&
            character is not '/' and not '\\' and not ':' and not '?' and
                not '#' and not '*' and not '"' and not '<' and not '>' and
                not '|');
    }
}

internal static class ReviewedTreeIdentityWriter
{
    private static readonly byte[] Domain =
        "agentic-pr-review.reviewed-tree.v1"u8.ToArray();

    internal static ReviewedTreeIdentity Mint(
        object authority,
        long repositoryId,
        long pullRequestNumber,
        string headSha,
        string rootTreeSha,
        ImmutableArray<ReviewedTreePathRecord> records)
    {
        if (!ReviewedTreeReader.HasMintAuthority(authority) ||
            records.Any(record => !record.WasMintedBy(authority)))
        {
            throw new InvalidOperationException(
                "Only the reviewed-tree reader may mint tree identity.");
        }

        using var stream = new MemoryStream();
        WriteFrame(stream, Domain);
        WriteInt64(stream, repositoryId);
        WriteInt64(stream, pullRequestNumber);
        WriteFrame(stream, Encoding.ASCII.GetBytes(headSha));
        WriteFrame(stream, Encoding.ASCII.GetBytes(rootTreeSha));
        var ordered = records
            .OrderBy(static record => record.Path, StringComparer.Ordinal)
            .ToImmutableArray();
        WriteInt32(stream, ordered.Length);
        foreach (var record in ordered)
        {
            stream.WriteByte((byte)record.Kind);
            WriteFrame(stream, Encoding.UTF8.GetBytes(record.Path));
            WriteFrame(stream, Encoding.ASCII.GetBytes(record.Mode));
            WriteFrame(stream, Encoding.ASCII.GetBytes(record.ObjectSha));
            WriteInt64(stream, record.Size ?? -1);
        }

        var preimage = stream.ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(preimage))
            .ToLowerInvariant();
        return new ReviewedTreeIdentity(
            digest,
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

internal readonly record struct ReviewedStagedFileIdentity(
    ulong Device,
    ulong File);

internal sealed class ReviewedStagedBlob
{
    private readonly object _authority;
    private readonly ReviewedBlobStagingLease _owner;
    private readonly string _path;
    private readonly ReviewedStagedFileIdentity _identity;

    private ReviewedStagedBlob(
        object authority,
        ReviewedBlobStagingLease owner,
        string path,
        string sha,
        long size,
        ReviewedStagedFileIdentity identity)
    {
        if (!ReviewedTreeReader.HasMintAuthority(authority) ||
            !owner.Owns(authority, path) ||
            !Path.IsPathFullyQualified(path) ||
            !ReviewedGitObjectValidation.IsSha(sha) ||
            size is < 0 or > ReviewedContentLimits.HeadBlobBytes)
        {
            throw new ArgumentException("Reviewed staged blob is invalid.");
        }

        _authority = authority;
        _owner = owner;
        _path = path;
        _identity = identity;
        Sha = sha;
        Size = size;
    }

    internal string Sha { get; }

    internal long Size { get; }

    internal static ReviewedStagedBlob Mint(
        object authority,
        ReviewedBlobStagingLease owner,
        string path,
        string sha,
        long size,
        ReviewedStagedFileIdentity identity) => new(
            authority,
            owner,
            path,
            sha,
            size,
            identity);

    internal bool WasMintedBy(object authority) =>
        ReferenceEquals(_authority, authority) &&
        ReviewedTreeReader.HasMintAuthority(authority) &&
        _owner.Owns(authority, _path);

    internal async Task<bool> CopyVerifiedToAsync(
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        try
        {
            if (!ReviewedStagedFileAccess.TryOpen(
                    _path,
                    out var openedHandle))
            {
                return false;
            }

            using var source = openedHandle!;
            if (!ReviewedStagedFileAccess.TryInspectRegular(
                    source,
                    out var openedIdentity,
                    out var openedLength) ||
                openedIdentity != _identity ||
                openedLength != Size)
            {
                return false;
            }

            var bytes = GC.AllocateUninitializedArray<byte>(checked((int)Size));
            var actual = 0;
            while (actual < bytes.Length)
            {
                var read = await RandomAccess.ReadAsync(
                    source,
                    bytes.AsMemory(actual),
                    actual,
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                actual += read;
            }

            var extra = new byte[1];
            var extraRead = await RandomAccess.ReadAsync(
                source,
                extra,
                actual,
                cancellationToken);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            hash.AppendData(GitBlobHeader(Size));
            hash.AppendData(bytes);
            var actualSha = Convert.ToHexString(hash.GetHashAndReset())
                .ToLowerInvariant();
            if (actual != bytes.Length || extraRead != 0 ||
                !StringComparer.Ordinal.Equals(actualSha, Sha))
            {
                return false;
            }

            await destination.WriteAsync(bytes, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static byte[] GitBlobHeader(long size) => Encoding.ASCII.GetBytes(
        "blob " + size.ToString(CultureInfo.InvariantCulture) + "\0");
}

internal static partial class ReviewedStagedFileAccess
{
    private const int OpenReadOnly = 0;
    private const int OpenNonBlocking = 0x800;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const uint FileTypeMask = 0xf000;
    private const uint RegularFile = 0x8000;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeDevice = 0x00000040;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagRandomAccess = 0x10000000;

    internal static bool TryOpen(
        string path,
        out SafeFileHandle? handle)
    {
        if (OperatingSystem.IsWindows())
        {
            handle = CreateFile(
                path,
                GenericRead,
                FileShareRead | FileShareWrite | FileShareDelete,
                0,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagRandomAccess,
                0);
            if (!handle.IsInvalid)
            {
                return true;
            }

            handle.Dispose();
            handle = null;
            return false;
        }

        if (OperatingSystem.IsLinux())
        {
            var descriptor = Open(
                path,
                OpenReadOnly |
                    OpenNonBlocking |
                    OpenNoFollow |
                    OpenCloseOnExec);
            if (descriptor >= 0)
            {
                handle = new SafeFileHandle(
                    (nint)descriptor,
                    ownsHandle: true);
                return true;
            }
        }

        handle = null;
        return false;
    }

    internal static bool TryInspectRegular(
        SafeFileHandle handle,
        out ReviewedStagedFileIdentity identity,
        out long length)
    {
        identity = default;
        length = 0;
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var info) ||
                (info.FileAttributes &
                    (FileAttributeDirectory |
                        FileAttributeDevice |
                        FileAttributeReparsePoint)) != 0)
            {
                return false;
            }

            identity = new ReviewedStagedFileIdentity(
                info.VolumeSerialNumber,
                ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
        }
        else if (OperatingSystem.IsLinux())
        {
            var descriptor = checked((int)handle.DangerousGetHandle());
            if (FStat(descriptor, out var info) != 0 ||
                (info.Mode & FileTypeMask) != RegularFile)
            {
                return false;
            }

            identity = new ReviewedStagedFileIdentity(
                info.Device,
                info.Inode);
        }
        else
        {
            return false;
        }

        try
        {
            length = RandomAccess.GetLength(handle);
            return length >= 0;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException)
        {
            identity = default;
            length = 0;
            return false;
        }
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

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

internal sealed class ReviewedBlobStagingLease
{
    private readonly object _authority;
    private readonly string _root;
    private readonly HashSet<string> _ownedPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _cleaned;

    private ReviewedBlobStagingLease(object authority, string root)
    {
        _authority = authority;
        _root = root;
    }

    internal static ReviewedBlobStagingLease? TryCreate(
        object authority,
        string parent)
    {
        if (!ReviewedTreeReader.HasMintAuthority(authority) ||
            string.IsNullOrEmpty(parent) ||
            !Path.IsPathFullyQualified(parent))
        {
            return null;
        }

        try
        {
            var fullParent = Path.GetFullPath(parent);
            var info = new DirectoryInfo(fullParent);
            if (!Path.IsPathFullyQualified(fullParent) || !info.Exists ||
                info.LinkTarget is not null ||
                (info.Attributes &
                    (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                return null;
            }

            for (var attempt = 0; attempt < 8; attempt++)
            {
                var root = Path.Join(
                    fullParent,
                    "apr-reviewed-" + Guid.NewGuid().ToString("N"));
                if (Directory.Exists(root) || File.Exists(root))
                {
                    continue;
                }

                Directory.CreateDirectory(root);
                if (OperatingSystem.IsLinux())
                {
                    File.SetUnixFileMode(
                        root,
                        UnixFileMode.UserRead |
                        UnixFileMode.UserWrite |
                        UnixFileMode.UserExecute);
                }

                return new ReviewedBlobStagingLease(authority, root);
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            return null;
        }
    }

    internal ReviewedBlobStageWriter? TryCreateWriter(
        string sha,
        long declaredSize)
    {
        if (_cleaned || !ReviewedGitObjectValidation.IsSha(sha) ||
            declaredSize < 0)
        {
            return null;
        }

        var partial = Path.Join(_root, sha + ".partial");
        var final = Path.Join(_root, sha + ".blob");
        try
        {
            var stream = new FileStream(
                partial,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            _ownedPaths.Add(partial);
            return new ReviewedBlobStageWriter(
                this,
                stream,
                partial,
                final,
                sha,
                declaredSize);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal void Commit(string partial, string final)
    {
        _ownedPaths.Remove(partial);
        _ownedPaths.Add(final);
    }

    internal ReviewedStagedBlob? MintBlob(
        string path,
        string sha,
        long size,
        ReviewedStagedFileIdentity identity) => Owns(_authority, path)
            ? ReviewedStagedBlob.Mint(
                _authority,
                this,
                path,
                sha,
                size,
                identity)
            : null;

    internal bool Owns(object authority, string path) =>
        !_cleaned &&
        ReferenceEquals(_authority, authority) &&
        ReviewedTreeReader.HasMintAuthority(authority) &&
        Path.IsPathFullyQualified(path) &&
        StringComparer.Ordinal.Equals(
            Path.GetDirectoryName(path),
            _root) &&
        _ownedPaths.Contains(path);

    internal bool Cleanup()
    {
        if (_cleaned)
        {
            return true;
        }

        _cleaned = true;
        var complete = true;
        foreach (var path in _ownedPaths.Order(StringComparer.Ordinal))
        {
            try
            {
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or
                UnauthorizedAccessException)
            {
                complete = false;
            }
        }

        try
        {
            Directory.Delete(_root, recursive: false);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException)
        {
            complete = false;
        }

        return complete;
    }
}

internal sealed class ReviewedBlobStageWriter : IAsyncDisposable
{
    private readonly ReviewedBlobStagingLease _owner;
    private readonly string _partialPath;
    private readonly string _finalPath;
    private readonly string _expectedSha;
    private readonly long _declaredSize;
    private readonly IncrementalHash _hash =
        IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
    private FileStream? _stream;
    private long _actualSize;

    internal ReviewedBlobStageWriter(
        ReviewedBlobStagingLease owner,
        FileStream stream,
        string partialPath,
        string finalPath,
        string expectedSha,
        long declaredSize)
    {
        _owner = owner;
        _stream = stream;
        _partialPath = partialPath;
        _finalPath = finalPath;
        _expectedSha = expectedSha;
        _declaredSize = declaredSize;
        _hash.AppendData(ReviewedStagedBlob.GitBlobHeader(declaredSize));
    }

    internal async Task<bool> WriteAsync(
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        if (_stream is null ||
            _actualSize > _declaredSize - bytes.Length)
        {
            return false;
        }

        await _stream.WriteAsync(bytes, cancellationToken);
        _hash.AppendData(bytes.Span);
        _actualSize += bytes.Length;
        return true;
    }

    internal async Task<ReviewedStagedBlob?> CompleteAsync(
        CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            return null;
        }

        var actualSha = Convert.ToHexString(_hash.GetHashAndReset())
            .ToLowerInvariant();
        if (_actualSize != _declaredSize ||
            !StringComparer.Ordinal.Equals(actualSha, _expectedSha))
        {
            await DisposeAsync();
            return null;
        }

        await _stream.FlushAsync(cancellationToken);
        _stream.Flush(flushToDisk: true);
        await _stream.DisposeAsync();
        _stream = null;
        File.Move(_partialPath, _finalPath, overwrite: false);
        _owner.Commit(_partialPath, _finalPath);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(_finalPath, UnixFileMode.UserRead);
        }
        else
        {
            File.SetAttributes(_finalPath, FileAttributes.ReadOnly);
        }

        if (!ReviewedStagedFileAccess.TryOpen(
                _finalPath,
                out var openedHandle))
        {
            return null;
        }

        using var handle = openedHandle!;
        if (!ReviewedStagedFileAccess.TryInspectRegular(
                handle,
                out var identity,
                out var length) ||
            length != _declaredSize)
        {
            return null;
        }

        return _owner.MintBlob(
            _finalPath,
            _expectedSha,
            _declaredSize,
            identity);
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null)
        {
            await _stream.DisposeAsync();
            _stream = null;
        }

        _hash.Dispose();
    }
}
