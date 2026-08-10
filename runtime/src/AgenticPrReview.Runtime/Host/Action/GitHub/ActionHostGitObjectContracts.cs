using AgenticPrReview.Runtime.ActionHost.Contracts;

namespace AgenticPrReview.Runtime.ActionHost.GitHub;

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
    string TreeSha);

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

internal sealed class ActionHostGitBlobReadBudget
{
    private ActionHostGitBlobReadBudget(
        int maximumResponseBytes,
        int maximumEncodedCharacters,
        int maximumDecodedBytes)
    {
        MaximumResponseBytes = maximumResponseBytes;
        MaximumEncodedCharacters = maximumEncodedCharacters;
        MaximumDecodedBytes = maximumDecodedBytes;
    }

    internal int MaximumResponseBytes { get; }

    internal int MaximumEncodedCharacters { get; }

    internal int MaximumDecodedBytes { get; }

    internal static ActionHostGitBlobReadBudget TrustedConfig { get; } =
        new(64 * 1024, 32 * 1024, 16 * 1024);

    internal static ActionHostGitBlobReadBudget TrustedInstructions
        { get; } = new(256 * 1024, 128 * 1024, 64 * 1024);

    internal static ActionHostGitBlobReadBudget MaximumSupported { get; } =
        new(2 * 1024 * 1024, 1536 * 1024, 1024 * 1024);
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
}
