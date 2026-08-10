using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgenticPrReview.Runtime.ActionHost.Snapshot.Diff;

internal sealed class ReviewedBaseBlobStagingLease : IDisposable
{
    private readonly ReviewedContentBudget _budget;
    private readonly string _parent;
    private readonly SafeFileHandle _parentHandle;
    private readonly ReviewedStagedFileIdentity _parentIdentity;
    private readonly string _prefix;
    private readonly List<ReviewedBaseStagedBlob> _blobs = [];
    private bool _disposed;
    private bool _cleanupComplete = true;

    private ReviewedBaseBlobStagingLease(
        ReviewedContentBudget budget,
        string parent,
        SafeFileHandle parentHandle,
        ReviewedStagedFileIdentity parentIdentity)
    {
        _budget = budget;
        _parent = parent;
        _parentHandle = parentHandle;
        _parentIdentity = parentIdentity;
        _prefix = "apr-base-" + Guid.NewGuid().ToString("N");
    }

    internal bool CleanupComplete => _cleanupComplete;

    internal static ReviewedBaseBlobStagingLease? TryCreate(
        string parent,
        ReviewedContentBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (string.IsNullOrEmpty(parent) || !Path.IsPathFullyQualified(parent))
        {
            return null;
        }

        try
        {
            var fullParent = Path.GetFullPath(parent);
            if (!ReviewedStagedFileAccess.TryOpenDirectory(
                    fullParent,
                    out var handle,
                    out var identity))
            {
                return null;
            }

            return new(budget, fullParent, handle!, identity);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or ArgumentException or
            NotSupportedException)
        {
            return null;
        }
    }

    internal ReviewedBaseBlobStageWriter? TryCreateWriter(
        string sha,
        long declaredSize)
    {
        if (_disposed ||
            !GitObjects.ReviewedGitObjectValidation.IsSha(sha) ||
            declaredSize is < 0 or > ReviewedContentLimits.BaseBlobBytes)
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

        return new(this, stream!, path, sha, declaredSize);
    }

    internal ReviewedBaseStagedBlob? Admit(
        string path,
        string sha,
        long size,
        ReviewedStagedFileIdentity identity,
        FileStream stream)
    {
        if (_disposed ||
            !Path.IsPathFullyQualified(path) ||
            !StringComparer.Ordinal.Equals(Path.GetDirectoryName(path), _parent) ||
            stream.SafeFileHandle.IsClosed)
        {
            return null;
        }

        var blob = new ReviewedBaseStagedBlob(
            _budget,
            sha,
            size,
            identity,
            stream);
        _blobs.Add(blob);
        return blob;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var blob in _blobs)
        {
            try
            {
                blob.Dispose();
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
    }
}

internal sealed class ReviewedBaseBlobStageWriter : Stream
{
    private readonly ReviewedBaseBlobStagingLease _owner;
    private readonly string _path;
    private readonly string _expectedSha;
    private readonly long _declaredSize;
    private readonly IncrementalHash _hash =
        IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
    private FileStream? _stream;
    private long _actualSize;
    private bool _completed;

    internal ReviewedBaseBlobStageWriter(
        ReviewedBaseBlobStagingLease owner,
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
        _hash.AppendData(GitBlobHeader(declaredSize));
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => _stream is not null;
    public override long Length => _actualSize;
    public override long Position
    {
        get => _actualSize;
        set => throw new NotSupportedException();
    }

    public override void Flush() =>
        (_stream ?? throw new ObjectDisposedException(nameof(Stream))).Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        (_stream ?? throw new ObjectDisposedException(nameof(Stream)))
            .FlushAsync(cancellationToken);

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateWrite(count);
        _stream!.Write(buffer, offset, count);
        _hash.AppendData(buffer, offset, count);
        _actualSize += count;
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ValidateWrite(buffer.Length);
        await _stream!.WriteAsync(buffer, cancellationToken);
        _hash.AppendData(buffer.Span);
        _actualSize += buffer.Length;
    }

    internal async Task<ReviewedBaseStagedBlob?> CompleteAsync(
        CancellationToken cancellationToken)
    {
        if (_stream is null || _completed)
        {
            return null;
        }

        _completed = true;
        var actualSha = Convert.ToHexString(_hash.GetHashAndReset())
            .ToLowerInvariant();
        if (_actualSize != _declaredSize ||
            !StringComparer.Ordinal.Equals(actualSha, _expectedSha))
        {
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

        var admitted = _owner.Admit(
            _path,
            _expectedSha,
            _declaredSize,
            identity,
            _stream);
        if (admitted is null)
        {
            return null;
        }

        _stream = null;
        _hash.Dispose();
        return admitted;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stream?.Dispose();
            _stream = null;
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    private void ValidateWrite(int count)
    {
        if (_stream is null)
        {
            throw new ObjectDisposedException(nameof(Stream));
        }

        if (count < 0 || _actualSize > _declaredSize - count)
        {
            throw new IOException("Base blob exceeded its declared size.");
        }
    }

    private static byte[] GitBlobHeader(long size) => Encoding.ASCII.GetBytes(
        "blob " + size.ToString(CultureInfo.InvariantCulture) + "\0");
}

internal sealed class ReviewedBaseStagedBlob : IDisposable
{
    private readonly ReviewedContentBudget _budget;
    private readonly ReviewedStagedFileIdentity _identity;
    private readonly FileStream _stream;
    private bool _disposed;

    internal ReviewedBaseStagedBlob(
        ReviewedContentBudget budget,
        string sha,
        long size,
        ReviewedStagedFileIdentity identity,
        FileStream stream)
    {
        _budget = budget;
        Sha = sha;
        Size = size;
        _identity = identity;
        _stream = stream;
    }

    internal string Sha { get; }
    internal long Size { get; }

    internal async Task<byte[]?> ReadVerifiedAsync(
        CancellationToken cancellationToken)
    {
        if (_disposed ||
            !_budget.TryBeginOperation(cancellationToken, out var operation))
        {
            return null;
        }

        using (operation)
        {
            if (!ReviewedStagedFileAccess.TryInspectRegular(
                    _stream.SafeFileHandle,
                    out var identity,
                    out var length) ||
                identity != _identity || length != Size)
            {
                return null;
            }

            var bytes = GC.AllocateUninitializedArray<byte>(checked((int)Size));
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = await RandomAccess.ReadAsync(
                    _stream.SafeFileHandle,
                    bytes.AsMemory(offset),
                    offset,
                    operation!.Token);
                if (read == 0)
                {
                    return null;
                }

                offset += read;
            }

            var extra = new byte[1];
            if (await RandomAccess.ReadAsync(
                    _stream.SafeFileHandle,
                    extra,
                    offset,
                    operation!.Token) != 0)
            {
                return null;
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            hash.AppendData(Encoding.ASCII.GetBytes(
                "blob " + Size.ToString(CultureInfo.InvariantCulture) + "\0"));
            hash.AppendData(bytes);
            return StringComparer.Ordinal.Equals(
                    Convert.ToHexString(hash.GetHashAndReset())
                        .ToLowerInvariant(),
                    Sha)
                ? bytes
                : null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
    }
}
