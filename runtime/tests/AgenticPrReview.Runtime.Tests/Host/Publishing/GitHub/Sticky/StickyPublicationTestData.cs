using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.Publishing.Rendering;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.GitHub.Sticky;

internal static class StickyPublicationTestData
{
    internal static async Task<(ActionHostGitHubToken Token,
        AuthorizedStickyPublicationRequest Request,
        R4RenderedStickyComment Rendered)> CreateAsync(
            bool empty = false)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowRun);
        var authorized = await scenario.CreateAuthorizer().AuthorizeAsync(
            scenario.Launch, CancellationToken.None);
        var invocation = Assert.IsType<ActionHostAuthorizer.AuthorizedInvocation>(
            authorized.Invocation);
        var scope = new R4PublicationScopeV1(
            (ulong)ActionHostAuthorizationScenario.RepositoryId,
            (ulong)ActionHostAuthorizationScenario.RepositoryId,
            ActionHostAuthorizationPolicy.PrivilegedWorkflowPath,
            "refs/heads/main",
            (ulong)ActionHostAuthorizationScenario.PullRequestNumber,
            R4PublicationTestData.PolicySha256,
            R4PublicationTestData.H1Identity);
        var identity = new AgenticPrReview.Runtime.Agent.Core.ReviewedIdentity(
            ActionHostAuthorizationScenario.RepositoryId.ToString(),
            ActionHostAuthorizationScenario.PullRequestNumber,
            ActionHostAuthorizationScenario.BaseSha,
            ActionHostAuthorizationScenario.HeadSha);
        var rendered = R4PublicationTestData.Render(
            findings: empty ? [] : [R4PublicationTestData.Finding()],
            identity: identity,
            scope: scope);
        Assert.True(AuthorizedStickyPublicationRequest.TryCreate(
            invocation, scope, rendered, out var request));
        return (scenario.Launch.Inputs.GitHubToken!, request!, rendered);
    }

    internal static BoundedGitHubIssueComment Comment(
        long id, string body) => new(id,
            $"https://api.github.com/repos/" +
            $"{ActionHostAuthorizationScenario.RepositoryName}/issues/comments/{id}",
            $"https://github.com/{ActionHostAuthorizationScenario.RepositoryName}/" +
            $"pull/{ActionHostAuthorizationScenario.PullRequestNumber}" +
            $"#issuecomment-{id}", body);
}

internal sealed class FakePublisherTransportFactory :
    IStickyGitHubPublisherTransportFactory
{
    internal FakePublisherTransport Transport { get; } = new();
    internal int Creates { get; private set; }

    public IStickyGitHubPublisherTransport Create(ActionHostGitHubToken token,
        AuthorizedStickyPublicationRequest request)
    {
        Creates++;
        Transport.Request = request;
        return Transport;
    }
}

internal sealed class FakePublisherTransport : IStickyGitHubPublisherTransport
{
    internal AuthorizedStickyPublicationRequest? Request { get; set; }
    internal Queue<BoundedGitHubPublisherResult<BoundedGitHubIssueCommentPage>>
        Pages
    { get; } = new();
    internal BoundedGitHubPublisherResult<BoundedGitHubIssueComment> Mutation
    {
        get; set;
    } = BoundedGitHubPublisherResult<BoundedGitHubIssueComment>.Failed(
        BoundedGitHubPublisherOutcome.OutcomeUnknown,
        BoundedGitHubPublisherReason.TransportFailure);
    internal BoundedGitHubPublisherResult<BoundedGitHubIssueComment> Read
    {
        get; set;
    } = BoundedGitHubPublisherResult<BoundedGitHubIssueComment>.Failed(
        BoundedGitHubPublisherOutcome.KnownNotWritten,
        BoundedGitHubPublisherReason.TransportFailure);
    internal int Creates { get; private set; }
    internal int Updates { get; private set; }
    internal int Reads { get; private set; }
    internal int Lists { get; private set; }
    internal List<byte[]> Bodies { get; } = [];
    internal List<CancellationToken> ListCancellationTokens { get; } = [];
    internal List<CancellationToken> ReadCancellationTokens { get; } = [];
    internal System.Action? OnMutation { get; set; }
    private long? _targetId;

    internal void Enqueue(params BoundedGitHubIssueComment[] comments) =>
        EnqueuePage(null, comments);

    internal void EnqueuePage(int? nextPage,
        params BoundedGitHubIssueComment[] comments) =>
        Pages.Enqueue(BoundedGitHubPublisherResult<BoundedGitHubIssueCommentPage>
            .Success(new(comments, nextPage)));

    public Task<BoundedGitHubPublisherResult<BoundedGitHubIssueCommentPage>>
        ListIssueCommentsAsync(int page, CancellationToken cancellationToken)
    {
        Lists++;
        ListCancellationTokens.Add(cancellationToken);
        var result = Pages.Count > 0 ? Pages.Dequeue() :
            BoundedGitHubPublisherResult<BoundedGitHubIssueCommentPage>.Failed(
                BoundedGitHubPublisherOutcome.KnownNotWritten,
                BoundedGitHubPublisherReason.InvalidPagination);
        if (result.Value is not null && Request is not null)
        {
            foreach (var comment in result.Value.Comments)
            {
                try
                {
                    var inspection = R4StickyMarker.Inspect(comment.Body);
                    if (inspection.Kind == R4StickyInspectionKind.ValidR4 &&
                        StringComparer.Ordinal.Equals(
                            inspection.Identity!.ScopeSha256,
                            Request.Rendered.Identity.ScopeSha256))
                        _targetId = comment.Id;
                }
                catch (R4PublicationException) { }
            }
        }
        return Task.FromResult(result);
    }

    public Task<BoundedGitHubPublisherResult<BoundedGitHubIssueComment>>
        MutateStickyCommentAsync(CancellationToken cancellationToken)
    {
        if (_targetId is null) Creates++;
        else Updates++;
        Assert.NotNull(Request);
        Assert.True(StickyCommentSerializer.TrySerialize(
            Request.Rendered.Comment, out var body));
        Bodies.Add(body!);
        OnMutation?.Invoke();
        return Task.FromResult(Mutation);
    }

    public Task<BoundedGitHubPublisherResult<BoundedGitHubIssueComment>>
        GetIssueCommentAsync(long commentId,
            CancellationToken cancellationToken)
    {
        Reads++;
        ReadCancellationTokens.Add(cancellationToken);
        return Task.FromResult(Read);
    }

    public void Dispose() { }
}
