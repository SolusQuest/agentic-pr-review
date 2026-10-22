using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.GitHubArtifacts;

internal sealed class GitHubArtifactRestClient : IDisposable
{
    private readonly HttpClient api;
    private readonly HttpClient anonymous;
    private readonly AuthenticationHeaderValue authorization;
    private readonly string repositoryPath;
    private readonly ActionHostLaunchContract launch;
    private bool disposed;
    internal ArtifactObservationCache Cache { get; } = new();
    internal ArtifactRestClock Clock { get; }
    internal ArtifactRestBudget Budget { get; }
    internal SemaphoreSlim Lane { get; } = new(1, 1);

    internal GitHubArtifactRestClient(ActionHostLaunchContract launch, Func<HttpMessageHandler>? handler = null,
        ArtifactRestBudgetPolicy? policy = null, ArtifactRestClock? clock = null)
    {
        this.launch = launch;
        if (launch.Inputs.GitHubToken is not { } credential ||
            !ActionHostContractValidation.IsRepositoryName(launch.RepositoryName))
            throw new ArtifactTransportException(OpaqueStoreFailure.Invalid);
        authorization = new AuthenticationHeaderValue("Bearer", credential.ExportForPrivateLaunch());
        repositoryPath = "/repos/" + string.Join('/', launch.RepositoryName.Split('/').Select(Uri.EscapeDataString));
        Clock = clock ?? new();
        Budget = new(ArtifactRestBudgetPolicy.ForBuild(launch.BuildDiscriminator, policy), Clock);
        api = CreateClient(handler);
        anonymous = CreateClient(handler);
    }

    internal string ArtifactPath(string id) => repositoryPath + "/actions/artifacts/" + id;
    internal string ListPath(int page) => repositoryPath + "/actions/artifacts?per_page=100&page=" + page.ToString(CultureInfo.InvariantCulture);
    internal string AttemptPath(string run, long attempt) => repositoryPath + "/actions/runs/" + run + "/attempts/" + attempt.ToString(CultureInfo.InvariantCulture);

