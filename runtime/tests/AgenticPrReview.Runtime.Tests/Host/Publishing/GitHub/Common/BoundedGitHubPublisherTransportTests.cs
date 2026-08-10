using System.Net;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.Publishing.GitHub.Sticky;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.GitHub.Common;

public sealed class BoundedGitHubPublisherTransportTests
{
    [Fact]
    public async Task FullPageWithoutNextRelationIsTerminalAndRequestIsExact()
    {
        var data = await StickyPublicationTestData.CreateAsync();
        var calls = 0;
        using var transport = Create(data, new DelegateHandler(async request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(
                $"https://api.github.com/repos/" +
                $"{ActionHostAuthorizationScenario.RepositoryName}/issues/" +
                $"{ActionHostAuthorizationScenario.PullRequestNumber}/comments" +
                "?per_page=100&page=1",
                request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("github-token-canary-value",
                request.Headers.Authorization.Parameter);
            Assert.Equal(BoundedGitHubPublisherPolicy.Accept,
                Assert.Single(request.Headers.GetValues("Accept")));
            Assert.Equal(BoundedGitHubPublisherPolicy.ApiVersion,
                Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
            await Task.CompletedTask;
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(
                Enumerable.Range(1, 100).Select(CommentDocument)));
        }));

        var result = await transport.ListIssueCommentsAsync(1,
            CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Equal(100, result.Value.Comments.Count);
        Assert.Null(result.Value.NextPage);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ForeignPaginationLinkFailsClosed()
    {
        var data = await StickyPublicationTestData.CreateAsync();
        using var transport = Create(data, new DelegateHandler(_ =>
        {
            var response = Json(HttpStatusCode.OK, "[]");
            response.Headers.TryAddWithoutValidation("Link",
                "<https://example.invalid/comments?per_page=100&page=2>; rel=\"next\"");
            return Task.FromResult(response);
        }));

        var result = await transport.ListIssueCommentsAsync(1,
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(BoundedGitHubPublisherFailure.Unavailable, result.Failure);
        Assert.Equal(BoundedGitHubPublisherReason.InvalidPagination,
            result.Reason);
    }

    [Fact]
    public async Task SkippedOrOverfullPaginationFailsBeforePartialUse()
    {
        var data = await StickyPublicationTestData.CreateAsync();
        using var skipped = Create(data, new DelegateHandler(_ =>
        {
            var response = Json(HttpStatusCode.OK, "[]");
            response.Headers.TryAddWithoutValidation("Link",
                $"<https://api.github.com/repos/" +
                $"{ActionHostAuthorizationScenario.RepositoryName}/issues/" +
                $"{ActionHostAuthorizationScenario.PullRequestNumber}/comments" +
                "?per_page=100&page=3>; rel=\"next\"");
            return Task.FromResult(response);
        }));

        var gap = await skipped.ListIssueCommentsAsync(1,
            CancellationToken.None);

        Assert.Null(gap.Value);
        Assert.Equal(BoundedGitHubPublisherReason.InvalidPagination,
            gap.Reason);

        var calls = 0;
        using var overfull = Create(data, new DelegateHandler(_ =>
        {
            calls++;
            return Task.FromResult(Json(HttpStatusCode.OK,
                JsonSerializer.Serialize(Enumerable.Range(1, 101)
                    .Select(CommentDocument))));
        }));

        var records = await overfull.ListIssueCommentsAsync(1,
            CancellationToken.None);
        var pages = await overfull.ListIssueCommentsAsync(
            BoundedGitHubPublisherPolicy.MaximumPages + 1,
            CancellationToken.None);

        Assert.Null(records.Value);
        Assert.Equal(BoundedGitHubPublisherReason.InvalidPagination,
            records.Reason);
        Assert.Null(pages.Value);
        Assert.Equal(BoundedGitHubPublisherReason.InvalidRequest, pages.Reason);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RecognizedFourHundredResponseRequiresBoundedJsonEvidence()
    {
        var data = await StickyPublicationTestData.CreateAsync();
        using var valid = Create(data, new DelegateHandler(_ =>
            Task.FromResult(Json(HttpStatusCode.UnprocessableEntity,
                "{\"message\":\"validation failed\"}"))));

        var classified = await valid.CreateIssueCommentAsync(
            Encoding.UTF8.GetBytes("{\"body\":\"x\"}"),
            CancellationToken.None);

        Assert.Equal(
            BoundedGitHubPublisherFailure.AuthorizationOrValidationFailure,
            classified.Failure);
        Assert.Equal(BoundedGitHubPublisherReason.ValidationRejected,
            classified.Reason);

        using var invalid = Create(data, new DelegateHandler(_ =>
        {
            var response = new HttpResponseMessage(
                HttpStatusCode.UnprocessableEntity)
            {
                Content = new StringContent("validation failed", Encoding.UTF8,
                    "text/plain"),
            };
            return Task.FromResult(response);
        }));

        var unknown = await invalid.CreateIssueCommentAsync(
            Encoding.UTF8.GetBytes("{\"body\":\"x\"}"),
            CancellationToken.None);

        Assert.Equal(BoundedGitHubPublisherFailure.OutcomeUnknown,
            unknown.Failure);
        Assert.Equal(BoundedGitHubPublisherReason.InvalidResponse,
            unknown.Reason);
    }

    [Fact]
    public async Task MutationTimeoutIsOutcomeUnknownAndNeverReplayed()
    {
        var data = await StickyPublicationTestData.CreateAsync();
        var calls = 0;
        using var transport = Create(data, new DelegateHandler(async (_, token) =>
        {
            calls++;
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            throw new InvalidOperationException("unreachable");
        }), TimeSpan.FromMilliseconds(5));

        var result = await transport.CreateIssueCommentAsync(
            Encoding.UTF8.GetBytes("{\"body\":\"x\"}"),
            CancellationToken.None);

        Assert.Equal(BoundedGitHubPublisherFailure.OutcomeUnknown,
            result.Failure);
        Assert.Equal(BoundedGitHubPublisherReason.Deadline, result.Reason);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ServerErrorAfterMutationDispatchIsOutcomeUnknown()
    {
        var data = await StickyPublicationTestData.CreateAsync();
        var calls = 0;
        using var transport = Create(data, new DelegateHandler(_ =>
        {
            calls++;
            return Task.FromResult(Json(HttpStatusCode.InternalServerError,
                "{\"message\":\"server error\"}"));
        }));

        var result = await transport.CreateIssueCommentAsync(
            Encoding.UTF8.GetBytes("{\"body\":\"x\"}"),
            CancellationToken.None);

        Assert.Equal(BoundedGitHubPublisherFailure.OutcomeUnknown,
            result.Failure);
        Assert.Equal(BoundedGitHubPublisherReason.InvalidResponse,
            result.Reason);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OversizedResponseAndRequestCountFailBeforePartialUse()
    {
        var data = await StickyPublicationTestData.CreateAsync();
        using var oversized = Create(data, new DelegateHandler(_ =>
        {
            var response = Json(HttpStatusCode.OK, "[]");
            response.Content.Headers.ContentLength =
                BoundedGitHubPublisherPolicy.MaximumResponseBytes + 1;
            return Task.FromResult(response);
        }));

        var responseLimit = await oversized.ListIssueCommentsAsync(1,
            CancellationToken.None);

        Assert.Null(responseLimit.Value);
        Assert.Equal(BoundedGitHubPublisherReason.ResponseLimit,
            responseLimit.Reason);

        var calls = 0;
        using var bounded = Create(data, new DelegateHandler(_ =>
        {
            calls++;
            return Task.FromResult(Json(HttpStatusCode.OK,
                JsonSerializer.Serialize(CommentDocument(7))));
        }));
        for (var i = 0; i < BoundedGitHubPublisherPolicy.MaximumRequests; i++)
        {
            var read = await bounded.GetIssueCommentAsync(7,
                CancellationToken.None);
            Assert.NotNull(read.Value);
        }

        var requestLimit = await bounded.GetIssueCommentAsync(7,
            CancellationToken.None);

        Assert.Null(requestLimit.Value);
        Assert.Equal(BoundedGitHubPublisherReason.RequestLimit,
            requestLimit.Reason);
        Assert.Equal(BoundedGitHubPublisherPolicy.MaximumRequests, calls);
    }

    [Fact]
    public async Task ResponseAndAggregateByteCapsAcceptExactAndRejectPlusOne()
    {
        var data = await StickyPublicationTestData.CreateAsync();
        var atResponseCap = AtResponseCapCommentJson(7);
        Assert.Equal(BoundedGitHubPublisherPolicy.MaximumResponseBytes,
            Encoding.UTF8.GetByteCount(atResponseCap));
        var calls = 0;
        using var transport = Create(data, new DelegateHandler(_ =>
        {
            calls++;
            return Task.FromResult(Json(HttpStatusCode.OK,
                calls <= 64 ? atResponseCap : "{"));
        }));

        for (var index = 0; index < 64; index++)
        {
            var accepted = await transport.GetIssueCommentAsync(7,
                CancellationToken.None);
            Assert.NotNull(accepted.Value);
        }

        var aggregatePlusOne = await transport.GetIssueCommentAsync(7,
            CancellationToken.None);

        Assert.Null(aggregatePlusOne.Value);
        Assert.Equal(BoundedGitHubPublisherReason.AggregateResponseLimit,
            aggregatePlusOne.Reason);
        Assert.Equal(65, calls);
    }

    [Fact]
    public async Task OverallDeadlineAndCancelledMutationAreKnownPreSend()
    {
        var data = await StickyPublicationTestData.CreateAsync();
        var calls = 0;
        using var expired = Create(data, new DelegateHandler(_ =>
        {
            calls++;
            return Task.FromResult(Json(HttpStatusCode.Created,
                JsonSerializer.Serialize(CommentDocument(7))));
        }), overallTimeout: TimeSpan.Zero);

        var deadline = await expired.CreateIssueCommentAsync(
            Encoding.UTF8.GetBytes("{\"body\":\"x\"}"),
            CancellationToken.None);

        Assert.Equal(BoundedGitHubPublisherFailure.Unavailable,
            deadline.Failure);
        Assert.Equal(BoundedGitHubPublisherReason.Deadline, deadline.Reason);
        Assert.Equal(0, calls);

        using var cancelled = Create(data, new DelegateHandler(_ =>
            throw new InvalidOperationException("must not send")));
        using var source = new CancellationTokenSource();
        source.Cancel();

        var cancellation = await cancelled.CreateIssueCommentAsync(
            Encoding.UTF8.GetBytes("{\"body\":\"x\"}"), source.Token);

        Assert.Equal(BoundedGitHubPublisherFailure.CancelledBeforeSend,
            cancellation.Failure);
    }

    private static BoundedGitHubPublisherTransport Create(
        (AgenticPrReview.Runtime.ActionHost.Contracts.ActionHostGitHubToken Token,
            AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky
                .AuthorizedStickyPublicationRequest Request,
            AgenticPrReview.Runtime.Host.Publishing.Rendering
                .R4RenderedStickyComment Rendered) data,
        HttpMessageHandler handler, TimeSpan? requestTimeout = null,
        TimeSpan? overallTimeout = null) =>
        BoundedGitHubPublisherTransport.CreateForTesting(
            data.Token.ExportForPrivateLaunch(), data.Request.Authorization,
            handler, requestTimeout, overallTimeout);

    private static object CommentDocument(int id) => new
    {
        id,
        url = $"https://api.github.com/repos/" +
            $"{ActionHostAuthorizationScenario.RepositoryName}/issues/comments/{id}",
        html_url = $"https://github.com/" +
            $"{ActionHostAuthorizationScenario.RepositoryName}/pull/" +
            $"{ActionHostAuthorizationScenario.PullRequestNumber}" +
            $"#issuecomment-{id}",
        body = "body",
    };

    private static string AtResponseCapCommentJson(int id)
    {
        var document = JsonSerializer.Serialize(CommentDocument(id));
        var prefix = document[..^1] + ",\"padding\":\"";
        const string suffix = "\"}";
        var padding = BoundedGitHubPublisherPolicy.MaximumResponseBytes -
            Encoding.UTF8.GetByteCount(prefix) - suffix.Length;
        Assert.True(padding > 0);
        return prefix + new string('a', padding) + suffix;
    }

    private static HttpResponseMessage Json(HttpStatusCode status,
        string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8,
                "application/json"),
        };

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken,
            Task<HttpResponseMessage>> _send;

        internal DelegateHandler(
            Func<HttpRequestMessage, Task<HttpResponseMessage>> send) :
            this((request, _) => send(request)) { }

        internal DelegateHandler(Func<HttpRequestMessage, CancellationToken,
            Task<HttpResponseMessage>> send) => _send = send;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            _send(request, cancellationToken);
    }
}
