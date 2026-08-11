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
        Assert.Equal(BoundedGitHubHttpOutcome.Success, result.Outcome);
        Assert.Equal(100, result.Value.Comments.Count);
        Assert.Null(result.Value.NextPage);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NullCommentDocumentFailsClosed()
    {
        var data = await StickyPublicationTestData.CreateAsync();
        using var transport = Create(data, new DelegateHandler(_ =>
            Task.FromResult(Json(HttpStatusCode.OK, "[null]"))));

        var result = await transport.ListIssueCommentsAsync(1,
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(BoundedGitHubHttpOutcome.KnownNotSent, result.Outcome);
        Assert.Equal(BoundedGitHubPublisherReason.InvalidResponse,
            result.Reason);
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
        Assert.Equal(BoundedGitHubHttpOutcome.KnownNotSent,
            result.Outcome);
        Assert.Equal(BoundedGitHubPublisherReason.InvalidPagination,
            result.Reason);
    }

    [Theory]
    [InlineData("last", "50", 1)]
    [InlineData("first", "2", 1)]
    [InlineData("prev", "1", 3)]
    [InlineData("next", "+2", 1)]
    [InlineData("next", "02", 1)]
    public async Task ContradictoryOrNoncanonicalPaginationFailsClosed(
        string relation, string linkedPage, int currentPage)
    {
        var data = await StickyPublicationTestData.CreateAsync();
        using var transport = Create(data, new DelegateHandler(_ =>
        {
            var response = Json(HttpStatusCode.OK, "[]");
            response.Headers.TryAddWithoutValidation("Link",
                $"<https://api.github.com/repos/" +
                $"{ActionHostAuthorizationScenario.RepositoryName}/issues/" +
                $"{ActionHostAuthorizationScenario.PullRequestNumber}/comments" +
                $"?per_page=100&page={linkedPage}>; rel=\"{relation}\"");
            return Task.FromResult(response);
        }));

        var result = await transport.ListIssueCommentsAsync(currentPage,
            CancellationToken.None);

        Assert.Null(result.Value);
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
        using var valid = Create(data, new DelegateHandler(request =>
            Task.FromResult(request.Method == HttpMethod.Get
                ? Json(HttpStatusCode.OK, "[]")
                : Json(HttpStatusCode.UnprocessableEntity,
                    "{\"message\":\"validation failed\"}"))));
        await AuthorizeAbsentAsync(valid);

        var classified = await valid.MutateStickyCommentAsync(
            CancellationToken.None);

        Assert.Equal(
            BoundedGitHubHttpOutcome.AuthorizationOrValidationFailure,
            classified.Outcome);
        Assert.Equal(BoundedGitHubPublisherReason.ValidationRejected,
            classified.Reason);
        Assert.Equal(422, classified.ValidationEvidence!.StatusCode);
        Assert.False(classified.ValidationEvidence.ReviewIdentityReturned);

        using var invalid = Create(data, new DelegateHandler(request =>
            Task.FromResult(request.Method == HttpMethod.Get
                ? Json(HttpStatusCode.OK, "[]")
                : Text(HttpStatusCode.UnprocessableEntity,
                    "validation failed"))));
        await AuthorizeAbsentAsync(invalid);

        var unknown = await invalid.MutateStickyCommentAsync(
            CancellationToken.None);

        Assert.Equal(BoundedGitHubHttpOutcome.OutcomeUnknown,
            unknown.Outcome);
        Assert.Equal(BoundedGitHubPublisherReason.InvalidResponse,
            unknown.Reason);

        using var identity = Create(data, new DelegateHandler(request =>
            Task.FromResult(request.Method == HttpMethod.Get
                ? Json(HttpStatusCode.OK, "[]")
                : Json(HttpStatusCode.UnprocessableEntity,
                    "{\"id\":123,\"message\":\"validation failed\"," +
                    "\"errors\":[{\"resource\":\"PullRequestReview\"," +
                    "\"field\":\"comments\",\"code\":\"invalid\"}]}"))));
        await AuthorizeAbsentAsync(identity);

        var withIdentity = await identity.MutateStickyCommentAsync(
            CancellationToken.None);

        Assert.True(withIdentity.ValidationEvidence!.ReviewIdentityReturned);
        Assert.Equal("PullRequestReview",
            Assert.Single(withIdentity.ValidationEvidence.Errors).Resource);
    }

    [Fact]
    public async Task MutationTimeoutIsOutcomeUnknownAndNeverReplayed()
    {
        var data = await StickyPublicationTestData.CreateAsync();
        var mutationCalls = 0;
        using var transport = Create(data, new DelegateHandler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, "[]");
            mutationCalls++;
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            throw new InvalidOperationException("unreachable");
        }), TimeSpan.FromMilliseconds(5));
        await AuthorizeAbsentAsync(transport);

        var result = await transport.MutateStickyCommentAsync(
            CancellationToken.None);

        Assert.Equal(BoundedGitHubHttpOutcome.OutcomeUnknown,
            result.Outcome);
        Assert.Equal(BoundedGitHubPublisherReason.Deadline, result.Reason);
        Assert.Equal(1, mutationCalls);
    }

    [Fact]
    public async Task ServerErrorAfterMutationDispatchIsOutcomeUnknown()
    {
        var data = await StickyPublicationTestData.CreateAsync();
        var mutationCalls = 0;
        using var transport = Create(data, new DelegateHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(Json(HttpStatusCode.OK, "[]"));
            mutationCalls++;
            return Task.FromResult(Json(HttpStatusCode.InternalServerError,
                "{\"message\":\"server error\"}"));
        }));
        await AuthorizeAbsentAsync(transport);

        var result = await transport.MutateStickyCommentAsync(
            CancellationToken.None);

        Assert.Equal(BoundedGitHubHttpOutcome.OutcomeUnknown,
            result.Outcome);
        Assert.Equal(BoundedGitHubPublisherReason.InvalidResponse,
            result.Reason);
        Assert.Equal(1, mutationCalls);
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
        var overflow = new CountingStream([123]);
        using var transport = Create(data, new DelegateHandler(_ =>
        {
            calls++;
            if (calls <= 64)
                return Task.FromResult(Json(HttpStatusCode.OK, atResponseCap));
            var response = JsonContent(new StreamContent(overflow));
            response.Content.Headers.ContentLength = 1;
            return Task.FromResult(response);
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
        Assert.Equal(0, overflow.BytesRead);
    }

    [Fact]
    public async Task UnknownLengthResponseStopsAtOneOverflowSentinel()
    {
        var data = await StickyPublicationTestData.CreateAsync();
        var stream = new CountingStream(new byte[
            BoundedGitHubPublisherPolicy.MaximumResponseBytes + 2]);
        using var transport = Create(data, new DelegateHandler(_ =>
            Task.FromResult(JsonContent(new UnknownLengthContent(stream)))));

        var result = await transport.ListIssueCommentsAsync(1,
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(BoundedGitHubPublisherReason.ResponseLimit,
            result.Reason);
        Assert.Equal(BoundedGitHubPublisherPolicy.MaximumResponseBytes + 1,
            stream.BytesRead);
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

        var deadline = await expired.ListIssueCommentsAsync(1,
            CancellationToken.None);

        Assert.Equal(BoundedGitHubHttpOutcome.KnownNotSent,
            deadline.Outcome);
        Assert.Equal(BoundedGitHubPublisherReason.Deadline, deadline.Reason);
        Assert.Equal(0, calls);

        using var cancelled = Create(data, new DelegateHandler(_ =>
            Task.FromResult(Json(HttpStatusCode.OK, "[]"))));
        await AuthorizeAbsentAsync(cancelled);
        using var source = new CancellationTokenSource();
        source.Cancel();

        var cancellation = await cancelled.MutateStickyCommentAsync(
            source.Token);

        Assert.Equal(BoundedGitHubHttpOutcome.CancelledBeforeSend,
            cancellation.Outcome);
    }

    [Fact]
    public async Task CrossPageLastEvidenceCannotBeDiscardedBeforeMutation()
    {
        var data = await StickyPublicationTestData.CreateAsync();
        var mutationCalls = 0;
        using var transport = Create(data, new DelegateHandler(request =>
        {
            if (request.Method != HttpMethod.Get)
            {
                mutationCalls++;
                return Task.FromResult(Json(HttpStatusCode.Created,
                    JsonSerializer.Serialize(CommentDocument(9))));
            }
            var first = request.RequestUri!.Query.EndsWith("page=1",
                StringComparison.Ordinal);
            var response = Json(HttpStatusCode.OK, "[]");
            if (first)
                response.Headers.TryAddWithoutValidation("Link",
                    PageLink(2, "next") + ", " + PageLink(50, "last"));
            return Task.FromResult(response);
        }));

        Assert.NotNull((await transport.ListIssueCommentsAsync(1,
            CancellationToken.None)).Value);
        Assert.NotNull((await transport.ListIssueCommentsAsync(2,
            CancellationToken.None)).Value);
        var mutation = await transport.MutateStickyCommentAsync(
            CancellationToken.None);

        Assert.Equal(BoundedGitHubHttpOutcome.KnownNotSent, mutation.Outcome);
        Assert.Equal(0, mutationCalls);
    }

    [Fact]
    public async Task ChangingLastAndDuplicateIdsBlockDirectMutation()
    {
        var data = await StickyPublicationTestData.CreateAsync();
        foreach (var duplicate in new[] { false, true })
        {
            var mutationCalls = 0;
            using var transport = Create(data, new DelegateHandler(request =>
            {
                if (request.Method != HttpMethod.Get)
                {
                    mutationCalls++;
                    return Task.FromResult(Json(HttpStatusCode.Created,
                        JsonSerializer.Serialize(CommentDocument(9))));
                }
                var first = request.RequestUri!.Query.EndsWith("page=1",
                    StringComparison.Ordinal);
                var body = duplicate
                    ? JsonSerializer.Serialize(new[] { CommentDocument(7) })
                    : "[]";
                var response = Json(HttpStatusCode.OK, body);
                response.Headers.TryAddWithoutValidation("Link", first
                    ? PageLink(2, "next") + ", " +
                        PageLink(duplicate ? 2 : 3, "last")
                    : PageLink(duplicate ? 2 : 4, "last"));
                return Task.FromResult(response);
            }));

            Assert.NotNull((await transport.ListIssueCommentsAsync(1,
                CancellationToken.None)).Value);
            var second = await transport.ListIssueCommentsAsync(2,
                CancellationToken.None);
            if (duplicate) Assert.NotNull(second.Value);
            else Assert.Null(second.Value);
            var mutation = await transport.MutateStickyCommentAsync(
                CancellationToken.None);

            Assert.Equal(BoundedGitHubHttpOutcome.KnownNotSent,
                mutation.Outcome);
            Assert.Equal(0, mutationCalls);
        }
    }

    [Fact]
    public async Task OverallDeadlineIncludesResponseProcessing()
    {
        var data = await StickyPublicationTestData.CreateAsync();
        var clock = new ManualClock();
        var content = new UnknownLengthContent(
            new DeadlineAdvancingStream(Encoding.UTF8.GetBytes("[]"), clock,
                TimeSpan.FromSeconds(181)));
        using var transport = Create(data, new DelegateHandler(_ =>
            Task.FromResult(JsonContent(content))),
            overallTimeout: TimeSpan.FromSeconds(180), operation: clock);

        var result = await transport.ListIssueCommentsAsync(1,
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(BoundedGitHubHttpOutcome.KnownNotSent, result.Outcome);
        Assert.Equal(BoundedGitHubPublisherReason.Deadline, result.Reason);
    }

    [Fact]
    public async Task SuccessfulResponsesAreOnlyHttpSuccessEvidence()
    {
        var data = await StickyPublicationTestData.CreateAsync();
        using var transport = Create(data, new DelegateHandler(request =>
            Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith(
                    "/comments", StringComparison.Ordinal)
                ? request.Method == HttpMethod.Get
                    ? Json(HttpStatusCode.OK, "[]")
                    : Json(HttpStatusCode.Created,
                        JsonSerializer.Serialize(CommentDocument(7)))
                : Json(HttpStatusCode.OK,
                    JsonSerializer.Serialize(CommentDocument(7))))));
        var listed = await transport.ListIssueCommentsAsync(1,
            CancellationToken.None);
        var mutation = await transport.MutateStickyCommentAsync(
            CancellationToken.None);
        var read = await transport.GetIssueCommentAsync(7,
            CancellationToken.None);

        Assert.All(new[] { listed.Outcome, mutation.Outcome, read.Outcome },
            outcome => Assert.Equal(BoundedGitHubHttpOutcome.Success,
                outcome));
    }

    private static BoundedGitHubPublisherTransport Create(
        (AgenticPrReview.Runtime.ActionHost.Contracts.ActionHostGitHubToken Token,
            AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky
                .AuthorizedStickyPublicationRequest Request,
            AgenticPrReview.Runtime.Host.Publishing.Rendering
                .R4RenderedStickyComment Rendered) data,
        HttpMessageHandler handler, TimeSpan? requestTimeout = null,
        TimeSpan? overallTimeout = null,
        IBoundedGitHubOperationClock? operation = null) =>
        BoundedGitHubPublisherTransport.CreateForTesting(
            data.Token.ExportForPrivateLaunch(), data.Request,
            handler, requestTimeout, overallTimeout, operation);

    private static async Task AuthorizeAbsentAsync(
        BoundedGitHubPublisherTransport transport)
    {
        var discovery = await transport.ListIssueCommentsAsync(1,
            CancellationToken.None);
        Assert.NotNull(discovery.Value);
        Assert.Null(discovery.Value.NextPage);
    }

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

    private static string PageLink(int page, string relation) =>
        $"<https://api.github.com/repos/" +
        $"{ActionHostAuthorizationScenario.RepositoryName}/issues/" +
        $"{ActionHostAuthorizationScenario.PullRequestNumber}/comments" +
        $"?per_page=100&page={page}>; rel=\"{relation}\"";

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

    private static HttpResponseMessage Text(HttpStatusCode status,
        string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        };

    private static HttpResponseMessage JsonContent(HttpContent content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        };
        response.Content.Headers.ContentType =
            new("application/json") { CharSet = "utf-8" };
        return response;
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken,
            Task<HttpResponseMessage>> _send;

        internal DelegateHandler(
            Func<HttpRequestMessage, Task<HttpResponseMessage>> send) :
            this((request, _) => send(request))
        { }

        internal DelegateHandler(Func<HttpRequestMessage, CancellationToken,
            Task<HttpResponseMessage>> send) => _send = send;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            _send(request, cancellationToken);
    }

    private sealed class UnknownLengthContent(Stream stream) :
        HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream target,
            TransportContext? context) => stream.CopyToAsync(target);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(stream);
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal long BytesRead { get; private set; }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }
    }

    private sealed class ManualClock : IBoundedGitHubOperationClock
    {
        public TimeSpan Elapsed { get; set; }
    }

    private sealed class DeadlineAdvancingStream(byte[] bytes,
        ManualClock clock, TimeSpan elapsed) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            if (read == 0) clock.Elapsed = elapsed;
            return read;
        }
    }
}
