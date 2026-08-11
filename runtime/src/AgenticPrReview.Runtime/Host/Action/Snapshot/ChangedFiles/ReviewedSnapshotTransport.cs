using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Snapshot.Diff;
using AgenticPrReview.Runtime.ActionHost.Snapshot.GitObjects;

namespace AgenticPrReview.Runtime.ActionHost.Snapshot.ChangedFiles;

internal enum ReviewedSnapshotReadFailure
{
    None = 0,
    InvalidRequest,
    UnsupportedSize,
    NotFound,
    Unauthorized,
    Forbidden,
    RateLimited,
    UpstreamUnavailable,
    InvalidResponse,
    IdentityMismatch,
    TransportFailure,
    StagingFailure,
    Cancelled,
}

internal sealed class ReviewedSnapshotReadResult<T>
    where T : class
{
    private ReviewedSnapshotReadResult(
        T? value,
        ReviewedSnapshotReadFailure failure)
    {
        Value = value;
        Failure = failure;
    }

    internal T? Value { get; }
    internal ReviewedSnapshotReadFailure Failure { get; }

    internal static ReviewedSnapshotReadResult<T> Success(T value) =>
        new(value, ReviewedSnapshotReadFailure.None);

    internal static ReviewedSnapshotReadResult<T> Failed(
        ReviewedSnapshotReadFailure failure) => new(null, failure);
}

internal interface IReviewedSnapshotTransportFactory
{
    IReviewedSnapshotTransport Create(
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        ActionHostGitHubToken token,
        ReviewedContentBudget budget);
}

internal interface IReviewedSnapshotTransport : IDisposable
{
    Task<ReviewedSnapshotReadResult<ActionHostGitHubPullRequestFact>>
        GetCurrentPullRequestAsync(CancellationToken cancellationToken);

    Task<ReviewedSnapshotReadResult<ActionHostPullRequestFilePageObject>>
        GetPullRequestFilesAsync(
            int page,
            CancellationToken cancellationToken);

    Task<ReviewedSnapshotReadResult<ActionHostGitCommitObject>>
        GetBaseCommitAsync(CancellationToken cancellationToken);

    Task<ReviewedSnapshotReadResult<ActionHostGitTreeObject>>
        GetTreeAsync(
            string treeSha,
            CancellationToken cancellationToken);

    Task<ReviewedSnapshotReadResult<ReviewedBaseStagedBlob>>
        StageBaseBlobAsync(
            string blobSha,
            long declaredSize,
            ReviewedBaseBlobStagingLease staging,
            CancellationToken cancellationToken);
}

internal sealed class ReviewedSnapshotTransportFactory :
    IReviewedSnapshotTransportFactory
{
    private static readonly object FactoryAuthority = new();
    private readonly IActionHostReviewedSnapshotTransportFactory _shared;

    internal ReviewedSnapshotTransportFactory(
        IActionHostReviewedSnapshotTransportFactory shared)
    {
        _shared = shared ?? throw new ArgumentNullException(nameof(shared));
    }

    public IReviewedSnapshotTransport Create(
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        ActionHostGitHubToken token,
        ReviewedContentBudget budget)
    {
        ArgumentNullException.ThrowIfNull(token);
        return ReviewedSnapshotTransport.Mint(
            FactoryAuthority,
            invocation,
            budget,
            _shared.CreateReviewedSnapshotTransport(token));
    }

    internal static bool HasAuthority(object authority) =>
        ReferenceEquals(authority, FactoryAuthority);
}

internal sealed class ReviewedSnapshotTransport : IReviewedSnapshotTransport
{
    private readonly string _repositoryName;
    private readonly long _pullRequestNumber;
    private readonly string _baseSha;
    private readonly ReviewedContentBudget _budget;
    private readonly IActionHostReviewedSnapshotTransport _shared;
    private bool _disposed;

    private ReviewedSnapshotTransport(
        string repositoryName,
        long pullRequestNumber,
        string baseSha,
        ReviewedContentBudget budget,
        IActionHostReviewedSnapshotTransport shared)
    {
        _repositoryName = repositoryName;
        _pullRequestNumber = pullRequestNumber;
        _baseSha = baseSha;
        _budget = budget;
        _shared = shared;
    }

