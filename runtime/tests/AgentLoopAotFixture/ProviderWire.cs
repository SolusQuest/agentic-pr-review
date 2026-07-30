using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;

namespace AgenticPrReview.Runtime.AgentLoopAotFixture;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ProviderRequestDto(
    ProviderMessageDto[] Messages,
    ProviderToolDto[] Tools,
    ProviderContinuationDto? Continuation,
    bool ThinkingRequired);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ProviderMessageDto(
    string Role,
    ProviderContentDto[] Contents);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ProviderContentDto(
    string Kind,
    string? CallId,
    string? Name,
    string? Text,
    string? Opaque,
    string? Framing,
    string? AssociatedCallId,
    int MessagePosition,
    int Position);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ProviderToolDto(
    string Name,
    string Description,
    string SchemaJson);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ProviderContinuationDto(
    string ProviderId,
    string ModelId,
    string AdapterId,
    string SessionId,
    ProviderContinuationItemDto[] Items);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ProviderContinuationItemDto(
    string Readable,
    string Opaque,
    string Framing,
    string? AssociatedCallId,
    int MessagePosition,
    int ContentPosition);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ProviderUsageDto(
    long InputTokens,
    long OutputTokens);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ProviderResponseDto(
    ProviderMessageDto Message,
    ProviderUsageDto Usage,
    ProviderContinuationDto? Continuation,
    string? Padding = null);

internal sealed record ProviderServerResponse(
    byte[] Body,
    int? DeclaredContentLength = null,
    int StatusCode = 200,
    string ContentType = "application/json",
    string? Location = null);

internal sealed record ProviderCapture(
    string RequestTarget,
    byte[] Body,
    string[] HeaderNames,
    string[] HeaderValues);

internal sealed class ProviderProtocolException : Exception;

internal static class ProviderEndpoint
{
    private const string Prefix = "http://127.0.0.1:";
    private const string Suffix = "/v1/chat/completions";

    internal static bool IsAllowed(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri ||
            !endpoint.OriginalString.StartsWith(Prefix, StringComparison.Ordinal) ||
            !endpoint.OriginalString.EndsWith(Suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var portText = endpoint.OriginalString[
            Prefix.Length..^Suffix.Length];
        if (portText.Length == 0 ||
            portText.Any(character => character is < '0' or > '9') ||
            !int.TryParse(
                portText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var originalPort) ||
            originalPort is < 1 or > 65_535 ||
            !StringComparer.Ordinal.Equals(
                portText,
                originalPort.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)))
        {
            return false;
        }

        return
        StringComparer.OrdinalIgnoreCase.Equals(endpoint.Scheme, Uri.UriSchemeHttp) &&
        StringComparer.Ordinal.Equals(endpoint.Host, "127.0.0.1") &&
        endpoint.Port == originalPort &&
        StringComparer.Ordinal.Equals(
            endpoint.AbsolutePath,
            Suffix) &&
        string.IsNullOrEmpty(endpoint.UserInfo) &&
        string.IsNullOrEmpty(endpoint.Query) &&
        string.IsNullOrEmpty(endpoint.Fragment) &&
        StringComparer.Ordinal.Equals(
            endpoint.GetComponents(UriComponents.Host, UriFormat.UriEscaped),
            "127.0.0.1");
    }
}

internal sealed class LoopbackProviderBackend : IMinimalChatBackend, IDisposable
{
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly string authorization;
    private readonly Action? onInvocation;

