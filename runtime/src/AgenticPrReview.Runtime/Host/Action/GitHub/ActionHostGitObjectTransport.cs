using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AgenticPrReview.Runtime.ActionHost.GitHub;

using AgenticPrReview.Runtime.ActionHost.Snapshot;

internal sealed class ActionHostGitObjectTransport :
    IActionHostGitObjectTransport
{
    private const int MaximumObjectResponseBytes =
        checked((int)ReviewedContentLimits.GitObjectResponseBytes);
    private const int MaximumArchiveDecodedBytes =
        checked((int)ReviewedContentLimits.HeadArchiveDecodedBytes);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _token;
    private readonly HttpClient _client;
    private bool _disposed;

    private ActionHostGitObjectTransport(string token, HttpClient client)
    {
        _token = token;
        _client = client;
    }

    internal static ActionHostGitObjectTransport Create(string token)
    {
        if (!TryCreateAuthorizationHeader(token, out _))
        {
            throw new ActionHostGitHubCredentialException();
        }

        return new(
            token,
            CreateClient(ActionHostGitHubAuthorizationTransport.CreateHandler(
                ActionHostGitHubAuthorizationPolicy.ConnectTimeout)));
    }

    internal static ActionHostGitObjectTransport CreateForTesting(
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

    public async Task<ActionHostGitObjectResult<ActionHostGitCommitObject>>
        GetCommitObjectAsync(
            string repositoryName,
            string commitSha,
            CancellationToken cancellationToken)
    {
        if (!TryRepositoryPath(repositoryName, out var repositoryPath) ||
            !ActionHostGitObjectMapper.IsSha(commitSha))
        {
            return ActionHostGitObjectResult<ActionHostGitCommitObject>
                .Failed(ActionHostGitObjectFailure.InvalidRequest);
        }

        var document = await SendDocumentAsync(
            $"/repos/{repositoryPath}/git/commits/{commitSha}",
            ActionHostGitObjectJsonContext.Default
                .ActionHostGitCommitDocument,
            MaximumObjectResponseBytes,
            cancellationToken);
        if (document.Value is null)
        {
            return ActionHostGitObjectResult<ActionHostGitCommitObject>
                .Failed(document.Failure, document.CapturedResponseBytes);
        }

        return ActionHostGitObjectMapper.TryMap(
                document.Value,
                commitSha,
                out var value) && value is not null
            ? ActionHostGitObjectResult<ActionHostGitCommitObject>.Success(
                value,
                document.CapturedResponseBytes)
            : ActionHostGitObjectResult<ActionHostGitCommitObject>.Failed(
                ActionHostGitObjectFailure.InvalidResponse,
                document.CapturedResponseBytes);
    }

    public async Task<ActionHostGitObjectResult<ActionHostGitTreeObject>>
        GetTreeObjectAsync(
            string repositoryName,
            string treeSha,
            CancellationToken cancellationToken)
    {
        if (!TryRepositoryPath(repositoryName, out var repositoryPath) ||
            !ActionHostGitObjectMapper.IsSha(treeSha))
        {
            return ActionHostGitObjectResult<ActionHostGitTreeObject>
                .Failed(ActionHostGitObjectFailure.InvalidRequest);
        }

        var document = await SendDocumentAsync(
            $"/repos/{repositoryPath}/git/trees/{treeSha}",
            ActionHostGitObjectJsonContext.Default.ActionHostGitTreeDocument,
            MaximumObjectResponseBytes,
            cancellationToken);
        if (document.Value is null)
        {
            return ActionHostGitObjectResult<ActionHostGitTreeObject>.Failed(
                document.Failure,
                document.CapturedResponseBytes);
        }

        return ActionHostGitObjectMapper.TryMap(
                document.Value,
                treeSha,
                out var value) && value is not null
            ? ActionHostGitObjectResult<ActionHostGitTreeObject>.Success(
                value,
                document.CapturedResponseBytes)
            : ActionHostGitObjectResult<ActionHostGitTreeObject>.Failed(
                ActionHostGitObjectFailure.InvalidResponse,
                document.CapturedResponseBytes);
    }

    public async Task<ActionHostGitObjectResult<ActionHostGitBlobObject>>
        GetBlobObjectAsync(
            string repositoryName,
            string blobSha,
            ActionHostGitBlobReadBudget budget,
            CancellationToken cancellationToken)
    {
        if (!TryRepositoryPath(repositoryName, out var repositoryPath) ||
            !ActionHostGitObjectMapper.IsSha(blobSha) ||
            !IsKnownBudget(budget))
        {
            return ActionHostGitObjectResult<ActionHostGitBlobObject>.Failed(
                ActionHostGitObjectFailure.InvalidRequest);
        }

        var document = await SendDocumentAsync(
            $"/repos/{repositoryPath}/git/blobs/{blobSha}",
            ActionHostGitObjectJsonContext.Default.ActionHostGitBlobDocument,
            budget.MaximumResponseBytes,
            cancellationToken);
        if (document.Value is null)
        {
            return ActionHostGitObjectResult<ActionHostGitBlobObject>.Failed(
                document.Failure,
                document.CapturedResponseBytes);
        }

        return ActionHostGitObjectMapper.TryMap(
                document.Value,
                blobSha,
                budget,
                out var value) && value is not null
            ? ActionHostGitObjectResult<ActionHostGitBlobObject>.Success(
                value,
                document.CapturedResponseBytes)
            : ActionHostGitObjectResult<ActionHostGitBlobObject>.Failed(
                ActionHostGitObjectFailure.InvalidResponse,
                document.CapturedResponseBytes);
    }

    public async Task<ActionHostGitObjectResult<ActionHostGitArchiveReader>>
        GetHeadArchiveAsync(
            string repositoryName,
            string headSha,
            CancellationToken cancellationToken)
    {
        if (!TryRepositoryPath(repositoryName, out var repositoryPath) ||
            !ActionHostGitObjectMapper.IsSha(headSha) || _disposed)
        {
            return ActionHostGitObjectResult<ActionHostGitArchiveReader>.Failed(
                ActionHostGitObjectFailure.InvalidRequest);
        }

        try
        {
            using var source = CreateAuthorizedRequest(new Uri(
                ActionHostGitHubAuthorizationPolicy.Origin +
                $"/repos/{repositoryPath}/tarball/{headSha}", UriKind.Absolute));
            if (source is null)
            {
                return ActionHostGitObjectResult<ActionHostGitArchiveReader>.Failed(
                    ActionHostGitObjectFailure.TransportFailure);
            }

            using var redirect = await _client.SendAsync(source,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var redirectRateLimit =
                ActionHostGitHubRateLimitClassifier.Classify(redirect);
            if (redirectRateLimit != ActionHostGitHubRateLimitClassification.None)
            {
                return ActionHostGitObjectResult<ActionHostGitArchiveReader>.Failed(
                    ClassifyRateLimit(redirectRateLimit));
            }
            if (redirect.StatusCode != HttpStatusCode.Found ||
                !TryCodeloadRedirect(redirect.Headers.Location, repositoryPath,
                    headSha, out var target))
            {
                return ActionHostGitObjectResult<ActionHostGitArchiveReader>.Failed(
                    redirect.StatusCode == HttpStatusCode.Found
                        ? ActionHostGitObjectFailure.InvalidResponse
                        : ClassifyStatus(redirect, redirectRateLimit));
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            if (!request.Headers.TryAddWithoutValidation("User-Agent",
                    ActionHostGitHubAuthorizationPolicy.UserAgent) ||
                !request.Headers.TryAddWithoutValidation("Accept", "application/x-gzip"))
            {
                return ActionHostGitObjectResult<ActionHostGitArchiveReader>.Failed(
                    ActionHostGitObjectFailure.TransportFailure);
            }

            var response = await _client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var responseRateLimit =
                ActionHostGitHubRateLimitClassifier.Classify(response);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var failure = ClassifyStatus(response, responseRateLimit);
                response.Dispose();
                return ActionHostGitObjectResult<ActionHostGitArchiveReader>.Failed(
                    failure);
            }
            if (responseRateLimit !=
                ActionHostGitHubRateLimitClassification.None)
            {
                var failure = ClassifyRateLimit(responseRateLimit);
                response.Dispose();
                return ActionHostGitObjectResult<ActionHostGitArchiveReader>.Failed(
                    failure);
            }

            if (response.Content.Headers.ContentLength is > MaximumObjectResponseBytes ||
                !HasArchiveContentType(response.Content.Headers.ContentType))
            {
                response.Dispose();
                return ActionHostGitObjectResult<ActionHostGitArchiveReader>.Failed(
                    response.Content.Headers.ContentLength is > MaximumObjectResponseBytes
                        ? ActionHostGitObjectFailure.ResponseTooLarge
                        : ActionHostGitObjectFailure.InvalidResponse);
            }

            Stream stream;
            try
            {
                stream = await response.Content.ReadAsStreamAsync(
                    cancellationToken);
            }
            catch
            {
                response.Dispose();
                throw;
            }

            try
            {
                return ActionHostGitObjectResult<ActionHostGitArchiveReader>
                    .Success(new GitHubCodeloadArchiveReader(response, stream,
                        MaximumObjectResponseBytes,
                        MaximumArchiveDecodedBytes), 0);
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return ActionHostGitObjectResult<ActionHostGitArchiveReader>.Failed(
                ActionHostGitObjectFailure.TransportFailure);
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

    public override string ToString() => "action_host_git_object_transport";

    private async Task<DocumentResult<T>> SendDocumentAsync<T>(
        string path,
        JsonTypeInfo<T> typeInfo,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed)
        {
            return DocumentResult<T>.Failed(
                ActionHostGitObjectFailure.TransportFailure);
        }

        var capturedResponseBytes = 0;
        try
        {
            using var request = CreateAuthorizedRequest(new Uri(
                ActionHostGitHubAuthorizationPolicy.Origin + path,
                UriKind.Absolute));
            if (request is null)
            {
                return DocumentResult<T>.Failed(
                    ActionHostGitObjectFailure.TransportFailure);
            }

            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var rateLimit = ActionHostGitHubRateLimitClassifier.Classify(response);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return DocumentResult<T>.Failed(
                    ClassifyStatus(response, rateLimit));
            }
            if (rateLimit != ActionHostGitHubRateLimitClassification.None)
            {
                return DocumentResult<T>.Failed(ClassifyRateLimit(rateLimit));
            }

            if (!HasJsonContentType(response.Content.Headers.ContentType))
            {
                return DocumentResult<T>.Failed(
                    ActionHostGitObjectFailure.InvalidResponse);
            }

            var read = await ReadBoundedAsync(
                response.Content,
                maximumResponseBytes,
                cancellationToken);
            capturedResponseBytes = read.CapturedResponseBytes;
            if (read.Body is null)
            {
                return DocumentResult<T>.Failed(
                    ActionHostGitObjectFailure.ResponseTooLarge,
                    capturedResponseBytes);
            }

            var value = JsonSerializer.Deserialize(read.Body, typeInfo);
            return value is null
                ? DocumentResult<T>.Failed(
                    ActionHostGitObjectFailure.InvalidResponse,
                    capturedResponseBytes)
                : DocumentResult<T>.Success(value, capturedResponseBytes);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or
            NotSupportedException or DecoderFallbackException)
        {
            return DocumentResult<T>.Failed(
                ActionHostGitObjectFailure.InvalidResponse,
                capturedResponseBytes);
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return DocumentResult<T>.Failed(
                ActionHostGitObjectFailure.TransportFailure);
        }
    }

    private static bool IsKnownBudget(ActionHostGitBlobReadBudget? budget) =>
        ReferenceEquals(budget, ActionHostGitBlobReadBudget.TrustedConfig) ||
        ReferenceEquals(
            budget,
            ActionHostGitBlobReadBudget.TrustedInstructions) ||
        ReferenceEquals(
            budget,
            ActionHostGitBlobReadBudget.MaximumSupported);

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
        path = Uri.EscapeDataString(parts[0]) + "/" +
            Uri.EscapeDataString(parts[1]);
        return true;
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

    private HttpRequestMessage? CreateAuthorizedRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!TryCreateAuthorizationHeader(_token, out var authorization) ||
            !request.Headers.TryAddWithoutValidation("User-Agent",
                ActionHostGitHubAuthorizationPolicy.UserAgent) ||
            !request.Headers.TryAddWithoutValidation("Accept",
                ActionHostGitHubAuthorizationPolicy.Accept) ||
            !request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version",
                ActionHostGitHubAuthorizationPolicy.ApiVersion))
        {
            request.Dispose();
            return null;
        }

        request.Headers.Authorization = authorization;
        return request;
    }

    private static bool TryCodeloadRedirect(
        Uri? location,
        string repositoryPath,
        string headSha,
        out Uri target)
    {
        target = null!;
        if (location is null || !location.IsAbsoluteUri ||
            !StringComparer.OrdinalIgnoreCase.Equals(location.Scheme, "https") ||
            !StringComparer.OrdinalIgnoreCase.Equals(location.Host,
                "codeload.github.com") || location.Port != 443 ||
            location.UserInfo.Length != 0 || location.Query.Length != 0 ||
            location.Fragment.Length != 0 ||
            !StringComparer.Ordinal.Equals(location.AbsolutePath,
                "/" + repositoryPath + "/legacy.tar.gz/" + headSha))
        {
            return false;
        }

        target = location;
        return true;
    }

    private static ActionHostGitObjectFailure ClassifyStatus(
        HttpResponseMessage response,
        ActionHostGitHubRateLimitClassification rateLimit)
    {
        if (rateLimit is ActionHostGitHubRateLimitClassification.Primary or
            ActionHostGitHubRateLimitClassification.Secondary or
            ActionHostGitHubRateLimitClassification.Combined)
        {
            return ActionHostGitObjectFailure.RateLimited;
        }
        if (rateLimit == ActionHostGitHubRateLimitClassification.Invalid)
        {
            return ActionHostGitObjectFailure.InvalidResponse;
        }

        return response.StatusCode switch
        {
            HttpStatusCode.NotFound => ActionHostGitObjectFailure.NotFound,
            HttpStatusCode.Unauthorized =>
                ActionHostGitObjectFailure.Unauthorized,
            HttpStatusCode.Forbidden => ActionHostGitObjectFailure.Forbidden,
            _ when (int)response.StatusCode >= 500 =>
                ActionHostGitObjectFailure.UpstreamFailure,
            _ => ActionHostGitObjectFailure.InvalidResponse,
        };
    }

    private static ActionHostGitObjectFailure ClassifyRateLimit(
        ActionHostGitHubRateLimitClassification classification) =>
        classification is ActionHostGitHubRateLimitClassification.Primary or
            ActionHostGitHubRateLimitClassification.Secondary or
            ActionHostGitHubRateLimitClassification.Combined
            ? ActionHostGitObjectFailure.RateLimited
            : ActionHostGitObjectFailure.InvalidResponse;

    private static bool HasJsonContentType(MediaTypeHeaderValue? contentType) =>
        contentType?.MediaType is { } mediaType &&
        (StringComparer.OrdinalIgnoreCase.Equals(
            mediaType,
            "application/json") ||
        mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    private static bool HasArchiveContentType(MediaTypeHeaderValue? contentType) =>
        contentType?.MediaType is { } mediaType &&
        (StringComparer.OrdinalIgnoreCase.Equals(mediaType, "application/x-gzip") ||
        StringComparer.OrdinalIgnoreCase.Equals(mediaType, "application/gzip") ||
        StringComparer.OrdinalIgnoreCase.Equals(mediaType, "application/octet-stream"));

    private static async Task<BoundedResponse> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 &&
            content.Headers.ContentLength > maximumBytes)
        {
            return new(null, 0);
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
                var body = captured.ToArray();
                return new(body, body.Length);
            }

            await captured.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }

        return new(null, checked((int)captured.Length));
    }

    private readonly record struct BoundedResponse(
        byte[]? Body,
        int CapturedResponseBytes);

    private static bool IsNonFatal(Exception exception) =>
        exception is HttpRequestException or IOException or
        InvalidOperationException;

    private sealed class DocumentResult<T>
        where T : class
    {
        private DocumentResult(
            T? value,
            ActionHostGitObjectFailure failure,
            int capturedResponseBytes)
        {
            Value = value;
            Failure = failure;
            CapturedResponseBytes = capturedResponseBytes;
        }

        internal T? Value { get; }

        internal ActionHostGitObjectFailure Failure { get; }

        internal int CapturedResponseBytes { get; }

        internal static DocumentResult<T> Success(
            T value,
            int capturedResponseBytes) =>
            new(value, ActionHostGitObjectFailure.None,
                capturedResponseBytes);

        internal static DocumentResult<T> Failed(
            ActionHostGitObjectFailure failure,
            int capturedResponseBytes = 0) =>
            new(null, failure, capturedResponseBytes);
    }
}
