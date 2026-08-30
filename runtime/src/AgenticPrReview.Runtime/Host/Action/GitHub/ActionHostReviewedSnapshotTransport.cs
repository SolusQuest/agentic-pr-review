using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AgenticPrReview.Runtime.ActionHost.Snapshot;

namespace AgenticPrReview.Runtime.ActionHost.GitHub;

internal sealed class ActionHostReviewedSnapshotTransport :
    IActionHostReviewedSnapshotTransport
{
    private const int MaximumJsonResponseBytes = 2 * 1024 * 1024;
    private const string RawAccept = "application/vnd.github.raw+json";
    private readonly string _token;
    private readonly HttpClient _client;
    private readonly IActionHostGitObjectTransport _objects;
    private bool _disposed;

    private ActionHostReviewedSnapshotTransport(
        string token,
        HttpClient client,
        IActionHostGitObjectTransport objects)
    {
        _token = token;
        _client = client;
        _objects = objects;
    }

    internal static ActionHostReviewedSnapshotTransport Create(string token)
    {
        var objects = ActionHostGitObjectTransport.Create(token);
        try
        {
            return new(
                token,
                CreateClient(ActionHostGitHubAuthorizationTransport.CreateHandler(
                    ActionHostGitHubAuthorizationPolicy.ConnectTimeout)),
                objects);
        }
        catch
        {
            objects.Dispose();
            throw;
        }
    }

    internal static ActionHostReviewedSnapshotTransport CreateForTesting(
        string token,
        HttpMessageHandler handler,
        IActionHostGitObjectTransport objects)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(objects);
        if (!TryCreateAuthorizationHeader(token, out _))
        {
            objects.Dispose();
            throw new ActionHostGitHubCredentialException();
        }

        return new(token, CreateClient(handler), objects);
    }

    public async Task<ActionHostGitObjectResult<ActionHostGitHubPullRequestFact>>
        GetCurrentPullRequestAsync(
            string repositoryName,
            long pullRequestNumber,
            CancellationToken cancellationToken)
    {
        if (!TryRepositoryPath(repositoryName, out var repositoryPath) ||
            pullRequestNumber <= 0)
        {
            return ActionHostGitObjectResult<ActionHostGitHubPullRequestFact>
                .Failed(ActionHostGitObjectFailure.InvalidRequest);
        }

        var document = await SendDocumentAsync(
            $"/repos/{repositoryPath}/pulls/{pullRequestNumber}",
            ActionHostGitHubJsonContext.Default
                .ActionHostGitHubPullRequestDocument,
            cancellationToken);
        if (document.Value is null)
        {
            return ActionHostGitObjectResult<ActionHostGitHubPullRequestFact>
                .Failed(document.Failure, document.CapturedResponseBytes);
        }

        return ActionHostGitHubDocumentMapper.TryMap(
                document.Value,
                out var fact) && fact is not null
            ? ActionHostGitObjectResult<ActionHostGitHubPullRequestFact>
                .Success(fact, document.CapturedResponseBytes)
            : ActionHostGitObjectResult<ActionHostGitHubPullRequestFact>
                .Failed(
                    ActionHostGitObjectFailure.InvalidResponse,
                    document.CapturedResponseBytes);
    }

    public async Task<ActionHostGitObjectResult<ActionHostPullRequestFilePageObject>>
        GetPullRequestFilesAsync(
            string repositoryName,
            long pullRequestNumber,
            int page,
            int perPage,
            CancellationToken cancellationToken)
    {
        if (!TryRepositoryPath(repositoryName, out var repositoryPath) ||
            pullRequestNumber <= 0 ||
            page <= 0 ||
            perPage != ReviewedContentLimits.ChangedFilesPerPage)
        {
            return ActionHostGitObjectResult<
                ActionHostPullRequestFilePageObject>.Failed(
                    ActionHostGitObjectFailure.InvalidRequest);
        }

        var document = await SendDocumentAsync(
            $"/repos/{repositoryPath}/pulls/{pullRequestNumber}/files" +
                $"?per_page={perPage.ToString(CultureInfo.InvariantCulture)}" +
                $"&page={page.ToString(CultureInfo.InvariantCulture)}",
            ActionHostReviewedSnapshotJsonContext.Default
                .ActionHostPullRequestFileDocumentArray,
            cancellationToken);
        if (document.Value is null)
        {
            return ActionHostGitObjectResult<
                ActionHostPullRequestFilePageObject>.Failed(
                    document.Failure,
                    document.CapturedResponseBytes);
        }

        if (document.Value.Length > perPage)
        {
            return ActionHostGitObjectResult<
                ActionHostPullRequestFilePageObject>.Failed(
                    ActionHostGitObjectFailure.InvalidResponse,
                    document.CapturedResponseBytes);
        }

        var files = new List<ActionHostPullRequestFileObject>(
            document.Value.Length);
        foreach (var item in document.Value)
        {
            if (!ActionHostReviewedSnapshotMapper.TryMap(item, out var file))
            {
                return ActionHostGitObjectResult<
                    ActionHostPullRequestFilePageObject>.Failed(
                        ActionHostGitObjectFailure.InvalidResponse,
                        document.CapturedResponseBytes);
            }

            files.Add(file!);
        }

        return ActionHostGitObjectResult<ActionHostPullRequestFilePageObject>
            .Success(
                new(files, files.Count < perPage),
                document.CapturedResponseBytes);
    }

    public Task<ActionHostGitObjectResult<ActionHostGitCommitObject>>
        GetCommitObjectAsync(
            string repositoryName,
            string commitSha,
            CancellationToken cancellationToken) =>
        _objects.GetCommitObjectAsync(
            repositoryName,
            commitSha,
            cancellationToken);

    public Task<ActionHostGitObjectResult<ActionHostGitTreeObject>>
        GetTreeObjectAsync(
            string repositoryName,
            string treeSha,
            CancellationToken cancellationToken) =>
        _objects.GetTreeObjectAsync(
            repositoryName,
            treeSha,
            cancellationToken);

    public async Task<ActionHostGitObjectResult<ActionHostStreamedBlobObject>>
        CopyBlobObjectAsync(
            string repositoryName,
            string blobSha,
            long declaredSize,
            Stream destination,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!TryRepositoryPath(repositoryName, out var repositoryPath) ||
            !ActionHostGitObjectMapper.IsSha(blobSha) ||
            declaredSize is < 0 or > ReviewedContentLimits.BaseBlobBytes ||
            !destination.CanWrite)
        {
            return ActionHostGitObjectResult<ActionHostStreamedBlobObject>
                .Failed(ActionHostGitObjectFailure.InvalidRequest);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed)
        {
            return ActionHostGitObjectResult<ActionHostStreamedBlobObject>
                .Failed(ActionHostGitObjectFailure.TransportFailure);
        }

        var captured = 0;
        try
        {
            using var request = CreateRequest(
                $"/repos/{repositoryPath}/git/blobs/{blobSha}",
                RawAccept);
            if (request is null)
            {
                return ActionHostGitObjectResult<ActionHostStreamedBlobObject>
                    .Failed(ActionHostGitObjectFailure.TransportFailure);
            }

            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var rateLimit = ActionHostGitHubRateLimitClassifier.Classify(response);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return ActionHostGitObjectResult<ActionHostStreamedBlobObject>
                    .Failed(ClassifyStatus(response.StatusCode, rateLimit));
            }
            if (rateLimit != ActionHostGitHubRateLimitClassification.None)
            {
                return ActionHostGitObjectResult<ActionHostStreamedBlobObject>
                    .Failed(rateLimit is
                        ActionHostGitHubRateLimitClassification.Primary or
                        ActionHostGitHubRateLimitClassification.Secondary or
                        ActionHostGitHubRateLimitClassification.Combined
                        ? ActionHostGitObjectFailure.RateLimited
                        : ActionHostGitObjectFailure.InvalidResponse);
            }

            if (response.Content.Headers.ContentLength is { } contentLength &&
                contentLength != declaredSize)
            {
                return ActionHostGitObjectResult<ActionHostStreamedBlobObject>
                    .Failed(contentLength > declaredSize
                        ? ActionHostGitObjectFailure.ResponseTooLarge
                        : ActionHostGitObjectFailure.InvalidResponse);
            }

            await using var stream =
                await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[ReviewedContentLimits.StreamBufferBytes];
            long actual = 0;
            while (actual < declaredSize)
            {
                var requested = checked((int)Math.Min(
                    buffer.Length,
                    declaredSize - actual));
                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, requested),
                    cancellationToken);
                if (read == 0)
                {
                    return ActionHostGitObjectResult<
                        ActionHostStreamedBlobObject>.Failed(
                            ActionHostGitObjectFailure.InvalidResponse,
                            captured);
                }

                captured = checked(captured + read);
                await destination.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken);
                actual += read;
            }

            var extra = new byte[1];
            var trailing = await stream.ReadAsync(extra, cancellationToken);
            if (trailing != 0)
            {
                captured = checked(captured + trailing);
                return ActionHostGitObjectResult<
                    ActionHostStreamedBlobObject>.Failed(
                        ActionHostGitObjectFailure.ResponseTooLarge,
                        captured);
            }

            return ActionHostGitObjectResult<ActionHostStreamedBlobObject>
                .Success(new(blobSha, actual), captured);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or
            IOException or InvalidOperationException or
            UnauthorizedAccessException)
        {
            return ActionHostGitObjectResult<ActionHostStreamedBlobObject>
                .Failed(ActionHostGitObjectFailure.TransportFailure, captured);
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
        _objects.Dispose();
    }

    public override string ToString() =>
        "action_host_reviewed_snapshot_transport";

    private async Task<DocumentResult<T>> SendDocumentAsync<T>(
        string pathAndQuery,
        JsonTypeInfo<T> typeInfo,
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
            using var request = CreateRequest(
                pathAndQuery,
                ActionHostGitHubAuthorizationPolicy.Accept);
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
                    ClassifyStatus(response.StatusCode, rateLimit));
            }
            if (rateLimit != ActionHostGitHubRateLimitClassification.None)
            {
                return DocumentResult<T>.Failed(
                    rateLimit is ActionHostGitHubRateLimitClassification.Primary or
                        ActionHostGitHubRateLimitClassification.Secondary or
                        ActionHostGitHubRateLimitClassification.Combined
                        ? ActionHostGitObjectFailure.RateLimited
                        : ActionHostGitObjectFailure.InvalidResponse);
            }

            if (!HasJsonContentType(response.Content.Headers.ContentType))
            {
                return DocumentResult<T>.Failed(
                    ActionHostGitObjectFailure.InvalidResponse);
            }

            var read = await ReadBoundedAsync(
                response.Content,
                MaximumJsonResponseBytes,
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
            NotSupportedException)
        {
            return DocumentResult<T>.Failed(
                ActionHostGitObjectFailure.InvalidResponse,
                capturedResponseBytes);
        }
        catch (Exception exception) when (exception is HttpRequestException or
            IOException or InvalidOperationException)
        {
            return DocumentResult<T>.Failed(
                ActionHostGitObjectFailure.TransportFailure);
        }
    }

    private HttpRequestMessage? CreateRequest(
        string pathAndQuery,
        string accept)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(
                ActionHostGitHubAuthorizationPolicy.Origin + pathAndQuery,
                UriKind.Absolute));
        if (!TryCreateAuthorizationHeader(_token, out var authorization) ||
            !request.Headers.TryAddWithoutValidation(
                "User-Agent",
                ActionHostGitHubAuthorizationPolicy.UserAgent) ||
            !request.Headers.TryAddWithoutValidation("Accept", accept) ||
            !request.Headers.TryAddWithoutValidation(
                "X-GitHub-Api-Version",
                ActionHostGitHubAuthorizationPolicy.ApiVersion))
        {
            request.Dispose();
            return null;
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

    private static ActionHostGitObjectFailure ClassifyStatus(
        HttpStatusCode status,
        ActionHostGitHubRateLimitClassification rateLimit) =>
        rateLimit is ActionHostGitHubRateLimitClassification.Primary or
            ActionHostGitHubRateLimitClassification.Secondary or
            ActionHostGitHubRateLimitClassification.Combined
            ? ActionHostGitObjectFailure.RateLimited
            : rateLimit == ActionHostGitHubRateLimitClassification.Invalid
            ? ActionHostGitObjectFailure.InvalidResponse
            : status switch
    {
        HttpStatusCode.NotFound => ActionHostGitObjectFailure.NotFound,
        HttpStatusCode.Unauthorized =>
            ActionHostGitObjectFailure.Unauthorized,
        HttpStatusCode.Forbidden => ActionHostGitObjectFailure.Forbidden,
        _ when (int)status >= 500 =>
            ActionHostGitObjectFailure.UpstreamFailure,
        _ => ActionHostGitObjectFailure.InvalidResponse,
    };

    private static bool HasJsonContentType(MediaTypeHeaderValue? contentType) =>
        contentType?.MediaType is { } mediaType &&
        (StringComparer.OrdinalIgnoreCase.Equals(
            mediaType,
            "application/json") ||
        mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

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
            new(value, ActionHostGitObjectFailure.None, capturedResponseBytes);

        internal static DocumentResult<T> Failed(
            ActionHostGitObjectFailure failure,
            int capturedResponseBytes = 0) =>
            new(null, failure, capturedResponseBytes);
    }
}
