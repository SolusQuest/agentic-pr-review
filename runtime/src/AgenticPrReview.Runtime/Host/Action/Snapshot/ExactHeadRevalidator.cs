using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;

namespace AgenticPrReview.Runtime.ActionHost.Snapshot;

internal enum ExactHeadRevalidationStatus
{
    Exact = 0,
    HeadChanged,
    PullRequestIneligible,
    PullRequestMissing,
    Unauthorized,
    Forbidden,
    RateLimited,
    UpstreamUnavailable,
    InvalidResponse,
    TransportFailure,
    DeadlineExceeded,
    Cancelled,
}

internal sealed record ExactHeadRevalidationResult(
    ExactHeadRevalidationStatus Status,
    string FrozenHeadSha,
    string? ObservedHeadSha)
{
    internal bool MayMutate => Status == ExactHeadRevalidationStatus.Exact;
}

internal static class ExactHeadRevalidator
{
    private static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(30);

    internal static async Task<ExactHeadRevalidationResult> RevalidateAsync(
        ActionHostAuthorizer.FrozenPullRequest frozen,
        ActionHostGitHubToken token,
        IActionHostReviewedSnapshotTransportFactory factory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frozen);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(factory);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        deadline.CancelAfter(RequestDeadline);
        try
        {
            using var transport = factory.CreateReviewedSnapshotTransport(token);
            var response = await transport.GetCurrentPullRequestAsync(
                frozen.BaseRepositoryName,
                frozen.Number,
                deadline.Token);
            if (response.Value is null)
            {
                return Result(
                    Map(response.Failure),
                    frozen,
                    observedHeadSha: null);
            }

            var current = response.Value;
            if (!Eligible(current, frozen))
            {
                return Result(
                    ExactHeadRevalidationStatus.PullRequestIneligible,
                    frozen,
                    current.HeadSha);
            }

            return Result(
                StringComparer.Ordinal.Equals(
                    current.HeadSha,
                    frozen.HeadSha)
                    ? ExactHeadRevalidationStatus.Exact
                    : ExactHeadRevalidationStatus.HeadChanged,
                frozen,
                current.HeadSha);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return Result(
                ExactHeadRevalidationStatus.Cancelled,
                frozen,
                observedHeadSha: null);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return Result(
                ExactHeadRevalidationStatus.DeadlineExceeded,
                frozen,
                observedHeadSha: null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and
            not AccessViolationException)
        {
            return Result(
                ExactHeadRevalidationStatus.TransportFailure,
                frozen,
                observedHeadSha: null);
        }
    }

    private static bool Eligible(
        ActionHostGitHubPullRequestFact current,
        ActionHostAuthorizer.FrozenPullRequest frozen) =>
        current.Number == frozen.Number &&
        StringComparer.Ordinal.Equals(current.State, "open") &&
        !current.Draft &&
        current.MergedAt is null &&
        current.BaseRepository.Id == frozen.RepositoryId &&
        current.HeadRepository.Id == frozen.RepositoryId &&
        StringComparer.Ordinal.Equals(
            current.BaseRepository.FullName,
            frozen.BaseRepositoryName) &&
        StringComparer.Ordinal.Equals(
            current.HeadRepository.FullName,
            frozen.HeadRepositoryName);

    private static ExactHeadRevalidationStatus Map(
        ActionHostGitObjectFailure failure) => failure switch
        {
            ActionHostGitObjectFailure.NotFound =>
                ExactHeadRevalidationStatus.PullRequestMissing,
            ActionHostGitObjectFailure.Unauthorized =>
                ExactHeadRevalidationStatus.Unauthorized,
            ActionHostGitObjectFailure.Forbidden =>
                ExactHeadRevalidationStatus.Forbidden,
            ActionHostGitObjectFailure.RateLimited =>
                ExactHeadRevalidationStatus.RateLimited,
            ActionHostGitObjectFailure.UpstreamFailure =>
                ExactHeadRevalidationStatus.UpstreamUnavailable,
            ActionHostGitObjectFailure.InvalidResponse or
                ActionHostGitObjectFailure.InvalidRequest =>
                ExactHeadRevalidationStatus.InvalidResponse,
            ActionHostGitObjectFailure.ResponseTooLarge =>
                ExactHeadRevalidationStatus.InvalidResponse,
            _ => ExactHeadRevalidationStatus.TransportFailure,
        };

    private static ExactHeadRevalidationResult Result(
        ExactHeadRevalidationStatus status,
        ActionHostAuthorizer.FrozenPullRequest frozen,
        string? observedHeadSha) => new(
            status,
            frozen.HeadSha,
            observedHeadSha);
}
