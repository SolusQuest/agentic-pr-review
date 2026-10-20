using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

// Optional, invocation-local proof transport behavior. A cache hit never skips
// the authenticated wire request. The budget observes the actual response
// before a matching 304 can reconstruct its exact prior JSON representation.
internal sealed class TrustedProofConditionalResponses : IDisposable
{
    private const int MaximumBodyBytes = 65_536;
    private const int MaximumBytes = 1_048_576;
    private const int MaximumEntries = 32;
    private static readonly UTF8Encoding IdentityEncoding = new(false, true);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> order = [];
    private int bytes;
    private bool disposed;

    internal sealed class Request(string key, string? etag, byte[]? body,
        string? contentType, KeyValuePair<string, string[]>[] headers) : IDisposable
    {
        internal string Key { get; } = key;
        internal string? ETag { get; } = etag;
        internal byte[]? Body { get; } = body;
        internal string? ContentType { get; } = contentType;
        internal KeyValuePair<string, string[]>[] Headers { get; } = headers;
        public void Dispose() { if (Body is not null) CryptographicOperations.ZeroMemory(Body); }
    }

    private sealed record Entry(string ETag, byte[] Body, string ContentType,
        KeyValuePair<string, string[]>[] Headers, LinkedListNode<string> Node);

    internal Request? Prepare(HttpRequestMessage request, TrustedProofRequestDomain domain)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        // The 178-tree head-source acquisition remains an uncached, separately
        // labelled witness. A caller's own conditional/range semantics are not
        // overridden by this optional representation owner.
        if (domain != TrustedProofRequestDomain.HostOtherGitHubRest ||
            request.Method != HttpMethod.Get || request.Content is not null ||
            request.RequestUri!.AbsoluteUri.Length > 4096 ||
            request.Headers.Any(header => header.Key.StartsWith("If-", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Range", StringComparison.OrdinalIgnoreCase))) return null;
        var headers = request.Headers.Take(65).ToArray();
        if (headers.Length > 64 || headers.Sum(header => header.Value.Count()) > 256 ||
            headers.Sum(header => header.Key.Length + header.Value.Sum(value => value.Length)) > 8192)
            return null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, domain.ToString());
        Append(hash, request.RequestUri.AbsoluteUri);
        Append(hash, request.Version.ToString());
        Append(hash, request.VersionPolicy.ToString());
        foreach (var header in headers.OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase))
        {
            Append(hash, header.Key.ToLowerInvariant());
            Append(hash, header.Value.Count().ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var value in header.Value) Append(hash, value);
        }
        var key = Convert.ToHexStringLower(hash.GetHashAndReset());
        lock (entries)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!entries.TryGetValue(key, out var entry)) return new(key, null, null, null, []);
            order.Remove(entry.Node);
            order.AddLast(entry.Node);
            var prepared = new Request(key, entry.ETag, entry.Body.ToArray(), entry.ContentType, entry.Headers);
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(entry.ETag));
            return prepared;
        }
    }

    internal async Task<bool> CompleteAsync(Request? request, HttpResponseMessage response, CancellationToken token)
    {
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            if (request?.Body is null || response.Headers.CacheControl?.NoStore == true ||
                response.Headers.Vary.Contains("*") || response.Content.Headers.ContentEncoding.Count != 0 ||
                response.Content.Headers.ContentType is { } type && type.ToString() != request.ContentType ||
                response.Headers.Contains("ETag") && ReadETag(response) != request.ETag)
            {
                Remove(request?.Key);
                return false;
            }
            // Fresh wire headers, especially rate/permission authority, always
            // win. Never fill missing rate headers from a prior observation.
            foreach (var header in request.Headers)
                if (!response.Headers.Contains(header.Key))
                    response.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content.Dispose();
            response.Content = new WipingContent(request.Body.ToArray());
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType!);
            response.StatusCode = HttpStatusCode.OK;
            response.ReasonPhrase = null;
            return true;
        }
        if (request is null) return true;
        Remove(request.Key);
        var etag = ReadETag(response);
        var contentType = response.Content.Headers.ContentType?.ToString();
        var declaredLength = response.Content.Headers.ContentLength;
        if (response.StatusCode != HttpStatusCode.OK || etag is null ||
            response.Headers.CacheControl?.NoStore == true || response.Headers.Vary.Contains("*") ||
            response.Headers.Contains("Set-Cookie") || response.Content.Headers.ContentEncoding.Count != 0 ||
            response.Content.Headers.ContentType?.MediaType != "application/json" ||
            contentType is null || contentType.Length > 1024 ||
            declaredLength is not (> 0 and <= MaximumBodyBytes)) return true;
        var observedHeaders = response.Headers.Where(header => !TransientHeader(header.Key)).Take(65).ToArray();
        if (observedHeaders.Length > 64 || observedHeaders.Sum(header => header.Value.Count()) > 256 ||
            observedHeaders.Sum(header => header.Key.Length + header.Value.Sum(value => value.Length)) > 8192)
            return true;
        var headers = observedHeaders.Select(header =>
            new KeyValuePair<string, string[]>(header.Key, header.Value.ToArray())).ToArray();
        // Enforce the bound before copying, including a dishonest short
        // Content-Length or an exceptional/partial body. Such a read fails the
        // caller instead of silently resurrecting its old representation.
        await response.Content.LoadIntoBufferAsync(MaximumBodyBytes, token).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        try
        {
            if (body.Length != declaredLength || response.Content.Headers.ContentType?.ToString() != contentType ||
                ReadETag(response) != etag) throw new HttpRequestException("conditional_body_invalid");
            using var completeJson = JsonDocument.Parse(body);
            lock (entries)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (body.Length is < 1 or > MaximumBodyBytes) throw new HttpRequestException("conditional_body_invalid");
                // Another reader may have installed this key while the body
                // was being read. Replace that owned buffer as one operation.
                RemoveCore(request.Key);
                while (entries.Count >= MaximumEntries || bytes + body.Length > MaximumBytes)
                    RemoveCore(order.First!.Value);
                entries.Add(request.Key, new(etag, body, contentType,
                    headers, order.AddLast(request.Key)));
                bytes += body.Length;
                body = [];
            }
        }
        finally { CryptographicOperations.ZeroMemory(body); }
        return true;
    }

    internal void Remove(string? key)
    {
        if (key is not null) lock (entries) RemoveCore(key);
    }

    private void RemoveCore(string key)
    {
        if (!entries.Remove(key, out var entry)) return;
        order.Remove(entry.Node);
        bytes -= entry.Body.Length;
        CryptographicOperations.ZeroMemory(entry.Body);
    }

    public void Dispose()
    {
        lock (entries)
        {
            disposed = true;
            while (order.First is { } first) RemoveCore(first.Value);
        }
    }

    private static string? ReadETag(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("ETag", out var values)) return null;
        var all = values.Take(2).ToArray();
        return all is [var value] && value.Length <= 512 &&
            EntityTagHeaderValue.TryParse(value, out var etag) && etag.Tag != "*"
            ? etag.ToString() : null;
    }

    private static bool TransientHeader(string name) =>
        name.StartsWith("x-ratelimit-", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Retry-After", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Date", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("WWW-Authenticate", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("x-oauth-", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("x-accepted-oauth-scopes", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("x-accepted-github-permissions", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("x-github-authentication-token-expiration", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("x-github-request-id", StringComparison.OrdinalIgnoreCase);

    private static void Append(IncrementalHash hash, string value)
    {
        var encoded = IdentityEncoding.GetBytes(value);
        try
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, encoded.Length);
            hash.AppendData(length);
            hash.AppendData(encoded);
        }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }

    private sealed class WipingContent : ByteArrayContent
    {
        private readonly byte[] owned;
        internal WipingContent(byte[] bytes) : base(bytes) { owned = bytes; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) CryptographicOperations.ZeroMemory(owned);
            base.Dispose(disposing);
        }
    }
}
