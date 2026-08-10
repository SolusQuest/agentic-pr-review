using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Snapshot.GitObjects;

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
    internal ReviewedTreePathRecord(
        string path,
        string mode,
        ReviewedTreeEntryKind kind,
        string objectSha,
        long? size,
        ReviewedStagedBlob? stagedBlob)
    {
        if (!ReviewedTreePath.IsValid(path) ||
            !ReviewedGitObjectValidation.IsSha(objectSha) ||
            !ModeMatches(kind, mode) ||
            kind == ReviewedTreeEntryKind.Regular != (stagedBlob is not null) ||
            kind == ReviewedTreeEntryKind.Regular != (size is not null) ||
            size is < 0 ||
            stagedBlob is not null &&
            (!StringComparer.Ordinal.Equals(stagedBlob.Sha, objectSha) ||
                stagedBlob.Size != size))
        {
            throw new ArgumentException("Reviewed-tree record is invalid.");
        }

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

    internal ReviewedTreeSnapshot(
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
        if (repositoryId <= 0 || pullRequestNumber <= 0 ||
            !ReviewedGitObjectValidation.IsSha(headSha) ||
            !ReviewedGitObjectValidation.IsSha(rootTreeSha))
        {
            throw new ArgumentException("Reviewed-tree root identity is invalid.");
        }

        var materialized = records.ToArray();
        if (materialized.Any(static record => record is null))
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
        Identity = ReviewedTreeIdentityWriter.Create(
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

    internal static ReviewedTreeIdentity Create(
        long repositoryId,
        long pullRequestNumber,
        string headSha,
        string rootTreeSha,
        ImmutableArray<ReviewedTreePathRecord> records)
    {
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

internal sealed class ReviewedStagedBlob
{
    private readonly string _path;

    internal ReviewedStagedBlob(string path, string sha, long size)
    {
        if (!Path.IsPathFullyQualified(path) ||
            !ReviewedGitObjectValidation.IsSha(sha) ||
            size is < 0 or > ReviewedContentLimits.HeadBlobBytes)
        {
            throw new ArgumentException("Reviewed staged blob is invalid.");
        }

        _path = path;
        Sha = sha;
        Size = size;
    }

    internal string Sha { get; }

    internal long Size { get; }

    internal async Task<bool> CopyVerifiedToAsync(
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.LinkTarget is not null ||
                (info.Attributes & (FileAttributes.Directory |
                    FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                return false;
            }

            await using var source = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var bytes = GC.AllocateUninitializedArray<byte>(checked((int)Size));
            var actual = 0;
            while (actual < bytes.Length)
            {
                var read = await source.ReadAsync(
                    bytes.AsMemory(actual),
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                actual += read;
            }

            var extra = new byte[1];
            var extraRead = await source.ReadAsync(
                extra,
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

internal sealed class ReviewedBlobStagingLease
{
    private readonly string _root;
    private readonly HashSet<string> _ownedPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _cleaned;

    private ReviewedBlobStagingLease(string root)
    {
        _root = root;
    }

    internal static ReviewedBlobStagingLease? TryCreate(string parent)
    {
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
                var root = Path.Combine(
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

                return new ReviewedBlobStagingLease(root);
            }
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
        }

        return null;
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

        var partial = Path.Combine(_root, sha + ".partial");
        var final = Path.Combine(_root, sha + ".blob");
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
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(_finalPath, UnixFileMode.UserRead);
        }
        else
        {
            File.SetAttributes(_finalPath, FileAttributes.ReadOnly);
        }

        _owner.Commit(_partialPath, _finalPath);
        return new ReviewedStagedBlob(
            _finalPath,
            _expectedSha,
            _declaredSize);
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