    internal async Task<JsonDocument> GetAsync(string path, ArtifactRestOperation operation)
    {
        var key = "json:" + path;
        var cached = Cache.Get(key);
        try
        {
            using var response = await SendAsync(HttpMethod.Get, path, operation, cached?.Etag).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Cache.Remove(key);
                throw new ArtifactTransportException(OpaqueStoreFailure.NotFound);
            }
            byte[] body;
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                ArtifactTransportFormat.Require(cached is { Etag: not null });
                body = cached!.Bytes;
                cached = null;
            }
            else
            {
                if (response.StatusCode != HttpStatusCode.OK) throw new ArtifactTransportException(OpaqueStoreFailure.Io);
                body = await ReadBoundedAsync(response.Content, ArtifactBridgeLimits.MaximumDocumentBytes, operation.Token).ConfigureAwait(false);
            }
            try
            {
                var document = ArtifactTransportFormat.ParseJson(body);
                var etag = response.Headers.ETag?.ToString();
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    if (etag is { Length: > 0 and <= 512 } && !etag.Any(char.IsControl)) Cache.Put(key, body, etag);
                    else Cache.Remove(key);
                }
                return document;
            }
            catch { Cache.Remove(key); throw; }
        }
        finally { if (cached is not null) CryptographicOperations.ZeroMemory(cached.Bytes); }
    }

    internal async Task<byte[]> DownloadAsync(string id, int maximum, ArtifactRestOperation operation)
    {
        using var redirect = await SendAsync(HttpMethod.Get, ArtifactPath(id) + "/zip", operation).ConfigureAwait(false);
        var location = redirect.Headers.Location;
        ArtifactTransportFormat.Require(redirect.StatusCode == HttpStatusCode.Found && SafeArchiveLocation(location));
        operation.CheckStart();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
        deadline.CancelAfter(ArtifactBridgeLimits.RequestTimeout);
        // A fresh request/client has no API authorization, cookies or inherited headers.
        using var request = new HttpRequestMessage(HttpMethod.Get, location);
        using var response = await ActionHostGitHubAuthorizationTransport.SendWithConnectionCancellationAsync(
            anonymous, request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw new ArtifactTransportException(OpaqueStoreFailure.Io);
        var bytes = await ReadBoundedAsync(response.Content, maximum, deadline.Token).ConfigureAwait(false);
        try { operation.Check(); }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
        return bytes;
    }

    internal async Task<HttpStatusCode> DeleteAsync(string id, ArtifactRestOperation operation, Action dispatched)
    {
        using var response = await SendAsync(HttpMethod.Delete, ArtifactPath(id), operation, dispatched: dispatched).ConfigureAwait(false);
        return response.StatusCode;
    }

    internal void InvalidateLists() => Cache.RemoveWhere((key, _) => key.StartsWith("json:" + repositoryPath + "/actions/artifacts?", StringComparison.Ordinal));
    internal void Invalidate(string id)
    {
        Cache.Remove("json:" + ArtifactPath(id));
        Cache.RemoveWhere((_, item) => item.Metadata?.Reference.ObjectId.Value == id);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, ArtifactRestOperation operation,
        string? etag = null, Action? dispatched = null)
    {
        await Budget.DispatchRestAsync(method == HttpMethod.Delete, operation).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
        deadline.CancelAfter(ArtifactBridgeLimits.RequestTimeout);
        using var request = new HttpRequestMessage(method, new Uri("https://api.github.com" + path));
        request.Headers.Authorization = authorization;
        request.Headers.UserAgent.ParseAdd("agentic-pr-review");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        dispatched?.Invoke();
        var response = await ActionHostGitHubAuthorizationTransport.SendWithConnectionCancellationAsync(
            api, request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        try
        {
            await Budget.ObserveAsync(response, deadline.Token).ConfigureAwait(false);
            operation.Check();
            // Complete the body under the same 30-second attempt deadline.
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var body = await ReadBoundedAsync(response.Content, ArtifactBridgeLimits.MaximumDocumentBytes, deadline.Token).ConfigureAwait(false);
                var replacement = new ByteArrayContent(body);
                foreach (var header in response.Content.Headers) replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
                response.Content.Dispose();
                response.Content = replacement;
            }
            return response;
        }
        catch { response.Dispose(); throw; }
    }

    internal static bool SafeArchiveLocation(Uri? uri) => uri is { IsAbsoluteUri: true, Scheme: "https", Port: 443 } &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) &&
        uri.IdnHost == uri.Host && !IPAddress.TryParse(uri.Host, out _) &&
        (uri.Host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".actions.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken token)
    {
        ArtifactTransportFormat.Require(content.Headers.ContentLength is not < 0 &&
            (content.Headers.ContentLength is not { } length || length <= maximum) && content.Headers.ContentEncoding.Count == 0);
        await using var input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream((int)Math.Min(content.Headers.ContentLength ?? 65536, maximum));
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximum + 1 - (int)output.Length)), token).ConfigureAwait(false);
                if (read == 0) break;
                ArtifactTransportFormat.Require(output.Length + read <= maximum);
                output.Write(buffer, 0, read);
            }
            ArtifactTransportFormat.Require(output.Length > 0 &&
                (content.Headers.ContentLength is not { } expected || expected == output.Length));
            return output.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(buffer); if (output.TryGetBuffer(out var bytes)) CryptographicOperations.ZeroMemory(bytes); }
    }

    private static HttpClient CreateClient(Func<HttpMessageHandler>? handler) => new(handler?.Invoke() ??
        ActionHostGitHubAuthorizationTransport.CreateHandler(TimeSpan.FromSeconds(10)), disposeHandler: true)
        { Timeout = Timeout.InfiniteTimeSpan };

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        var receipt = Budget.Receipt(launch);
        Cache.Dispose(); api.Dispose(); anonymous.Dispose(); Lane.Dispose();
        if (receipt is not null) Console.Error.WriteLine(receipt);
    }
}
