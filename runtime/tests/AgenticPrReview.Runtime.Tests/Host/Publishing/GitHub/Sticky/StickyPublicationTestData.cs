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
        var validated = R4PublicationTestData.Validated(
            findings: empty ? [] : [R4PublicationTestData.Finding()],
            identity: identity,
            scope: scope);
        var rendered = R4StickyRenderer.Render(validated);
        Assert.True(AuthorizedStickyPublicationRequest.TryCreate(
            invocation, validated, out var request));
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
    internal Queue<BoundedGitHubHttpResult<BoundedGitHubIssueCommentPage>>
        Pages
    { get; } = new();
    internal BoundedGitHubHttpResult<BoundedGitHubIssueComment> Mutation
    {
        get; set;
    } = BoundedGitHubHttpResult<BoundedGitHubIssueComment>.Failed(
        BoundedGitHubHttpOutcome.OutcomeUnknown,
        BoundedGitHubPublisherReason.TransportFailure);
    internal BoundedGitHubHttpResult<BoundedGitHubIssueComment> Read
    {
        get; set;
    } = BoundedGitHubHttpResult<BoundedGitHubIssueComment>.Failed(
        BoundedGitHubHttpOutcome.KnownNotSent,
        BoundedGitHubPublisherReason.TransportFailure);
    internal int Creates { get; private set; }
    internal int Updates { get; private set; }
    internal int Reads { get; private set; }
    internal int Lists { get; private set; }
    internal List<byte[]> Bodies { get; } = [];
    internal List<CancellationToken> ListCancellationTokens { get; } = [];
    internal List<CancellationToken> ReadCancellationTokens { get; } = [];
    internal System.Action? OnMutation { get; set; }
    internal System.Action? OnList { get; set; }
    internal Func<bool>? DeadlineProbe { get; set; }
    private long? _targetId;

    public bool IsWithinOverallDeadline => DeadlineProbe?.Invoke() ?? true;

    internal void Enqueue(params BoundedGitHubIssueComment[] comments) =>
        EnqueuePage(null, comments);

    internal void EnqueuePage(int? nextPage,
        params BoundedGitHubIssueComment[] comments) =>
        Pages.Enqueue(BoundedGitHubHttpResult<BoundedGitHubIssueCommentPage>
            .Success(new(comments, nextPage, null)));

    internal void EnqueuePageWithLast(int? nextPage, int? lastPage,
        params BoundedGitHubIssueComment[] comments) =>
        Pages.Enqueue(BoundedGitHubHttpResult<BoundedGitHubIssueCommentPage>
            .Success(new(comments, nextPage, lastPage)));

    public Task<BoundedGitHubHttpResult<BoundedGitHubIssueCommentPage>>
        ListIssueCommentsAsync(int page, CancellationToken cancellationToken)
    {
        Lists++;
        ListCancellationTokens.Add(cancellationToken);
        var result = Pages.Count > 0 ? Pages.Dequeue() :
            BoundedGitHubHttpResult<BoundedGitHubIssueCommentPage>.Failed(
                BoundedGitHubHttpOutcome.KnownNotSent,
                BoundedGitHubPublisherReason.InvalidPagination);
        if (result.Value is not null && Request is not null)
        {
            foreach (var comment in result.Value.Comments)
            {
                if (TryInspect(comment.Body, out var inspection) &&
                    inspection.Kind == R4StickyInspectionKind.ValidR4 &&
                    StringComparer.Ordinal.Equals(
                        inspection.Identity!.ScopeSha256,
                        Request.Rendered.Identity.ScopeSha256))
                    _targetId = comment.Id;
            }
        }
        OnList?.Invoke();
        return Task.FromResult(result);
    }

    public Task<BoundedGitHubHttpResult<BoundedGitHubIssueComment>>
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

    public Task<BoundedGitHubHttpResult<BoundedGitHubIssueComment>>
        GetIssueCommentAsync(long commentId,
            CancellationToken cancellationToken)
    {
        Reads++;
        ReadCancellationTokens.Add(cancellationToken);
        return Task.FromResult(Read);
    }

    public void Dispose() { }

    private static bool TryInspect(string body,
        out R4StickyInspection inspection)
    {
        try
        {
            inspection = R4StickyMarker.Inspect(body);
            return true;
        }
        catch (R4PublicationException)
        {
            inspection = R4StickyInspection.Invalid(
                R4StickyInvalidReason.BodyDigestMismatch);
            return false;
        }
    }
}
