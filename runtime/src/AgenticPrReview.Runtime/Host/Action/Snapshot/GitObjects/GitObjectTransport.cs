using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;

namespace AgenticPrReview.Runtime.ActionHost.Snapshot.GitObjects;

internal sealed class ReviewedGitObjectTransportFactory :
    IReviewedGitObjectTransportFactory
{
    private static readonly object FactoryAuthority = new();
    private readonly IActionHostGitObjectTransportFactory _sharedFactory;

    internal ReviewedGitObjectTransportFactory(
        IActionHostGitObjectTransportFactory sharedFactory)
    {
        _sharedFactory = sharedFactory ??
            throw new ArgumentNullException(nameof(sharedFactory));
    }

    public IReviewedGitObjectTransport Create(
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        ActionHostGitHubToken token,
        ReviewedContentBudget budget)
    {
        ArgumentNullException.ThrowIfNull(token);
        return ReviewedGitObjectTransport.Mint(
            FactoryAuthority,
            invocation,
            budget,
            _sharedFactory.CreateExactObjectTransport(token));
    }

    internal static bool HasFactoryAuthority(object authority) =>
        ReferenceEquals(authority, FactoryAuthority);
}

internal sealed class ReviewedGitObjectCredentialException : Exception
{
    internal ReviewedGitObjectCredentialException()
        : base("The reviewed Git-object authority is invalid.")
    {
    }
}

