using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.Tests.Execution.DeepSeek;

public sealed class DeepSeekTransportTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("")]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\0")]
    public void CredentialRejectsInvalidTextWithoutEchoingIt(string value)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            DeepSeekCredential.Create(value));

        if (value.Length > 0)
        {
            Assert.DoesNotContain(
                value,
                error.Message,
                StringComparison.Ordinal);
        }
        Assert.Equal("value", error.ParamName);
    }

    [Fact]
    public void CredentialUsesStrictUtf8ByteBoundsAndSanitizedFormatting()
    {
        var maximum = DeepSeekCredential.Create(new string('k', 256));
        var tooLarge = string.Concat(Enumerable.Repeat("😀", 65));
        var malformed = new string(['\ud800']);

        Assert.Equal(nameof(DeepSeekCredential), maximum.ToString());
        Assert.Throws<ArgumentException>(() =>
            DeepSeekCredential.Create(tooLarge));
        Assert.Throws<ArgumentException>(() =>
            DeepSeekCredential.Create(malformed));
    }

    [Fact]
    public async Task CredentialCanaryAppearsOnceOnlyInOutboundAuthorization()
    {
        const string canary = "ds-secret-7d81c0d3f8bc4989a56f";
        var requestBody = Encoding.UTF8.GetBytes(
            """{"message":"opaque request without the key"}""");
        var successBody = Encoding.UTF8.GetBytes(
            """{"opaque":"provider bytes without the key"}""");
        using var handler = new RecordingHandler(_ =>
            Response(200, new TrackingStream(successBody)));
        using var transport = Transport(handler, canary);

        var result = await transport.SendAsync(requestBody, default);

        Assert.Equal(DeepSeekTransportOutcome.Success, result.Outcome);
        Assert.Equal(successBody, result.Body);
        Assert.Equal(requestBody, handler.RequestBody);
        var wire = string.Join(
            "\n",
            handler.Headers.Select(header =>
                $"{header.Key}:{string.Join(",", header.Value)}"));
        Assert.Equal(1, CountOccurrences(wire, canary));
        Assert.Contains($"Authorization:Bearer {canary}", wire);
        Assert.DoesNotContain(
            canary,
            Encoding.UTF8.GetString(handler.RequestBody),
            StringComparison.Ordinal);
        Assert.DoesNotContain(canary, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            canary,
            transport.ToString()!,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://other.example/chat/completions")]
    [InlineData("http://api.deepseek.com/chat/completions")]
    [InlineData("https://user@api.deepseek.com/chat/completions")]
    [InlineData("https://api.deepseek.com/chat/completions?x=1")]
    [InlineData("https://api.deepseek.com/chat/completions#fragment")]
    [InlineData("https://api.deepseek.com/other")]
    [InlineData("https://api.deepseek.com:443/chat/completions")]
    [InlineData("https://API.DEEPSEEK.COM/chat/completions")]
    [InlineData("https://api.deepseek.com/chat/completions/")]
    public void EndpointAliasesAreRejectedWithoutEchoingThem(string alias)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            DeepSeekTransport.CreateEndpoint(alias));

        Assert.DoesNotContain(alias, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionEndpointAndHandlerPolicyAreExact()
    {
        Assert.Equal(
            DeepSeekTransportPolicy.Endpoint,
            DeepSeekTransport.CreateEndpoint(
                DeepSeekTransportPolicy.Endpoint).AbsoluteUri);
        using var handler = DeepSeekTransport.CreateHandler(
            DeepSeekTransportPolicy.ConnectTimeout);

        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Null(handler.Credentials);
        Assert.False(handler.PreAuthenticate);
        Assert.Equal(TimeSpan.FromSeconds(15), handler.ConnectTimeout);
        Assert.Equal(0, handler.MaxResponseDrainSize);
        Assert.Equal(TimeSpan.Zero, handler.ResponseDrainTimeout);
        Assert.NotNull(handler.RequestHeaderEncodingSelector);
        using var request = new HttpRequestMessage();
        Assert.Equal(
            Encoding.UTF8.CodePage,
            handler.RequestHeaderEncodingSelector!(
                "Authorization",
                request)!.CodePage);
        Assert.Null(
            handler.RequestHeaderEncodingSelector("Host", request));
        Assert.NotNull(handler.ActivityHeadersPropagator);

        using var transport = DeepSeekTransport.Create(
            DeepSeekCredential.Create("key"));
        var client = (HttpClient)typeof(DeepSeekTransport)
            .GetField(
                "_client",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(transport)!;
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Fact]
    public async Task ProductionHandlerSerializesMultibyteCredentialAsUtf8()
    {
        const string credential = "clé-密钥-😀";
        await using var server = new TlsLoopbackServer(
            status: 200,
            responsePrefix: [],
            responseTail: [],
            delayTail: false);
        using var handler = DeepSeekTransport.CreateHandler(
            TestTimeout,
            server.ConnectAsync);
        server.TrustCertificateFor(handler);
        using var transport = DeepSeekTransport.CreateForTesting(
            DeepSeekCredential.Create(credential),
            handler,
            TestTimeout);

        var result = await transport.SendAsync(
            new byte[] { 1 },
            default);
        var request = await server.RequestBytes;
        await server.Completion;

        Assert.Equal(DeepSeekTransportOutcome.Success, result.Outcome);
        var authorization = Encoding.UTF8.GetBytes(
            $"Authorization: Bearer {credential}\r\n");
        Assert.Equal(1, CountOccurrences(request, authorization));
    }

    [Fact]
    public async Task ProductionHandlerDoesNotDrainPastErrorCapOnDispose()
    {
        var prefix = PatternBytes(
            DeepSeekTransportPolicy.ErrorBodyDiscardMaxBytes);
        var tail = PatternBytes(512);
        await using var server = new TlsLoopbackServer(
            status: 500,
            responsePrefix: prefix,
            responseTail: tail,
            delayTail: true);
        using var handler = DeepSeekTransport.CreateHandler(
            TestTimeout,
            server.ConnectAsync);
        server.TrustCertificateFor(handler);
        using var transport = DeepSeekTransport.CreateForTesting(
            DeepSeekCredential.Create("key"),
            handler,
            TestTimeout);

        var result = await transport.SendAsync(
            new byte[] { 1 },
            default);
        var clientStream = server.ClientStream!;
        var readsAtReturn = clientStream.ReadCalls;

        Assert.Equal(DeepSeekTransportOutcome.HttpFailure, result.Outcome);
        Assert.Equal(
            DeepSeekTransportPolicy.ErrorBodyDiscardMaxBytes,
            result.DiscardedErrorCount);
        Assert.Equal(0, clientStream.ActiveReads);

        server.ReleaseTail();
        await server.Completion;
        await Task.Delay(100);

        Assert.Equal(readsAtReturn, clientStream.ReadCalls);
    }

    [Fact]
    public async Task RedirectIsNotFollowedAndOnlyOneRequestIsSent()
    {
        using var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers =
                {
                    Location = new Uri("https://redirect.example/steal"),
                },
                Content = new TrackingContent(new TrackingStream([])),
            });
        using var transport = Transport(handler);

        var result = await transport.SendAsync(new byte[] { 1 }, default);

        Assert.Equal(DeepSeekTransportOutcome.TransportFailure, result.Outcome);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(DeepSeekTransportPolicy.Endpoint, handler.RequestUri);
    }

    [Fact]
    public async Task ExactRequestCapIsSentByteForByte()
    {
        var request = PatternBytes(DeepSeekTransportPolicy.RequestBodyMaxBytes);
        using var handler = new RecordingHandler(_ => Response(200, []));
        using var transport = Transport(handler);

        var result = await transport.SendAsync(request, default);

        Assert.Equal(DeepSeekTransportOutcome.Success, result.Outcome);
        Assert.Equal(request, handler.RequestBody);
        Assert.Equal("application/json", handler.ContentType);
        Assert.Equal("POST", handler.Method);
    }

    [Theory]
    [InlineData(1_048_577)]
    [InlineData(1_064_960)]
    public async Task OversizedRequestReturnsSentinelWithoutSending(int size)
    {
        using var handler = new RecordingHandler(_ => Response(200, []));
        using var transport = Transport(handler);

        var result = await transport.SendAsync(new byte[size], default);

        Assert.Equal(
            DeepSeekTransportOutcome.RequestRejected,
            result.Outcome);
        Assert.Equal(
            DeepSeekTransportPolicy.RequestRejectedCount,
            result.ActualCount);
        Assert.False(result.HasBody);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task RequestMemoryIsSnapshottedBeforeTheAsynchronousSend()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Response(200, []);
        });
        using var transport = Transport(handler);
        var request = new byte[] { 1, 2, 3 };

        var operation = transport.SendAsync(request, default);
        await entered.Task;
        request[0] = 9;
        release.SetResult();
        await operation;

        Assert.Equal(new byte[] { 1, 2, 3 }, handler.RequestBody);
    }

    [Fact]
    public async Task PreObservedCallerCancellationWinsBeforeSizeAndNetwork()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var handler = new RecordingHandler(_ => Response(200, []));
        using var transport = Transport(handler);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transport.SendAsync(
                new byte[DeepSeekTransportPolicy.RequestRejectedCount],
                cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task CallerCancellationDuringHeadersIsRethrownWithCallerToken()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new RecordingHandler(
            async (_, token) =>
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Response(200, []);
            });
        using var transport = Transport(handler);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transport.SendAsync(new byte[] { 1 }, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task ObservedCallerCancellationWinsOverReturnedStatus()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new RecordingHandler(_ =>
        {
            cancellation.Cancel();
            return Response(429, []);
        });
        using var transport = Transport(handler);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transport.SendAsync(new byte[] { 1 }, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task ProductionHandlerPathMapsActualConnectTimeout()
    {
        static async ValueTask<Stream> Stall(
            SocketsHttpConnectionContext _,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Stream.Null;
        }

        using var handler = DeepSeekTransport.CreateHandler(
            TimeSpan.FromMilliseconds(100),
            Stall);
        using var transport = DeepSeekTransport.CreateForTesting(
            DeepSeekCredential.Create("key"),
            handler,
            TestTimeout);

        var result = await transport.SendAsync(new byte[] { 1 }, default);

        Assert.Equal(DeepSeekTransportOutcome.ConnectTimeout, result.Outcome);
    }

    [Fact]
    public async Task UnownedGenericCancellationIsTransportFailure()
    {
        using var handler = new ThrowingHandler(
            new OperationCanceledException("unowned cancellation"));
        using var transport = Transport(handler);

        var result = await transport.SendAsync(new byte[] { 1 }, default);

        Assert.Equal(DeepSeekTransportOutcome.TransportFailure, result.Outcome);
        Assert.DoesNotContain(
            "unowned cancellation",
            result.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectTimeoutShapeIsPhaseLocalToInitialSend()
    {
        var exception = new OperationCanceledException(
            "body connect-looking cancellation",
            new TimeoutException("not a connection phase"),
            default);
        using var handler = new RecordingHandler(_ =>
            Response(200, new ThrowingStream(exception)));
        using var transport = Transport(handler);

        var result = await transport.SendAsync(new byte[] { 1 }, default);

        Assert.Equal(DeepSeekTransportOutcome.TransportFailure, result.Outcome);
    }

    [Fact]
    public async Task ProviderTimeoutCoversHeaders()
    {
        using var handler = new RecordingHandler(
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Response(200, []);
            });
        using var transport = Transport(
            handler,
            providerTimeout: TimeSpan.FromMilliseconds(30));

        var result = await transport.SendAsync(new byte[] { 1 }, default);

        Assert.Equal(DeepSeekTransportOutcome.ProviderTimeout, result.Outcome);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(429)]
    public async Task ProviderTimeoutCoversSuccessAndErrorBodies(int status)
    {
        using var handler = new RecordingHandler(_ =>
            Response(status, new StallingStream()));
        using var transport = Transport(
            handler,
            providerTimeout: TimeSpan.FromMilliseconds(30));

        var result = await transport.SendAsync(new byte[] { 1 }, default);

        Assert.Equal(DeepSeekTransportOutcome.ProviderTimeout, result.Outcome);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(500)]
    public async Task CallerCancellationDuringBodyReadWins(int status)
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new CallbackStallingStream(() => cancellation.Cancel());
        using var handler = new RecordingHandler(_ => Response(status, stream));
        using var transport = Transport(handler);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transport.SendAsync(new byte[] { 1 }, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Theory]
    [InlineData(400, 0)]
    [InlineData(401, 1)]
    [InlineData(402, 2)]
    [InlineData(404, 3)]
    [InlineData(408, 4)]
    [InlineData(422, 5)]
    [InlineData(429, 6)]
    [InlineData(418, 7)]
    [InlineData(500, 8)]
    [InlineData(599, 8)]
    public async Task HttpFailureClassesAreClosedAndSanitized(
        int status,
        int expected)
    {
        const string rawError = "raw-provider-error-secret";
        using var handler = new RecordingHandler(_ =>
            Response(status, Encoding.UTF8.GetBytes(rawError)));
        using var transport = Transport(handler);

        var result = await transport.SendAsync(new byte[] { 1 }, default);

        Assert.Equal(DeepSeekTransportOutcome.HttpFailure, result.Outcome);
        Assert.Equal((DeepSeekHttpStatusClass)expected, result.StatusClass);
        Assert.Equal(rawError.Length, result.DiscardedErrorCount);
        Assert.False(result.HasBody);
        Assert.DoesNotContain(rawError, result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(101)]
    [InlineData(201)]
    [InlineData(302)]
    [InlineData(399)]
    [InlineData(600)]
    public async Task UnclassifiedStatusesFailClosed(int status)
    {
        using var handler = new RecordingHandler(_ => Response(status, [7]));
        using var transport = Transport(handler);

        var result = await transport.SendAsync(new byte[] { 1 }, default);

        Assert.Equal(DeepSeekTransportOutcome.TransportFailure, result.Outcome);
        Assert.False(result.HasBody);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8_192)]
    [InlineData(8_193)]
    [InlineData(20_000)]
    public async Task ErrorDiscardStopsAtExactCapWithoutAnExtraRead(int size)
    {
        var stream = new TrackingStream(size);
        using var handler = new RecordingHandler(_ => Response(500, stream));
        using var transport = Transport(handler);

        var result = await transport.SendAsync(new byte[] { 1 }, default);

        var expected = Math.Min(
            size,
            DeepSeekTransportPolicy.ErrorBodyDiscardMaxBytes);
        Assert.Equal(DeepSeekTransportOutcome.HttpFailure, result.Outcome);
        Assert.Equal(expected, result.DiscardedErrorCount);
        Assert.Equal(expected, stream.BytesRead);
        Assert.Equal(size == 0 ? 1 : 0, stream.EofReads);
    }

    [Fact]
    public async Task ErrorDiscardIgnoresMisleadingContentLength()
    {
        var stream = new TrackingStream(20_000);
        using var handler = new RecordingHandler(_ =>
            Response(429, stream, contentLength: 1));
        using var transport = Transport(handler);

        var result = await transport.SendAsync(new byte[] { 1 }, default);

        Assert.Equal(8_192, result.DiscardedErrorCount);
        Assert.Equal(8_192, stream.BytesRead);
    }

    [Fact]
    public async Task ErrorReadFailureOverridesObservedHttpStatus()
    {
        const string providerText = "provider body read stack detail";
        using var handler = new RecordingHandler(_ =>
            Response(429, new ThrowingStream(
                new IOException(providerText))));
        using var transport = Transport(handler);

        var result = await transport.SendAsync(new byte[] { 1 }, default);

        Assert.Equal(DeepSeekTransportOutcome.TransportFailure, result.Outcome);
        Assert.Null(result.StatusClass);
        Assert.DoesNotContain(
            providerText,
            result.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessReturnsOpaqueTransportOwnedBytes()
    {
        var source = Encoding.UTF8.GetBytes("not-json-and-not-parsed");
        using var handler = new RecordingHandler(_ => Response(200, source));
        using var transport = Transport(handler);

        var result = await transport.SendAsync(new byte[] { 1 }, default);
        source[0] = (byte)'X';

        Assert.Equal(DeepSeekTransportOutcome.Success, result.Outcome);
        Assert.Equal(200, result.Status);
        Assert.Equal("not-json-and-not-parsed", Encoding.UTF8.GetString(
            result.Body.AsSpan()));
        Assert.Equal(result.Body.Length, result.CapturedCount);
        Assert.True(result.HasBody);
    }

    [Fact]
    public void ResultFactoriesRejectOutOfContractStates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DeepSeekTransportResult.Success(
                new byte[
                    DeepSeekTransportPolicy.SuccessBodyMaxBytes + 1]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DeepSeekTransportResult.HttpFailure(
                (DeepSeekHttpStatusClass)999,
                0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DeepSeekTransportResult.HttpFailure(
                DeepSeekHttpStatusClass.BadRequest,
                -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DeepSeekTransportResult.HttpFailure(
                DeepSeekHttpStatusClass.BadRequest,
                DeepSeekTransportPolicy.ErrorBodyDiscardMaxBytes + 1));
    }

    [Fact]
    public void ResultFactoryOwnsSuccessBody()
    {
        var source = new byte[] { 1, 2, 3 };

        var result = DeepSeekTransportResult.Success(source);
        source[0] = 9;

        Assert.Equal(new byte[] { 1, 2, 3 }, result.Body);
        Assert.Equal(3, result.CapturedCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    [InlineData(1_048_576)]
    [InlineData(1_048_577)]
    [InlineData(1_064_960)]
    public async Task SuccessReadUsesExactCapPlusOneBoundary(int size)
    {
        var stream = new TrackingStream(size);
        using var handler = new RecordingHandler(_ => Response(200, stream));
        using var transport = Transport(handler);

        var result = await transport.SendAsync(new byte[] { 1 }, default);

        var consumed = Math.Min(
            size,
            DeepSeekTransportPolicy.ResponseTooLargeCount);
        Assert.Equal(consumed, stream.BytesRead);
        Assert.Equal(
            size <= DeepSeekTransportPolicy.SuccessBodyMaxBytes
                ? DeepSeekTransportOutcome.Success
                : DeepSeekTransportOutcome.ResponseTooLarge,
            result.Outcome);
        Assert.Equal(
            size <= DeepSeekTransportPolicy.SuccessBodyMaxBytes,
            result.HasBody);
        Assert.Equal(
            size <= DeepSeekTransportPolicy.SuccessBodyMaxBytes ? 1 : 0,
            stream.EofReads);
        if (size > DeepSeekTransportPolicy.SuccessBodyMaxBytes)
        {
            Assert.Equal(
                DeepSeekTransportPolicy.ResponseTooLargeCount,
                result.CapturedCount);
        }
    }

    [Fact]
    public async Task EndlessSuccessStreamStopsAtCapPlusOne()
    {
        var stream = new TrackingStream(long.MaxValue);
        using var handler = new RecordingHandler(_ => Response(200, stream));
        using var transport = Transport(handler);

        var result = await transport.SendAsync(new byte[] { 1 }, default);

        Assert.Equal(
            DeepSeekTransportOutcome.ResponseTooLarge,
            result.Outcome);
        Assert.Equal(
            DeepSeekTransportPolicy.ResponseTooLargeCount,
            stream.BytesRead);
        Assert.Equal(0, stream.EofReads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(9_000_000)]
    public async Task SuccessIgnoresUnknownOrMisleadingContentLength(
        int? contentLength)
    {
        var stream = new TrackingStream(11);
        using var handler = new RecordingHandler(_ =>
            Response(200, stream, contentLength));
        using var transport = Transport(handler);

        var result = await transport.SendAsync(new byte[] { 1 }, default);

        Assert.Equal(DeepSeekTransportOutcome.Success, result.Outcome);
        Assert.Equal(11, result.CapturedCount);
        Assert.Equal(11, stream.BytesRead);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(500)]
    public async Task ProviderReadExceptionsAreSanitizedTransportFailures(
        int status)
    {
        const string exceptionText = "provider-secret-exception-text";
        using var handler = new RecordingHandler(_ =>
            Response(status, new ThrowingStream(
                new InvalidOperationException(exceptionText))));
        using var transport = Transport(handler);

        var result = await transport.SendAsync(new byte[] { 1 }, default);

        Assert.Equal(DeepSeekTransportOutcome.TransportFailure, result.Outcome);
        Assert.DoesNotContain(
            exceptionText,
            result.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendExceptionIsSanitized()
    {
        const string exceptionText = "provider-send-secret";
        using var handler = new ThrowingHandler(
            new HttpRequestException(exceptionText));
        using var transport = Transport(handler);

        var result = await transport.SendAsync(new byte[] { 1 }, default);

        Assert.Equal(DeepSeekTransportOutcome.TransportFailure, result.Outcome);
        Assert.DoesNotContain(
            exceptionText,
            result.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestResponseContentStreamAndHandlerAreDisposed()
    {
        var stream = new TrackingStream(3);
        TrackingContent? content = null;
        using var handler = new RecordingHandler(_ =>
        {
            content = new TrackingContent(stream);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            };
        });
        var transport = Transport(handler);

        var result = await transport.SendAsync(
            new byte[] { 1, 2, 3 },
            default);

        Assert.Equal(DeepSeekTransportOutcome.Success, result.Outcome);
        Assert.True(content!.Disposed);
        Assert.True(stream.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            handler.Request!.Content!.ReadAsByteArrayAsync());

        transport.Dispose();
        Assert.True(handler.Disposed);
        transport.Dispose();
    }

    [Fact]
    public async Task SendingAfterDisposalReturnsClosedFailure()
    {
        using var handler = new RecordingHandler(_ => Response(200, []));
        var transport = Transport(handler);
        transport.Dispose();

        var result = await transport.SendAsync(new byte[] { 1 }, default);

        Assert.Equal(DeepSeekTransportOutcome.TransportFailure, result.Outcome);
        Assert.Equal(0, handler.Requests);
    }

    private static DeepSeekTransport Transport(
        HttpMessageHandler handler,
        string credential = "test-key",
        TimeSpan? providerTimeout = null) =>
        DeepSeekTransport.CreateForTesting(
            DeepSeekCredential.Create(credential),
            handler,
            providerTimeout ?? TestTimeout);

    private static HttpResponseMessage Response(
        int status,
        byte[] bytes) =>
        Response(status, new TrackingStream(bytes));

    private static HttpResponseMessage Response(
        int status,
        Stream stream,
        long? contentLength = null)
    {
        var content = new TrackingContent(stream);
        if (contentLength.HasValue)
        {
            content.Headers.ContentLength = contentLength.Value;
        }

        return new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = content,
        };
    }

    private static byte[] PatternBytes(int count)
    {
        var bytes = new byte[count];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(index % 251);
        }

        return bytes;
    }

    private static int CountOccurrences(string value, string fragment)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(
                   fragment,
                   offset,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += fragment.Length;
        }

        return count;
    }

    private static int CountOccurrences(byte[] value, byte[] fragment)
    {
        var count = 0;
        for (var offset = 0;
             offset <= value.Length - fragment.Length;
             offset++)
        {
            if (value.AsSpan(offset, fragment.Length).SequenceEqual(fragment))
            {
                count++;
            }
        }

        return count;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<
            HttpRequestMessage,
            CancellationToken,
            Task<HttpResponseMessage>> _response;

        internal RecordingHandler(
            Func<HttpRequestMessage, HttpResponseMessage> response)
            : this((request, _) => Task.FromResult(response(request)))
        {
        }

        internal RecordingHandler(
            Func<
                HttpRequestMessage,
                CancellationToken,
                Task<HttpResponseMessage>> response)
        {
            _response = response;
        }

        internal int Requests { get; private set; }
        internal HttpRequestMessage? Request { get; private set; }
        internal byte[] RequestBody { get; private set; } = [];
        internal string? RequestUri { get; private set; }
        internal string? ContentType { get; private set; }
        internal string? Method { get; private set; }
        internal Dictionary<string, string[]> Headers { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        internal bool Disposed { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            Request = request;
            RequestUri = request.RequestUri?.AbsoluteUri;
            Method = request.Method.Method;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            foreach (var header in request.Headers)
            {
                Headers[header.Key] = header.Value.ToArray();
            }

            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                {
                    Headers[header.Key] = header.Value.ToArray();
                }

                RequestBody = await request.Content.ReadAsByteArrayAsync(
                    cancellationToken);
            }

            return await _response(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingHandler(Exception exception)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class TrackingContent : HttpContent
    {
        private readonly Stream _stream;

        internal TrackingContent(Stream stream)
        {
            _stream = stream;
        }

        internal bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult(_stream);

        protected override Task<Stream> CreateContentReadStreamAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(_stream);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
                _stream.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private class TrackingStream : Stream
    {
        private readonly byte[]? _bytes;
        private readonly long _length;
        private long _position;
        private bool _eofReturned;

        internal TrackingStream(byte[] bytes)
        {
            _bytes = bytes;
            _length = bytes.Length;
        }

        internal TrackingStream(long length)
        {
            _length = length;
        }

        internal long BytesRead { get; private set; }
        internal int EofReads { get; private set; }
        internal bool Disposed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= _length)
            {
                if (_eofReturned)
                {
                    throw new InvalidOperationException(
                        "The bounded reader attempted an extra EOF read.");
                }

                _eofReturned = true;
                EofReads++;
                return 0;
            }

            var read = checked((int)Math.Min(buffer.Length, _length - _position));
            for (var index = 0; index < read; index++)
            {
                buffer[index] = _bytes is null
                    ? (byte)((_position + index) % 251)
                    : _bytes[checked((int)_position + index)];
            }

            _position += read;
            BytesRead += read;
            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class StallingStream : TrackingStream
    {
        internal StallingStream()
            : base(long.MaxValue)
        {
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class CallbackStallingStream(Action onRead)
        : TrackingStream(long.MaxValue)
    {
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            onRead();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class ThrowingStream(Exception exception)
        : TrackingStream(long.MaxValue)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(exception);
    }

    private sealed class TlsLoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _certificate;
        private readonly byte[] _responsePrefix;
        private readonly byte[] _responseTail;
        private readonly bool _delayTail;
        private readonly int _status;
        private readonly CancellationTokenSource _timeout =
            new(TimeSpan.FromSeconds(10));
        private readonly TaskCompletionSource<byte[]> _requestBytes = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseTail = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _serverTask;

        internal TlsLoopbackServer(
            int status,
            byte[] responsePrefix,
            byte[] responseTail,
            bool delayTail)
        {
            _status = status;
            _responsePrefix = responsePrefix;
            _responseTail = responseTail;
            _delayTail = delayTail;
            _certificate = CreateCertificate();
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _serverTask = RunAsync();
        }

        internal Task<byte[]> RequestBytes => _requestBytes.Task;
        internal Task Completion => _serverTask;
        internal CountingNetworkStream? ClientStream { get; private set; }

        internal async ValueTask<Stream> ConnectAsync(
            SocketsHttpConnectionContext _,
            CancellationToken cancellationToken)
        {
            var socket = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Stream,
                ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(
                    (IPEndPoint)_listener.LocalEndpoint,
                    cancellationToken);
                var stream = new CountingNetworkStream(
                    new NetworkStream(socket, ownsSocket: true));
                ClientStream = stream;
                return stream;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        internal void TrustCertificateFor(SocketsHttpHandler handler)
        {
            handler.SslOptions.RemoteCertificateValidationCallback =
                static (_, _, _, _) => true;
        }

        internal void ReleaseTail() => _releaseTail.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            ReleaseTail();
            _timeout.Cancel();
            _listener.Stop();
            try
            {
                await _serverTask;
            }
            catch
            {
            }

            _timeout.Dispose();
            _certificate.Dispose();
        }

        private async Task RunAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(
                    _timeout.Token);
                await using var tls = new SslStream(
                    client.GetStream(),
                    leaveInnerStreamOpen: false);
                await tls.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        EnabledSslProtocols =
                            SslProtocols.Tls12 | SslProtocols.Tls13,
                        ServerCertificate = _certificate,
                    },
                    _timeout.Token);
                var request = await ReadRequestAsync(tls, _timeout.Token);
                _requestBytes.TrySetResult(request);

                var statusText = _status == 200
                    ? "OK"
                    : "Internal Server Error";
                var responseHeader = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {_status} {statusText}\r\n" +
                    $"Content-Length: " +
                    $"{_responsePrefix.Length + _responseTail.Length}\r\n" +
                    "\r\n");
                await tls.WriteAsync(responseHeader, _timeout.Token);
                await tls.WriteAsync(_responsePrefix, _timeout.Token);
                await tls.FlushAsync(_timeout.Token);

                if (_delayTail)
                {
                    await _releaseTail.Task.WaitAsync(_timeout.Token);
                }

                try
                {
                    await tls.WriteAsync(_responseTail, _timeout.Token);
                    await tls.FlushAsync(_timeout.Token);
                }
                catch (Exception exception)
                    when (exception is IOException or
                        ObjectDisposedException or
                        OperationCanceledException)
                {
                }
            }
            catch (Exception exception)
            {
                _requestBytes.TrySetException(exception);
                throw;
            }
            finally
            {
                _listener.Stop();
            }
        }

        private static async Task<byte[]> ReadRequestAsync(
            Stream stream,
            CancellationToken cancellationToken)
        {
            using var request = new MemoryStream();
            var buffer = new byte[4 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(
                    buffer,
                    cancellationToken);
                if (read == 0)
                {
                    throw new IOException(
                        "The loopback request ended before its body.");
                }

                await request.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken);
                var bytes = request.ToArray();
                var headerEnd = FindSequence(
                    bytes,
                    "\r\n\r\n"u8);
                if (headerEnd < 0)
                {
                    continue;
                }

                var headerLength = headerEnd + 4;
                var header = Encoding.ASCII.GetString(
                    bytes,
                    0,
                    headerLength);
                var contentLength = header
                    .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => line.StartsWith(
                        "Content-Length:",
                        StringComparison.OrdinalIgnoreCase))
                    .Select(line => int.Parse(
                        line["Content-Length:".Length..].Trim(),
                        System.Globalization.CultureInfo.InvariantCulture))
                    .Single();
                if (bytes.Length >= headerLength + contentLength)
                {
                    return bytes;
                }
            }
        }

        private static int FindSequence(
            byte[] value,
            ReadOnlySpan<byte> sequence)
        {
            for (var offset = 0;
                 offset <= value.Length - sequence.Length;
                 offset++)
            {
                if (value.AsSpan(offset, sequence.Length)
                    .SequenceEqual(sequence))
                {
                    return offset;
                }
            }

            return -1;
        }

        private static X509Certificate2 CreateCertificate()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=api.deepseek.com",
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            var alternativeNames = new SubjectAlternativeNameBuilder();
            alternativeNames.AddDnsName("api.deepseek.com");
            request.CertificateExtensions.Add(alternativeNames.Build());
            using var generated = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1));
            const string password = "loopback-test";
            return X509CertificateLoader.LoadPkcs12(
                generated.Export(
                    X509ContentType.Pkcs12,
                    password),
                password,
                X509KeyStorageFlags.Exportable |
                    X509KeyStorageFlags.UserKeySet);
        }
    }

    private sealed class CountingNetworkStream(Stream inner) : Stream
    {
        private long _readCalls;
        private int _activeReads;

        internal long ReadCalls => Interlocked.Read(ref _readCalls);
        internal int ActiveReads => Volatile.Read(ref _activeReads);

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Interlocked.Increment(ref _readCalls);
            Interlocked.Increment(ref _activeReads);
            try
            {
                return inner.Read(buffer, offset, count);
            }
            finally
            {
                Interlocked.Decrement(ref _activeReads);
            }
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readCalls);
            Interlocked.Increment(ref _activeReads);
            try
            {
                return await inner.ReadAsync(buffer, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeReads);
            }
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(
                buffer.AsMemory(offset, count),
                cancellationToken).AsTask();

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(
            CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            inner.Seek(offset, origin);

        public override void SetLength(long value) =>
            inner.SetLength(value);

        public override void Write(
            byte[] buffer,
            int offset,
            int count) =>
            inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, offset, count, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
