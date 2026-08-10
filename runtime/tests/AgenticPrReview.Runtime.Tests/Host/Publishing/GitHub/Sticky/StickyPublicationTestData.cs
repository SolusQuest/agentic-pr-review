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
    IBoundedGitHubPublisherTransportFactory
{
    internal FakePublisherTransport Transport { get; } = new();
    internal int Creates { get; private set; }

    public IBoundedGitHubPublisherTransport Create(ActionHostGitHubToken token,
        ActionHostAuthorizer.AuthorizedInvocation authorization)
    {
        Creates++;
        return Transport;
    }
}

internal sealed class FakePublisherTransport : IBoundedGitHubPublisherTransport
{
    internal Queue<BoundedGitHubPublisherResult<BoundedGitHubIssueCommentPage>>
        Pages { get; } = new();
    internal BoundedGitHubPublisherResult<BoundedGitHubIssueComment> Mutation {
        get; set;
    } = BoundedGitHubPublisherResult<BoundedGitHubIssueComment>.Failed(
        BoundedGitHubPublisherFailure.OutcomeUnknown,
        BoundedGitHubPublisherReason.TransportFailure);
    internal BoundedGitHubPublisherResult<BoundedGitHubIssueComment> Read {
        get; set;
    } = BoundedGitHubPublisherResult<BoundedGitHubIssueComment>.Failed(
        BoundedGitHubPublisherFailure.Unavailable,
        BoundedGitHubPublisherReason.TransportFailure);
    internal int Creates { get; private set; }
    internal int Updates { get; private set; }
    internal int Reads { get; private set; }
    internal int Lists { get; private set; }
    internal List<byte[]> Bodies { get; } = [];
    internal List<CancellationToken> ListCancellationTokens { get; } = [];
    internal List<CancellationToken> ReadCancellationTokens { get; } = [];
    internal System.Action? OnMutation { get; set; }

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
        return Task.FromResult(Pages.Count > 0 ? Pages.Dequeue() :
            BoundedGitHubPublisherResult<BoundedGitHubIssueCommentPage>.Failed(
                BoundedGitHubPublisherFailure.Unavailable,
                BoundedGitHubPublisherReason.InvalidPagination));
    }

    public Task<BoundedGitHubPublisherResult<BoundedGitHubIssueComment>>
        CreateIssueCommentAsync(ReadOnlyMemory<byte> requestBody,
            CancellationToken cancellationToken)
    {
        Creates++;
        Bodies.Add(requestBody.ToArray());
        OnMutation?.Invoke();
        return Task.FromResult(Mutation);
    }

    public Task<BoundedGitHubPublisherResult<BoundedGitHubIssueComment>>
        UpdateIssueCommentAsync(long commentId, ReadOnlyMemory<byte> requestBody,
            CancellationToken cancellationToken)
    {
        Updates++;
        Bodies.Add(requestBody.ToArray());
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