    internal static ReviewedSnapshotTransport Mint(
        object authority,
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        ReviewedContentBudget budget,
        IActionHostReviewedSnapshotTransport shared)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(shared);
        var pullRequest = invocation.PullRequest;
        if (!ReviewedSnapshotTransportFactory.HasAuthority(authority) ||
            pullRequest.RepositoryId <= 0 ||
            pullRequest.Number <= 0 ||
            pullRequest.BaseRepositoryId != pullRequest.RepositoryId ||
            pullRequest.HeadRepositoryId != pullRequest.RepositoryId ||
            !StringComparer.Ordinal.Equals(
                pullRequest.BaseRepositoryName,
                pullRequest.HeadRepositoryName) ||
            !ReviewedGitObjectValidation.IsRepositoryName(
                pullRequest.BaseRepositoryName) ||
            !ReviewedGitObjectValidation.IsSha(pullRequest.BaseSha) ||
            !ReviewedGitObjectValidation.IsSha(pullRequest.HeadSha))
        {
            shared.Dispose();
            throw new ReviewedGitObjectCredentialException();
        }

        return new(
            pullRequest.BaseRepositoryName,
            pullRequest.Number,
            pullRequest.BaseSha,
            budget,
            shared);
    }

    public Task<ReviewedSnapshotReadResult<ActionHostGitHubPullRequestFact>>
        GetCurrentPullRequestAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(
            token => _shared.GetCurrentPullRequestAsync(
                _repositoryName,
                _pullRequestNumber,
                token),
            cancellationToken);

    public Task<ReviewedSnapshotReadResult<ActionHostPullRequestFilePageObject>>
        GetPullRequestFilesAsync(
            int page,
            CancellationToken cancellationToken)
    {
        if (page <= 0)
        {
            return Task.FromResult(ReviewedSnapshotReadResult<
                ActionHostPullRequestFilePageObject>.Failed(
                    ReviewedSnapshotReadFailure.InvalidRequest));
        }

        return ExecuteAsync(
            token => _shared.GetPullRequestFilesAsync(
                _repositoryName,
                _pullRequestNumber,
                page,
                ReviewedContentLimits.ChangedFilesPerPage,
                token),
            cancellationToken);
    }

    public Task<ReviewedSnapshotReadResult<ActionHostGitCommitObject>>
        GetBaseCommitAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(
            token => _shared.GetCommitObjectAsync(
                _repositoryName,
                _baseSha,
                token),
            cancellationToken);

    public Task<ReviewedSnapshotReadResult<ActionHostGitTreeObject>>
        GetTreeAsync(
            string treeSha,
            CancellationToken cancellationToken)
    {
        if (!ReviewedGitObjectValidation.IsSha(treeSha))
        {
            return Task.FromResult(ReviewedSnapshotReadResult<
                ActionHostGitTreeObject>.Failed(
                    ReviewedSnapshotReadFailure.InvalidRequest));
        }

        return ExecuteAsync(
            token => _shared.GetTreeObjectAsync(
                _repositoryName,
                treeSha,
                token),
            cancellationToken);
    }

    public async Task<ReviewedSnapshotReadResult<ReviewedBaseStagedBlob>>
        StageBaseBlobAsync(
            string blobSha,
            long declaredSize,
            ReviewedBaseBlobStagingLease staging,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staging);
        if (!ReviewedGitObjectValidation.IsSha(blobSha) || declaredSize < 0)
        {
            return ReviewedSnapshotReadResult<ReviewedBaseStagedBlob>.Failed(
                ReviewedSnapshotReadFailure.InvalidRequest);
        }

        if (declaredSize > ReviewedContentLimits.BaseBlobBytes)
        {
            return ReviewedSnapshotReadResult<ReviewedBaseStagedBlob>.Failed(
                ReviewedSnapshotReadFailure.UnsupportedSize);
        }

        if (!TryBeginRequest(cancellationToken, out var operation))
        {
            return ReviewedSnapshotReadResult<ReviewedBaseStagedBlob>.Failed(
                ReviewedSnapshotReadFailure.UnsupportedSize);
        }

        using (operation)
        await using (var writer = staging.TryCreateWriter(blobSha, declaredSize))
        {
            if (writer is null)
            {
                return ReviewedSnapshotReadResult<ReviewedBaseStagedBlob>.Failed(
                    ReviewedSnapshotReadFailure.StagingFailure);
            }

            try
            {
                var result = await _shared.CopyBlobObjectAsync(
                    _repositoryName,
                    blobSha,
                    declaredSize,
                    writer,
                    operation!.Token);
                if (!TryCharge(result.CapturedResponseBytes, operation.Token))
                {
                    return ReviewedSnapshotReadResult<ReviewedBaseStagedBlob>
                        .Failed(ReviewedSnapshotReadFailure.UnsupportedSize);
                }

                if (result.Value is null)
                {
                    return ReviewedSnapshotReadResult<ReviewedBaseStagedBlob>
                        .Failed(MapFailure(result.Failure));
                }

                var staged = await writer.CompleteAsync(operation.Token);
                return staged is null
                    ? ReviewedSnapshotReadResult<ReviewedBaseStagedBlob>.Failed(
                        ReviewedSnapshotReadFailure.IdentityMismatch)
                    : ReviewedSnapshotReadResult<ReviewedBaseStagedBlob>.Success(
                        staged);
            }
            catch (OperationCanceledException) when (operation!.DeadlineExpired)
            {
                return ReviewedSnapshotReadResult<ReviewedBaseStagedBlob>.Failed(
                    ReviewedSnapshotReadFailure.UnsupportedSize);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsNonFatal(exception))
            {
                return ReviewedSnapshotReadResult<ReviewedBaseStagedBlob>.Failed(
                    ReviewedSnapshotReadFailure.TransportFailure);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shared.Dispose();
    }

    private async Task<ReviewedSnapshotReadResult<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<ActionHostGitObjectResult<T>>> action,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!TryBeginRequest(cancellationToken, out var operation))
        {
            return ReviewedSnapshotReadResult<T>.Failed(
                ReviewedSnapshotReadFailure.UnsupportedSize);
        }

        using (operation)
        {
            try
            {
                var result = await action(operation!.Token);
                if (!TryCharge(result.CapturedResponseBytes, operation.Token))
                {
                    return ReviewedSnapshotReadResult<T>.Failed(
                        ReviewedSnapshotReadFailure.UnsupportedSize);
                }

                return result.Value is { } value
                    ? ReviewedSnapshotReadResult<T>.Success(value)
                    : ReviewedSnapshotReadResult<T>.Failed(
                        MapFailure(result.Failure));
            }
            catch (OperationCanceledException) when (operation!.DeadlineExpired)
            {
                return ReviewedSnapshotReadResult<T>.Failed(
                    ReviewedSnapshotReadFailure.UnsupportedSize);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsNonFatal(exception))
            {
                return ReviewedSnapshotReadResult<T>.Failed(
                    ReviewedSnapshotReadFailure.TransportFailure);
            }
        }
    }

    private bool TryBeginRequest(
        CancellationToken cancellationToken,
        out ReviewedContentBudget.OperationLease? operation)
    {
        operation = null;
        return !_disposed &&
            _budget.TryReserveRequest(cancellationToken) &&
            _budget.TryBeginOperation(cancellationToken, out operation);
    }

    private bool TryCharge(
        int capturedResponseBytes,
        CancellationToken cancellationToken)
    {
        long responseBytes = 0;
        return _budget.TryConsumeResponseBytes(
            ref responseBytes,
            capturedResponseBytes,
            cancellationToken);
    }

    private static ReviewedSnapshotReadFailure MapFailure(
        ActionHostGitObjectFailure failure) => failure switch
        {
            ActionHostGitObjectFailure.InvalidRequest =>
                ReviewedSnapshotReadFailure.InvalidRequest,
            ActionHostGitObjectFailure.NotFound =>
                ReviewedSnapshotReadFailure.NotFound,
            ActionHostGitObjectFailure.Unauthorized =>
                ReviewedSnapshotReadFailure.Unauthorized,
            ActionHostGitObjectFailure.Forbidden =>
                ReviewedSnapshotReadFailure.Forbidden,
            ActionHostGitObjectFailure.RateLimited =>
                ReviewedSnapshotReadFailure.RateLimited,
            ActionHostGitObjectFailure.UpstreamFailure =>
                ReviewedSnapshotReadFailure.UpstreamUnavailable,
            ActionHostGitObjectFailure.InvalidResponse =>
                ReviewedSnapshotReadFailure.InvalidResponse,
            ActionHostGitObjectFailure.ResponseTooLarge =>
                ReviewedSnapshotReadFailure.UnsupportedSize,
            _ => ReviewedSnapshotReadFailure.TransportFailure,
        };

    private static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;
}
