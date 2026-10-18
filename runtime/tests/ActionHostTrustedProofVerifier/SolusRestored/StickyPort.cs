using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;

namespace AgenticPrReview.Runtime.SolusFreshHostProof;

// Public sticky text survives Host process death; restricted context never does.
internal sealed class StickyPort(string repository, FaultControl fault) : IStickyGitHubPublisherTransportFactory
{
    private readonly string repositoryName = repository;
    private readonly FaultControl control = fault;
    internal string? BodyPath;
    public IStickyGitHubPublisherTransport Create(ActionHostGitHubToken token,
        AuthorizedStickyPublicationRequest request, CancellationToken reconciliationToken = default) => new Port(this, request);
    public IStickyGitHubReadbackTransport CreateReadback(ActionHostGitHubToken token,
        AuthorizedStickyReadbackRequest request, CancellationToken reconciliationToken = default) => new Port(this, null);

    private sealed class Port(StickyPort owner, AuthorizedStickyPublicationRequest? request) : IStickyGitHubPublisherTransport,
        IStickyGitHubReadbackTransport
    {
        public bool IsWithinOverallDeadline => true;
        private BoundedGitHubIssueComment? Current => owner.BodyPath is { } path && File.Exists(path)
            ? new(81, $"https://api.github.com/repos/{owner.repositoryName}/issues/comments/81",
                $"https://github.com/{owner.repositoryName}/pull/242#issuecomment-81", File.ReadAllText(path)) : null;
        public Task<BoundedGitHubHttpResult<BoundedGitHubIssueCommentPage>> ListIssueCommentsAsync(int page, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Proof.Require(page == 1, "bounded_sticky_page");
            return Task.FromResult(BoundedGitHubHttpResult<BoundedGitHubIssueCommentPage>.Success(
                new(Current is { } comment ? [comment] : [], null, null)));
        }
        public Task<BoundedGitHubHttpResult<BoundedGitHubIssueComment>> MutateStickyCommentAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Proof.Require(request is not null && owner.BodyPath is not null, "sticky_request_authority");
            owner.control.Boundary("sticky-before-upload");
            if (owner.control.Once("fail-sticky"))
                return Task.FromResult(BoundedGitHubHttpResult<BoundedGitHubIssueComment>.Failed(
                    BoundedGitHubHttpOutcome.KnownNotSent, BoundedGitHubPublisherReason.TransportFailure));
            if (owner.control.Mode == "unknown-unwritten-sticky")
                return Task.FromResult(BoundedGitHubHttpResult<BoundedGitHubIssueComment>.Failed(
                    BoundedGitHubHttpOutcome.OutcomeUnknown, BoundedGitHubPublisherReason.TransportFailure));
            if (Current is { } previous)
            {
                Proof.Require(request!.PinPreviousTarget && request.PreviousTarget?.CommentId == previous.Id &&
                    previous.Body != request.Rendered.Comment, "new_review_pins_distinct_publication");
            }
            else Proof.Require(request!.PreviousTarget is null, "initial_publication_has_no_predecessor");
            File.WriteAllText(owner.BodyPath!, request!.Rendered.Comment);
            owner.control.Log(new { operation = "sticky", written = true });
            owner.control.Boundary("sticky-upload");
            if (owner.control.Once("unknown-sticky"))
                return Task.FromResult(BoundedGitHubHttpResult<BoundedGitHubIssueComment>.Failed(
                    BoundedGitHubHttpOutcome.OutcomeUnknown, BoundedGitHubPublisherReason.TransportFailure));
            return Task.FromResult(BoundedGitHubHttpResult<BoundedGitHubIssueComment>.Success(Current!));
        }
        public Task<BoundedGitHubHttpResult<BoundedGitHubIssueComment>> GetIssueCommentAsync(long id, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Proof.Require(id == 81 && Current is not null, "exact_sticky_readback");
            owner.control.Boundary("sticky-readback");
            return Task.FromResult(BoundedGitHubHttpResult<BoundedGitHubIssueComment>.Success(Current!));
        }
        public void Dispose() { }
    }
}