    internal LoopbackProviderBackend(
        Uri endpoint,
        string providerCanary,
        Action? onInvocation = null)
    {
        if (!ProviderEndpoint.IsAllowed(endpoint) ||
            !CanarySet.ContainsCanary(providerCanary) ||
            providerCanary.Contains('\r') ||
            providerCanary.Contains('\n'))
        {
            throw new ArgumentException("Provider endpoint or canary is invalid.");
        }

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            Credentials = null,
            PreAuthenticate = false,
            ActivityHeadersPropagator =
                DistributedContextPropagator.CreateNoOutputPropagator(),
        };
        client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.Clear();
        this.endpoint = endpoint;
        authorization = string.Concat("Bearer ", providerCanary);
        this.onInvocation = onInvocation;
    }

    public async Task<MinimalChatResponse> GetResponseAsync(
        MinimalChatRequest request,
        CancellationToken cancellationToken)
    {
        onInvocation?.Invoke();
        var wire = ToWire(request);
        var body = JsonSerializer.SerializeToUtf8Bytes(
            wire,
            ProofJsonContext.Default.ProviderRequestDto);
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(body),
        };
        message.Headers.Host = string.Concat("127.0.0.1:", endpoint.Port);
        message.Headers.TryAddWithoutValidation("Authorization", authorization);
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        message.Headers.ExpectContinue = false;
        message.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };
        message.Content.Headers.ContentLength = body.Length;

        using var response = await client.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK ||
            response.Content.Headers.ContentType is null ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                response.Content.Headers.ContentType.MediaType,
                "application/json"))
        {
            throw new ProviderProtocolException();
        }

        await using var stream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        var captured = await ReadBoundedAsync(
            stream,
            AgentLimits.ResponseBytes + 1,
            cancellationToken);
        if (captured.Length > AgentLimits.ResponseBytes)
        {
            return new MinimalChatResponse(
                new MinimalChatMessage(
                    "assistant",
                    [new MinimalChatContent(
                        "text",
                        null,
                        null,
                        "oversized",
                        null,
                        null,
                        null,
                        0,
                        0)]),
                new MinimalChatUsage(0, 0),
                captured.Length);
        }

        if (response.Content.Headers.ContentLength is { } declared &&
            declared != captured.Length)
        {
            throw new ProviderProtocolException();
        }

        ProviderResponseDto? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(
                captured,
                ProofJsonContext.Default.ProviderResponseDto);
        }
        catch (JsonException)
        {
            throw new ProviderProtocolException();
        }

        if (parsed is null)
        {
            throw new ProviderProtocolException();
        }

        return ToMinimal(parsed, captured.Length);
    }

    public void Dispose() => client.Dispose();

    internal static byte[] WriteRequest(MinimalChatRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(
            ToWire(request),
            ProofJsonContext.Default.ProviderRequestDto);

    private static ProviderRequestDto ToWire(MinimalChatRequest request) => new(
        request.Messages.Select(message => new ProviderMessageDto(
            message.Role,
            message.Contents.Select(content => new ProviderContentDto(
                content.Kind,
                content.CallId,
                content.Name,
                content.Text,
                content.Opaque,
                content.Framing,
                content.AssociatedCallId,
                content.MessagePosition,
                content.Position)).ToArray())).ToArray(),
        request.Tools.Select(tool => new ProviderToolDto(
            tool.Name,
            tool.Description,
            tool.SchemaJson)).ToArray(),
        request.Continuation is null
            ? null
            : new ProviderContinuationDto(
                request.Continuation.ProviderId,
                request.Continuation.ModelId,
                request.Continuation.AdapterId,
                request.Continuation.SessionId,
                request.Continuation.Items.Select(item =>
                    new ProviderContinuationItemDto(
                        item.Readable,
                        item.Opaque,
                        item.Framing,
                        item.AssociatedCallId,
                        item.MessagePosition,
                        item.ContentPosition)).ToArray()),
        request.ThinkingRequired);

    private static MinimalChatResponse ToMinimal(
        ProviderResponseDto response,
        int capturedBytes) => new(
        new MinimalChatMessage(
            response.Message.Role,
            response.Message.Contents.Select(content => new MinimalChatContent(
                content.Kind,
                content.CallId,
                content.Name,
                content.Text,
                content.Opaque,
                content.Framing,
                content.AssociatedCallId,
                content.MessagePosition,
                content.Position)).ToArray()),
        new MinimalChatUsage(
            response.Usage.InputTokens,
            response.Usage.OutputTokens),
        capturedBytes,
        response.Continuation is null
            ? null
            : new MinimalChatContinuation(
                response.Continuation.ProviderId,
                response.Continuation.ModelId,
                response.Continuation.AdapterId,
                response.Continuation.SessionId,
                response.Continuation.Items.Select(item =>
                    new MinimalChatContinuationItem(
                        item.Readable,
                        item.Opaque,
                        item.Framing,
                        item.AssociatedCallId,
                        item.MessagePosition,
                        item.ContentPosition)).ToArray()));

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int limit,
        CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream(limit);
        var buffer = new byte[16 * 1024];
        while (memory.Length < limit)
        {
            var remaining = limit - (int)memory.Length;
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }
}

internal sealed class StrictLoopbackServer : IAsyncDisposable
{
    private static readonly string[] RequiredHeaders =
    [
        "Host",
        "Authorization",
        "Accept",
        "Content-Type",
        "Content-Length",
    ];

    private readonly TcpListener listener;
    private readonly string authorization;
    private readonly IReadOnlyList<Func<byte[], ProviderServerResponse>> scripts;
    private readonly CancellationTokenSource stop = new();
    private readonly Task run;
    private readonly List<ProviderCapture> captures = [];

    private StrictLoopbackServer(
        TcpListener listener,
        string providerCanary,
        IReadOnlyList<Func<byte[], ProviderServerResponse>> scripts)
    {
        this.listener = listener;
        authorization = string.Concat("Bearer ", providerCanary);
        this.scripts = scripts;
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Endpoint = new Uri(
            string.Concat(
                "http://127.0.0.1:",
                port,
                "/v1/chat/completions"),
            UriKind.Absolute);
        run = RunAsync();
    }

