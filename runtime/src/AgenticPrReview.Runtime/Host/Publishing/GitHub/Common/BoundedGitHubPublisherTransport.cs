using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Inline;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;

namespace AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;

internal sealed class BoundedGitHubPublisherTransportFactory :
    IStickyGitHubPublisherTransportFactory,
    IInlineGitHubPublisherTransportFactory
{
    public IStickyGitHubPublisherTransport Create(ActionHostGitHubToken token,
        AuthorizedStickyPublicationRequest request)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(request);
        return BoundedGitHubPublisherTransport.Create(
            token.ExportForPrivateLaunch(), request);
    }

    public IStickyGitHubReadbackTransport CreateReadback(
        ActionHostGitHubToken token, AuthorizedStickyReadbackRequest request)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(request);
        return BoundedGitHubPublisherTransport.CreateReadback(
            token.ExportForPrivateLaunch(), request);
    }

    public IInlineGitHubPublisherTransport Create(
        AuthorizedInlinePublicationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return BoundedGitHubPublisherTransport.CreateInline(
            request.Token.ExportForPrivateLaunch(), request);
    }
}

internal sealed class BoundedGitHubPublisherCredentialException : Exception
{
    internal BoundedGitHubPublisherCredentialException()
        : base("The GitHub publisher credential is invalid.") { }
}

