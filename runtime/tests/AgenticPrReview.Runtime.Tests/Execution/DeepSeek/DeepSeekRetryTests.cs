using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Tests.Agent.Loop;

namespace AgenticPrReview.Runtime.Tests.Execution.DeepSeek;

public sealed class DeepSeekRetryTests
{
    [Theory]
    [InlineData(408, true)] [InlineData(429, true)] [InlineData(500, true)]
    [InlineData(502, true)] [InlineData(503, true)] [InlineData(504, true)]
    [InlineData(400, false)] [InlineData(401, false)] [InlineData(402, false)]
    [InlineData(404, false)] [InlineData(418, false)] [InlineData(422, false)]
    [InlineData(501, false)] [InlineData(505, false)] [InlineData(599, false)]
    [InlineData(301, false)] [InlineData(204, false)]
    public async Task OnlySixExactStatusesAreEligibleEvenWhenErrorBodyFails(int status, bool eligible)
    {
        foreach (var readFailure in new[] { false, true })
        {
            using var transport = Create(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = readFailure ? new StreamContent(new FaultStream()) : new StringContent("private-error-canary"),
            })));
            var result = await transport.SendAsync("{}"u8.ToArray(), default);
            Assert.Equal(eligible, result.RetryEligible);
            Assert.False(result.HasBody);
            Assert.DoesNotContain("private-error-canary", result.ToString());
        }
    }

    [Theory]
    [InlineData(SocketError.ConnectionReset, true)] [InlineData(SocketError.ConnectionAborted, true)]
    [InlineData(SocketError.ConnectionRefused, true)] [InlineData(SocketError.TimedOut, true)]
    [InlineData(SocketError.TryAgain, true)] [InlineData(SocketError.NetworkUnreachable, true)]
    [InlineData(SocketError.HostUnreachable, true)] [InlineData(SocketError.AccessDenied, false)]
    [InlineData(SocketError.HostNotFound, false)] [InlineData(SocketError.InvalidArgument, false)]
    public async Task ConnectionEvidenceIsAClosedSocketDomain(SocketError error, bool eligible)
    {
        using var transport = Create(new Handler((_, _) => throw new HttpRequestException("private-canary", new SocketException((int)error))));
        Assert.Equal(eligible, (await transport.SendAsync("{}"u8.ToArray(), default)).RetryEligible);
    }

    [Fact]
    public async Task GenericTlsAndProtocolFailuresCannotBorrowSocketEligibility()
    {
        Exception[] failures = [new IOException("private"), new InvalidOperationException("private"),
            new AuthenticationException("private", new SocketException((int)SocketError.ConnectionReset)),
            new HttpRequestException(HttpRequestError.SecureConnectionError, "private", new SocketException((int)SocketError.ConnectionReset)),
            new HttpRequestException(HttpRequestError.HttpProtocolError, "private", new SocketException((int)SocketError.ConnectionReset))];
        foreach (var failure in failures)
        {
            using var transport = Create(new Handler((_, _) => throw failure));
            Assert.False((await transport.SendAsync("{}"u8.ToArray(), default)).RetryEligible);
        }
    }

    [Theory]
    [InlineData("0", 0)] [InlineData("31", 31)] [InlineData("-1", null)]
    [InlineData("1.5", null)] [InlineData("1, 2", null)] [InlineData("garbage", null)]
    [InlineData("999999999999999999999999999999999", null)]
    public void RetryAfterHasOneUnambiguousBoundedValue(string value, int? seconds)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.TryAddWithoutValidation("Retry-After", value);
        Assert.Equal(seconds.HasValue ? TimeSpan.FromSeconds(seconds.Value) : (TimeSpan?)null,
            DeepSeekTransport.ReadRetryAfter(response, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void RetryAfterDatesAndMultipleValuesDoNotSelectArbitraryDelays()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        using var date = new HttpResponseMessage();
        date.Headers.TryAddWithoutValidation("Retry-After", now.AddSeconds(40).ToString("R"));
        Assert.Equal(TimeSpan.FromSeconds(40), DeepSeekTransport.ReadRetryAfter(date, now));
        Assert.Null(DeepSeekTransport.ReadRetryAfter(date, now.AddSeconds(41)));
        foreach (var values in new[] { new[] { "1", "2" }, new[] { "1", "1" }, new[] { "1", "bad" } })
        {
            using var multiple = new HttpResponseMessage();
            multiple.Headers.TryAddWithoutValidation("Retry-After", values);
            Assert.Null(DeepSeekTransport.ReadRetryAfter(multiple, now));
        }
    }

    [Theory]
    [InlineData("{\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":3}}", 10L, 3L)]
    [InlineData("{\"usage\":{\"completion_tokens\":3}}", null, 3L)]
    [InlineData("{\"usage\":{\"completion_tokens\":3,\"completion_tokens\":4}}", null, null)]
    [InlineData("{\"usage\":{\"completion_tokens\":-1}}", null, null)]
    [InlineData("{\"usage\":{\"completion_tokens\":1.5}}", null, null)]
    [InlineData("{\"usage\":{\"completion_tokens\":9223372036854775808}}", null, null)]
    [InlineData("{\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":3,\"total_tokens\":12}}", null, null)]
    [InlineData("{\"usage\":{\"completion_tokens\":3}", null, null)]
    public async Task FailedUsageIsNumericBoundedAndIndependentOfStatus(string body, long? input, long? output)
    {
        foreach (var status in new[] { 401, 503 })
        {
            var capture = new ProviderAttemptCapture(0, 0);
            using var transport = Create(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent(body) })));
            var result = await transport.SendAsync("{}"u8.ToArray(), default, capture);
            var observation = capture.Freeze(true, false);
            Assert.Equal(input, observation.Usage.InputTokens);
            Assert.Equal(output, observation.Usage.OutputTokens);
            Assert.Equal(status == 503, result.RetryEligible);
            Assert.False(result.HasBody);
        }
    }

    [Fact]
    public async Task CompleteJsonAtDiscardCapStillHasUnknownCompleteness()
    {
        var body = "{\"usage\":{\"completion_tokens\":3}}".PadRight(8192);
        var capture = new ProviderAttemptCapture(0, 0);
        using var transport = Create(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        { Content = new StringContent(body) })));
        var result = await transport.SendAsync("{}"u8.ToArray(), default, capture);
        Assert.Equal(8192, result.DiscardedErrorCount);
        Assert.False(capture.Freeze(true, false).Usage.HasAny);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CallerAndFullAttemptTimeoutDominateObservedEligibleStatus(bool caller)
    {
        var clock = new DeadlineTestClock();
        using var cancel = new CancellationTokenSource();
        using var transport = DeepSeekTransport.CreateForTesting(DeepSeekCredential.Create("synthetic"),
            new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            { Content = new StreamContent(new FaultStream(() => { if (caller) cancel.Cancel(); else clock.Advance(TimeSpan.FromSeconds(300)); })) })),
            TimeSpan.FromSeconds(300), clock);
        if (caller) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.SendAsync("{}"u8.ToArray(), cancel.Token));
        else
        {
            var result = await transport.SendAsync("{}"u8.ToArray(), default);
            Assert.Equal(DeepSeekTransportOutcome.ProviderTimeout, result.Outcome);
            Assert.False(result.RetryEligible);
        }
    }

    private static DeepSeekTransport Create(HttpMessageHandler handler) =>
        DeepSeekTransport.CreateForTesting(DeepSeekCredential.Create("synthetic"), handler, TimeSpan.FromSeconds(5));

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class FaultStream(Action? before = null) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            before?.Invoke();
            throw new IOException("private-read-canary", new SocketException((int)SocketError.ConnectionReset));
        }
    }
}
