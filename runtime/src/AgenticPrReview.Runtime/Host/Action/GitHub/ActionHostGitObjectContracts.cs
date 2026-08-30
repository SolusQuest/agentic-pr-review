using AgenticPrReview.Runtime.ActionHost.Contracts;

namespace AgenticPrReview.Runtime.ActionHost.GitHub;

using AgenticPrReview.Runtime.ActionHost.Snapshot;

internal enum ActionHostGitObjectFailure
{
    None = 0,
    InvalidRequest,
    NotFound,
    Unauthorized,
    Forbidden,
    RateLimited,
    UpstreamFailure,
    InvalidResponse,
    ResponseTooLarge,
    TransportFailure,
}

internal sealed class ActionHostGitObjectResult<T>
    where T : class
{
    private ActionHostGitObjectResult(
        T? value,
        ActionHostGitObjectFailure failure,
        int capturedResponseBytes)
    {
        Value = value;
        Failure = failure;
        CapturedResponseBytes = capturedResponseBytes;
    }

    internal T? Value { get; }

    internal ActionHostGitObjectFailure Failure { get; }

    internal int CapturedResponseBytes { get; }

    internal static ActionHostGitObjectResult<T> Success(
        T value,
        int capturedResponseBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (capturedResponseBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capturedResponseBytes));
        }

        return new(value, ActionHostGitObjectFailure.None,
            capturedResponseBytes);
    }

    internal static ActionHostGitObjectResult<T> Failed(
        ActionHostGitObjectFailure failure,
        int capturedResponseBytes = 0)
    {
        if (failure == ActionHostGitObjectFailure.None)
        {
            throw new ArgumentOutOfRangeException(nameof(failure));
        }

        if (capturedResponseBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capturedResponseBytes));
        }

        return new(null, failure, capturedResponseBytes);
    }
}

internal sealed record ActionHostGitCommitObject(
    string Sha,
    string TreeSha,
    IReadOnlyList<string>? ParentShas = null);

internal sealed record ActionHostGitTreeEntryObject(
    string Path,
    string Mode,
    string Type,
    string Sha,
    long? Size = null);

internal sealed record ActionHostGitTreeObject(
    string Sha,
    IReadOnlyList<ActionHostGitTreeEntryObject> Entries);

internal sealed record ActionHostGitBlobObject(
    string Sha,
    byte[] Bytes);

// The GitHub adapter owns the archive wire format.  Snapshot receives only a
// forward-only sequence of narrow entry views; it never gets a whole archive
// buffer or a gzip/tar parser authority.
internal enum ActionHostGitArchiveEntryType
{
    Directory,
    RegularFile,
    SymbolicLink,
    Unsupported,
}

internal sealed record ActionHostGitArchiveEntry(
    string Name,
    ActionHostGitArchiveEntryType EntryType,
    int Mode,
    long Length,
    string? LinkName,
    Stream? DataStream);

internal abstract class ActionHostGitArchiveReader : IDisposable
{
    internal abstract int CapturedResponseBytes { get; }

    internal abstract Task<ActionHostGitArchiveEntry?> GetNextEntryAsync(
        CancellationToken cancellationToken);

    public abstract void Dispose();
}

// Archive wire parsing stays at the GitHub edge, but Snapshot needs the
// bounded-size cause to distinguish an admitted-size exhaustion from a
// malformed identity payload.  Do not use this for path or Git identity
// decisions; those remain Snapshot authority.
internal enum ActionHostGitArchiveReadFailure
{
    CompressedLimitExceeded,
    DecodedLimitExceeded,
}

internal sealed class ActionHostGitArchiveReadException : IOException
{
    internal ActionHostGitArchiveReadException(
        ActionHostGitArchiveReadFailure failure)
        : base("The codeload archive exceeds its configured byte limit.")
    {
        Failure = failure;
    }

    internal ActionHostGitArchiveReadFailure Failure { get; }
}