internal sealed class BoundedGitHubPublisherTransport :
    IStickyGitHubPublisherTransport,
    IInlineGitHubPublisherTransport
{
    private readonly string _token;
    private readonly string _repositoryName;
    private readonly string _repositoryPath;
    private readonly long _pullRequestNumber;
    private readonly string _headSha;
    private readonly HttpClient _client;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _overallTimeout;
    private readonly IBoundedGitHubOperationClock _operation;
    private readonly SemaphoreSlim _responseReadGate = new(1, 1);
    private readonly R4PublicationIdentityV1? _stickyIdentity;
    private readonly AuthorizedStickyPublicationRequest? _stickyRequest;
    private int _requestCount;
    private long _aggregateBytes;
    private int _nextStickyPage = 1;
    private int? _stickyLastPage;
    private readonly HashSet<long> _stickySeenIds = [];
    private int _stickyRecordCount;
    private long? _stickyTargetId;
    private bool _stickyDiscoveryComplete;
    private bool _stickyDiscoveryInvalid;
    private int _mutationDispatched;
    private int _inlineBatchDispatched;
    private int _inlineIndividualDispatched;
    private bool _disposed;

    private BoundedGitHubPublisherTransport(string token,
        ActionHostAuthorizer.AuthorizedInvocation authorization,
        R4PublicationIdentityV1? stickyIdentity,
        AuthorizedStickyPublicationRequest? stickyRequest,
        HttpMessageHandler handler, TimeSpan requestTimeout,
        TimeSpan overallTimeout, IBoundedGitHubOperationClock operation)
    {
        Validate(token, authorization);
        _stickyIdentity = stickyIdentity;
        _stickyRequest = stickyRequest;
        _token = token;
        _repositoryName = authorization.PullRequest.BaseRepositoryName;
        _repositoryPath = RepositoryPath(_repositoryName);
        _pullRequestNumber = authorization.PullRequest.Number;
        _headSha = authorization.PullRequest.HeadSha;
        _requestTimeout = requestTimeout;
        _overallTimeout = overallTimeout;
        _operation = operation ?? throw new ArgumentNullException(
            nameof(operation));
        _client = new HttpClient(handler, true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _client.DefaultRequestHeaders.Clear();
    }

    internal static BoundedGitHubPublisherTransport Create(string token,
        AuthorizedStickyPublicationRequest request) => new(token,
            request.Authorization, request.Rendered.Identity, request,
            ActionHostGitHubAuthorizationTransport.CreateHandler(
                TimeSpan.FromSeconds(10)),
            BoundedGitHubPublisherPolicy.RequestTimeout,
            BoundedGitHubPublisherPolicy.OverallTimeout,
            new StopwatchBoundedGitHubOperationClock());

    internal static BoundedGitHubPublisherTransport CreateInline(string token,
        AuthorizedInlinePublicationRequest request) => new(token,
            request.Authorization, null, null,
            ActionHostGitHubAuthorizationTransport.CreateHandler(
                TimeSpan.FromSeconds(10)),
            BoundedGitHubPublisherPolicy.RequestTimeout,
            BoundedGitHubPublisherPolicy.OverallTimeout,
            new StopwatchBoundedGitHubOperationClock());

    internal static BoundedGitHubPublisherTransport CreateInlineForTesting(
        string token, AuthorizedInlinePublicationRequest request,
        HttpMessageHandler handler, TimeSpan? requestTimeout = null,
        TimeSpan? overallTimeout = null,
        IBoundedGitHubOperationClock? operation = null) => new(token,
            request.Authorization, null, null, handler,
            requestTimeout ?? BoundedGitHubPublisherPolicy.RequestTimeout,
            overallTimeout ?? BoundedGitHubPublisherPolicy.OverallTimeout,
            operation ?? new StopwatchBoundedGitHubOperationClock());

    internal static IStickyGitHubReadbackTransport CreateReadback(string token,
        AuthorizedStickyReadbackRequest request) =>
        new ReadbackTransport(new(token, request.Authorization,
            request.ExpectedIdentity, null,
            ActionHostGitHubAuthorizationTransport.CreateHandler(
                TimeSpan.FromSeconds(10)),
            BoundedGitHubPublisherPolicy.RequestTimeout,
            BoundedGitHubPublisherPolicy.OverallTimeout,
            new StopwatchBoundedGitHubOperationClock()));

    internal static BoundedGitHubPublisherTransport CreateForTesting(
        string token, AuthorizedStickyPublicationRequest request,
        HttpMessageHandler handler, TimeSpan? requestTimeout = null,
        TimeSpan? overallTimeout = null,
        IBoundedGitHubOperationClock? operation = null) => new(token,
            request.Authorization, request.Rendered.Identity, request, handler,
            requestTimeout ?? BoundedGitHubPublisherPolicy.RequestTimeout,
            overallTimeout ?? BoundedGitHubPublisherPolicy.OverallTimeout,
            operation ?? new StopwatchBoundedGitHubOperationClock());

    public bool IsWithinOverallDeadline =>
        !_disposed && _operation.Elapsed < _overallTimeout;

    public async Task<BoundedGitHubHttpResult<BoundedGitHubIssueCommentPage>>
        ListIssueCommentsAsync(int page, CancellationToken cancellationToken)
    {
        if (page is < 1 or > BoundedGitHubPublisherPolicy.MaximumPages)
            return Fail<BoundedGitHubIssueCommentPage>(
                BoundedGitHubHttpOutcome.KnownNotSent,
                BoundedGitHubPublisherReason.InvalidRequest);
        var captured = await SendAsync(HttpMethod.Get,
            $"/repos/{_repositoryPath}/issues/{_pullRequestNumber}/comments" +
            $"?per_page={BoundedGitHubPublisherPolicy.PerPage}&page={page}",
            null, HttpStatusCode.OK, false, cancellationToken);
        if (captured.Value is null)
            return Fail<BoundedGitHubIssueCommentPage>(captured.Outcome,
                captured.Reason, captured.ValidationEvidence);
        try
        {
            var docs = JsonSerializer.Deserialize(captured.Value.Body,
                BoundedGitHubPublisherJsonContext.Default
                    .BoundedGitHubIssueCommentDocumentArray);
            if (docs is null ||
                docs.Length > BoundedGitHubPublisherPolicy.PerPage ||
                !TryNextPage(captured.Value.Links, page,
                    $"/repos/{_repositoryPath}/issues/" +
                    $"{_pullRequestNumber}/comments", out var next,
                    out var last))
                return Fail<BoundedGitHubIssueCommentPage>(
                    BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.InvalidPagination);
            if (!IsWithinOverallDeadline)
                return Fail<BoundedGitHubIssueCommentPage>(
                    BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.Deadline);
            var comments = new List<BoundedGitHubIssueComment>(docs.Length);
            foreach (var doc in docs)
            {
                if (doc is null || !TryMap(doc, out var comment))
                    return Fail<BoundedGitHubIssueCommentPage>(
                        BoundedGitHubHttpOutcome.KnownNotSent,
                        BoundedGitHubPublisherReason.InvalidResponse);
                comments.Add(comment!);
                if (!IsWithinOverallDeadline)
                    return Fail<BoundedGitHubIssueCommentPage>(
                        BoundedGitHubHttpOutcome.KnownNotSent,
                        BoundedGitHubPublisherReason.Deadline);
            }
            var result = new BoundedGitHubIssueCommentPage(comments, next,
                last);
            ObserveStickyPage(page, result);
            return BoundedGitHubHttpResult<BoundedGitHubIssueCommentPage>
                .Success(result);
        }
        catch (JsonException)
        {
            return Fail<BoundedGitHubIssueCommentPage>(
                BoundedGitHubHttpOutcome.KnownNotSent,
                BoundedGitHubPublisherReason.InvalidResponse);
        }
    }

    public async Task<BoundedGitHubHttpResult<BoundedGitHubIssueComment>>
        MutateStickyCommentAsync(CancellationToken cancellationToken)
    {
        if (!_stickyDiscoveryComplete || _stickyDiscoveryInvalid ||
            _stickyRequest is null ||
            Interlocked.Exchange(ref _mutationDispatched, 1) != 0 ||
            !StickyCommentSerializer.TrySerialize(
                _stickyRequest.Rendered.Comment, out var body))
            return Fail<BoundedGitHubIssueComment>(
                BoundedGitHubHttpOutcome.KnownNotSent,
                BoundedGitHubPublisherReason.InvalidRequest);
        return _stickyTargetId is long targetId
            ? await SendCommentAsync(HttpMethod.Patch,
                $"/repos/{_repositoryPath}/issues/comments/{targetId}",
                body!, HttpStatusCode.OK, cancellationToken)
            : await SendCommentAsync(HttpMethod.Post,
                $"/repos/{_repositoryPath}/issues/{_pullRequestNumber}/comments",
                body!, HttpStatusCode.Created, cancellationToken);
    }

    public async Task<BoundedGitHubHttpResult<BoundedGitHubIssueComment>>
        GetIssueCommentAsync(long commentId,
            CancellationToken cancellationToken)
    {
        if (commentId <= 0)
            return Fail<BoundedGitHubIssueComment>(
                BoundedGitHubHttpOutcome.KnownNotSent,
                BoundedGitHubPublisherReason.InvalidRequest);
        var captured = await SendAsync(HttpMethod.Get,
            $"/repos/{_repositoryPath}/issues/comments/{commentId}", null,
            HttpStatusCode.OK, false, cancellationToken);
        return MapComment(captured, false);
    }

    public async Task<BoundedGitHubHttpResult<
        BoundedGitHubReviewCommentPage>> ListReviewCommentsAsync(
            int page,
            CancellationToken cancellationToken)
    {
        if (page is < 1 or > BoundedGitHubPublisherPolicy.MaximumPages)
        {
            return Fail<BoundedGitHubReviewCommentPage>(
                BoundedGitHubHttpOutcome.KnownNotSent,
                BoundedGitHubPublisherReason.InvalidRequest);
        }

        var endpoint = $"/repos/{_repositoryPath}/pulls/" +
            $"{_pullRequestNumber}/comments";
        var captured = await SendAsync(HttpMethod.Get,
            $"{endpoint}?per_page={BoundedGitHubPublisherPolicy.PerPage}" +
            $"&page={page}", null, HttpStatusCode.OK, false,
            cancellationToken);
        if (captured.Value is null)
        {
            return Fail<BoundedGitHubReviewCommentPage>(captured.Outcome,
                captured.Reason, captured.ValidationEvidence);
        }

        try
        {
            var docs = JsonSerializer.Deserialize(captured.Value.Body,
                BoundedGitHubPublisherJsonContext.Default
                    .BoundedGitHubReviewCommentDocumentArray);
            if (docs is null ||
                docs.Length > BoundedGitHubPublisherPolicy.PerPage ||
                !TryNextPage(captured.Value.Links, page, endpoint,
                    out var next, out var last))
            {
                return Fail<BoundedGitHubReviewCommentPage>(
                    BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.InvalidPagination);
            }

            if (!IsWithinOverallDeadline)
            {
                return Fail<BoundedGitHubReviewCommentPage>(
                    BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.Deadline);
            }

            var comments = new List<BoundedGitHubReviewComment>(docs.Length);
            foreach (var doc in docs)
            {
                if (doc is null || !TryMap(doc, out var comment))
                {
                    return Fail<BoundedGitHubReviewCommentPage>(
                        BoundedGitHubHttpOutcome.KnownNotSent,
                        BoundedGitHubPublisherReason.InvalidResponse);
                }

                comments.Add(comment!);
                if (!IsWithinOverallDeadline)
                {
                    return Fail<BoundedGitHubReviewCommentPage>(
                        BoundedGitHubHttpOutcome.KnownNotSent,
                        BoundedGitHubPublisherReason.Deadline);
                }
            }

            return BoundedGitHubHttpResult<BoundedGitHubReviewCommentPage>
                .Success(new(comments, next, last));
        }
        catch (JsonException)
        {
            return Fail<BoundedGitHubReviewCommentPage>(
                BoundedGitHubHttpOutcome.KnownNotSent,
                BoundedGitHubPublisherReason.InvalidResponse);
        }
    }

    public async Task<BoundedGitHubHttpResult<
        BoundedGitHubPullRequestReview>> CreateBatchReviewAsync(
            ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken)
    {
        if (body.IsEmpty || body.Length >
                BoundedGitHubPublisherPolicy.MaximumInlineBatchRequestBytes ||
            Interlocked.Exchange(ref _inlineBatchDispatched, 1) != 0)
        {
            return Fail<BoundedGitHubPullRequestReview>(
                BoundedGitHubHttpOutcome.KnownNotSent,
                BoundedGitHubPublisherReason.InvalidRequest);
        }

        var captured = await SendAsync(HttpMethod.Post,
            $"/repos/{_repositoryPath}/pulls/{_pullRequestNumber}/reviews",
            body, HttpStatusCode.OK, true, cancellationToken,
            exactBatchValidation: true);
        return MapReview(captured);
    }

    public async Task<BoundedGitHubHttpResult<
        BoundedGitHubReviewComment>> CreateReviewCommentAsync(
            ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken)
    {
        if (body.IsEmpty || body.Length > BoundedGitHubPublisherPolicy
                .MaximumIndividualInlineRequestBytes ||
            Interlocked.Increment(ref _inlineIndividualDispatched) > 5)
        {
            return Fail<BoundedGitHubReviewComment>(
                BoundedGitHubHttpOutcome.KnownNotSent,
                BoundedGitHubPublisherReason.InvalidRequest);
        }

        var captured = await SendAsync(HttpMethod.Post,
            $"/repos/{_repositoryPath}/pulls/" +
            $"{_pullRequestNumber}/comments", body,
            HttpStatusCode.Created, true, cancellationToken);
        return MapReviewComment(captured, mutation: true);
    }

    public async Task<BoundedGitHubHttpResult<BoundedGitHubReviewComment>>
        GetReviewCommentAsync(long commentId,
            CancellationToken cancellationToken)
    {
        if (commentId <= 0)
        {
            return Fail<BoundedGitHubReviewComment>(
                BoundedGitHubHttpOutcome.KnownNotSent,
                BoundedGitHubPublisherReason.InvalidRequest);
        }

        var captured = await SendAsync(HttpMethod.Get,
            $"/repos/{_repositoryPath}/pulls/comments/{commentId}", null,
            HttpStatusCode.OK, false, cancellationToken);
        return MapReviewComment(captured, mutation: false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
        _responseReadGate.Dispose();
    }

    private void ObserveStickyPage(int page,
        BoundedGitHubIssueCommentPage result)
    {
        if (_stickyDiscoveryComplete || _stickyDiscoveryInvalid) return;
        if (page != _nextStickyPage)
        {
            _stickyDiscoveryInvalid = true;
            return;
        }
        if (result.LastPage is int lastPage)
        {
            if (_stickyLastPage is int expectedLast &&
                    expectedLast != lastPage ||
                result.NextPage is int nextPage && nextPage > lastPage)
            {
                _stickyDiscoveryInvalid = true;
                return;
            }
            _stickyLastPage = lastPage;
        }
        if (result.NextPage is null && _stickyLastPage is int last &&
            last != page)
        {
            _stickyDiscoveryInvalid = true;
            return;
        }
        foreach (var comment in result.Comments)
        {
            _stickyRecordCount++;
            if (_stickyRecordCount >
                    BoundedGitHubPublisherPolicy.MaximumRecords ||
                !_stickySeenIds.Add(comment.Id) ||
                !TryInspect(comment.Body, out var inspection) ||
                inspection.Kind == R4StickyInspectionKind.InvalidR4)
            {
                _stickyDiscoveryInvalid = true;
                return;
            }
            if (inspection.Kind == R4StickyInspectionKind.ValidR4 &&
                StringComparer.Ordinal.Equals(
                    inspection.Identity!.ScopeSha256,
                    _stickyIdentity?.ScopeSha256))
            {
                if (_stickyTargetId is not null)
                {
                    _stickyDiscoveryInvalid = true;
                    return;
                }
                _stickyTargetId = comment.Id;
            }
        }
        if (result.NextPage is null)
            _stickyDiscoveryComplete = true;
        else if (result.NextPage == page + 1)
            _nextStickyPage = result.NextPage.Value;
        else
            _stickyDiscoveryInvalid = true;
    }

    private async Task<BoundedGitHubHttpResult<BoundedGitHubIssueComment>>
        SendCommentAsync(HttpMethod method, string path,
            ReadOnlyMemory<byte> body, HttpStatusCode expected,
            CancellationToken cancellationToken)
    {
        if (body.IsEmpty || body.Length >
            BoundedGitHubPublisherPolicy.MaximumStickyRequestBytes)
            return Fail<BoundedGitHubIssueComment>(
                BoundedGitHubHttpOutcome.KnownNotSent,
                BoundedGitHubPublisherReason.InvalidRequest);
        var captured = await SendAsync(method, path, body, expected, true,
            cancellationToken);
        return MapComment(captured, true);
    }

    private BoundedGitHubHttpResult<BoundedGitHubIssueComment> MapComment(
        BoundedGitHubHttpResult<CapturedResponse> captured, bool mutation)
    {
        if (captured.Value is null)
            return Fail<BoundedGitHubIssueComment>(captured.Outcome,
                captured.Reason, captured.ValidationEvidence);
        try
        {
            var doc = JsonSerializer.Deserialize(captured.Value.Body,
                BoundedGitHubPublisherJsonContext.Default
                    .BoundedGitHubIssueCommentDocument);
            if (doc is null || !TryMap(doc, out var comment))
                return Fail<BoundedGitHubIssueComment>(mutation
                        ? BoundedGitHubHttpOutcome.OutcomeUnknown
                        : BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.InvalidResponse);
            if (!IsWithinOverallDeadline)
                return Fail<BoundedGitHubIssueComment>(mutation
                        ? BoundedGitHubHttpOutcome.OutcomeUnknown
                        : BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.Deadline);
            return BoundedGitHubHttpResult<BoundedGitHubIssueComment>
                .Success(comment!);
        }
        catch (JsonException)
        {
            return Fail<BoundedGitHubIssueComment>(mutation
                    ? BoundedGitHubHttpOutcome.OutcomeUnknown
                    : BoundedGitHubHttpOutcome.KnownNotSent,
                BoundedGitHubPublisherReason.InvalidResponse);
        }
    }

    private BoundedGitHubHttpResult<BoundedGitHubReviewComment>
        MapReviewComment(BoundedGitHubHttpResult<CapturedResponse> captured,
            bool mutation)
    {
        if (captured.Value is null)
        {
            return Fail<BoundedGitHubReviewComment>(captured.Outcome,
                captured.Reason, captured.ValidationEvidence);
        }

        try
        {
            var doc = JsonSerializer.Deserialize(captured.Value.Body,
                BoundedGitHubPublisherJsonContext.Default
                    .BoundedGitHubReviewCommentDocument);
            if (doc is null || !TryMap(doc, out var comment))
            {
                return Fail<BoundedGitHubReviewComment>(mutation
                        ? BoundedGitHubHttpOutcome.OutcomeUnknown
                        : BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.InvalidResponse);
            }

            if (!IsWithinOverallDeadline)
            {
                return Fail<BoundedGitHubReviewComment>(mutation
                        ? BoundedGitHubHttpOutcome.OutcomeUnknown
                        : BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.Deadline);
            }

            return BoundedGitHubHttpResult<BoundedGitHubReviewComment>
                .Success(comment!);
        }
        catch (JsonException)
        {
            return Fail<BoundedGitHubReviewComment>(mutation
                    ? BoundedGitHubHttpOutcome.OutcomeUnknown
                    : BoundedGitHubHttpOutcome.KnownNotSent,
                BoundedGitHubPublisherReason.InvalidResponse);
        }
    }

    private BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>
        MapReview(BoundedGitHubHttpResult<CapturedResponse> captured)
    {
        if (captured.Value is null)
        {
            return Fail<BoundedGitHubPullRequestReview>(captured.Outcome,
                captured.Reason, captured.ValidationEvidence);
        }

        try
        {
            var doc = JsonSerializer.Deserialize(captured.Value.Body,
                BoundedGitHubPublisherJsonContext.Default
                    .BoundedGitHubPullRequestReviewDocument);
            if (doc?.Id is not > 0 || doc.Url is null ||
                doc.PullRequestUrl is null || doc.HtmlUrl is null ||
                doc.CommitId is null ||
                !StringComparer.Ordinal.Equals(doc.Url,
                    $"{BoundedGitHubPublisherPolicy.Origin}/repos/" +
                    $"{_repositoryName}/pulls/{_pullRequestNumber}/" +
                    $"reviews/{doc.Id}") ||
                !StringComparer.Ordinal.Equals(doc.PullRequestUrl,
                    $"{BoundedGitHubPublisherPolicy.Origin}/repos/" +
                    $"{_repositoryName}/pulls/{_pullRequestNumber}") ||
                !StringComparer.Ordinal.Equals(doc.CommitId, _headSha) ||
                !IsCanonicalReviewHtmlUrl(doc.HtmlUrl, doc.Id.Value))
            {
                return Fail<BoundedGitHubPullRequestReview>(
                    BoundedGitHubHttpOutcome.OutcomeUnknown,
                    BoundedGitHubPublisherReason.InvalidResponse);
            }

            if (!IsWithinOverallDeadline)
            {
                return Fail<BoundedGitHubPullRequestReview>(
                    BoundedGitHubHttpOutcome.OutcomeUnknown,
                    BoundedGitHubPublisherReason.Deadline);
            }

            return BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>
                .Success(new(doc.Id.Value, doc.Url, doc.PullRequestUrl,
                    doc.HtmlUrl, doc.CommitId));
        }
        catch (JsonException)
        {
            return Fail<BoundedGitHubPullRequestReview>(
                BoundedGitHubHttpOutcome.OutcomeUnknown,
                BoundedGitHubPublisherReason.InvalidResponse);
        }
    }

    private async Task<BoundedGitHubHttpResult<CapturedResponse>> SendAsync(
        HttpMethod method, string path, ReadOnlyMemory<byte>? body,
        HttpStatusCode expected, bool mutation,
        CancellationToken callerCancellation,
        bool exactBatchValidation = false)
    {
        if (callerCancellation.IsCancellationRequested)
            return Fail<CapturedResponse>(
                BoundedGitHubHttpOutcome.CancelledBeforeSend,
                BoundedGitHubPublisherReason.Deadline);
        if (_disposed || Interlocked.Increment(ref _requestCount) >
            BoundedGitHubPublisherPolicy.MaximumRequests)
            return Fail<CapturedResponse>(
                BoundedGitHubHttpOutcome.KnownNotSent,
                _disposed ? BoundedGitHubPublisherReason.TransportFailure :
                    BoundedGitHubPublisherReason.RequestLimit);
        var remaining = _overallTimeout - _operation.Elapsed;
        if (remaining <= TimeSpan.Zero)
            return Fail<CapturedResponse>(
                BoundedGitHubHttpOutcome.KnownNotSent,
                BoundedGitHubPublisherReason.Deadline);
        using var deadline = new CancellationTokenSource(
            remaining < _requestTimeout ? remaining : _requestTimeout);
        using var linked = mutation ? null :
            CancellationTokenSource.CreateLinkedTokenSource(
                callerCancellation, deadline.Token);
        var token = mutation ? deadline.Token : linked!.Token;
        try
        {
            using var request = new HttpRequestMessage(method,
                new Uri(BoundedGitHubPublisherPolicy.Origin + path));
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.TryAddWithoutValidation("User-Agent",
                BoundedGitHubPublisherPolicy.UserAgent);
            request.Headers.TryAddWithoutValidation("Accept",
                BoundedGitHubPublisherPolicy.Accept);
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version",
                BoundedGitHubPublisherPolicy.ApiVersion);
            if (body is { } bytes)
            {
                request.Content = new ByteArrayContent(bytes.ToArray());
                request.Content.Headers.ContentType =
                    new MediaTypeHeaderValue("application/json")
                    { CharSet = "utf-8" };
            }
            using var response = await _client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, token);
            var read = await ReadBoundedAsync(response.Content, token);
            if (read.Body is null)
                return Fail<CapturedResponse>(mutation
                        ? BoundedGitHubHttpOutcome.OutcomeUnknown
                        : BoundedGitHubHttpOutcome.KnownNotSent,
                    read.Reason);
            if (!IsWithinOverallDeadline)
                return Fail<CapturedResponse>(mutation
                        ? BoundedGitHubHttpOutcome.OutcomeUnknown
                        : BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.Deadline);
            if (response.StatusCode != expected)
            {
                if (exactBatchValidation && response.StatusCode ==
                        HttpStatusCode.UnprocessableEntity &&
                    HasJsonContentType(response.Content.Headers.ContentType) &&
                    InlineBatchValidationParser.IsExactKnownNotSent(read.Body))
                {
                    return Fail<CapturedResponse>(
                        BoundedGitHubHttpOutcome.KnownNotSent,
                        BoundedGitHubPublisherReason.BatchValidationRejected);
                }

                if (IsRecognized4xx(response.StatusCode) &&
                    HasJsonContentType(response.Content.Headers.ContentType) &&
                    TryErrorEvidence(response.StatusCode, read.Body,
                        out var evidence))
                {
                    if (!IsWithinOverallDeadline)
                        return Fail<CapturedResponse>(mutation
                                ? BoundedGitHubHttpOutcome.OutcomeUnknown
                                : BoundedGitHubHttpOutcome.KnownNotSent,
                            BoundedGitHubPublisherReason.Deadline);
                    return Fail<CapturedResponse>(
                        BoundedGitHubHttpOutcome
                            .AuthorizationOrValidationFailure,
                        response.StatusCode == (HttpStatusCode)429
                            ? BoundedGitHubPublisherReason.RateLimited
                            : response.StatusCode ==
                                HttpStatusCode.UnprocessableEntity
                                ? BoundedGitHubPublisherReason
                                    .ValidationRejected
                                : BoundedGitHubPublisherReason
                            .AuthorizationDenied,
                        evidence);
                }
                return Fail<CapturedResponse>(mutation
                        ? BoundedGitHubHttpOutcome.OutcomeUnknown
                        : BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.InvalidResponse);
            }
            if (!HasJsonContentType(response.Content.Headers.ContentType))
                return Fail<CapturedResponse>(mutation
                        ? BoundedGitHubHttpOutcome.OutcomeUnknown
                        : BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.InvalidResponse);
            return BoundedGitHubHttpResult<CapturedResponse>.Success(new(
                read.Body, response.Headers.TryGetValues("Link", out var links)
                    ? links.ToArray() : []));
        }
        catch (OperationCanceledException)
        {
            return Fail<CapturedResponse>(mutation
                    ? BoundedGitHubHttpOutcome.OutcomeUnknown
                    : callerCancellation.IsCancellationRequested
                        ? BoundedGitHubHttpOutcome.CancelledBeforeSend
                        : BoundedGitHubHttpOutcome.KnownNotSent,
                BoundedGitHubPublisherReason.Deadline);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException)
        {
            return Fail<CapturedResponse>(mutation
                    ? BoundedGitHubHttpOutcome.OutcomeUnknown
                    : BoundedGitHubHttpOutcome.KnownNotSent,
                BoundedGitHubPublisherReason.TransportFailure);
        }
    }

    private async Task<(byte[]? Body, BoundedGitHubPublisherReason Reason)>
        ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await _responseReadGate.WaitAsync(cancellationToken);
        try
        {
            var aggregateRemaining =
                BoundedGitHubPublisherPolicy.MaximumAggregateResponseBytes -
                _aggregateBytes;
            if (content.Headers.ContentLength is long length)
            {
                if (length > BoundedGitHubPublisherPolicy.MaximumResponseBytes)
                    return (null, BoundedGitHubPublisherReason.ResponseLimit);
                if (length > aggregateRemaining)
                    return (null,
                        BoundedGitHubPublisherReason.AggregateResponseLimit);
            }
            if (aggregateRemaining <= 0)
                return (null,
                    BoundedGitHubPublisherReason.AggregateResponseLimit);

            await using var stream = await content.ReadAsStreamAsync(
                cancellationToken);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                if (!IsWithinOverallDeadline)
                    return (null, BoundedGitHubPublisherReason.Deadline);
                var responseRemaining =
                    BoundedGitHubPublisherPolicy.MaximumResponseBytes -
                    output.Length;
                aggregateRemaining =
                    BoundedGitHubPublisherPolicy.MaximumAggregateResponseBytes -
                    _aggregateBytes;
                if (responseRemaining == 0 || aggregateRemaining == 0)
                {
                    var sentinel = await stream.ReadAsync(
                        buffer.AsMemory(0, 1), cancellationToken);
                    if (sentinel == 0) break;
                    _aggregateBytes = checked(_aggregateBytes + sentinel);
                    return (null, responseRemaining == 0
                        ? BoundedGitHubPublisherReason.ResponseLimit
                        : BoundedGitHubPublisherReason.AggregateResponseLimit);
                }
                var count = (int)Math.Min(buffer.Length,
                    Math.Min(responseRemaining, aggregateRemaining));
                var read = await stream.ReadAsync(buffer.AsMemory(0, count),
                    cancellationToken);
                if (read == 0) break;
                output.Write(buffer, 0, read);
                _aggregateBytes = checked(_aggregateBytes + read);
            }
            if (!IsWithinOverallDeadline)
                return (null, BoundedGitHubPublisherReason.Deadline);
            return (output.ToArray(), BoundedGitHubPublisherReason.None);
        }
        finally
        {
            _responseReadGate.Release();
        }
    }

    private bool TryMap(BoundedGitHubIssueCommentDocument doc,
        out BoundedGitHubIssueComment? comment)
    {
        comment = null;
        if (doc.Id is not > 0 || doc.Url is null || doc.HtmlUrl is null ||
            doc.Body is null || !StringComparer.Ordinal.Equals(doc.Url,
                $"{BoundedGitHubPublisherPolicy.Origin}/repos/" +
                $"{_repositoryName}/issues/comments/{doc.Id}") ||
            !StringComparer.Ordinal.Equals(doc.HtmlUrl,
                $"https://github.com/{_repositoryName}/pull/" +
                $"{_pullRequestNumber}#issuecomment-{doc.Id}")) return false;
        comment = new(doc.Id.Value, doc.Url, doc.HtmlUrl, doc.Body);
        return true;
    }

    private bool TryMap(BoundedGitHubReviewCommentDocument doc,
        out BoundedGitHubReviewComment? comment)
    {
        comment = null;
        if (doc.Id is not > 0 || doc.Url is null ||
            doc.PullRequestUrl is null || doc.HtmlUrl is null ||
            doc.Body is null ||
            !StringComparer.Ordinal.Equals(doc.Url,
                $"{BoundedGitHubPublisherPolicy.Origin}/repos/" +
                $"{_repositoryName}/pulls/comments/{doc.Id}") ||
            !StringComparer.Ordinal.Equals(doc.PullRequestUrl,
                $"{BoundedGitHubPublisherPolicy.Origin}/repos/" +
                $"{_repositoryName}/pulls/{_pullRequestNumber}") ||
            !IsCanonicalCommentHtmlUrl(doc.HtmlUrl!, doc.Id!.Value))
        {
            return false;
        }

        var marker = InlineCommentMarker.Inspect(doc.Body);
        if (marker.Kind == InlineMarkerInspectionKind.Valid &&
            (doc.PullRequestReviewId is not > 0 || doc.Path is null ||
                doc.CommitId is null || doc.Line is < 1 ||
                doc.Side != "RIGHT" ||
                !AgenticPrReview.Runtime.Agent.Tools.RepositoryPath.IsValid(
                    doc.Path) ||
                R4Markdown.ValidateBodyText(doc.Body) !=
                    R4BodyTextValidation.Valid ||
                !IsLowerHex(doc.CommitId, 40)))
        {
            return false;
        }

        comment = new(doc.Id.Value, doc.PullRequestReviewId, doc.Url,
            doc.PullRequestUrl, doc.HtmlUrl, doc.Body, doc.Path,
            doc.Line, doc.Side, doc.CommitId!);
        return true;
    }

    private bool IsCanonicalReviewHtmlUrl(string value, long reviewId) =>
        TryPullRequestHtmlUrl(value, out var fragment) &&
        StringComparer.Ordinal.Equals(fragment,
            $"#pullrequestreview-{reviewId}");

    private bool IsCanonicalCommentHtmlUrl(string value, long commentId)
    {
        if (!TryPullRequestHtmlUrl(value, out var fragment))
        {
            return false;
        }

        if (StringComparer.Ordinal.Equals(fragment,
                $"#discussion_r{commentId}"))
        {
            return true;
        }

        const string prefix = "#discussion-diff-";
        return fragment.StartsWith(prefix, StringComparison.Ordinal) &&
            IsCanonicalUnsignedDecimal(fragment[prefix.Length..]);
    }

    private bool TryPullRequestHtmlUrl(string value, out string fragment)
    {
        fragment = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !StringComparer.OrdinalIgnoreCase.Equals(uri.Host, "github.com") ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.Query) ||
            string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        if (!StringComparer.Ordinal.Equals(uri.AbsolutePath,
                $"/{_repositoryName}/pull/{_pullRequestNumber}"))
        {
            return false;
        }

        fragment = uri.Fragment;
        return true;
    }

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private bool TryNextPage(IReadOnlyList<string> values, int current,
        string endpointPath, out int? next, out int? lastPage)
    {
        next = null;
        lastPage = null;
        var pages = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var part in values
            .SelectMany(static value => value.Split(','))
            .Select(static raw => raw.Trim()))
        {
            var close = part.IndexOf('>');
            var relIndex = part.IndexOf("; rel=\"", StringComparison.Ordinal);
            if (!part.StartsWith('<') || close < 2 || relIndex != close + 1 ||
                !part.EndsWith('"')) return false;
            var relation = part[(relIndex + 7)..^1];
            if (relation is not ("next" or "prev" or "first" or "last") ||
                pages.ContainsKey(relation) ||
                !Uri.TryCreate(part[1..close], UriKind.Absolute, out var uri) ||
                !StringComparer.Ordinal.Equals(
                    uri.GetLeftPart(UriPartial.Authority),
                    BoundedGitHubPublisherPolicy.Origin) ||
                !string.IsNullOrEmpty(uri.Fragment) ||
                !StringComparer.Ordinal.Equals(uri.AbsolutePath,
                    endpointPath) ||
                !TryPageQuery(uri.Query, out var linked)) return false;
            pages.Add(relation, linked);
        }
        if (pages.TryGetValue("first", out var first) && first != 1 ||
            pages.TryGetValue("prev", out var previous) &&
                previous != current - 1 ||
            pages.TryGetValue("next", out var following) &&
                (following != current + 1 ||
                    following > BoundedGitHubPublisherPolicy.MaximumPages) ||
            pages.TryGetValue("last", out var last) &&
                (last < current ||
                    last > BoundedGitHubPublisherPolicy.MaximumPages ||
                    following != 0 && last < following ||
                    following == 0 && last != current)) return false;
        next = following == 0 ? null : following;
        lastPage = last == 0 ? null : last;
        return true;
    }

    private static bool TryPageQuery(string query, out int page)
    {
        page = 0;
        var seenPage = false;
        var seenPerPage = false;
        foreach (var parts in query.TrimStart('?').Split('&')
            .Select(static pair => pair.Split('=', 2)))
        {
            if (parts.Length != 2) return false;
            if (parts[0] == "page")
            {
                if (seenPage || !IsCanonicalUnsignedDecimal(parts[1]) ||
                    !int.TryParse(parts[1], NumberStyles.None,
                        CultureInfo.InvariantCulture, out page) || page < 1)
                    return false;
                seenPage = true;
            }
            else if (parts[0] == "per_page")
            {
                if (seenPerPage || parts[1] != "100") return false;
                seenPerPage = true;
            }
            else return false;
        }
        return seenPage && seenPerPage;
    }

    private static bool IsCanonicalUnsignedDecimal(string value) =>
        value.Length > 0 && (value.Length == 1 || value[0] != '0') &&
        value.All(static c => c is >= '0' and <= '9');

    private static bool TryErrorEvidence(HttpStatusCode status, byte[] body,
        out BoundedGitHubValidationEvidence? evidence)
    {
        evidence = null;
        try
        {
            var error = JsonSerializer.Deserialize(body,
                BoundedGitHubPublisherJsonContext.Default
                    .BoundedGitHubErrorDocument);
            if (error?.Message is not { Length: > 0 and <= 4096 } message ||
                error.Id is <= 0 ||
                error.DocumentationUrl is { Length: > 2048 } ||
                error.Errors is { Length: > 100 }) return false;
            var items = new List<BoundedGitHubErrorItemEvidence>(
                error.Errors?.Length ?? 0);
            foreach (var item in error.Errors ?? [])
            {
                if (item is null || !IsBounded(item.Resource) ||
                    !IsBounded(item.Field) ||
                    !IsBounded(item.Code)) return false;
                items.Add(new(item.Resource, item.Field, item.Code));
            }
            evidence = new((int)status, error.Id is > 0, message,
                error.DocumentationUrl, items);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool IsBounded(string? value) => value is null or
        { Length: <= 256 };

    private static bool TryInspect(string body,
        out R4StickyInspection inspection)
    {
        try
        {
            inspection = R4StickyMarker.Inspect(body);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException)
        {
            inspection = new(R4StickyInspectionKind.InvalidR4, null, null,
                R4StickyInvalidReason.BodyDigestMismatch);
            return false;
        }
    }

    private static bool IsRecognized4xx(HttpStatusCode status) =>
        status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or
            HttpStatusCode.Forbidden or HttpStatusCode.NotFound or
            HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity ||
        status == (HttpStatusCode)429;

    private static bool HasJsonContentType(MediaTypeHeaderValue? value) =>
        value is not null && StringComparer.OrdinalIgnoreCase.Equals(
            value.MediaType, "application/json") &&
        (value.CharSet is null || StringComparer.OrdinalIgnoreCase.Equals(
            value.CharSet.Trim('"'), "utf-8"));

    private static void Validate(string token,
        ActionHostAuthorizer.AuthorizedInvocation authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        var pr = authorization.PullRequest;
        if (string.IsNullOrEmpty(token) || token.Any(c => c is < '!' or > '~') ||
            pr.Number <= 0 || pr.RepositoryId <= 0 ||
            pr.BaseRepositoryId != pr.RepositoryId ||
            pr.HeadRepositoryId != pr.RepositoryId ||
            !StringComparer.Ordinal.Equals(pr.BaseRepositoryName,
                pr.HeadRepositoryName))
            throw new BoundedGitHubPublisherCredentialException();
    }

    private static string RepositoryPath(string name)
    {
        var parts = name.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new BoundedGitHubPublisherCredentialException();
        return $"{Uri.EscapeDataString(parts[0])}/" +
            Uri.EscapeDataString(parts[1]);
    }

    private static BoundedGitHubHttpResult<T> Fail<T>(
        BoundedGitHubHttpOutcome outcome,
        BoundedGitHubPublisherReason reason,
        BoundedGitHubValidationEvidence? evidence = null) where T : class =>
        BoundedGitHubHttpResult<T>.Failed(outcome, reason, evidence);

    private sealed record CapturedResponse(byte[] Body,
        IReadOnlyList<string> Links);

    private sealed class ReadbackTransport(
        BoundedGitHubPublisherTransport inner) : IStickyGitHubReadbackTransport
    {
        public bool IsWithinOverallDeadline =>
            inner.IsWithinOverallDeadline;

        public Task<BoundedGitHubHttpResult<BoundedGitHubIssueCommentPage>>
            ListIssueCommentsAsync(int page,
                CancellationToken cancellationToken) =>
            inner.ListIssueCommentsAsync(page, cancellationToken);

        public Task<BoundedGitHubHttpResult<BoundedGitHubIssueComment>>
            GetIssueCommentAsync(long commentId,
                CancellationToken cancellationToken) =>
            inner.GetIssueCommentAsync(commentId, cancellationToken);

        public void Dispose() => inner.Dispose();
    }
}
