using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AgenticPrReview.Runtime.ActionHost.Contracts;

namespace AgenticPrReview.Runtime.ActionHost.GitHub;

internal sealed class ActionHostGitHubAuthorizationTransportFactory :
    IActionHostGitHubAuthorizationTransportFactory
{
    public IActionHostGitHubAuthorizationTransport Create(
        ActionHostGitHubToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return ActionHostGitHubAuthorizationTransport.Create(
            token.ExportForPrivateLaunch());
    }
}

internal sealed class ActionHostGitHubCredentialException : Exception
{
    internal ActionHostGitHubCredentialException()
        : base(
            "The GitHub credential is invalid for an HTTP Authorization header.")
    {
    }
}

internal sealed class ActionHostGitHubAuthorizationTransport :
    IActionHostGitHubAuthorizationTransport
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string _token;
    private readonly HttpClient _client;
    private int _requestCount;
    private bool _disposed;

    private ActionHostGitHubAuthorizationTransport(
        string token,
        HttpClient client)
    {
        _token = token;
        _client = client;
    }

    internal static ActionHostGitHubAuthorizationTransport Create(string token)
    {
        if (!TryCreateAuthorizationHeader(token, out _))
        {
            throw new ActionHostGitHubCredentialException();
        }

        return new(
            token,
            CreateClient(CreateHandler(
                ActionHostGitHubAuthorizationPolicy.ConnectTimeout)));
    }

    internal static ActionHostGitHubAuthorizationTransport CreateForTesting(
        string token,
        HttpMessageHandler handler)
    {
        if (!TryCreateAuthorizationHeader(token, out _))
        {
            throw new ActionHostGitHubCredentialException();
        }

        ArgumentNullException.ThrowIfNull(handler);
        return new(token, CreateClient(handler));
    }

    internal static SocketsHttpHandler CreateHandler(TimeSpan connectTimeout)
    {
        if (connectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeout));
        }

        return new SocketsHttpHandler
        {
            ActivityHeadersPropagator =
                DistributedContextPropagator.CreateNoOutputPropagator(),
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = connectTimeout,
            Credentials = null,
            MaxResponseDrainSize = 0,
            PreAuthenticate = false,
            RequestHeaderEncodingSelector = static (headerName, _) =>
                StringComparer.OrdinalIgnoreCase.Equals(
                    headerName,
                    "Authorization")
                    ? StrictUtf8
                    : null,
            ResponseDrainTimeout = TimeSpan.Zero,
            UseCookies = false,
            UseProxy = false,
        };
    }

    public async Task<ActionHostGitHubResult<ActionHostGitHubRepositoryFact>>
        GetRepositoryAsync(
            string repositoryName,
            CancellationToken cancellationToken)
    {
        if (!TryRepositoryPath(repositoryName, out var repositoryPath))
        {
            return ActionHostGitHubResult<ActionHostGitHubRepositoryFact>
                .Failed(ActionHostGitHubFailure.InvalidRequest);
        }

        var result = await SendDocumentAsync(
            $"/repos/{repositoryPath}",
            ActionHostGitHubJsonContext.Default
                .ActionHostGitHubRepositoryDocument,
            cancellationToken);
        return Map<
            ActionHostGitHubRepositoryDocument,
            ActionHostGitHubRepositoryFact>(
            result,
            ActionHostGitHubDocumentMapper.TryMap);
    }

    public async Task<
        ActionHostGitHubResult<ActionHostGitHubWorkflowRunFact>>
        GetWorkflowRunAttemptAsync(
            string repositoryName,
            long runId,
            int attempt,
            CancellationToken cancellationToken)
    {
        if (!TryRepositoryPath(repositoryName, out var repositoryPath) ||
            runId <= 0 ||
            attempt <= 0)
        {
            return ActionHostGitHubResult<ActionHostGitHubWorkflowRunFact>
                .Failed(ActionHostGitHubFailure.InvalidRequest);
        }

        var result = await SendDocumentAsync(
            $"/repos/{repositoryPath}/actions/runs/{runId}" +
                $"/attempts/{attempt}?exclude_pull_requests=false",
            ActionHostGitHubJsonContext.Default
                .ActionHostGitHubWorkflowRunDocument,
            cancellationToken);
        return Map<
            ActionHostGitHubWorkflowRunDocument,
            ActionHostGitHubWorkflowRunFact>(
            result,
            ActionHostGitHubDocumentMapper.TryMap);
    }

    public async Task<
        ActionHostGitHubResult<ActionHostGitHubWorkflowSourceFact>>
        GetWorkflowSourceAsync(
            string repositoryName,
            string workflowPath,
            string workflowCommitSha,
            CancellationToken cancellationToken)
    {
        if (!TryRepositoryPath(repositoryName, out var repositoryPath) ||
            !TryWorkflowPath(workflowPath, out var encodedWorkflowPath) ||
            !ActionHostGitHubDocumentMapper.IsCommitSha(workflowCommitSha))
        {
            return ActionHostGitHubResult<ActionHostGitHubWorkflowSourceFact>
                .Failed(ActionHostGitHubFailure.InvalidRequest);
        }

        var result = await SendDocumentAsync(
            $"/repos/{repositoryPath}/contents/{encodedWorkflowPath}" +
                $"?ref={workflowCommitSha}",
            ActionHostGitHubJsonContext.Default
                .ActionHostGitHubContentDocument,
            cancellationToken);
        return MapContent(result, workflowPath);
    }

    public async Task<
        ActionHostGitHubResult<ActionHostGitHubPullRequestPageFact>>
        GetCommitPullRequestsAsync(
            string repositoryName,
            string commitSha,
            int page,
            int perPage,
            CancellationToken cancellationToken)
    {
        if (!TryRepositoryPath(repositoryName, out var repositoryPath) ||
            !ActionHostGitHubDocumentMapper.IsCommitSha(commitSha) ||
            page is < 1 or >
                ActionHostGitHubAuthorizationPolicy
                    .MaximumAssociatedPullRequestPages ||
            perPage != ActionHostGitHubAuthorizationPolicy
                .AssociatedPullRequestsPerPage)
        {
            return ActionHostGitHubResult<
                ActionHostGitHubPullRequestPageFact>.Failed(
                    ActionHostGitHubFailure.InvalidRequest);
        }

        var result = await SendDocumentAsync(
            $"/repos/{repositoryPath}/commits/{commitSha}/pulls" +
                $"?per_page={perPage}&page={page}",
            ActionHostGitHubJsonContext.Default
                .ActionHostGitHubPullRequestDocumentArray,
            cancellationToken);
        if (result.Value is null)
        {
            return ActionHostGitHubResult<
                ActionHostGitHubPullRequestPageFact>.Failed(result.Failure);
        }

        if (result.Value.Length > perPage)
        {
            return ActionHostGitHubResult<
                ActionHostGitHubPullRequestPageFact>.Failed(
                    ActionHostGitHubFailure.InvalidResponse);
        }

        var pullRequests = new List<ActionHostGitHubPullRequestFact>(
            result.Value.Length);
        foreach (var document in result.Value)
        {
            if (!ActionHostGitHubDocumentMapper.TryMap(
                    document,
                    out var fact))
            {
                return ActionHostGitHubResult<
                    ActionHostGitHubPullRequestPageFact>.Failed(
                        ActionHostGitHubFailure.InvalidResponse);
            }

            pullRequests.Add(fact!);
        }

        return ActionHostGitHubResult<
            ActionHostGitHubPullRequestPageFact>.Success(new(
                pullRequests,
                pullRequests.Count < perPage));
    }

    public async Task<
        ActionHostGitHubResult<ActionHostGitHubPermissionFact>>
        GetCollaboratorPermissionAsync(
            string repositoryName,
            string login,
            CancellationToken cancellationToken)
    {
        if (!TryRepositoryPath(repositoryName, out var repositoryPath) ||
            !TrySegment(login, out var encodedLogin))
        {
            return ActionHostGitHubResult<ActionHostGitHubPermissionFact>
                .Failed(ActionHostGitHubFailure.InvalidRequest);
        }

        var result = await SendDocumentAsync(
            $"/repos/{repositoryPath}/collaborators/{encodedLogin}/permission",
            ActionHostGitHubJsonContext.Default
                .ActionHostGitHubPermissionDocument,
            cancellationToken);
        return Map<
            ActionHostGitHubPermissionDocument,
            ActionHostGitHubPermissionFact>(
            result,
            ActionHostGitHubDocumentMapper.TryMap);
    }

    public async Task<
        ActionHostGitHubResult<ActionHostGitHubPullRequestFact>>
        GetPullRequestAsync(
            string repositoryName,
            long pullRequestNumber,
            CancellationToken cancellationToken)
    {
        if (!TryRepositoryPath(repositoryName, out var repositoryPath) ||
            pullRequestNumber <= 0)
        {
            return ActionHostGitHubResult<ActionHostGitHubPullRequestFact>
                .Failed(ActionHostGitHubFailure.InvalidRequest);
        }

        var result = await SendDocumentAsync(
            $"/repos/{repositoryPath}/pulls/{pullRequestNumber}",
            ActionHostGitHubJsonContext.Default
                .ActionHostGitHubPullRequestDocument,
            cancellationToken);
        return Map<
            ActionHostGitHubPullRequestDocument,
            ActionHostGitHubPullRequestFact>(
            result,
            ActionHostGitHubDocumentMapper.TryMap);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.Dispose();
    }

    private async Task<ActionHostGitHubResult<T>> SendDocumentAsync<T>(
        string pathAndQuery,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed)
        {
            return ActionHostGitHubResult<T>.Failed(
                ActionHostGitHubFailure.TransportFailure);
        }

        if (Interlocked.Increment(ref _requestCount) >
            ActionHostGitHubAuthorizationPolicy.MaximumRequests)
        {
            return ActionHostGitHubResult<T>.Failed(
                ActionHostGitHubFailure.RequestLimitExceeded);
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(
                    ActionHostGitHubAuthorizationPolicy.Origin + pathAndQuery,
                    UriKind.Absolute));
            if (!TryCreateAuthorizationHeader(
                    _token,
                    out var authorization) ||
                !request.Headers.TryAddWithoutValidation(
                    "User-Agent",
                    ActionHostGitHubAuthorizationPolicy.UserAgent) ||
                !request.Headers.TryAddWithoutValidation(
                    "Accept",
                    ActionHostGitHubAuthorizationPolicy.Accept) ||
                !request.Headers.TryAddWithoutValidation(
                    "X-GitHub-Api-Version",
                    ActionHostGitHubAuthorizationPolicy.ApiVersion))
            {
                return ActionHostGitHubResult<T>.Failed(
                    ActionHostGitHubFailure.TransportFailure);
            }

            request.Headers.Authorization = authorization;

            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return ActionHostGitHubResult<T>.Failed(
                    ClassifyStatus(response.StatusCode));
            }

            if (!HasJsonContentType(response.Content.Headers.ContentType))
            {
                return ActionHostGitHubResult<T>.Failed(
                    ActionHostGitHubFailure.InvalidResponse);
            }

            var body = await ReadBoundedAsync(
                response.Content,
                ActionHostGitHubAuthorizationPolicy.MaximumResponseBytes,
                cancellationToken);
            if (body is null)
            {
                return ActionHostGitHubResult<T>.Failed(
                    ActionHostGitHubFailure.ResponseTooLarge);
            }

            var value = JsonSerializer.Deserialize(body, typeInfo);
            return value is null
                ? ActionHostGitHubResult<T>.Failed(
                    ActionHostGitHubFailure.InvalidResponse)
                : ActionHostGitHubResult<T>.Success(value);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or
            NotSupportedException or
            DecoderFallbackException)
        {
            return ActionHostGitHubResult<T>.Failed(
                ActionHostGitHubFailure.InvalidResponse);
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return ActionHostGitHubResult<T>.Failed(
                ActionHostGitHubFailure.TransportFailure);
        }
    }

    private static ActionHostGitHubResult<TFact> Map<TDocument, TFact>(
        ActionHostGitHubResult<TDocument> result,
        TryMap<TDocument, TFact> mapper)
        where TDocument : class
        where TFact : class
    {
        if (result.Value is null)
        {
            return ActionHostGitHubResult<TFact>.Failed(result.Failure);
        }

        return mapper(result.Value, out var fact) && fact is not null
            ? ActionHostGitHubResult<TFact>.Success(fact)
            : ActionHostGitHubResult<TFact>.Failed(
                ActionHostGitHubFailure.InvalidResponse);
    }

    private static ActionHostGitHubResult<ActionHostGitHubWorkflowSourceFact>
        MapContent(
            ActionHostGitHubResult<ActionHostGitHubContentDocument> result,
            string expectedPath)
    {
        if (result.Value is null)
        {
            return ActionHostGitHubResult<ActionHostGitHubWorkflowSourceFact>
                .Failed(result.Failure);
        }

        return ActionHostGitHubDocumentMapper.TryMapContent(
                result.Value,
                expectedPath,
                out var fact) && fact is not null
            ? ActionHostGitHubResult<ActionHostGitHubWorkflowSourceFact>
                .Success(fact)
            : ActionHostGitHubResult<ActionHostGitHubWorkflowSourceFact>
                .Failed(ActionHostGitHubFailure.InvalidResponse);
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.Clear();
        return client;
    }

    private static bool TryCreateAuthorizationHeader(
        string? token,
        out AuthenticationHeaderValue? authorization)
    {
        authorization = null;
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        foreach (var character in token)
        {
            if (character is < '\u0021' or > '\u007e')
            {
                return false;
            }
        }

        try
        {
            authorization = new AuthenticationHeaderValue("Bearer", token);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 &&
            content.Headers.ContentLength > maximumBytes)
        {
            return null;
        }

        await using var stream =
            await content.ReadAsStreamAsync(cancellationToken);
        using var captured = new MemoryStream(
            Math.Min(16 * 1024, maximumBytes));
        var buffer = new byte[16 * 1024];
        while (captured.Length <= maximumBytes)
        {
            var remaining = checked(
                maximumBytes + 1 - (int)captured.Length);
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                cancellationToken);
            if (read == 0)
            {
                return captured.ToArray();
            }

            await captured.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }

        return null;
    }

    private static bool HasJsonContentType(MediaTypeHeaderValue? contentType)
    {
        if (contentType is null ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                contentType.MediaType,
                "application/json"))
        {
            return false;
        }

        return contentType.CharSet is null ||
            StringComparer.OrdinalIgnoreCase.Equals(
                contentType.CharSet.Trim('"'),
                "utf-8");
    }

    private static ActionHostGitHubFailure ClassifyStatus(
        HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => ActionHostGitHubFailure.Unauthorized,
        HttpStatusCode.Forbidden => ActionHostGitHubFailure.Forbidden,
        HttpStatusCode.NotFound => ActionHostGitHubFailure.NotFound,
        (HttpStatusCode)429 => ActionHostGitHubFailure.RateLimited,
        >= HttpStatusCode.InternalServerError =>
            ActionHostGitHubFailure.UpstreamFailure,
        _ => ActionHostGitHubFailure.UpstreamFailure,
    };

    private static bool TryRepositoryPath(
        string repositoryName,
        out string path)
    {
        path = string.Empty;
        if (!ActionHostGitHubDocumentMapper.IsRepositoryName(repositoryName))
        {
            return false;
        }

        var parts = repositoryName.Split('/');
        path = $"{Uri.EscapeDataString(parts[0])}/" +
            Uri.EscapeDataString(parts[1]);
        return true;
    }

    private static bool TryWorkflowPath(string value, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 1024 ||
            value.StartsWith('/') ||
            value.EndsWith('/') ||
            value.Contains('\\') ||
            value.Split('/').Any(static segment =>
                segment.Length == 0 || segment is "." or ".."))
        {
            return false;
        }

        path = string.Join(
            '/',
            value.Split('/').Select(Uri.EscapeDataString));
        return true;
    }

    private static bool TrySegment(string value, out string segment)
    {
        segment = string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 255 ||
            value.Any(static character =>
                char.IsControl(character) || character is '/' or '\\'))
        {
            return false;
        }

        segment = Uri.EscapeDataString(value);
        return true;
    }

    private static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;

    private delegate bool TryMap<TDocument, TFact>(
        TDocument document,
        out TFact? fact)
        where TDocument : class
        where TFact : class;
}
