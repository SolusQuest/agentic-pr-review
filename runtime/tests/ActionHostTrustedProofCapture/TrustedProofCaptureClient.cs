using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofCapture;

public sealed record SafeResponseCapture(
    string Route,
    int Page,
    int Status,
    string BodySha256,
    long BodySize,
    string SafeHeadersSha256,
    long RequestStartedUnixMilliseconds,
    long ResponseReceivedUnixMilliseconds,
    string? NextRoute);

public sealed record CapturePageSet(
    ImmutableArray<SafeResponseCapture> Captures,
    ImmutableArray<byte[]> Bodies);

public sealed class TrustedProofCaptureClient : IDisposable
{
    public const string ApiVersion = "2026-03-10";
    private readonly HttpClient api;
    private readonly HttpMessageInvoker artifact;
    private readonly byte[] token;

    public TrustedProofCaptureClient(
        byte[] token,
        HttpMessageHandler apiHandler,
        HttpMessageHandler artifactHandler)
    {
        if (token.Length is < 1 or > EvidenceLimits.MaximumCredentialBytes)
        {
            throw new InvalidDataException("github_token_invalid");
        }

        this.token = token.ToArray();
        api = new HttpClient(apiHandler, disposeHandler: true)
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = EvidenceLimits.RequestTimeout,
        };
        artifact = new HttpMessageInvoker(artifactHandler, disposeHandler: true);
    }

    public static TrustedProofCaptureClient CreateProduction(byte[] token)
    {
        var apiHandler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
        };
        var artifactHandler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
        };
        return new TrustedProofCaptureClient(token, apiHandler, artifactHandler);
    }

    public async Task<CapturePageSet> GetPaginatedAsync(
        string firstRoute,
        CancellationToken cancellationToken)
        => await GetPaginatedAsync(firstRoute, firstRoute.Split('?', 2)[0], cancellationToken);

    public async Task<CapturePageSet> GetPaginatedAsync(
        string firstRoute,
        string endpointFamily,
        CancellationToken cancellationToken)
    {
        var captures = ImmutableArray.CreateBuilder<SafeResponseCapture>();
        var bodies = ImmutableArray.CreateBuilder<byte[]>();
        try
        {
            string? route = firstRoute;
            for (var page = 1; route is not null; page++)
            {
                if (page > EvidenceLimits.MaximumPages ||
                    !ValidApiRoute(route) ||
                    !(route == endpointFamily || route.StartsWith($"{endpointFamily}?", StringComparison.Ordinal)))
                {
                    throw new InvalidDataException("github_pagination_invalid");
                }

                using var request = CreateApiRequest(route);
                var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                using var response = await api.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new InvalidDataException("github_response_invalid");
                }

                var body = await ReadBoundedAsync(
                    response.Content,
                    EvidenceLimits.MaximumDocumentBytes,
                    cancellationToken);
                var received = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                route = NextRoute(response.Headers);
                var safeHeadersSha256 = SafeHeadersSha256(response, route);
                captures.Add(new SafeResponseCapture(
                    request.RequestUri!.PathAndQuery,
                    page,
                    (int)response.StatusCode,
                    CanonicalEvidence.Sha256(body),
                    body.Length,
                    safeHeadersSha256,
                    started,
                    received,
                    route));
                bodies.Add(body);
                if (bodies.Count > EvidenceLimits.MaximumRecords)
                {
                    throw new InvalidDataException("github_pagination_invalid");
                }
            }

            if (captures.Count == 0)
            {
                throw new InvalidDataException("github_pagination_invalid");
            }

            return new CapturePageSet(captures.ToImmutable(), bodies.ToImmutable());
        }
        catch
        {
            foreach (var body in bodies)
            {
                CryptographicOperations.ZeroMemory(body);
            }
            throw;
        }
    }

    public async Task<(byte[] Archive, SafeResponseCapture Capture)> DownloadArtifactAsync(
        string apiRoute,
        CancellationToken cancellationToken)
    {
        if (!ValidApiRoute(apiRoute))
        {
            throw new InvalidDataException("artifact_route_invalid");
        }

        using var request = CreateApiRequest(apiRoute);
        var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var redirect = await api.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (redirect.StatusCode != HttpStatusCode.Found ||
            redirect.Headers.Location is not { } location ||
            !ValidArtifactRedirect(location))
        {
            throw new InvalidDataException("artifact_redirect_invalid");
        }

        using var download = new HttpRequestMessage(HttpMethod.Get, location);
        download.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
        download.Headers.UserAgent.ParseAdd("agentic-pr-review-r4-evidence/1");
        using var response = await artifact.SendAsync(download, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK ||
            response.Headers.Location is not null)
        {
            throw new InvalidDataException("artifact_response_invalid");
        }

        var archive = await ReadBoundedAsync(
            response.Content,
            EvidenceLimits.MaximumArchiveBytes,
            cancellationToken);
        var received = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return (
            archive,
            new SafeResponseCapture(
                request.RequestUri!.AbsolutePath,
                1,
                (int)response.StatusCode,
                CanonicalEvidence.Sha256(archive),
                archive.Length,
                SafeHeadersSha256(response, null),
                started,
                received,
                null));
    }

    public static bool ValidArtifactRedirect(Uri value)
    {
        if (!value.IsAbsoluteUri ||
            !StringComparer.OrdinalIgnoreCase.Equals(value.Scheme, Uri.UriSchemeHttps) ||
            value.Port != 443 ||
            !string.IsNullOrEmpty(value.UserInfo) ||
            !string.IsNullOrEmpty(value.Fragment))
        {
            return false;
        }

        var host = value.DnsSafeHost.ToLowerInvariant();
        const string suffix = ".blob.core.windows.net";
        return host.EndsWith(suffix, StringComparison.Ordinal) &&
            host.Length > suffix.Length &&
            host[..^suffix.Length].All(character =>
                character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.');
    }

    public void Dispose()
    {
        api.Dispose();
        artifact.Dispose();
        CryptographicOperations.ZeroMemory(token);
    }

    private HttpRequestMessage CreateApiRequest(string route)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        request.Headers.UserAgent.ParseAdd("agentic-pr-review-r4-evidence/1");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            Encoding.UTF8.GetString(token));
        return request;
    }

    private static bool ValidApiRoute(string route)
    {
        if (!Uri.TryCreate(new Uri("https://api.github.com/"), route, out var uri))
        {
            return false;
        }

        return StringComparer.OrdinalIgnoreCase.Equals(uri.Scheme, Uri.UriSchemeHttps) &&
            StringComparer.OrdinalIgnoreCase.Equals(uri.Host, "api.github.com") &&
            uri.Port == 443 &&
            string.IsNullOrEmpty(uri.UserInfo) &&
            string.IsNullOrEmpty(uri.Fragment) &&
            uri.AbsolutePath.StartsWith("/repos/", StringComparison.Ordinal) &&
            !uri.Query.Contains("access_token", StringComparison.OrdinalIgnoreCase) &&
            !uri.Query.Contains("signature", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NextRoute(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Link", out var values))
        {
            return null;
        }

        string? next = null;
        foreach (var segments in string.Join(",", values)
            .Split(',')
            .Select(part => part.Trim().Split(';')))
        {
            if (segments.Length != 2 ||
                segments[0].Length < 3 ||
                segments[0][0] != '<' ||
                segments[0][^1] != '>')
            {
                throw new InvalidDataException("github_pagination_invalid");
            }

            var candidate = segments[0][1..^1];
            var relation = segments[1].Trim();
            if (!new[] { "rel=\"next\"", "rel=\"prev\"", "rel=\"first\"", "rel=\"last\"" }
                    .Contains(relation, StringComparer.Ordinal) ||
                !ValidApiRoute(candidate))
            {
                throw new InvalidDataException("github_pagination_invalid");
            }

            if (!StringComparer.Ordinal.Equals(relation, "rel=\"next\""))
            {
                continue;
            }

            if (next is not null)
            {
                throw new InvalidDataException("github_pagination_invalid");
            }

            var uri = new Uri(candidate, UriKind.Absolute);
            next = uri.PathAndQuery;
        }

        return next;
    }

    private static string SafeHeadersSha256(HttpResponseMessage response, string? nextRoute)
    {
        var value = string.Join(
            "\n",
            [
                $"status:{(int)response.StatusCode}",
                $"content-type:{response.Content.Headers.ContentType?.MediaType ?? string.Empty}",
                $"content-length:{response.Content.Headers.ContentLength?.ToString() ?? string.Empty}",
                $"next-route:{nextRoute ?? string.Empty}",
            ]);
        return CanonicalEvidence.Sha256(Encoding.UTF8.GetBytes(value));
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximum,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is < 1 ||
            content.Headers.ContentLength > maximum)
        {
            throw new InvalidDataException("response_size_invalid");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            destination.Write(buffer, 0, read);
            if (destination.Length > maximum)
            {
                throw new InvalidDataException("response_size_invalid");
            }
        }

        return destination.ToArray();
    }
}