internal sealed class ActionHostGitBlobReadBudget
{
    private ActionHostGitBlobReadBudget(
        int maximumResponseBytes,
        int maximumEncodedCharacters,
        int maximumDecodedBytes)
    {
        if (maximumResponseBytes <= MaximumBlobJsonEnvelopeBytes ||
            maximumEncodedCharacters < 0 ||
            maximumDecodedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResponseBytes));
        }

        MaximumResponseBytes = maximumResponseBytes;
        MaximumEncodedCharacters = maximumEncodedCharacters;
        MaximumDecodedBytes = maximumDecodedBytes;
        MaximumWhitespaceCharacters = checked(
            (maximumResponseBytes - MaximumBlobJsonEnvelopeBytes -
                maximumEncodedCharacters) /
            MaximumJsonEscapedWhitespaceBytes);
    }

    internal int MaximumResponseBytes { get; }

    internal int MaximumEncodedCharacters { get; }

    internal int MaximumDecodedBytes { get; }

    // The codec accepts only CRLF or LF between complete Base64 quanta. JSON
    // may represent each admitted control character as a six-byte \u00XX
    // escape, so reserve that worst-case wire cost plus a closed object
    // envelope. This makes an accepted maximum-size wrapped blob fit under
    // MaximumResponseBytes rather than relying on ReadBoundedAsync to reject
    // a presentation the codec would otherwise admit.
    internal int MaximumWhitespaceCharacters { get; }

    private const int MaximumBlobJsonEnvelopeBytes = 1_024;
    private const int MaximumJsonEscapedWhitespaceBytes = 6;

    internal static ActionHostGitBlobReadBudget TrustedConfig { get; } =
        new(64 * 1024, 32 * 1024, 16 * 1024);

    internal static ActionHostGitBlobReadBudget TrustedInstructions { get; } =
        new(256 * 1024, 128 * 1024, 64 * 1024);

    private const int MaximumSupportedDecodedBytes =
        checked((int)ReviewedContentLimits.HeadBlobBytes);
    private const int MaximumSupportedEncodedCharacters =
        4 * ((MaximumSupportedDecodedBytes + 2) / 3);

    internal static ActionHostGitBlobReadBudget MaximumSupported { get; } =
        new(
            checked((int)ReviewedContentLimits.GitObjectResponseBytes),
            MaximumSupportedEncodedCharacters,
            MaximumSupportedDecodedBytes);
}

internal interface IActionHostGitObjectTransportFactory
{
    IActionHostGitObjectTransport CreateExactObjectTransport(
        ActionHostGitHubToken token);
}

internal interface IActionHostGitObjectTransport : IDisposable
{
    Task<ActionHostGitObjectResult<ActionHostGitCommitObject>>
        GetCommitObjectAsync(
            string repositoryName,
            string commitSha,
            CancellationToken cancellationToken);

    Task<ActionHostGitObjectResult<ActionHostGitTreeObject>>
        GetTreeObjectAsync(
            string repositoryName,
            string treeSha,
            CancellationToken cancellationToken);

    Task<ActionHostGitObjectResult<ActionHostGitBlobObject>>
        GetBlobObjectAsync(
            string repositoryName,
            string blobSha,
            ActionHostGitBlobReadBudget budget,
            CancellationToken cancellationToken);

    Task<ActionHostGitObjectResult<ActionHostGitArchiveReader>>
        GetHeadArchiveAsync(
            string repositoryName,
            string headSha,
            CancellationToken cancellationToken);
}

internal sealed class ActionHostBoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly int _maximumBytes;
    private int _capturedBytes;
    private bool _exceeded;

    internal ActionHostBoundedReadStream(Stream inner, int maximumBytes)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _maximumBytes = maximumBytes > 0 ? maximumBytes :
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
    }

    internal int CapturedBytes => _capturedBytes;
    internal bool Exceeded => _exceeded;
    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) =>
        Count(_inner.Read(buffer, offset, count));
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        Count(await _inner.ReadAsync(buffer, cancellationToken));
    private int Count(int read)
    {
        if (read > 0 && _capturedBytes > _maximumBytes - read)
        {
            _capturedBytes = checked(_maximumBytes + 1);
            _exceeded = true;
            throw new InvalidDataException("The archive response exceeds its byte limit.");
        }

        _capturedBytes += read;
        return read;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
