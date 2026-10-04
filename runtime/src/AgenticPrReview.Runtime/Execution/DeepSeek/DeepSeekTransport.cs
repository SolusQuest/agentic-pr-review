using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AgenticPrReview.Runtime.Agent.Core;

namespace AgenticPrReview.Runtime.Execution.DeepSeek;

internal sealed class DeepSeekTransport : IAccountedDeepSeekTransport
{
    private static readonly HttpRequestOptionsKey<CancellationToken> ConnectCancellation = new("apr.connect-cancellation");
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly DeepSeekCredential _credential;
    private readonly HttpClient? _client;
    private readonly Func<HttpMessageHandler>? _handlerFactory;
    private readonly TimeSpan _providerTimeout;
    private readonly TimeProvider _timeProvider;
    private bool _disposed;

    private DeepSeekTransport(
        DeepSeekCredential credential,
        HttpClient? client,
        TimeSpan providerTimeout,
        TimeProvider timeProvider,
        Func<HttpMessageHandler>? handlerFactory = null)
    {
        _credential = credential;
        _client = client;
        _handlerFactory = handlerFactory;
        _providerTimeout = providerTimeout;
        _timeProvider = timeProvider;
    }

    internal static DeepSeekTransport Create(DeepSeekCredential credential) => CreateCore(credential, TimeProvider.System);

    internal static DeepSeekTransport Create(DeepSeekCredential credential, TimeProvider timeProvider) => CreateCore(credential, timeProvider);

    private static DeepSeekTransport CreateCore(DeepSeekCredential credential, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return new DeepSeekTransport(
            credential, null, DeepSeekTransportPolicy.ProviderTimeout, timeProvider,
            () => CreateHandler(DeepSeekTransportPolicy.ConnectTimeout));
    }

    internal static DeepSeekTransport CreateForTesting(
        DeepSeekCredential credential,
        HttpMessageHandler handler,
        TimeSpan providerTimeout) => CreateForTesting(credential, handler, providerTimeout, TimeProvider.System);

    internal static DeepSeekTransport CreateForTesting(
        DeepSeekCredential credential,
        HttpMessageHandler handler,
        TimeSpan providerTimeout,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(handler);
        if (providerTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(providerTimeout),
                "The provider timeout must be positive.");
        }

