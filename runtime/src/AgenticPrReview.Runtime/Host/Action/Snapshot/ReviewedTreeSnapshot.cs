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

internal sealed class ReviewedTreeIdentity
{
    private ReviewedTreeIdentity(
        object authority,
        string sha256,
        ImmutableArray<byte> canonicalPreimage)
    {
        if (!ReviewedTreeReader.HasMintAuthority(authority) ||
            sha256.Length != 64 ||
            sha256.Any(static character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')) ||
            canonicalPreimage.IsDefault)
        {
            throw new ArgumentException("Reviewed-tree identity is invalid.");
        }

        Sha256 = sha256;
        CanonicalPreimage = canonicalPreimage;
    }

    internal string Sha256 { get; }

    internal ImmutableArray<byte> CanonicalPreimage { get; }

    internal static ReviewedTreeIdentity Mint(
        object authority,
        string sha256,
        ImmutableArray<byte> canonicalPreimage) => new(
            authority,
            sha256,
            canonicalPreimage);
}

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
            !_staging.WasMintedBy(authority, Budget) ||
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
        return ReviewedTreeIdentity.Mint(
            authority,
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

internal enum ReviewedStagedBlobCopyFailure
{
    None = 0,
    IdentityMismatch,
    UnsupportedSize,
    IoFailure,
}

internal sealed class ReviewedStagedBlob
{
    private readonly object _authority;
    private readonly ReviewedBlobStagingLease _owner;
    private readonly ReviewedContentBudget _budget;
    private readonly string _path;
    private readonly ReviewedStagedFileIdentity _identity;
    private readonly FileStream _source;

    private ReviewedStagedBlob(
        object authority,
        ReviewedBlobStagingLease owner,
        ReviewedContentBudget budget,
        string path,
        string sha,
        long size,
        ReviewedStagedFileIdentity identity,
        FileStream source)
    {
        if (!ReviewedTreeReader.HasMintAuthority(authority) ||
            !owner.CanMint(authority, budget, path, source) ||
            !Path.IsPathFullyQualified(path) ||
            !ReviewedGitObjectValidation.IsSha(sha) ||
            size is < 0 or > ReviewedContentLimits.HeadBlobBytes)
        {
            throw new ArgumentException("Reviewed staged blob is invalid.");
        }

        _authority = authority;
        _owner = owner;
        _budget = budget;
        _path = path;
        _identity = identity;
        _source = source;
        Sha = sha;
        Size = size;
    }

    internal string Sha { get; }

    internal long Size { get; }

    internal static ReviewedStagedBlob Mint(
        object authority,
        ReviewedBlobStagingLease owner,
        ReviewedContentBudget budget,
        string path,
        string sha,
        long size,
        ReviewedStagedFileIdentity identity,
        FileStream source) => new(
            authority,
            owner,
            budget,
            path,
            sha,
            size,
            identity,
            source);

    internal bool WasMintedBy(object authority) =>
        ReferenceEquals(_authority, authority) &&
        ReviewedTreeReader.HasMintAuthority(authority) &&
        _owner.Owns(authority, _budget, this);

    internal async Task<bool> CopyVerifiedToAsync(
        Stream destination,
        CancellationToken cancellationToken) =>
        await CopyVerifiedDetailedAsync(destination, cancellationToken) ==
            ReviewedStagedBlobCopyFailure.None;

    internal async Task<ReviewedStagedBlobCopyFailure>
        CopyVerifiedDetailedAsync(
            Stream destination,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!_budget.TryBeginOperation(
                cancellationToken,
                out var operationLease))
        {
            return ReviewedStagedBlobCopyFailure.UnsupportedSize;
        }

        using var operation = operationLease!;
        try
        {
            if (!ReviewedStagedFileAccess.TryInspectRegular(
                    _source.SafeFileHandle,
                    out var openedIdentity,
                    out var openedLength) ||
                openedIdentity != _identity ||
                openedLength != Size)
            {
                return ReviewedStagedBlobCopyFailure.IdentityMismatch;
            }

            var buffer = new byte[ReviewedContentLimits.StreamBufferBytes];
            long actual = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            hash.AppendData(GitBlobHeader(Size));
            while (actual < Size)
            {
                var requested = checked((int)Math.Min(
                    buffer.Length,
                    Size - actual));
                var read = await RandomAccess.ReadAsync(
                    _source.SafeFileHandle,
                    buffer.AsMemory(0, requested),
                    actual,
                    operation.Token);
                if (read == 0)
                {
                    return ReviewedStagedBlobCopyFailure.IdentityMismatch;
                }

                hash.AppendData(buffer.AsSpan(0, read));
                actual += read;
            }

            var extra = new byte[1];
            var extraRead = await RandomAccess.ReadAsync(
                _source.SafeFileHandle,
                extra,
                actual,
                operation.Token);
            var actualSha = Convert.ToHexString(hash.GetHashAndReset())
                .ToLowerInvariant();
            if (actual != Size || extraRead != 0 ||
                !StringComparer.Ordinal.Equals(actualSha, Sha))
            {
                return ReviewedStagedBlobCopyFailure.IdentityMismatch;
            }

            if (!ReviewedStagedFileAccess.TryInspectRegular(
                    _source.SafeFileHandle,
                    out openedIdentity,
                    out openedLength) ||
                openedIdentity != _identity ||
                openedLength != Size)
            {
                return ReviewedStagedBlobCopyFailure.IdentityMismatch;
            }

            actual = 0;
            hash.AppendData(GitBlobHeader(Size));
            while (actual < Size)
            {
                var requested = checked((int)Math.Min(
                    buffer.Length,
                    Size - actual));
                var read = await RandomAccess.ReadAsync(
                    _source.SafeFileHandle,
                    buffer.AsMemory(0, requested),
                    actual,
                    operation.Token);
                if (read == 0)
                {
                    return ReviewedStagedBlobCopyFailure.IdentityMismatch;
                }

                hash.AppendData(buffer.AsSpan(0, read));
                await destination.WriteAsync(
                    buffer.AsMemory(0, read),
                    operation.Token);
                actual += read;
            }

            actualSha = Convert.ToHexString(hash.GetHashAndReset())
                .ToLowerInvariant();
            if (!StringComparer.Ordinal.Equals(actualSha, Sha) ||
                !ReviewedStagedFileAccess.TryInspectRegular(
                    _source.SafeFileHandle,
                    out openedIdentity,
                    out openedLength) ||
                openedIdentity != _identity ||
                openedLength != Size)
            {
                return ReviewedStagedBlobCopyFailure.IdentityMismatch;
            }

            return ReviewedStagedBlobCopyFailure.None;
        }
        catch (OperationCanceledException) when (operation.DeadlineExpired)
        {
            return ReviewedStagedBlobCopyFailure.UnsupportedSize;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException)
        {
            return ReviewedStagedBlobCopyFailure.IoFailure;
        }
    }

    internal static byte[] GitBlobHeader(long size) => Encoding.ASCII.GetBytes(
        "blob " + size.ToString(CultureInfo.InvariantCulture) + "\0");

    internal void DisposeOwned() => _source.Dispose();
}

internal static partial class ReviewedStagedFileAccess
{
    private const int OpenReadOnly = 0;
    private const int OpenReadWrite = 2;
    private const int OpenCreate = 0x40;
    private const int OpenExclusive = 0x80;
    private const int OpenDirectory = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const int UnlinkRemoveDirectory = 0x200;
    private const uint FileTypeMask = 0xf000;
    private const uint RegularFile = 0x8000;
    private const uint DirectoryFile = 0x4000;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeDevice = 0x00000040;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;

