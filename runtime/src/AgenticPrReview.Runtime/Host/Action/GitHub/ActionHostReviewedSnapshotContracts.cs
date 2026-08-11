using AgenticPrReview.Runtime.ActionHost.Contracts;

namespace AgenticPrReview.Runtime.ActionHost.GitHub;

internal sealed record ActionHostPullRequestFileObject(
    string Sha,
    string Path,
    string? PreviousPath,
    string Status,
    int Additions,
    int Deletions,
    int Changes,
    string? Patch);

internal sealed record ActionHostPullRequestFilePageObject(
    IReadOnlyList<ActionHostPullRequestFileObject> Files,
    bool IsComplete);

internal sealed record ActionHostStreamedBlobObject(
    string Sha,
    long Size);

internal interface IActionHostReviewedSnapshotTransportFactory
{
    IActionHostReviewedSnapshotTransport CreateReviewedSnapshotTransport(
        ActionHostGitHubToken token);
}

internal interface IActionHostReviewedSnapshotTransport : IDisposable
{
    Task<ActionHostGitObjectResult<ActionHostGitHubPullRequestFact>>
        GetCurrentPullRequestAsync(
            string repositoryName,
            long pullRequestNumber,
            CancellationToken cancellationToken);

    Task<ActionHostGitObjectResult<ActionHostPullRequestFilePageObject>>
        GetPullRequestFilesAsync(
            string repositoryName,
            long pullRequestNumber,
            int page,
            int perPage,
            CancellationToken cancellationToken);

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

    Task<ActionHostGitObjectResult<ActionHostStreamedBlobObject>>
        CopyBlobObjectAsync(
            string repositoryName,
            string blobSha,
            long declaredSize,
            Stream destination,
            CancellationToken cancellationToken);
}
