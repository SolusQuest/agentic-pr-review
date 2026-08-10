using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Snapshot;

namespace AgenticPrReview.Runtime.ActionHost.Snapshot.GitObjects;

internal enum ReviewedGitObjectFailure
{
    None = 0,
    InvalidRequest,
    UnsupportedSize,
    NotFound,
    Unauthorized,
    Forbidden,
    RateLimited,
    UpstreamFailure,
    InvalidResponse,
    IdentityMismatch,
    TransportFailure,
    StagingFailure,
}

internal sealed class ReviewedGitObjectResult<T>
    where T : class
{
    private ReviewedGitObjectResult(
        T? value,
        ReviewedGitObjectFailure failure)
    {
        Value = value;
        Failure = failure;
    }

    internal T? Value { get; }

    internal ReviewedGitObjectFailure Failure { get; }

    internal static ReviewedGitObjectResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, ReviewedGitObjectFailure.None);
    }

    internal static ReviewedGitObjectResult<T> Failed(
        ReviewedGitObjectFailure failure)
    {
        if (failure == ReviewedGitObjectFailure.None)
        {
            throw new ArgumentOutOfRangeException(nameof(failure));
        }

        return new(null, failure);
    }
}

internal sealed record ReviewedGitCommitFact(
    string Sha,
    string TreeSha);

internal sealed record ReviewedGitTreeEntryFact(
    string Path,
    string Mode,
    string Type,
    string Sha,
    long? Size);

internal sealed record ReviewedGitTreeFact(
    string Sha,
    IReadOnlyList<ReviewedGitTreeEntryFact> Entries);

internal interface IReviewedGitObjectTransportFactory
{
    IReviewedGitObjectTransport Create(
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        ActionHostGitHubToken token,
        ReviewedContentBudget budget);
}

internal interface IReviewedGitObjectTransport : IDisposable
{
    Task<ReviewedGitObjectResult<ReviewedGitCommitFact>> GetCommitAsync(
        CancellationToken cancellationToken);

    Task<ReviewedGitObjectResult<ReviewedGitTreeFact>> GetTreeAsync(
        string treeSha,
        CancellationToken cancellationToken);

    Task<ReviewedGitObjectResult<ReviewedStagedBlob>> StageBlobAsync(
        string blobSha,
        long declaredSize,
        ReviewedBlobStagingLease staging,
        CancellationToken cancellationToken);
}

internal static class ReviewedGitObjectValidation
{
    internal static bool IsSha(string? value) =>
        value is not null &&
        value.Length == 40 &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool IsRepositoryName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200)
        {
            return false;
        }

        var parts = value.Split('/');
        return parts.Length == 2 && parts.All(static part =>
            part.Length is > 0 and <= 100 &&
            part.All(static character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '_' or '.'));
    }
}