internal sealed class ReviewedGitObjectTransport :
    IReviewedGitObjectTransport
{
    private readonly string _repositoryName;
    private readonly string _headSha;
    private readonly ReviewedContentBudget _budget;
    private readonly IActionHostGitObjectTransport _sharedTransport;
    private bool _disposed;

    private ReviewedGitObjectTransport(
        string repositoryName,
        string headSha,
        ReviewedContentBudget budget,
        IActionHostGitObjectTransport sharedTransport)
    {
        _repositoryName = repositoryName;
        _headSha = headSha;
        _budget = budget;
        _sharedTransport = sharedTransport;
    }

    internal static ReviewedGitObjectTransport Mint(
        object authority,
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        ReviewedContentBudget budget,
        IActionHostGitObjectTransport sharedTransport)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(sharedTransport);
        if (!ReviewedGitObjectTransportFactory.HasFactoryAuthority(authority) ||
            !TryAuthorizedSource(
                invocation,
                out var repositoryName,
                out var headSha))
        {
            sharedTransport.Dispose();
            throw new ReviewedGitObjectCredentialException();
        }

        return new ReviewedGitObjectTransport(
            repositoryName,
            headSha,
            budget,
            sharedTransport);
    }

    public async Task<ReviewedGitObjectResult<ReviewedGitCommitFact>>
        GetCommitAsync(CancellationToken cancellationToken)
    {
        if (!TryBeginRequest(cancellationToken, out var operation))
        {
            return ReviewedGitObjectResult<ReviewedGitCommitFact>.Failed(
                ReviewedGitObjectFailure.UnsupportedSize);
        }

        using (operation)
        {
            try
            {
                var result = await _sharedTransport.GetCommitObjectAsync(
                    _repositoryName,
                    _headSha,
                    operation!.Token);
                if (!TryCharge(result.CapturedResponseBytes, operation.Token))
                {
                    return ReviewedGitObjectResult<ReviewedGitCommitFact>
                        .Failed(ReviewedGitObjectFailure.UnsupportedSize);
                }

                return result.Value is { } value
                    ? ReviewedGitObjectResult<ReviewedGitCommitFact>.Success(
                        new(value.Sha, value.TreeSha))
                    : ReviewedGitObjectResult<ReviewedGitCommitFact>.Failed(
                        MapFailure(result.Failure));
            }
            catch (OperationCanceledException)
                when (operation!.DeadlineExpired)
            {
                return ReviewedGitObjectResult<ReviewedGitCommitFact>.Failed(
                    ReviewedGitObjectFailure.UnsupportedSize);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsNonFatal(exception))
            {
                return ReviewedGitObjectResult<ReviewedGitCommitFact>.Failed(
                    ReviewedGitObjectFailure.TransportFailure);
            }
        }
    }

    public async Task<ReviewedGitObjectResult<ReviewedGitTreeFact>>
        GetTreeAsync(
            string treeSha,
            CancellationToken cancellationToken)
    {
        if (!ReviewedGitObjectValidation.IsSha(treeSha))
        {
            return ReviewedGitObjectResult<ReviewedGitTreeFact>.Failed(
                ReviewedGitObjectFailure.InvalidRequest);
        }

        if (!TryBeginRequest(cancellationToken, out var operation))
        {
            return ReviewedGitObjectResult<ReviewedGitTreeFact>.Failed(
                ReviewedGitObjectFailure.UnsupportedSize);
        }

        using (operation)
        {
            try
            {
                var result = await _sharedTransport.GetTreeObjectAsync(
                    _repositoryName,
                    treeSha,
                    operation!.Token);
                if (!TryCharge(result.CapturedResponseBytes, operation.Token))
                {
                    return ReviewedGitObjectResult<ReviewedGitTreeFact>.Failed(
                        ReviewedGitObjectFailure.UnsupportedSize);
                }

                if (result.Value is null)
                {
                    return ReviewedGitObjectResult<ReviewedGitTreeFact>.Failed(
                        MapFailure(result.Failure));
                }

                var entries = result.Value.Entries.Select(static entry =>
                    new ReviewedGitTreeEntryFact(
                        entry.Path,
                        entry.Mode,
                        entry.Type,
                        entry.Sha,
                        entry.Size)).ToArray();
                return ReviewedGitObjectResult<ReviewedGitTreeFact>.Success(
                    new(result.Value.Sha, entries));
            }
            catch (OperationCanceledException)
                when (operation!.DeadlineExpired)
            {
                return ReviewedGitObjectResult<ReviewedGitTreeFact>.Failed(
                    ReviewedGitObjectFailure.UnsupportedSize);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsNonFatal(exception))
            {
                return ReviewedGitObjectResult<ReviewedGitTreeFact>.Failed(
                    ReviewedGitObjectFailure.TransportFailure);
            }
        }
    }

    public async Task<ReviewedGitObjectResult<ReviewedStagedBlob>>
        StageBlobAsync(
            string blobSha,
            long declaredSize,
            ReviewedBlobStagingLease staging,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staging);
        if (!ReviewedGitObjectValidation.IsSha(blobSha) || declaredSize < 0)
        {
            return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                ReviewedGitObjectFailure.InvalidRequest);
        }

        if (declaredSize > ReviewedContentLimits.HeadBlobBytes)
        {
            return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                ReviewedGitObjectFailure.UnsupportedSize);
        }

        if (!TryBeginRequest(cancellationToken, out var operation))
        {
            return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                ReviewedGitObjectFailure.UnsupportedSize);
        }

        using (operation)
        {
            try
            {
                var result = await _sharedTransport.GetBlobObjectAsync(
                    _repositoryName,
                    blobSha,
                    ActionHostGitBlobReadBudget.MaximumSupported,
                    operation!.Token);
                if (!TryCharge(result.CapturedResponseBytes, operation.Token))
                {
                    return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                        ReviewedGitObjectFailure.UnsupportedSize);
                }

                if (result.Value is null)
                {
                    return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                        MapBlobFailure(result.Failure));
                }

                if (result.Value.Bytes.LongLength != declaredSize)
                {
                    return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                        ReviewedGitObjectFailure.IdentityMismatch);
                }

                await using var writer = staging.TryCreateWriter(
                    blobSha,
                    declaredSize);
                if (writer is null)
                {
                    return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                        ReviewedGitObjectFailure.StagingFailure);
                }

                if (!await writer.WriteAsync(
                        result.Value.Bytes,
                        operation.Token))
                {
                    return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                        ReviewedGitObjectFailure.IdentityMismatch);
                }

                var staged = await writer.CompleteAsync(operation.Token);
                return staged is null
                    ? ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                        ReviewedGitObjectFailure.IdentityMismatch)
                    : ReviewedGitObjectResult<ReviewedStagedBlob>.Success(
                        staged);
            }
            catch (OperationCanceledException)
                when (operation!.DeadlineExpired)
            {
                return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                    ReviewedGitObjectFailure.UnsupportedSize);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsNonFatal(exception))
            {
                return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                    ReviewedGitObjectFailure.TransportFailure);
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
        _sharedTransport.Dispose();
    }

    internal static bool TryAuthorizedSource(
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        out string repositoryName,
        out string headSha)
    {
        repositoryName = string.Empty;
        headSha = string.Empty;
        var pullRequest = invocation.PullRequest;
        if (pullRequest.RepositoryId <= 0 || pullRequest.Number <= 0 ||
            pullRequest.BaseRepositoryId != pullRequest.RepositoryId ||
            pullRequest.HeadRepositoryId != pullRequest.RepositoryId ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                pullRequest.BaseRepositoryName,
                pullRequest.HeadRepositoryName) ||
            !ReviewedGitObjectValidation.IsRepositoryName(
                pullRequest.HeadRepositoryName) ||
            !ReviewedGitObjectValidation.IsSha(pullRequest.HeadSha))
        {
            return false;
        }

        repositoryName = pullRequest.HeadRepositoryName;
        headSha = pullRequest.HeadSha;
        return true;
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

    private static ReviewedGitObjectFailure MapBlobFailure(
        ActionHostGitObjectFailure failure) => failure switch
        {
            ActionHostGitObjectFailure.InvalidResponse =>
                ReviewedGitObjectFailure.IdentityMismatch,
            _ => MapFailure(failure),
        };

    private static ReviewedGitObjectFailure MapFailure(
        ActionHostGitObjectFailure failure) => failure switch
        {
            ActionHostGitObjectFailure.InvalidRequest =>
                ReviewedGitObjectFailure.InvalidRequest,
            ActionHostGitObjectFailure.NotFound =>
                ReviewedGitObjectFailure.NotFound,
            ActionHostGitObjectFailure.Unauthorized =>
                ReviewedGitObjectFailure.Unauthorized,
            ActionHostGitObjectFailure.Forbidden =>
                ReviewedGitObjectFailure.Forbidden,
            ActionHostGitObjectFailure.RateLimited =>
                ReviewedGitObjectFailure.RateLimited,
            ActionHostGitObjectFailure.UpstreamFailure =>
                ReviewedGitObjectFailure.UpstreamFailure,
            ActionHostGitObjectFailure.InvalidResponse =>
                ReviewedGitObjectFailure.InvalidResponse,
            ActionHostGitObjectFailure.ResponseTooLarge =>
                ReviewedGitObjectFailure.UnsupportedSize,
            _ => ReviewedGitObjectFailure.TransportFailure,
        };

    private static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;
}
