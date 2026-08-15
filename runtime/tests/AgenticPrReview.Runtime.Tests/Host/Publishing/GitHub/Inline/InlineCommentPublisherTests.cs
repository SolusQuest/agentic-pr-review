using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Reflection;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Inline;
using AgenticPrReview.Runtime.Host.Publishing.Inline;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.Publishing.Rendering;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.GitHub.Inline;

public sealed class InlineCommentPublisherTests
{
    private const string Exact422 =
        "{\"message\":\"Validation Failed\"," +
        "\"documentation_url\":\"https://docs.github.com/rest/pulls/" +
        "reviews#create-a-review-for-a-pull-request\"," +
        "\"errors\":[{\"resource\":\"PullRequestReview\"," +
        "\"field\":\"comments\",\"code\":\"invalid\"}]}";

    [Fact]
    public void PublicationAuthorityTypesHaveNoPublicConstructionSurface()
    {
        Assert.Empty(typeof(ActionHostCoordinator.PostAcceptanceInlineOperation)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        Assert.Empty(typeof(AuthorizedInlinePublicationRequest)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        Assert.DoesNotContain(
            typeof(BoundedGitHubPublisherTransportFactory).GetMethods(
                BindingFlags.Instance | BindingFlags.Public),
            static method => method.Name == "Create" &&
                method.GetParameters().Any(static parameter =>
                    parameter.ParameterType == typeof(ActionHostGitHubToken)) &&
                method.GetParameters().Any(static parameter =>
                    parameter.ParameterType ==
                        typeof(AuthorizedInlinePublicationRequest)));
    }

    [Fact]
    public async Task CompleteInitialListSuppressesExactIdentityWithoutWrite()
    {
        var data = await CreateAsync();
        var rendered = Render(data.Request);
        var transport = new FakeInlineTransport();
        transport.EnqueuePage(Comment(data.Request, rendered[0], 7));
        var publisher = new InlineCommentPublisher(
            new FakeInlineFactory(transport));

        var result = await publisher.PublishAsync(
            data.Request, CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(1, result.Reasons.ExistingDuplicate);
        Assert.Equal(0, result.BatchAttempts);
        Assert.Equal(0, data.Revalidation.Calls);
        Assert.Equal(0, transport.BatchCalls);
    }

    [Fact]
    public async Task AmbiguousBatchNeverFallsBackAndCompletesOnlyByReadback()
    {
        var data = await CreateAsync();
        var rendered = Render(data.Request);
        var transport = new FakeInlineTransport
        {
            Batch = BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>
                .Failed(BoundedGitHubHttpOutcome.OutcomeUnknown,
                    BoundedGitHubPublisherReason.TransportFailure),
        };
        transport.EnqueuePage();
        transport.EnqueuePage(Comment(data.Request, rendered[0], 8));
        var publisher = new InlineCommentPublisher(
            new FakeInlineFactory(transport));

        var result = await publisher.PublishAsync(
            data.Request, CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(1, result.Reasons.ReconciledPublished);
        Assert.Equal(1, result.BatchAttempts);
        Assert.Equal(0, result.IndividualAttempts);
        Assert.Equal(1, transport.BatchCalls);
        Assert.Equal(0, transport.IndividualCalls);
    }

    [Fact]
    public async Task UnresolvedAmbiguousBatchNeverFallsBack()
    {
        var data = await CreateAsync();
        var transport = new FakeInlineTransport
        {
            Batch = BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>
                .Failed(BoundedGitHubHttpOutcome.OutcomeUnknown,
                    BoundedGitHubPublisherReason.TransportFailure),
        };
        transport.EnqueuePage();
        transport.EnqueuePage();
        var publisher = new InlineCommentPublisher(
            new FakeInlineFactory(transport));

        var result = await publisher.PublishAsync(
            data.Request, CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Equal(1, result.Reasons.BatchOutcomeUnknown);
        Assert.Equal(0, result.IndividualAttempts);
        Assert.Equal(0, transport.IndividualCalls);
    }

    [Fact]
    public async Task ExactBatchValidationRelistsAndUsesBoundedIndividualPath()
    {
        var data = await CreateAsync();
        var rendered = Render(data.Request);
        var exact = Comment(data.Request, rendered[0], 9);
        var transport = new FakeInlineTransport
        {
            Batch = BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>
                .Failed(BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.BatchValidationRejected),
        };
        transport.EnqueuePage();
        transport.EnqueuePage();
        transport.Creates.Enqueue(
            BoundedGitHubHttpResult<BoundedGitHubReviewComment>.Success(exact));
        transport.Reads.Enqueue(
            BoundedGitHubHttpResult<BoundedGitHubReviewComment>.Success(exact));
        var publisher = new InlineCommentPublisher(
            new FakeInlineFactory(transport));

        var result = await publisher.PublishAsync(
            data.Request, CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(1, result.Reasons.IndividualPublished);
        Assert.Equal(1, result.BatchAttempts);
        Assert.Equal(1, result.IndividualAttempts);
        Assert.Equal(2, data.Revalidation.Calls);
        Assert.Equal(2, transport.ListCalls);
        Assert.Equal(1, transport.IndividualCalls);
        Assert.Equal(1, transport.ReadCalls);
    }

    [Fact]
    public async Task ChangedHeadAtFirstBarrierClosesBeforeBatch()
    {
        var data = await CreateAsync();
        data.Revalidation.Current = data.Revalidation.Current with
        {
            HeadSha = new string('f', 40),
        };
        var transport = new FakeInlineTransport();
        transport.EnqueuePage();
        var publisher = new InlineCommentPublisher(
            new FakeInlineFactory(transport));

        var result = await publisher.PublishAsync(
            data.Request, CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Equal(1, result.Reasons.HeadNotExact);
        Assert.Equal(0, result.BatchAttempts);
        Assert.Equal(1, data.Revalidation.Calls);
        Assert.Equal(0, transport.BatchCalls);
    }

    [Fact]
    public async Task ChangedHeadAtFallbackBarrierClosesBeforeIndividuals()
    {
        var data = await CreateAsync();
        data.Revalidation.OnCall = call => call == 1
            ? data.Revalidation.Current
            : data.Revalidation.Current with
            {
                HeadSha = new string('f', 40),
            };
        var transport = new FakeInlineTransport
        {
            Batch = BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>
                .Failed(BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.BatchValidationRejected),
        };
        transport.EnqueuePage();
        transport.EnqueuePage();
        var publisher = new InlineCommentPublisher(
            new FakeInlineFactory(transport));

        var result = await publisher.PublishAsync(
            data.Request, CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Equal(1, result.Reasons.HeadNotExact);
        Assert.Equal(1, result.BatchAttempts);
        Assert.Equal(0, result.IndividualAttempts);
        Assert.Equal(2, data.Revalidation.Calls);
        Assert.Equal(0, transport.IndividualCalls);
    }

    [Fact]
    public async Task CommonTransportUsesExactBatchEndpointAndStrict422Shape()
    {
        var data = await CreateAsync();
        using var transport = BoundedGitHubPublisherTransport
            .CreateInlineForTesting("token-canary", data.Request,
                new DelegateHandler(request =>
                {
                    Assert.Equal(HttpMethod.Post, request.Method);
                    Assert.Equal(
                        $"https://api.github.com/repos/" +
                        $"{ActionHostAuthorizationScenario.RepositoryName}/" +
                        $"pulls/{ActionHostAuthorizationScenario.PullRequestNumber}/" +
                        "reviews",
                        request.RequestUri!.AbsoluteUri);
                    Assert.Equal(BoundedGitHubPublisherPolicy.ApiVersion,
                        Assert.Single(request.Headers.GetValues(
                            "X-GitHub-Api-Version")));
                    return Json(HttpStatusCode.UnprocessableEntity, Exact422);
                }));

        var result = await transport.CreateBatchReviewAsync(
            "{}"u8.ToArray(), CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(BoundedGitHubHttpOutcome.KnownNotSent, result.Outcome);
        Assert.Equal(BoundedGitHubPublisherReason.BatchValidationRejected,
            result.Reason);
        Assert.Null(result.ValidationEvidence);
    }

    [Fact]
    public async Task CommonTransportDoesNotTreatUnknown422AsFallbackSafe()
    {
        var data = await CreateAsync();
        using var transport = BoundedGitHubPublisherTransport
            .CreateInlineForTesting("token-canary", data.Request,
                new DelegateHandler(_ => Json(
                    HttpStatusCode.UnprocessableEntity,
                    Exact422[..^1] + ",\"extra\":true}")));

        var result = await transport.CreateBatchReviewAsync(
            "{}"u8.ToArray(), CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(
            BoundedGitHubHttpOutcome.AuthorizationOrValidationFailure,
            result.Outcome);
        Assert.Equal(BoundedGitHubPublisherReason.ValidationRejected,
            result.Reason);
        Assert.NotNull(result.ValidationEvidence);
    }

    [Fact]
    public async Task CommonTransportListsReviewCommentsOnCanonicalEndpoint()
    {
        var data = await CreateAsync();
        using var transport = BoundedGitHubPublisherTransport
            .CreateInlineForTesting("token-canary", data.Request,
                new DelegateHandler(request =>
                {
                    Assert.Equal(HttpMethod.Get, request.Method);
                    Assert.EndsWith(
                        $"/pulls/{ActionHostAuthorizationScenario.PullRequestNumber}/" +
                        "comments?per_page=100&page=1",
                        request.RequestUri!.AbsoluteUri,
                        StringComparison.Ordinal);
                    return Json(HttpStatusCode.OK, "[]");
                }));

        var result = await transport.ListReviewCommentsAsync(
            1, CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Empty(result.Value.Comments);
        Assert.Null(result.Value.NextPage);
    }

    [Fact]
    public async Task SerializerFreezesHeadCoordinatesAndEscapesCanaries()
    {
        var finding = new AgentFinding(
            "high",
            "@ title <!-- fake",
            "message --> [link](https://example.invalid)",
            ImmutableArray.Create(new AgentEvidence(
                "observation", "file.txt", 1, 1)));
        var data = await CreateAsync(finding);
        var rendered = Render(data.Request);

        Assert.Single(rendered);
        Assert.DoesNotContain("@ title", rendered[0].Body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("<!-- fake", rendered[0].Body,
            StringComparison.Ordinal);
        var marker = InlineCommentMarker.Inspect(rendered[0].Body);
        Assert.Equal(InlineMarkerInspectionKind.Valid, marker.Kind);
        Assert.True(InlineCommentSerializer.TryBatch(
            data.Request, rendered, out var batch));
        Assert.NotNull(batch);
        Assert.True(batch.Length <= BoundedGitHubPublisherPolicy
            .MaximumInlineBatchRequestBytes);

        using var json = JsonDocument.Parse(batch);
        var root = json.RootElement;
        Assert.Equal(data.Request.Authorization.PullRequest.HeadSha,
            root.GetProperty("commit_id").GetString());
        Assert.Equal("COMMENT", root.GetProperty("event").GetString());
        var comment = Assert.Single(root.GetProperty("comments")
            .EnumerateArray());
        Assert.Equal("file.txt", comment.GetProperty("path").GetString());
        Assert.Equal(1, comment.GetProperty("line").GetInt32());
        Assert.Equal("RIGHT", comment.GetProperty("side").GetString());
        Assert.Equal(rendered[0].Body,
            comment.GetProperty("body").GetString());
    }

    private static IReadOnlyList<RenderedInlineComment> Render(
        AuthorizedInlinePublicationRequest request)
    {
        Assert.True(InlineCommentSerializer.TryRender(request,
            out var rendered));
        return Assert.IsAssignableFrom<IReadOnlyList<RenderedInlineComment>>(
            rendered);
    }

    private static BoundedGitHubReviewComment Comment(
        AuthorizedInlinePublicationRequest request,
        RenderedInlineComment rendered,
        long id) => new(
            id,
            99,
            $"https://api.github.com/repos/owner/repository/pulls/comments/{id}",
            "https://api.github.com/repos/owner/repository/pulls/17",
            "https://github.com/owner/repository/pull/17#discussion-diff-1",
            rendered.Body,
            rendered.Candidate.Path,
            rendered.Candidate.Line,
            "RIGHT",
            request.Authorization.PullRequest.HeadSha);

    private static async Task<TestData> CreateAsync(
        AgentFinding? suppliedFinding = null)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowRun);
        var authorized = await scenario.CreateAuthorizer().AuthorizeAsync(
            scenario.Launch, CancellationToken.None);
        var invocation = Assert.IsType<
            ActionHostAuthorizer.AuthorizedInvocation>(authorized.Invocation);
        var identity = new AgenticPrReview.Runtime.Agent.Core.ReviewedIdentity(
            invocation.PullRequest.RepositoryId.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            invocation.PullRequest.Number,
            invocation.PullRequest.BaseSha,
            invocation.PullRequest.HeadSha);
        var finding = suppliedFinding ?? R4PublicationTestData.Finding();
        var candidate = new InlineCandidate(
            new R4FindingIdentityV1(finding, new string('b', 64)),
            "file.txt",
            1,
            new string('c', 64));
        var map = new InlineCandidateMap(
            identity,
            new string('d', 64),
            new string('e', 64),
            ImmutableArray.Create(candidate),
            [],
            new(0, 0));
        var revalidation = new FakeRevalidationFactory(
            scenario.Transport.PullRequest);
        var constructor = typeof(ActionHostCoordinator
                .PostAcceptanceInlineOperation)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();
        var operation = Assert.IsType<ActionHostCoordinator
            .PostAcceptanceInlineOperation>(constructor.Invoke([
                scenario.Launch.Inputs.GitHubToken!,
                invocation,
                revalidation,
                map,
            ]));
        Assert.True(AuthorizedInlinePublicationRequest.TryCreate(
            operation, out var request));
        return new(request!, revalidation);
    }

    private sealed record TestData(
        AuthorizedInlinePublicationRequest Request,
        FakeRevalidationFactory Revalidation);

    private sealed class FakeInlineFactory(FakeInlineTransport transport) :
        IInlineGitHubPublisherTransportFactory
    {
        public IInlineGitHubPublisherTransport Create(
            AuthorizedInlinePublicationRequest request) => transport;
    }

    private sealed class FakeInlineTransport : IInlineGitHubPublisherTransport
    {
        private readonly Queue<BoundedGitHubHttpResult<
            BoundedGitHubReviewCommentPage>> _pages = new();

        internal BoundedGitHubHttpResult<BoundedGitHubPullRequestReview> Batch
        {
            get; set;
        } = BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>.Success(
            new(1, "api", "pull", "html#review", new string('a', 40)));

        internal Queue<BoundedGitHubHttpResult<BoundedGitHubReviewComment>>
            Creates { get; } = new();

        internal Queue<BoundedGitHubHttpResult<BoundedGitHubReviewComment>>
            Reads { get; } = new();

        internal int ListCalls { get; private set; }
        internal int BatchCalls { get; private set; }
        internal int IndividualCalls { get; private set; }
        internal int ReadCalls { get; private set; }

        public bool IsWithinOverallDeadline => true;

        internal void EnqueuePage(
            params BoundedGitHubReviewComment[] comments) =>
            _pages.Enqueue(BoundedGitHubHttpResult<
                BoundedGitHubReviewCommentPage>.Success(
                    new(comments, null, null)));

        public Task<BoundedGitHubHttpResult<BoundedGitHubReviewCommentPage>>
            ListReviewCommentsAsync(int page,
                CancellationToken cancellationToken)
        {
            ListCalls++;
            return Task.FromResult(_pages.Dequeue());
        }

        public Task<BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>>
            CreateBatchReviewAsync(ReadOnlyMemory<byte> body,
                CancellationToken cancellationToken)
        {
            BatchCalls++;
            return Task.FromResult(Batch);
        }

        public Task<BoundedGitHubHttpResult<BoundedGitHubReviewComment>>
            CreateReviewCommentAsync(ReadOnlyMemory<byte> body,
                CancellationToken cancellationToken)
        {
            IndividualCalls++;
            return Task.FromResult(Creates.Dequeue());
        }

        public Task<BoundedGitHubHttpResult<BoundedGitHubReviewComment>>
            GetReviewCommentAsync(long commentId,
                CancellationToken cancellationToken)
        {
            ReadCalls++;
            return Task.FromResult(Reads.Dequeue());
        }

        public void Dispose() { }
    }

    private sealed class FakeRevalidationFactory(
        ActionHostGitHubPullRequestFact current) :
        IActionHostReviewedSnapshotTransportFactory
    {
        internal int Calls { get; private set; }
        internal ActionHostGitHubPullRequestFact Current { get; set; } =
            current;
        internal Func<int, ActionHostGitHubPullRequestFact>? OnCall
        {
            get; set;
        }

        public IActionHostReviewedSnapshotTransport
            CreateReviewedSnapshotTransport(ActionHostGitHubToken token) =>
            new Transport(this);

        private sealed class Transport(FakeRevalidationFactory owner) :
            IActionHostReviewedSnapshotTransport
        {
            public Task<ActionHostGitObjectResult<
                ActionHostGitHubPullRequestFact>> GetCurrentPullRequestAsync(
                    string repositoryName,
                    long pullRequestNumber,
                    CancellationToken cancellationToken)
            {
                owner.Calls++;
                var value = owner.OnCall?.Invoke(owner.Calls) ?? owner.Current;
                return Task.FromResult(ActionHostGitObjectResult<
                    ActionHostGitHubPullRequestFact>.Success(value, 64));
            }

            public Task<ActionHostGitObjectResult<
                ActionHostPullRequestFilePageObject>> GetPullRequestFilesAsync(
                    string repositoryName, long pullRequestNumber, int page,
                    int perPage, CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task<ActionHostGitObjectResult<ActionHostGitCommitObject>>
                GetCommitObjectAsync(string repositoryName, string commitSha,
                    CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task<ActionHostGitObjectResult<ActionHostGitTreeObject>>
                GetTreeObjectAsync(string repositoryName, string treeSha,
                    CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task<ActionHostGitObjectResult<
                ActionHostStreamedBlobObject>> CopyBlobObjectAsync(
                    string repositoryName, string blobSha, long declaredSize,
                    Stream destination,
                    CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public void Dispose() { }
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status,
        string json)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json)),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/json") { CharSet = "utf-8" };
        return response;
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, HttpResponseMessage> callback) :
        HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(callback(request));
    }
}