        return new DeepSeekTransport(
            credential,
            CreateClient(handler),
            providerTimeout,
            timeProvider);
    }

    internal static DeepSeekTransport CreateForTesting(
        DeepSeekCredential credential, Func<HttpMessageHandler> handlerFactory,
        TimeSpan providerTimeout, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(handlerFactory);
        if (providerTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(providerTimeout));
        return new(credential, null, providerTimeout, timeProvider, handlerFactory);
    }

    internal static SocketsHttpHandler CreateHandler(
        TimeSpan connectTimeout,
        Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>?
            connectCallback = null)
    {
        if (connectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(connectTimeout),
                "The connect timeout must be positive.");
        }

        return new SocketsHttpHandler
        {
            ActivityHeadersPropagator =
                DistributedContextPropagator.CreateNoOutputPropagator(),
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = (context, token) => ConnectAsync(context, token, connectCallback),
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

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken connectionToken,
        Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? callback)
    {
        // The handler can keep connecting after its request waiter is cancelled.
        // Bind the actual socket operation to this attempt's review/deadline token.
        context.InitialRequestMessage.Options.TryGetValue(ConnectCancellation, out var requestToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(connectionToken, requestToken);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if (callback is not null) return await callback(context, linked.Token).ConfigureAwait(false);
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(context.DnsEndPoint, linked.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException) when (connectionToken.IsCancellationRequested && !requestToken.IsCancellationRequested)
        {
            // Preserve the handler-owned cancellation identity used to classify
            // its independent ConnectTimeout, rather than a generic failure.
            connectionToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    internal static Uri CreateEndpoint(string candidate)
    {
        if (!StringComparer.Ordinal.Equals(
                candidate,
                DeepSeekTransportPolicy.Endpoint))
        {
            throw new ArgumentException(
                "The endpoint must be the fixed DeepSeek endpoint.",
                nameof(candidate));
        }

        return new Uri(candidate, UriKind.Absolute);
    }

    public Task<DeepSeekTransportResult> SendAsync(
        ReadOnlyMemory<byte> requestBody,
        CancellationToken cancellationToken) => SendAsync(requestBody, cancellationToken, null);

    public async Task<DeepSeekTransportResult> SendAsync(
        ReadOnlyMemory<byte> requestBody,
        CancellationToken cancellationToken,
        ProviderAttemptCapture? accounting)
    {
        accounting?.ObserveNoDispatch();
        cancellationToken.ThrowIfCancellationRequested();
        if (requestBody.Length > DeepSeekTransportPolicy.RequestBodyMaxBytes)
        {
            return DeepSeekTransportResult.RequestRejected();
        }

        if (_disposed)
        {
            return DeepSeekTransportResult.TransportFailure();
        }

        var requestSnapshot = requestBody.ToArray();
        cancellationToken.ThrowIfCancellationRequested();

        var started = _timeProvider.GetTimestamp();
        using var providerDeadline = new CancellationTokenSource(
            _providerTimeout, _timeProvider);
        bool Expired() => providerDeadline.IsCancellationRequested ||
            _timeProvider.GetElapsedTime(started) >= _providerTimeout;
        using var providerCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                providerDeadline.Token);
        var phase = TransportPhase.Send;
        int? observedStatus = null;
        TimeSpan? retryAfter = null;
        try
        {
            using var ownedClient = _handlerFactory is null ? null : CreateClient(_handlerFactory());
            var client = ownedClient ?? _client!;
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                CreateEndpoint(DeepSeekTransportPolicy.Endpoint))
            {
                Content = new ByteArrayContent(requestSnapshot),
            };
            request.Options.Set(ConnectCancellation, providerCancellation.Token);
            if (!request.Headers.TryAddWithoutValidation(
                    "Authorization",
                    $"Bearer {_credential.Value}") ||
                !request.Content.Headers.TryAddWithoutValidation(
                    "Content-Type",
                    "application/json"))
            {
                return DeepSeekTransportResult.TransportFailure();
            }

            providerCancellation.Token.ThrowIfCancellationRequested();
            if (Expired()) return DeepSeekTransportResult.ProviderTimeout();
            if (accounting is not null && !accounting.TryBeginDispatch())
                throw new OperationCanceledException(cancellationToken);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                providerCancellation.Token);
            cancellationToken.ThrowIfCancellationRequested();
            if (Expired())
            {
                return DeepSeekTransportResult.ProviderTimeout();
            }

            phase = TransportPhase.Body;
            var status = (int)response.StatusCode;
            observedStatus = status;
            retryAfter = ReadRetryAfter(response, _timeProvider.GetUtcNow());
            if (status == 200)
            {
                var successBody = await ReadBoundedAsync(
                    response.Content,
                    DeepSeekTransportPolicy.ResponseTooLargeCount,
                    providerCancellation.Token);
                cancellationToken.ThrowIfCancellationRequested();
                if (Expired())
                {
                    return DeepSeekTransportResult.ProviderTimeout();
                }

                return successBody.Length >
                    DeepSeekTransportPolicy.SuccessBodyMaxBytes
                    ? DeepSeekTransportResult.ResponseTooLarge()
                    : DeepSeekTransportResult.Success(successBody);
            }

            if (!TryClassifyHttpFailure(status, out var statusClass))
            {
                return DeepSeekTransportResult.TransportFailure();
            }

            var errorBody = await ReadBoundedAsync(
                response.Content,
                DeepSeekTransportPolicy.ErrorBodyDiscardMaxBytes,
                providerCancellation.Token);
            // Below the cap means ReadBoundedAsync observed EOF. At the cap,
            // completeness is unknown and no extra read is permitted.
            if (errorBody.Length < DeepSeekTransportPolicy.ErrorBodyDiscardMaxBytes)
                DeepSeekUsageReader.ObserveError(errorBody, accounting);
            cancellationToken.ThrowIfCancellationRequested();
            if (Expired()) return DeepSeekTransportResult.ProviderTimeout();
            return DeepSeekTransportResult.HttpFailure(
                statusClass, errorBody.Length, status, retryAfter);
        }
        catch (Exception exception)
            when (IsNonFatal(exception) && cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception exception)
            when (IsNonFatal(exception) && Expired())
        {
            return DeepSeekTransportResult.ProviderTimeout();
        }
        catch (OperationCanceledException exception)
            when (phase == TransportPhase.Send &&
                exception.InnerException is TimeoutException)
        {
            return DeepSeekTransportResult.ConnectTimeout();
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            // An observed status is authoritative even if draining later fails.
            // A 200 partial body can retry only for proved connection evidence.
            var eligible = observedStatus is { } status && status != 200
                ? DeepSeekTransportResult.IsRetryableStatus(status)
                : IsTransientConnection(exception);
            return DeepSeekTransportResult.TransportFailure(eligible, retryAfter);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client?.Dispose();
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

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var stream =
            await content.ReadAsStreamAsync(cancellationToken);
        using var captured = new MemoryStream(Math.Min(16 * 1024, limit));
        var buffer = new byte[Math.Min(16 * 1024, limit)];
        while (captured.Length < limit)
        {
            var remaining = limit - checked((int)captured.Length);
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            await captured.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }

        return captured.ToArray();
    }

    internal static TimeSpan? ReadRetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values)) return null;
        var entries = values.Take(2).ToArray();
        if (entries.Length != 1 || entries[0].Length > 128 ||
            !RetryConditionHeaderValue.TryParse(entries[0], out var parsed)) return null;
        // Overflow is rejected by TryParse; dates use UTC only for conversion.
        var delay = parsed.Delta ?? (parsed.Date - now);
        return delay >= TimeSpan.Zero ? delay : null;
    }

    private static bool IsTransientConnection(Exception exception)
    {
        for (var depth = 0; depth < 8; depth++)
        {
            if (exception is SocketException socket)
                return socket.SocketErrorCode is SocketError.ConnectionReset or
                    SocketError.ConnectionAborted or SocketError.ConnectionRefused or
                    SocketError.TimedOut or SocketError.TryAgain or
                    SocketError.NetworkUnreachable or SocketError.HostUnreachable;
            if (exception is HttpRequestException http && http.HttpRequestError is not
                (HttpRequestError.Unknown or HttpRequestError.ConnectionError or
                 HttpRequestError.NameResolutionError or HttpRequestError.ResponseEnded)) return false;
            if (exception is not HttpRequestException and not IOException ||
                exception.InnerException is not { } inner) return false;
            exception = inner;
        }
        return false;
    }

    private static bool TryClassifyHttpFailure(
        int status,
        out DeepSeekHttpStatusClass statusClass)
    {
        statusClass = status switch
        {
            400 => DeepSeekHttpStatusClass.BadRequest,
            401 => DeepSeekHttpStatusClass.Unauthorized,
            402 => DeepSeekHttpStatusClass.PaymentRequired,
            404 => DeepSeekHttpStatusClass.NotFound,
            408 => DeepSeekHttpStatusClass.RequestTimeout,
            422 => DeepSeekHttpStatusClass.UnprocessableContent,
            429 => DeepSeekHttpStatusClass.TooManyRequests,
            >= 400 and <= 499 => DeepSeekHttpStatusClass.Other4xx,
            >= 500 and <= 599 => DeepSeekHttpStatusClass.Other5xx,
            _ => default,
        };
        return status is >= 400 and <= 599;
    }

    private static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;

    private enum TransportPhase
    {
        Send,
        Body,
    }
}
