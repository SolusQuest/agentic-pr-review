using AgenticPrReview.Runtime.ActionHost.Contracts;

namespace AgenticPrReview.Runtime.ActionHost.GitHub;

internal enum ActionHostGitHubFailure
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
    RequestLimitExceeded,
    TransportFailure,
}

internal sealed class ActionHostGitHubResult<T>
    where T : class
{
    private ActionHostGitHubResult(T? value, ActionHostGitHubFailure failure)
    {
        Value = value;
        Failure = failure;
    }

    internal T? Value { get; }

    internal ActionHostGitHubFailure Failure { get; }

    internal static ActionHostGitHubResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, ActionHostGitHubFailure.None);
    }

    internal static ActionHostGitHubResult<T> Failed(
        ActionHostGitHubFailure failure)
    {
        if (failure == ActionHostGitHubFailure.None)
        {
            throw new ArgumentOutOfRangeException(nameof(failure));
        }

        return new(null, failure);
    }
}

internal sealed record ActionHostGitHubRepositoryFact(
    long Id,
    string FullName,
    string DefaultBranch);

internal sealed record ActionHostGitHubRepositoryIdentity(
    long Id,
    string FullName);

internal sealed record ActionHostGitHubActorFact(
    long Id,
    string Login);

internal sealed record ActionHostGitHubPullRequestReferenceFact(
    long Id,
    long Number,
    string BaseSha,
    ActionHostGitHubRepositoryIdentity BaseRepository,
    string HeadSha,
    ActionHostGitHubRepositoryIdentity HeadRepository);

internal sealed record ActionHostGitHubWorkflowRunFact(
    long Id,
    int Attempt,
    long WorkflowId,
    string Name,
    string Path,
    string HeadBranch,
    string HeadSha,
    string Event,
    string? Conclusion,
    ActionHostGitHubRepositoryIdentity Repository,
    ActionHostGitHubRepositoryIdentity HeadRepository,
    ActionHostGitHubActorFact Actor,
    ActionHostGitHubActorFact TriggeringActor,
    IReadOnlyList<ActionHostGitHubPullRequestReferenceFact> PullRequests);

internal sealed record ActionHostGitHubWorkflowSourceFact(
    string Path,
    string Name,
    string BlobSha,
    byte[] Bytes);

internal sealed record ActionHostGitHubPullRequestPageFact(
    IReadOnlyList<ActionHostGitHubPullRequestFact> PullRequests,
    bool IsComplete);

internal sealed record ActionHostGitHubPullRequestFact(
    long Id,
    long Number,
    string State,
    bool Draft,
    DateTimeOffset? MergedAt,
    string BaseSha,
    ActionHostGitHubRepositoryIdentity BaseRepository,
    string HeadSha,
    ActionHostGitHubRepositoryIdentity HeadRepository);

internal sealed record ActionHostGitHubPermissionFact(string Permission);

internal interface IActionHostGitHubAuthorizationTransportFactory
{
    IActionHostGitHubAuthorizationTransport Create(
        ActionHostGitHubToken token);
}

internal interface IActionHostGitHubAuthorizationTransport : IDisposable
{
    Task<ActionHostGitHubResult<ActionHostGitHubRepositoryFact>>
        GetRepositoryAsync(
            string repositoryName,
            CancellationToken cancellationToken);

    Task<ActionHostGitHubResult<ActionHostGitHubWorkflowRunFact>>
        GetWorkflowRunAttemptAsync(
            string repositoryName,
            long runId,
            int attempt,
            CancellationToken cancellationToken);

    Task<ActionHostGitHubResult<ActionHostGitHubWorkflowSourceFact>>
        GetWorkflowSourceAsync(
            string repositoryName,
            string workflowPath,
            string workflowCommitSha,
            CancellationToken cancellationToken);

    Task<ActionHostGitHubResult<ActionHostGitHubPullRequestPageFact>>
        GetCommitPullRequestsAsync(
            string repositoryName,
            string commitSha,
            int page,
            int perPage,
            CancellationToken cancellationToken);

    Task<ActionHostGitHubResult<ActionHostGitHubPermissionFact>>
        GetCollaboratorPermissionAsync(
            string repositoryName,
            string login,
            CancellationToken cancellationToken);

    Task<ActionHostGitHubResult<ActionHostGitHubPullRequestFact>>
        GetPullRequestAsync(
            string repositoryName,
            long pullRequestNumber,
            CancellationToken cancellationToken);
}

internal static class ActionHostGitHubAuthorizationPolicy
{
    internal const string Origin = "https://api.github.com";
    internal const string UserAgent = "agentic-pr-review-actionhost";
    internal const string Accept = "application/vnd.github+json";
    internal const string ApiVersion = "2026-03-10";
    internal const int MaximumRequests = 32;
    internal const int MaximumResponseBytes = 1024 * 1024;
    internal const int MaximumWorkflowBytes = 256 * 1024;
    internal const int AssociatedPullRequestsPerPage = 100;
    internal const int MaximumAssociatedPullRequestPages = 10;
    internal const int MaximumAssociatedPullRequests =
        AssociatedPullRequestsPerPage * MaximumAssociatedPullRequestPages;
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan AuthorizationTimeout =
        TimeSpan.FromSeconds(60);
}