    internal static bool TryOpenDirectory(
        string path,
        out SafeFileHandle? handle,
        out ReviewedStagedFileIdentity identity)
    {
        identity = default;
        if (OperatingSystem.IsWindows())
        {
            handle = CreateFile(
                path,
                GenericRead,
                FileShareRead | FileShareWrite | FileShareDelete,
                0,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagBackupSemantics,
                0);
            if (!handle.IsInvalid && TryInspectDirectory(handle, out identity))
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
                    OpenDirectory |
                    OpenNoFollow |
                    OpenCloseOnExec);
            if (descriptor >= 0)
            {
                handle = new SafeFileHandle(
                    (nint)descriptor,
                    ownsHandle: true);
                if (TryInspectDirectory(handle, out identity))
                {
                    return true;
                }

                handle.Dispose();
            }
        }

        handle = null;
        identity = default;
        return false;
    }

    internal static bool TryCreateStagedFile(
        SafeFileHandle parent,
        string parentPath,
        ReviewedStagedFileIdentity parentIdentity,
        string fileName,
        out FileStream? stream)
    {
        stream = null;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var parentDescriptor = checked(
                    (int)parent.DangerousGetHandle());
                var descriptor = OpenAt(
                    parentDescriptor,
                    fileName,
                    OpenReadWrite |
                        OpenCreate |
                        OpenExclusive |
                        OpenNoFollow |
                        OpenCloseOnExec,
                    0x180);
                if (descriptor < 0)
                {
                    return false;
                }

                var handle = new SafeFileHandle(
                    (nint)descriptor,
                    ownsHandle: true);
                if (UnlinkAt(parentDescriptor, fileName, 0) != 0)
                {
                    handle.Dispose();
                    return false;
                }

                stream = new FileStream(
                    handle,
                    FileAccess.ReadWrite,
                    64 * 1024,
                    isAsync: false);
                return true;
            }