    internal Uri Endpoint { get; }

    internal IReadOnlyList<ProviderCapture> Captures => captures;

    internal bool Rejected { get; private set; }

    internal Task Completion => run;

    internal static StrictLoopbackServer Start(
        string providerCanary,
        IReadOnlyList<Func<byte[], ProviderServerResponse>> scripts)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(backlog: 1);
        return new StrictLoopbackServer(listener, providerCanary, scripts);
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ProviderProtocolException)
        {
        }
        stop.Dispose();
    }

    private async Task RunAsync()
    {
        for (var index = 0; index < scripts.Count; index++)
        {
            using var client =
                await listener.AcceptTcpClientAsync(stop.Token);
            await using var stream = client.GetStream();
            CapturedRequest request;
            try
            {
                request = await ReadRequestAsync(stream, stop.Token);
            }
            catch (ProviderProtocolException)
            {
                Rejected = true;
                return;
            }

            captures.Add(new ProviderCapture(
                Endpoint.OriginalString,
                request.Body,
                request.HeaderNames,
                request.HeaderValues));
            var scripted = scripts[index](request.Body);
            await WriteResponseAsync(stream, scripted, stop.Token);
        }
    }

    private async Task<CapturedRequest> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var received = new List<byte>(8_192);
        var buffer = new byte[4_096];
        var headerEnd = -1;
        while (headerEnd < 0 && received.Count <= 32 * 1024)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                throw new ProviderProtocolException();
            }

            received.AddRange(buffer.AsSpan(0, read).ToArray());
            headerEnd = FindHeaderEnd(received);
        }

        if (headerEnd < 0)
        {
            throw new ProviderProtocolException();
        }

        var headerText = Encoding.ASCII.GetString(
            CollectionsMarshal.AsSpan(received)[..headerEnd]);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length != 6 ||
            !StringComparer.Ordinal.Equals(
                lines[0],
                "POST /v1/chat/completions HTTP/1.1"))
        {
            throw new ProviderProtocolException();
        }

        var names = new string[5];
        var values = new string[5];
        for (var index = 0; index < 5; index++)
        {
            var separator = lines[index + 1].IndexOf(':');
            if (separator <= 0)
            {
                throw new ProviderProtocolException();
            }

            names[index] = lines[index + 1][..separator];
            values[index] = lines[index + 1][(separator + 1)..].TrimStart();
        }

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        if (!names.SequenceEqual(RequiredHeaders, StringComparer.Ordinal) ||
            !StringComparer.Ordinal.Equals(
                values[0],
                string.Concat("127.0.0.1:", port)) ||
            !StringComparer.Ordinal.Equals(values[1], authorization) ||
            !StringComparer.Ordinal.Equals(values[2], "application/json") ||
            !StringComparer.Ordinal.Equals(
                values[3],
                "application/json; charset=utf-8") ||
            !int.TryParse(
                values[4],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var contentLength) ||
            contentLength < 0)
        {
            throw new ProviderProtocolException();
        }

        var bodyStart = headerEnd + 4;
        while (received.Count - bodyStart < contentLength)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                throw new ProviderProtocolException();
            }

            received.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        if (received.Count - bodyStart != contentLength)
        {
            throw new ProviderProtocolException();
        }

        return new CapturedRequest(
            received.GetRange(bodyStart, contentLength).ToArray(),
            names,
            values);
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        ProviderServerResponse response,
        CancellationToken cancellationToken)
    {
        var reason = response.StatusCode == 200 ? "OK" : "Error";
        var declared = response.DeclaredContentLength ?? response.Body.Length;
        var headers = string.Concat(
            "HTTP/1.1 ",
            response.StatusCode,
            " ",
            reason,
            "\r\nContent-Type: ",
            response.ContentType,
            "\r\nContent-Length: ",
            declared.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            response.Location is null
                ? string.Empty
                : string.Concat("\r\nLocation: ", response.Location),
            "\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(
            Encoding.ASCII.GetBytes(headers),
            cancellationToken);
        await stream.WriteAsync(response.Body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static int FindHeaderEnd(IReadOnlyList<byte> bytes)
    {
        for (var index = 0; index <= bytes.Count - 4; index++)
        {
            if (bytes[index] == '\r' &&
                bytes[index + 1] == '\n' &&
                bytes[index + 2] == '\r' &&
                bytes[index + 3] == '\n')
            {
                return index;
            }
        }

        return -1;
    }

    private sealed record CapturedRequest(
        byte[] Body,
        string[] HeaderNames,
        string[] HeaderValues);
}
