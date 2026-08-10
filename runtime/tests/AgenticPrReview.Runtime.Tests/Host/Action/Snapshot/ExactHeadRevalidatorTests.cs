using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Snapshot;

public sealed class ExactHeadRevalidatorTests
{
    [Fact]
    public void RevalidationUsesTheClosedCauseSet()
    {
        Assert.Equal(
            [
                "Exact",
                "HeadChanged",
                "PullRequestIneligible",
                "PullRequestMissing",
                "Unauthorized",
                "Forbidden",
                "RateLimited",
                "UpstreamUnavailable",
                "InvalidResponse",
                "TransportFailure",
                "DeadlineExceeded",
                "Cancelled",
            ],
            Enum.GetNames<ExactHeadRevalidationStatus>());
    }

    [Fact]
    public async Task ExactHeadAllowsMutationEvenWhenBaseAdvanced()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var current = H5SnapshotTestSupport.PullRequest(
            invocation.PullRequest,
            baseSha: new string('7', 40));

        var result = await ExactHeadRevalidator.RevalidateAsync(
            invocation.PullRequest,
            H5SnapshotTestSupport.Token(),
            new ScriptedFactory(ActionHostGitObjectResult<
                ActionHostGitHubPullRequestFact>.Success(current, 100)),
            CancellationToken.None);

        Assert.Equal(ExactHeadRevalidationStatus.Exact, result.Status);
        Assert.True(result.MayMutate);
        Assert.Equal(invocation.PullRequest.HeadSha, result.ObservedHeadSha);
    }

    [Fact]
    public async Task ChangedHeadIsAnExplicitNoMutationOutcome()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var changedHead = new string('8', 40);
        var current = H5SnapshotTestSupport.PullRequest(
            invocation.PullRequest,
            headSha: changedHead);

        var result = await ExactHeadRevalidator.RevalidateAsync(
            invocation.PullRequest,
            H5SnapshotTestSupport.Token(),
            new ScriptedFactory(ActionHostGitObjectResult<
                ActionHostGitHubPullRequestFact>.Success(current, 100)),
            CancellationToken.None);

        Assert.Equal(
            ExactHeadRevalidationStatus.HeadChanged,
            result.Status);
        Assert.False(result.MayMutate);
        Assert.Equal(changedHead, result.ObservedHeadSha);
    }

    [Fact]
    public async Task DraftOrForkAndMissingPullRequestFailClosed()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var draft = H5SnapshotTestSupport.PullRequest(
            invocation.PullRequest,
            draft: true);
        var ineligible = await ExactHeadRevalidator.RevalidateAsync(
            invocation.PullRequest,
            H5SnapshotTestSupport.Token(),
            new ScriptedFactory(ActionHostGitObjectResult<
                ActionHostGitHubPullRequestFact>.Success(draft, 100)),
            CancellationToken.None);
        Assert.Equal(
            ExactHeadRevalidationStatus.PullRequestIneligible,
            ineligible.Status);
        Assert.False(ineligible.MayMutate);

        var missing = await ExactHeadRevalidator.RevalidateAsync(
            invocation.PullRequest,
            H5SnapshotTestSupport.Token(),
            new ScriptedFactory(ActionHostGitObjectResult<
                ActionHostGitHubPullRequestFact>.Failed(
                    ActionHostGitObjectFailure.NotFound)),
            CancellationToken.None);
        Assert.Equal(
            ExactHeadRevalidationStatus.PullRequestMissing,
            missing.Status);
        Assert.False(missing.MayMutate);
    }

    private sealed class ScriptedFactory :
        IActionHostReviewedSnapshotTransportFactory
    {
        private readonly ActionHostGitObjectResult<
            ActionHostGitHubPullRequestFact> _result;

        internal ScriptedFactory(ActionHostGitObjectResult<
            ActionHostGitHubPullRequestFact> result)
        {
            _result = result;
        }

        public IActionHostReviewedSnapshotTransport
            CreateReviewedSnapshotTransport(ActionHostGitHubToken token) =>
            new ScriptedTransport(_result);
    }

    private sealed class ScriptedTransport :
        IActionHostReviewedSnapshotTransport
    {
        private readonly ActionHostGitObjectResult<
            ActionHostGitHubPullRequestFact> _result;

        internal ScriptedTransport(ActionHostGitObjectResult<
            ActionHostGitHubPullRequestFact> result)
        {
            _result = result;
        }

        public Task<ActionHostGitObjectResult<ActionHostGitHubPullRequestFact>>
            GetCurrentPullRequestAsync(
                string repositoryName,
                long pullRequestNumber,
                CancellationToken cancellationToken) =>
            Task.FromResult(_result);

        public Task<ActionHostGitObjectResult<
            ActionHostPullRequestFilePageObject>> GetPullRequestFilesAsync(
            string repositoryName,
            long pullRequestNumber,
            int page,
            int perPage,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Files must not be listed.");

        public Task<ActionHostGitObjectResult<ActionHostGitCommitObject>>
            GetCommitObjectAsync(
                string repositoryName,
                string commitSha,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Commit must not be read.");

        public Task<ActionHostGitObjectResult<ActionHostGitTreeObject>>
            GetTreeObjectAsync(
                string repositoryName,
                string treeSha,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Tree must not be read.");

        public Task<ActionHostGitObjectResult<ActionHostStreamedBlobObject>>
            CopyBlobObjectAsync(
                string repositoryName,
                string blobSha,
                long declaredSize,
                Stream destination,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Blob must not be read.");

        public void Dispose()
        {
        }
    }
}
