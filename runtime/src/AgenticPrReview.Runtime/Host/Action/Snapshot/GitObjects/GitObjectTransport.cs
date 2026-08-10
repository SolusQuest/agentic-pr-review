using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;

namespace AgenticPrReview.Runtime.ActionHost.Snapshot.GitObjects;

internal sealed class ReviewedGitObjectTransportFactory :
    IReviewedGitObjectTransportFactory
{
    private static readonly object FactoryAuthority = new();

    public IReviewedGitObjectTransport Create(
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        ActionHostGitHubToken token,
        ReviewedContentBudget budget)
    {
        return ReviewedGitObjectTransport.Mint(
            FactoryAuthority,
            invocation,
            token,
            budget);
    }

    internal static bool HasFactoryAuthority(object authority) =>
        ReferenceEquals(authority, FactoryAuthority);
}

internal sealed class ReviewedGitObjectCredentialException : Exception
{
    internal ReviewedGitObjectCredentialException()
        : base("The reviewed Git-object credential or authority is invalid.")
    {
    }
}

internal sealed class ReviewedGitObjectTransport :
    IReviewedGitObjectTransport
{
    private const string Origin = "https://api.github.com";
    private const string UserAgent = "agentic-pr-review-actionhost";
    private const string JsonAccept = "application/vnd.github+json";
    private const string RawAccept = "application/vnd.github.raw+json";
    private const string ApiVersion = "2026-03-10";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly string _token;
    private readonly string _repositoryPath;
    private readonly string _headSha;
    private readonly ReviewedContentBudget _budget;
    private readonly HttpClient _client;
    private bool _disposed;

    private ReviewedGitObjectTransport(
        string token,
        string repositoryName,
        string headSha,
        ReviewedContentBudget budget,
        HttpClient client)
    {
        _token = token;
        _repositoryPath = RepositoryPath(repositoryName);
        _headSha = headSha;
        _budget = budget;
        _client = client;
    }

    internal static ReviewedGitObjectTransport Mint(
        object authority,
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        ActionHostGitHubToken token,
        ReviewedContentBudget budget)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(budget);
        if (!ReviewedGitObjectTransportFactory.HasFactoryAuthority(authority) ||
            !TryAuthorizedSource(
                invocation,
                out var repositoryName,
                out var headSha))
        {
            throw new ReviewedGitObjectCredentialException();
        }

        var exported = token.ExportForPrivateLaunch();
        ValidateCreation(exported, repositoryName, headSha, budget);
        return new ReviewedGitObjectTransport(
            exported,
            repositoryName,
            headSha,
            budget,
            CreateClient(CreateHandler(TimeSpan.FromSeconds(10))));
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

    public async Task<ReviewedGitObjectResult<ReviewedGitCommitFact>>
        GetCommitAsync(CancellationToken cancellationToken)
    {
        var result = await SendJsonAsync(
            $"/repos/{_repositoryPath}/git/commits/{_headSha}",
            ReviewedGitObjectJsonContext.Default.ReviewedGitCommitDocument,
            cancellationToken);
        if (result.Value is null)
        {
            return ReviewedGitObjectResult<ReviewedGitCommitFact>.Failed(
                result.Failure);
        }

        return ReviewedGitObjectDocumentMapper.TryMap(
                result.Value,
                _headSha,
                out var fact)
            ? ReviewedGitObjectResult<ReviewedGitCommitFact>.Success(fact!)
            : ReviewedGitObjectResult<ReviewedGitCommitFact>.Failed(
                ReviewedGitObjectFailure.InvalidResponse);
    }

    public async Task<ReviewedGitObjectResult<ReviewedGitTreeFact>>
        GetTreeAsync(
            string treeSha,
            CancellationToken cancellationToken)
    {
        if (!ReviewedGitObjectValidation.IsSha(treeSha))
        {
            return ReviewedGitObjectResult<ReviewedGitTreeFact>.Failed(
                ReviewedGitObjectFailure.InvalidRequest);
        }

        var result = await SendJsonAsync(
            $"/repos/{_repositoryPath}/git/trees/{treeSha}",
            ReviewedGitObjectJsonContext.Default.ReviewedGitTreeDocument,
            cancellationToken);
        if (result.Value is null)
        {
            return ReviewedGitObjectResult<ReviewedGitTreeFact>.Failed(
                result.Failure);
        }

        return ReviewedGitObjectDocumentMapper.TryMap(
                result.Value,
                treeSha,
                out var fact)
            ? ReviewedGitObjectResult<ReviewedGitTreeFact>.Success(fact!)
            : ReviewedGitObjectResult<ReviewedGitTreeFact>.Failed(
                ReviewedGitObjectFailure.InvalidResponse);
    }

    public async Task<ReviewedGitObjectResult<ReviewedStagedBlob>>
        StageBlobAsync(
            string blobSha,
            long declaredSize,
            ReviewedBlobStagingLease staging,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staging);
        if (!ReviewedGitObjectValidation.IsSha(blobSha) || declaredSize < 0)
        {
            return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                ReviewedGitObjectFailure.InvalidRequest);
        }

        if (declaredSize > ReviewedContentLimits.HeadBlobBytes)
        {
            return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                ReviewedGitObjectFailure.UnsupportedSize);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed)
        {
            return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                ReviewedGitObjectFailure.TransportFailure);
        }

        if (!_budget.TryReserveRequest(cancellationToken))
        {
            return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                ReviewedGitObjectFailure.UnsupportedSize);
        }

        if (!_budget.TryBeginOperation(
                cancellationToken,
                out var operationLease))
        {
            return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                ReviewedGitObjectFailure.UnsupportedSize);
        }

        using var operation = operationLease!;
        try
        {
            using var request = CreateRequest(
                $"/repos/{_repositoryPath}/git/blobs/{blobSha}",
                RawAccept);
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                operation.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                    ClassifyStatus(response.StatusCode));
            }

            if (response.Content.Headers.ContentLength is { } contentLength)
            {
                if (contentLength > ReviewedContentLimits.HeadBlobBytes ||
                    _budget.WouldExceedResponse(0, contentLength))
                {
                    return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                        ReviewedGitObjectFailure.UnsupportedSize);
                }

                if (contentLength != declaredSize)
                {
                    return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                        ReviewedGitObjectFailure.IdentityMismatch);
                }
            }

            await using var writer = staging.TryCreateWriter(
                blobSha,
                declaredSize);
            if (writer is null)
            {
                return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                    ReviewedGitObjectFailure.StagingFailure);
            }

            await using var stream =
                await response.Content.ReadAsStreamAsync(operation.Token);
            var buffer = new byte[64 * 1024];
            long responseBytes = 0;
            while (responseBytes <= declaredSize)
            {
                var maximumRead = checked((int)Math.Min(
                    buffer.Length,
                    declaredSize + 1 - responseBytes));
                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, maximumRead),
                    operation.Token);
                if (read == 0)
                {
                    break;
                }

                if (!_budget.TryConsumeResponseBytes(
                        ref responseBytes,
                        read,
                        operation.Token))
                {
                    return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                        ReviewedGitObjectFailure.UnsupportedSize);
                }

                if (!await writer.WriteAsync(
                        buffer.AsMemory(0, read),
                        operation.Token))
                {
                    return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                        ReviewedGitObjectFailure.IdentityMismatch);
                }
            }

            var staged = await writer.CompleteAsync(operation.Token);
            return staged is null
                ? ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                    ReviewedGitObjectFailure.IdentityMismatch)
                : ReviewedGitObjectResult<ReviewedStagedBlob>.Success(staged);
        }
        catch (OperationCanceledException) when (operation.DeadlineExpired)
        {
            return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                ReviewedGitObjectFailure.UnsupportedSize);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                ReviewedGitObjectFailure.TransportFailure);
        }
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

    internal static bool TryAuthorizedSource(
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        out string repositoryName,
        out string headSha)
    {
        repositoryName = string.Empty;
        headSha = string.Empty;
        var pullRequest = invocation.PullRequest;
        if (pullRequest.RepositoryId <= 0 || pullRequest.Number <= 0 ||
            pullRequest.BaseRepositoryId != pullRequest.RepositoryId ||
            pullRequest.HeadRepositoryId != pullRequest.RepositoryId ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                pullRequest.BaseRepositoryName,
                pullRequest.HeadRepositoryName) ||
            !ReviewedGitObjectValidation.IsRepositoryName(
                pullRequest.HeadRepositoryName) ||
            !ReviewedGitObjectValidation.IsSha(pullRequest.HeadSha))
        {
            return false;
        }

        repositoryName = pullRequest.HeadRepositoryName;
        headSha = pullRequest.HeadSha;
        return true;
    }

    private async Task<ReviewedGitObjectResult<T>> SendJsonAsync<T>(
        string path,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed)
        {
            return ReviewedGitObjectResult<T>.Failed(
                ReviewedGitObjectFailure.TransportFailure);
        }

        if (!_budget.TryReserveRequest(cancellationToken))
        {
            return ReviewedGitObjectResult<T>.Failed(
                ReviewedGitObjectFailure.UnsupportedSize);
        }

        if (!_budget.TryBeginOperation(
                cancellationToken,
                out var operationLease))
        {
            return ReviewedGitObjectResult<T>.Failed(
                ReviewedGitObjectFailure.UnsupportedSize);
        }

        using var operation = operationLease!;
        try
        {
            using var request = CreateRequest(path, JsonAccept);
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                operation.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return ReviewedGitObjectResult<T>.Failed(
                    ClassifyStatus(response.StatusCode));
            }

            if (!HasJsonContentType(response.Content.Headers.ContentType))
            {
                return ReviewedGitObjectResult<T>.Failed(
                    ReviewedGitObjectFailure.InvalidResponse);
            }

            if (response.Content.Headers.ContentLength is { } contentLength &&
                _budget.WouldExceedResponse(0, contentLength))
            {
                return ReviewedGitObjectResult<T>.Failed(
                    ReviewedGitObjectFailure.UnsupportedSize);
            }

            var body = await ReadJsonBodyAsync(
                response.Content,
                operation.Token);
            if (body.Bytes is null)
            {
                return ReviewedGitObjectResult<T>.Failed(body.Failure);
            }

            var value = JsonSerializer.Deserialize(body.Bytes, typeInfo);
            return value is null
                ? ReviewedGitObjectResult<T>.Failed(
                    ReviewedGitObjectFailure.InvalidResponse)
                : ReviewedGitObjectResult<T>.Success(value);
        }
        catch (OperationCanceledException) when (operation.DeadlineExpired)
        {
            return ReviewedGitObjectResult<T>.Failed(
                ReviewedGitObjectFailure.UnsupportedSize);
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
            return ReviewedGitObjectResult<T>.Failed(
                ReviewedGitObjectFailure.InvalidResponse);
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return ReviewedGitObjectResult<T>.Failed(
                ReviewedGitObjectFailure.TransportFailure);
        }
    }

    private async Task<(byte[]? Bytes, ReviewedGitObjectFailure Failure)>
        ReadJsonBodyAsync(
            HttpContent content,
            CancellationToken cancellationToken)
    {
        await using var stream =
            await content.ReadAsStreamAsync(cancellationToken);
        using var captured = new MemoryStream(16 * 1024);
        var buffer = new byte[16 * 1024];
        long responseBytes = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return (captured.ToArray(), ReviewedGitObjectFailure.None);
            }

            if (!_budget.TryConsumeResponseBytes(
                    ref responseBytes,
                    read,
                    cancellationToken))
            {
                return (null, ReviewedGitObjectFailure.UnsupportedSize);
            }

            await captured.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }
    }

    private HttpRequestMessage CreateRequest(string path, string accept)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(Origin + path, UriKind.Absolute));
        if (!TryCreateAuthorizationHeader(_token, out var authorization) ||
            !request.Headers.TryAddWithoutValidation(
                "User-Agent",
                UserAgent) ||
            !request.Headers.TryAddWithoutValidation("Accept", accept) ||
            !request.Headers.TryAddWithoutValidation(
                "X-GitHub-Api-Version",
                ApiVersion))
        {
            request.Dispose();
            throw new ReviewedGitObjectCredentialException();
        }

        request.Headers.Authorization = authorization;
        return request;
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

    private static void ValidateCreation(
        string token,
        string repositoryName,
        string headSha,
        ReviewedContentBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (!TryCreateAuthorizationHeader(token, out _) ||
            !ReviewedGitObjectValidation.IsRepositoryName(repositoryName) ||
            !ReviewedGitObjectValidation.IsSha(headSha))
        {
            throw new ReviewedGitObjectCredentialException();
        }
    }

    private static bool TryCreateAuthorizationHeader(
        string? token,
        out AuthenticationHeaderValue? authorization)
    {
        authorization = null;
        if (string.IsNullOrEmpty(token) || token.Any(static character =>
                character is < '\u0021' or > '\u007e'))
        {
            return false;
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

    private static bool HasJsonContentType(MediaTypeHeaderValue? contentType) =>
        contentType is not null &&
        StringComparer.OrdinalIgnoreCase.Equals(
            contentType.MediaType,
            "application/json") &&
        (contentType.CharSet is null ||
            StringComparer.OrdinalIgnoreCase.Equals(
                contentType.CharSet.Trim('"'),
                "utf-8"));

    private static ReviewedGitObjectFailure ClassifyStatus(
        HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => ReviewedGitObjectFailure.Unauthorized,
        HttpStatusCode.Forbidden => ReviewedGitObjectFailure.Forbidden,
        HttpStatusCode.NotFound => ReviewedGitObjectFailure.NotFound,
        (HttpStatusCode)429 => ReviewedGitObjectFailure.RateLimited,
        >= HttpStatusCode.InternalServerError =>
            ReviewedGitObjectFailure.UpstreamFailure,
        _ => ReviewedGitObjectFailure.UpstreamFailure,
    };

    private static string RepositoryPath(string repositoryName)
    {
        var parts = repositoryName.Split('/');
        return Uri.EscapeDataString(parts[0]) + "/" +
            Uri.EscapeDataString(parts[1]);
    }

    private static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;
}
