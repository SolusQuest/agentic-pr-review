using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;

namespace AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;

internal sealed class BoundedGitHubPublisherTransportFactory :
    IBoundedGitHubPublisherTransportFactory
{
    public IBoundedGitHubPublisherTransport Create(ActionHostGitHubToken token,
        ActionHostAuthorizer.AuthorizedInvocation authorization)
    {
        ArgumentNullException.ThrowIfNull(token);
        return BoundedGitHubPublisherTransport.Create(
            token.ExportForPrivateLaunch(), authorization);
    }
}

internal sealed class BoundedGitHubPublisherCredentialException : Exception
{
    internal BoundedGitHubPublisherCredentialException()
        : base("The GitHub publisher credential is invalid.") { }
}

internal sealed class BoundedGitHubPublisherTransport :
    IBoundedGitHubPublisherTransport
{
    private readonly string _token;
    private readonly string _repositoryName;
    private readonly string _repositoryPath;
    private readonly long _pullRequestNumber;
    private readonly HttpClient _client;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _overallTimeout;
    private readonly Stopwatch _operation = Stopwatch.StartNew();
    private int _requestCount;
    private long _aggregateBytes;
    private bool _disposed;

    private BoundedGitHubPublisherTransport(string token,
        ActionHostAuthorizer.AuthorizedInvocation authorization,
        HttpMessageHandler handler, TimeSpan requestTimeout,
        TimeSpan overallTimeout)
    {
        Validate(token, authorization);
        _token = token;
        _repositoryName = authorization.PullRequest.BaseRepositoryName;
        _repositoryPath = RepositoryPath(_repositoryName);
        _pullRequestNumber = authorization.PullRequest.Number;
        _requestTimeout = requestTimeout;
        _overallTimeout = overallTimeout;
        _client = new HttpClient(handler, true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _client.DefaultRequestHeaders.Clear();
    }

    internal static BoundedGitHubPublisherTransport Create(string token,
        ActionHostAuthorizer.AuthorizedInvocation authorization) => new(
            token, authorization,
            ActionHostGitHubAuthorizationTransport.CreateHandler(
                TimeSpan.FromSeconds(10)),
            BoundedGitHubPublisherPolicy.RequestTimeout,
            BoundedGitHubPublisherPolicy.OverallTimeout);

    internal static BoundedGitHubPublisherTransport CreateForTesting(
        string token, ActionHostAuthorizer.AuthorizedInvocation authorization,
        HttpMessageHandler handler, TimeSpan? requestTimeout = null,
        TimeSpan? overallTimeout = null) => new(token, authorization, handler,
            requestTimeout ?? BoundedGitHubPublisherPolicy.RequestTimeout,
            overallTimeout ?? BoundedGitHubPublisherPolicy.OverallTimeout);

    public async Task<BoundedGitHubPublisherResult<BoundedGitHubIssueCommentPage>>
        ListIssueCommentsAsync(int page, CancellationToken cancellationToken)
    {
        if (page is < 1 or > BoundedGitHubPublisherPolicy.MaximumPages)
            return Fail<BoundedGitHubIssueCommentPage>(
                BoundedGitHubPublisherFailure.Unavailable,
                BoundedGitHubPublisherReason.InvalidRequest);
        var captured = await SendAsync(HttpMethod.Get,
            $"/repos/{_repositoryPath}/issues/{_pullRequestNumber}/comments" +
            $"?per_page={BoundedGitHubPublisherPolicy.PerPage}&page={page}",
            null, HttpStatusCode.OK, false, cancellationToken);
        if (captured.Value is null)
            return Fail<BoundedGitHubIssueCommentPage>(captured.Failure,
                captured.Reason);
        try
        {
            var docs = JsonSerializer.Deserialize(captured.Value.Body,
                BoundedGitHubPublisherJsonContext.Default
                    .BoundedGitHubIssueCommentDocumentArray);
            if (docs is null || docs.Length > BoundedGitHubPublisherPolicy.PerPage ||
                !TryNextPage(captured.Value.Links, page, out var next))
                return Fail<BoundedGitHubIssueCommentPage>(
                    BoundedGitHubPublisherFailure.Unavailable,
                    BoundedGitHubPublisherReason.InvalidPagination);
            var comments = new List<BoundedGitHubIssueComment>(docs.Length);
            foreach (var doc in docs)
            {
                if (!TryMap(doc, out var comment))
                    return Fail<BoundedGitHubIssueCommentPage>(
                        BoundedGitHubPublisherFailure.Unavailable,
                        BoundedGitHubPublisherReason.InvalidResponse);
                comments.Add(comment!);
            }
            return BoundedGitHubPublisherResult<BoundedGitHubIssueCommentPage>
                .Success(new(comments, next));
        }
        catch (JsonException)
        {
            return Fail<BoundedGitHubIssueCommentPage>(
                BoundedGitHubPublisherFailure.Unavailable,
                BoundedGitHubPublisherReason.InvalidResponse);
        }
    }

    public Task<BoundedGitHubPublisherResult<BoundedGitHubIssueComment>>
        CreateIssueCommentAsync(ReadOnlyMemory<byte> requestBody,
            CancellationToken cancellationToken) => SendCommentAsync(
                HttpMethod.Post,
                $"/repos/{_repositoryPath}/issues/{_pullRequestNumber}/comments",
                requestBody, HttpStatusCode.Created, cancellationToken);

    public Task<BoundedGitHubPublisherResult<BoundedGitHubIssueComment>>
        UpdateIssueCommentAsync(long commentId,
            ReadOnlyMemory<byte> requestBody,
            CancellationToken cancellationToken) => SendCommentAsync(
                HttpMethod.Patch,
                $"/repos/{_repositoryPath}/issues/comments/{commentId}",
                requestBody, HttpStatusCode.OK, cancellationToken);

    public async Task<BoundedGitHubPublisherResult<BoundedGitHubIssueComment>>
        GetIssueCommentAsync(long commentId,
            CancellationToken cancellationToken)
    {
        if (commentId <= 0)
            return Fail<BoundedGitHubIssueComment>(
                BoundedGitHubPublisherFailure.Unavailable,
                BoundedGitHubPublisherReason.InvalidRequest);
        var captured = await SendAsync(HttpMethod.Get,
            $"/repos/{_repositoryPath}/issues/comments/{commentId}", null,
            HttpStatusCode.OK, false, cancellationToken);
        return MapComment(captured, false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
    }

    private async Task<BoundedGitHubPublisherResult<BoundedGitHubIssueComment>>
        SendCommentAsync(HttpMethod method, string path,
            ReadOnlyMemory<byte> body, HttpStatusCode expected,
            CancellationToken cancellationToken)
    {
        if (body.IsEmpty || body.Length >
            BoundedGitHubPublisherPolicy.MaximumStickyRequestBytes)
            return Fail<BoundedGitHubIssueComment>(
                BoundedGitHubPublisherFailure.Unavailable,
                BoundedGitHubPublisherReason.InvalidRequest);
        var captured = await SendAsync(method, path, body, expected, true,
            cancellationToken);
        return MapComment(captured, true);
    }

    private BoundedGitHubPublisherResult<BoundedGitHubIssueComment> MapComment(
        BoundedGitHubPublisherResult<CapturedResponse> captured, bool mutation)
    {
        if (captured.Value is null)
            return Fail<BoundedGitHubIssueComment>(captured.Failure,
                captured.Reason);
        try
        {
            var doc = JsonSerializer.Deserialize(captured.Value.Body,
                BoundedGitHubPublisherJsonContext.Default
                    .BoundedGitHubIssueCommentDocument);
            if (doc is null || !TryMap(doc, out var comment))
                return Fail<BoundedGitHubIssueComment>(mutation
                        ? BoundedGitHubPublisherFailure.OutcomeUnknown
                        : BoundedGitHubPublisherFailure.Unavailable,
                    BoundedGitHubPublisherReason.InvalidResponse);
            return BoundedGitHubPublisherResult<BoundedGitHubIssueComment>
                .Success(comment!);
        }
        catch (JsonException)
        {
            return Fail<BoundedGitHubIssueComment>(mutation
                    ? BoundedGitHubPublisherFailure.OutcomeUnknown
                    : BoundedGitHubPublisherFailure.Unavailable,
                BoundedGitHubPublisherReason.InvalidResponse);
        }
    }

    private async Task<BoundedGitHubPublisherResult<CapturedResponse>> SendAsync(
        HttpMethod method, string path, ReadOnlyMemory<byte>? body,
        HttpStatusCode expected, bool mutation,
        CancellationToken callerCancellation)
    {
        if (callerCancellation.IsCancellationRequested)
            return Fail<CapturedResponse>(
                BoundedGitHubPublisherFailure.CancelledBeforeSend,
                BoundedGitHubPublisherReason.Deadline);
        if (_disposed || Interlocked.Increment(ref _requestCount) >
            BoundedGitHubPublisherPolicy.MaximumRequests)
            return Fail<CapturedResponse>(
                BoundedGitHubPublisherFailure.Unavailable,
                _disposed ? BoundedGitHubPublisherReason.TransportFailure :
                    BoundedGitHubPublisherReason.RequestLimit);
        var remaining = _overallTimeout - _operation.Elapsed;
        if (remaining <= TimeSpan.Zero)
            return Fail<CapturedResponse>(
                BoundedGitHubPublisherFailure.Unavailable,
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
                        ? BoundedGitHubPublisherFailure.OutcomeUnknown
                        : BoundedGitHubPublisherFailure.Unavailable,
                    read.Reason);
            if (response.StatusCode != expected)
            {
                if (IsRecognized4xx(response.StatusCode) &&
                    HasJsonContentType(response.Content.Headers.ContentType) &&
                    IsValidError(read.Body))
                    return Fail<CapturedResponse>(
                        BoundedGitHubPublisherFailure
                            .AuthorizationOrValidationFailure,
                        response.StatusCode == (HttpStatusCode)429
                            ? BoundedGitHubPublisherReason.RateLimited
                            : BoundedGitHubPublisherReason.ValidationRejected);
                return Fail<CapturedResponse>(mutation
                        ? BoundedGitHubPublisherFailure.OutcomeUnknown
                        : BoundedGitHubPublisherFailure.Unavailable,
                    BoundedGitHubPublisherReason.InvalidResponse);
            }
            if (!HasJsonContentType(response.Content.Headers.ContentType))
                return Fail<CapturedResponse>(mutation
                        ? BoundedGitHubPublisherFailure.OutcomeUnknown
                        : BoundedGitHubPublisherFailure.Unavailable,
                    BoundedGitHubPublisherReason.InvalidResponse);
            return BoundedGitHubPublisherResult<CapturedResponse>.Success(new(
                read.Body, response.Headers.TryGetValues("Link", out var links)
                    ? links.ToArray() : []));
        }
        catch (OperationCanceledException)
        {
            return Fail<CapturedResponse>(mutation
                    ? BoundedGitHubPublisherFailure.OutcomeUnknown
                    : callerCancellation.IsCancellationRequested
                        ? BoundedGitHubPublisherFailure.CancelledBeforeSend
                        : BoundedGitHubPublisherFailure.Unavailable,
                BoundedGitHubPublisherReason.Deadline);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException)
        {
            return Fail<CapturedResponse>(mutation
                    ? BoundedGitHubPublisherFailure.OutcomeUnknown
                    : BoundedGitHubPublisherFailure.Unavailable,
                BoundedGitHubPublisherReason.TransportFailure);
        }
    }

    private async Task<(byte[]? Body, BoundedGitHubPublisherReason Reason)>
        ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is >
            BoundedGitHubPublisherPolicy.MaximumResponseBytes)
            return (null, BoundedGitHubPublisherReason.ResponseLimit);
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (output.Length <= BoundedGitHubPublisherPolicy.MaximumResponseBytes)
        {
            var remaining = BoundedGitHubPublisherPolicy.MaximumResponseBytes + 1 -
                (int)output.Length;
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                cancellationToken);
            if (read == 0) break;
            output.Write(buffer, 0, read);
        }
        if (output.Length > BoundedGitHubPublisherPolicy.MaximumResponseBytes)
            return (null, BoundedGitHubPublisherReason.ResponseLimit);
        if (Interlocked.Add(ref _aggregateBytes, output.Length) >
            BoundedGitHubPublisherPolicy.MaximumAggregateResponseBytes)
            return (null, BoundedGitHubPublisherReason.AggregateResponseLimit);
        return (output.ToArray(), BoundedGitHubPublisherReason.None);
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

    private bool TryNextPage(IReadOnlyList<string> values, int current,
        out int? next)
    {
        next = null;
        var relations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        foreach (var raw in value.Split(','))
        {
            var part = raw.Trim();
            var close = part.IndexOf('>');
            var relIndex = part.IndexOf("; rel=\"", StringComparison.Ordinal);
            if (!part.StartsWith('<') || close < 2 || relIndex != close + 1 ||
                !part.EndsWith('"')) return false;
            var relation = part[(relIndex + 7)..^1];
            if (relation is not ("next" or "prev" or "first" or "last") ||
                !relations.Add(relation) ||
                !Uri.TryCreate(part[1..close], UriKind.Absolute, out var uri) ||
                !StringComparer.Ordinal.Equals(uri.GetLeftPart(UriPartial.Authority),
                    BoundedGitHubPublisherPolicy.Origin) ||
                !StringComparer.Ordinal.Equals(uri.AbsolutePath,
                    $"/repos/{_repositoryPath}/issues/{_pullRequestNumber}/comments") ||
                !TryPageQuery(uri.Query, out var linked)) return false;
            if (relation == "next") next = linked;
        }
        return next is null || next == current + 1;
    }

    private static bool TryPageQuery(string query, out int page)
    {
        page = 0;
        var seenPage = false;
        var seenPerPage = false;
        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            var p = pair.Split('=', 2);
            if (p.Length != 2) return false;
            if (p[0] == "page")
            {
                if (seenPage || !int.TryParse(p[1], out page) || page < 1)
                    return false;
                seenPage = true;
            }
            else if (p[0] == "per_page")
            {
                if (seenPerPage || p[1] != "100") return false;
                seenPerPage = true;
            }
            else return false;
        }
        return seenPage && seenPerPage;
    }

    private static bool IsValidError(byte[] body)
    {
        try
        {
            var error = JsonSerializer.Deserialize(body,
                BoundedGitHubPublisherJsonContext.Default
                    .BoundedGitHubErrorDocument);
            return error?.Message is { Length: > 0 and <= 4096 };
        }
        catch (JsonException) { return false; }
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
        var p = name.Split('/');
        if (p.Length != 2 || p.Any(string.IsNullOrWhiteSpace))
            throw new BoundedGitHubPublisherCredentialException();
        return $"{Uri.EscapeDataString(p[0])}/{Uri.EscapeDataString(p[1])}";
    }

    private static BoundedGitHubPublisherResult<T> Fail<T>(
        BoundedGitHubPublisherFailure failure,
        BoundedGitHubPublisherReason reason) where T : class =>
        BoundedGitHubPublisherResult<T>.Failed(failure, reason);

    private sealed record CapturedResponse(byte[] Body,
        IReadOnlyList<string> Links);
}