            if (OperatingSystem.IsWindows() &&
                DirectoryMatches(parentPath, parentIdentity))
            {
                var candidate = new FileStream(
                    Path.Join(parentPath, fileName),
                    new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.ReadWrite,
                        Share = FileShare.Read,
                        BufferSize = 64 * 1024,
                        Options = FileOptions.Asynchronous |
                            FileOptions.SequentialScan |
                            FileOptions.DeleteOnClose,
                    });
                if (!DirectoryMatches(parentPath, parentIdentity))
                {
                    candidate.Dispose();
                    return false;
                }

                stream = candidate;
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException)
        {
            stream?.Dispose();
            stream = null;
        }

        return false;
    }

    internal static bool TryOpenOrCreateChildDirectory(
        SafeFileHandle parent,
        string parentPath,
        ReviewedStagedFileIdentity parentIdentity,
        string name,
        out SafeFileHandle? handle,
        out ReviewedStagedFileIdentity identity,
        out string childPath)
    {
        handle = null;
        identity = default;
        childPath = Path.Join(parentPath, name);
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var parentDescriptor = checked(
                    (int)parent.DangerousGetHandle());
                _ = MakeDirectoryAt(parentDescriptor, name, 0x1c0);
                var descriptor = OpenAt(
                    parentDescriptor,
                    name,
                    OpenReadOnly |
                        OpenDirectory |
                        OpenNoFollow |
                        OpenCloseOnExec,
                    0);
                if (descriptor < 0)
                {
                    return false;
                }

                handle = new SafeFileHandle(
                    (nint)descriptor,
                    ownsHandle: true);
                if (TryInspectDirectory(handle, out identity))
                {
                    return true;
                }

                handle.Dispose();
                handle = null;
                return false;
            }

            if (OperatingSystem.IsWindows() &&
                DirectoryMatches(parentPath, parentIdentity))
            {
                Directory.CreateDirectory(childPath);
                if (!DirectoryMatches(parentPath, parentIdentity) ||
                    !TryOpenDirectory(childPath, out handle, out identity))
                {
                    handle?.Dispose();
                    handle = null;
                    identity = default;
                    return false;
                }

                return true;
            }
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            handle?.Dispose();
            handle = null;
            identity = default;
        }

        return false;
    }

    internal static bool TryCreateChildDirectoryExclusive(
        SafeFileHandle parent,
        string parentPath,
        ReviewedStagedFileIdentity parentIdentity,
        string name,
        out SafeFileHandle? handle,
        out ReviewedStagedFileIdentity identity,
        out string childPath)
    {
        handle = null;
        identity = default;
        childPath = Path.Join(parentPath, name);
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var parentDescriptor = checked(
                    (int)parent.DangerousGetHandle());
                if (MakeDirectoryAt(parentDescriptor, name, 0x1c0) != 0)
                {
                    return false;
                }

                var descriptor = OpenAt(
                    parentDescriptor,
                    name,
                    OpenReadOnly |
                        OpenDirectory |
                        OpenNoFollow |
                        OpenCloseOnExec,
                    0);
                if (descriptor < 0)
                {
                    _ = UnlinkAt(
                        parentDescriptor,
                        name,
                        UnlinkRemoveDirectory);
                    return false;
                }

                handle = new SafeFileHandle(
                    (nint)descriptor,
                    ownsHandle: true);
                if (TryInspectDirectory(handle, out identity))
                {
                    return true;
                }

                handle.Dispose();
                handle = null;
                _ = UnlinkAt(
                    parentDescriptor,
                    name,
                    UnlinkRemoveDirectory);
                return false;
            }

            if (OperatingSystem.IsWindows() &&
                DirectoryMatches(parentPath, parentIdentity) &&
                !Directory.Exists(childPath))
            {
                Directory.CreateDirectory(childPath);
                if (DirectoryMatches(parentPath, parentIdentity) &&
                    TryOpenDirectory(childPath, out handle, out identity))
                {
                    return true;
                }

                handle?.Dispose();
                handle = null;
                identity = default;
            }
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            handle?.Dispose();
            handle = null;
            identity = default;
        }

        return false;
    }

    internal static bool TryOpenChildDirectory(
        SafeFileHandle parent,
        string parentPath,
        ReviewedStagedFileIdentity parentIdentity,
        string name,
        out SafeFileHandle? handle,
        out ReviewedStagedFileIdentity identity)
    {
        handle = null;
        identity = default;
        if (OperatingSystem.IsLinux())
        {
            var descriptor = OpenAt(
                checked((int)parent.DangerousGetHandle()),
                name,
                OpenReadOnly |
                    OpenDirectory |
                    OpenNoFollow |
                    OpenCloseOnExec,
                0);
            if (descriptor >= 0)
            {
                handle = new SafeFileHandle(
                    (nint)descriptor,
                    ownsHandle: true);
                if (TryInspectDirectory(handle, out identity))
                {
                    return true;
                }

                handle.Dispose();
            }

            handle = null;
            identity = default;
            return false;
        }

        return DirectoryMatches(parentPath, parentIdentity) &&
            TryOpenDirectory(Path.Join(parentPath, name), out handle, out identity);
    }

    internal static bool TryRemoveChild(
        SafeFileHandle parent,
        string parentPath,
        ReviewedStagedFileIdentity parentIdentity,
        string name,
        bool directory)
    {
        if (OperatingSystem.IsLinux())
        {
            return UnlinkAt(
                checked((int)parent.DangerousGetHandle()),
                name,
                directory ? UnlinkRemoveDirectory : 0) == 0;
        }

        if (!DirectoryMatches(parentPath, parentIdentity))
        {
            return false;
        }

        var child = Path.Join(parentPath, name);
        if (directory)
        {
            Directory.Delete(child, recursive: false);
        }
        else
        {
            File.Delete(child);
        }

        return true;
    }

    internal static bool TryCreateVisibleFile(
        SafeFileHandle parent,
        string parentPath,
        ReviewedStagedFileIdentity parentIdentity,
        string name,
        out FileStream? stream)
    {
        stream = null;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var parentDescriptor = checked(
                    (int)parent.DangerousGetHandle());
                var descriptor = OpenAt(
                    parentDescriptor,
                    name,
                    OpenReadWrite |
                        OpenCreate |
                        OpenExclusive |
                        OpenNoFollow |
                        OpenCloseOnExec,
                    0x180);
                if (descriptor < 0)
                {
                    return false;
                }

                stream = new FileStream(
                    new SafeFileHandle((nint)descriptor, ownsHandle: true),
                    FileAccess.ReadWrite,
                    ReviewedContentLimits.StreamBufferBytes,
                    isAsync: false);
                return true;
            }

            if (OperatingSystem.IsWindows() &&
                DirectoryMatches(parentPath, parentIdentity))
            {
                var candidate = new FileStream(
                    Path.Join(parentPath, name),
                    new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.ReadWrite,
                        Share = FileShare.Read,
                        BufferSize = ReviewedContentLimits.StreamBufferBytes,
                        Options = FileOptions.Asynchronous |
                            FileOptions.SequentialScan,
                    });
                if (!DirectoryMatches(parentPath, parentIdentity))
                {
                    candidate.Dispose();
                    return false;
                }

                stream = candidate;
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException)
        {
            stream?.Dispose();
            stream = null;
        }

        return false;
    }

    internal static bool DirectoryMatches(
        string path,
        ReviewedStagedFileIdentity expected)
    {
        if (!TryOpenDirectory(path, out var handle, out var actual))
        {
            return false;
        }

        using (handle)
        {
            return actual == expected;
        }
    }

    internal static bool TryMakeReadOnly(SafeFileHandle handle) =>
        !OperatingSystem.IsLinux() ||
        FChmod(checked((int)handle.DangerousGetHandle()), 0x100) == 0;

    internal static bool TrySetDirectoryWritable(
        SafeFileHandle handle,
        bool writable) =>
        !OperatingSystem.IsLinux() ||
        FChmod(
            checked((int)handle.DangerousGetHandle()),
            writable ? 0x1c0u : 0x140u) == 0;

    private static bool TryInspectDirectory(
        SafeFileHandle handle,
        out ReviewedStagedFileIdentity identity)
    {
        identity = default;
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var info) ||
                (info.FileAttributes & FileAttributeDirectory) == 0 ||
                (info.FileAttributes &
                    (FileAttributeDevice | FileAttributeReparsePoint)) != 0)
            {
                return false;
            }

            identity = new ReviewedStagedFileIdentity(
                info.VolumeSerialNumber,
                ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
            return true;
        }

        if (OperatingSystem.IsLinux())
        {
            var descriptor = checked((int)handle.DangerousGetHandle());
            if (FStat(descriptor, out var info) != 0 ||
                (info.Mode & FileTypeMask) != DirectoryFile)
            {
                return false;
            }

            identity = new ReviewedStagedFileIdentity(
                info.Device,
                info.Inode);
            return true;
        }

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

    [LibraryImport(
        "libc",
        EntryPoint = "openat",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAt(
        int directory,
        string path,
        int flags,
        uint mode);

    [LibraryImport(
        "libc",
        EntryPoint = "unlinkat",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UnlinkAt(
        int directory,
        string path,
        int flags);

    [LibraryImport(
        "libc",
        EntryPoint = "mkdirat",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MakeDirectoryAt(
        int directory,
        string path,
        uint mode);

    [LibraryImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static partial int FChmod(int descriptor, uint mode);

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
    private readonly ReviewedContentBudget _budget;
    private readonly string _parent;
    private readonly SafeFileHandle _parentHandle;
    private readonly ReviewedStagedFileIdentity _parentIdentity;
    private readonly string _prefix;
    private readonly List<ReviewedStagedBlob> _ownedBlobs = [];
    private bool _cleaned;
    private bool _cleanupComplete = true;

    private ReviewedBlobStagingLease(
        object authority,
        ReviewedContentBudget budget,
        string parent,
        SafeFileHandle parentHandle,
        ReviewedStagedFileIdentity parentIdentity)
    {
        _authority = authority;
        _budget = budget;
        _parent = parent;
        _parentHandle = parentHandle;
        _parentIdentity = parentIdentity;
        _prefix = "apr-reviewed-" + Guid.NewGuid().ToString("N");
    }

    internal static ReviewedBlobStagingLease? TryCreate(
        object authority,
        string parent,
        ReviewedContentBudget budget)
    {
        if (!ReviewedTreeReader.HasMintAuthority(authority) ||
            budget is null ||
            string.IsNullOrEmpty(parent) ||
            !Path.IsPathFullyQualified(parent))
        {
            return null;
        }

        try
        {
            var fullParent = Path.GetFullPath(parent);
            if (!Path.IsPathFullyQualified(fullParent) ||
                !ReviewedStagedFileAccess.TryOpenDirectory(
                    fullParent,
                    out var parentHandle,
                    out var parentIdentity))
            {
                return null;
            }

            return new ReviewedBlobStagingLease(
                authority,
                budget,
                fullParent,
                parentHandle!,
                parentIdentity);
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

        var fileName = _prefix + "-" + sha + ".stage";
        var path = Path.Join(_parent, fileName);
        if (!ReviewedStagedFileAccess.TryCreateStagedFile(
                _parentHandle,
                _parent,
                _parentIdentity,
                fileName,
                out var stream))
        {
            return null;
        }

        return new ReviewedBlobStageWriter(
            this,
            stream!,
            path,
            sha,
            declaredSize);
    }

    internal ReviewedStagedBlob? MintBlob(
        string path,
        string sha,
        long size,
        ReviewedStagedFileIdentity identity,
        FileStream source)
    {
        if (!CanMint(_authority, _budget, path, source))
        {
            return null;
        }

        var blob = ReviewedStagedBlob.Mint(
            _authority,
            this,
            _budget,
            path,
            sha,
            size,
            identity,
            source);
        _ownedBlobs.Add(blob);
        return blob;
    }

    internal bool CanMint(
        object authority,
        ReviewedContentBudget budget,
        string path,
        FileStream source) =>
        !_cleaned &&
        ReferenceEquals(_authority, authority) &&
        ReferenceEquals(_budget, budget) &&
        ReviewedTreeReader.HasMintAuthority(authority) &&
        Path.IsPathFullyQualified(path) &&
        StringComparer.Ordinal.Equals(Path.GetDirectoryName(path), _parent) &&
        !_parentHandle.IsClosed &&
        !source.SafeFileHandle.IsClosed;

    internal bool Owns(
        object authority,
        ReviewedContentBudget budget,
        ReviewedStagedBlob blob) =>
        !_cleaned &&
        ReferenceEquals(_authority, authority) &&
        ReferenceEquals(_budget, budget) &&
        ReviewedTreeReader.HasMintAuthority(authority) &&
        _ownedBlobs.Contains(blob);

    internal bool WasMintedBy(
        object authority,
        ReviewedContentBudget budget) =>
        !_cleaned &&
        ReferenceEquals(_authority, authority) &&
        ReferenceEquals(_budget, budget) &&
        ReviewedTreeReader.HasMintAuthority(authority) &&
        !_parentHandle.IsClosed;

    internal bool Cleanup()
    {
        if (_cleaned)
        {
            return _cleanupComplete;
        }

        _cleaned = true;
        foreach (var blob in _ownedBlobs)
        {
            try
            {
                blob.DisposeOwned();
            }
            catch (Exception exception) when (exception is IOException or
                UnauthorizedAccessException)
            {
                _cleanupComplete = false;
            }
        }

        try
        {
            _parentHandle.Dispose();
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException)
        {
            _cleanupComplete = false;
        }

        return _cleanupComplete;
    }
}

internal sealed class ReviewedBlobStageWriter : IAsyncDisposable
{
    private readonly ReviewedBlobStagingLease _owner;
    private readonly string _path;
    private readonly string _expectedSha;
    private readonly long _declaredSize;
    private readonly IncrementalHash _hash =
        IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
    private FileStream? _stream;
    private long _actualSize;

    internal ReviewedBlobStageWriter(
        ReviewedBlobStagingLease owner,
        FileStream stream,
        string path,
        string expectedSha,
        long declaredSize)
    {
        _owner = owner;
        _stream = stream;
        _path = path;
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
        if (!ReviewedStagedFileAccess.TryInspectRegular(
                _stream.SafeFileHandle,
                out var identity,
                out var length) ||
            length != _declaredSize ||
            !ReviewedStagedFileAccess.TryMakeReadOnly(
                _stream.SafeFileHandle))
        {
            return null;
        }

        var staged = _owner.MintBlob(
            _path,
            _expectedSha,
            _declaredSize,
            identity,
            _stream);
        if (staged is null)
        {
            return null;
        }

        _stream = null;
        _hash.Dispose();
        return staged;
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
