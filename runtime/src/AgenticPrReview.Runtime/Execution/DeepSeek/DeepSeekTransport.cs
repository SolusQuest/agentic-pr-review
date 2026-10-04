using System.Diagnostics;
using System.Net;
using System.Text;
using AgenticPrReview.Runtime.Agent.Core;

namespace AgenticPrReview.Runtime.Execution.DeepSeek;

internal sealed class DeepSeekTransport : IAccountedDeepSeekTransport
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly DeepSeekCredential _credential;
    private readonly HttpClient _client;
    private readonly TimeSpan _providerTimeout;
    private readonly TimeProvider _timeProvider;
    private bool _disposed;

    private DeepSeekTransport(
        DeepSeekCredential credential,
        HttpClient client,
        TimeSpan providerTimeout,
        TimeProvider timeProvider)
    {
        _credential = credential;
        _client = client;
        _providerTimeout = providerTimeout;
        _timeProvider = timeProvider;
    }

    internal static DeepSeekTransport Create(DeepSeekCredential credential) => CreateCore(credential, TimeProvider.System);

    internal static DeepSeekTransport Create(DeepSeekCredential credential, TimeProvider timeProvider) => CreateCore(credential, timeProvider);

    private static DeepSeekTransport CreateCore(DeepSeekCredential credential, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var handler = CreateHandler(DeepSeekTransportPolicy.ConnectTimeout);
        return new DeepSeekTransport(
            credential,
            CreateClient(handler),
            DeepSeekTransportPolicy.ProviderTimeout,
            timeProvider);
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
            ConnectCallback = connectCallback,
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
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                CreateEndpoint(DeepSeekTransportPolicy.Endpoint))
            {
                Content = new ByteArrayContent(requestSnapshot),
            };
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
            using var response = await _client.SendAsync(
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

            var discarded = await DiscardBoundedAsync(
                response.Content,
                DeepSeekTransportPolicy.ErrorBodyDiscardMaxBytes,
                providerCancellation.Token);
            cancellationToken.ThrowIfCancellationRequested();
            if (Expired())
            {
                return DeepSeekTransportResult.ProviderTimeout();
            }

            return DeepSeekTransportResult.HttpFailure(
                statusClass,
                discarded);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException)
            when (Expired())
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
            return DeepSeekTransportResult.TransportFailure();
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

    private static async Task<int> DiscardBoundedAsync(
        HttpContent content,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var stream =
            await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[Math.Min(8 * 1024, limit)];
        var discarded = 0;
        while (discarded < limit)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(
                    0,
                    Math.Min(buffer.Length, limit - discarded)),
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            discarded = checked(discarded + read);
        }

        return discarded;
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
