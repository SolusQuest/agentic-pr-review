using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests;

public sealed class DeepSeekLiveProviderExecutorTests
{
    [Fact]
    public void RequestContractSha256IsPinned()
    {
        Assert.Equal(
            "312f55d0038a4bcefb26703158edcf196bdb1e6a458c6ec88f3f08b1211f0356",
            DeepSeekProviderContract.RequestContractSha256);
        Assert.Equal(
            "29b9fb2b505b72a008e703d61c7847d475119dfb72b90bbe167e7fade527a787",
            DeepSeekLiveProviderExecutor.PolicyIdForMaxFindings(50));
    }

    [Fact]
    public void InvalidIdentityFailsBeforeProviderExecution()
    {
        var error = Assert.Throws<ProviderFailureException>(() =>
            DeepSeekLiveProviderExecutor.ValidateIdentities(
                Plan(),
                Identities() with { AdapterId = new string('c', 64) }));

        Assert.Equal("APR_PROVIDER_CONFIG", error.Code);
    }

    [Fact]
    public async Task SuccessUsesExactRequestProjectionAndMapsPartialUsage()
    {
        var handler = new RecordingHandler(SuccessResponse());
        var executor = new DeepSeekLiveProviderExecutor("k", handler, TimeSpan.FromSeconds(5));

        var observation = await executor.ExecuteAsync(Plan(), Identities());

        Assert.Equal("hit", observation.CacheStatus);
        Assert.Null(observation.NormalizedUsage["aggregate"]!["cacheWriteInputTokens"]);
        Assert.Empty(observation.Findings);
        Assert.Equal("ok", observation.Summary);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal(DeepSeekProviderContract.Endpoint, handler.Request.RequestUri!.ToString());
        Assert.Equal("Bearer k", handler.Request.Headers.Authorization!.ToString());
        Assert.Equal("application/json", handler.Request.Content!.Headers.ContentType!.MediaType);

        using var body = JsonDocument.Parse(handler.RequestBody);
        var root = body.RootElement;
        Assert.Equal(7, root.EnumerateObject().Count());
        Assert.Equal("deepseek-v4-flash", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(0, root.GetProperty("temperature").GetInt32());
        Assert.Equal(4096, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal("disabled", root.GetProperty("thinking").GetProperty("type").GetString());
        var messages = root.GetProperty("messages");
        Assert.Equal(5, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("system", messages[3].GetProperty("role").GetString());
        Assert.Contains("evidence is not a model field", messages[3].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Equal("user", messages[4].GetProperty("role").GetString());
        Assert.Contains("patch text", messages[4].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("tools", root.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task RequestContractMismatchFailsBeforeNetwork()
    {
        var handler = new RecordingHandler(SuccessResponse());
        var executor = new DeepSeekLiveProviderExecutor("k", handler, TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<ProviderFailureException>(() =>
            executor.ExecuteAsync(Plan() with { RequestContractSha256 = "f".PadLeft(64, 'f') }, Identities()));

        Assert.Equal("APR_PROVIDER_CONFIG", error.Code);
        Assert.Equal(20, error.ExitCode);
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public async Task FindingsCapIsStrict(int count, bool succeeds)
    {
        var handler = new RecordingHandler(SuccessResponse(BuildModel(count, 0)));
        var executor = new DeepSeekLiveProviderExecutor("k", handler, TimeSpan.FromSeconds(5));

        if (succeeds)
        {
            var observation = await executor.ExecuteAsync(Plan(), Identities());
            Assert.Equal(count, observation.Findings.Length);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ProviderFailureException>(() => executor.ExecuteAsync(Plan(), Identities()));
            Assert.Equal("APR_PROVIDER_RESPONSE", error.Code);
        }
    }

    [Theory]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public async Task LimitationsCapIsStrict(int count, bool succeeds)
    {
        var handler = new RecordingHandler(SuccessResponse(BuildModel(0, count)));
        var executor = new DeepSeekLiveProviderExecutor("k", handler, TimeSpan.FromSeconds(5));

        if (succeeds)
            Assert.Equal(count, (await executor.ExecuteAsync(Plan(), Identities())).Limitations.Length);
        else
            Assert.Equal("APR_PROVIDER_RESPONSE", (await Assert.ThrowsAsync<ProviderFailureException>(() => executor.ExecuteAsync(Plan(), Identities()))).Code);
    }

    [Theory]
    [InlineData(1, 1, 2, 1, 0, "hit")]
    [InlineData(2, 2, 4, 0, 2, "miss")]
    [InlineData(3, 3, 6, 1, 2, "partial")]
    public async Task UsageOracleMapsOnlyDocumentedCounters(long prompt, long completion, long total, long hit, long miss, string status)
    {
        var handler = new RecordingHandler(SuccessResponse(BuildModel(0, 0), prompt, completion, total, hit, miss));
        var observation = await new DeepSeekLiveProviderExecutor("k", handler, TimeSpan.FromSeconds(5))
            .ExecuteAsync(Plan(), Identities());

        Assert.Equal(status, observation.CacheStatus);
        var aggregate = observation.NormalizedUsage["aggregate"]!.AsObject();
        Assert.Equal(prompt, aggregate["totalInputTokens"]!.GetValue<long>());
        Assert.Equal(completion, aggregate["outputTokens"]!.GetValue<long>());
        Assert.Equal(hit, aggregate["cacheReadInputTokens"]!.GetValue<long>());
        Assert.Equal(miss, aggregate["uncachedInputTokens"]!.GetValue<long>());
        Assert.Null(aggregate["cacheWriteInputTokens"]);
    }

    [Fact]
    public async Task MissingOrInconsistentUsageFailsClosed()
    {
        var missing = new RecordingHandler(SuccessResponse(BuildModel(0, 0), omitMiss: true));
        var missingError = await Assert.ThrowsAsync<ProviderFailureException>(() =>
            new DeepSeekLiveProviderExecutor("k", missing, TimeSpan.FromSeconds(5)).ExecuteAsync(Plan(), Identities()));
        Assert.Equal("APR_PROVIDER_RESPONSE", missingError.Code);

        var inconsistent = new RecordingHandler(SuccessResponse(BuildModel(0, 0), 3, 1, 4, 0, 0));
        var inconsistentError = await Assert.ThrowsAsync<ProviderFailureException>(() =>
            new DeepSeekLiveProviderExecutor("k", inconsistent, TimeSpan.FromSeconds(5)).ExecuteAsync(Plan(), Identities()));
        Assert.Equal("APR_PROVIDER_RESPONSE", inconsistentError.Code);
    }

    [Fact]
    public async Task ProviderStatusAndMalformedResponseAreBoundedFailures()
    {
        var rateLimited = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(new string('x', 12_000)),
        });
        var rateError = await Assert.ThrowsAsync<ProviderFailureException>(() =>
            new DeepSeekLiveProviderExecutor("k", rateLimited, TimeSpan.FromSeconds(5)).ExecuteAsync(Plan(), Identities()));
        Assert.Equal("APR_PROVIDER_RATE_LIMITED", rateError.Code);

        var malformed = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("```json\n{}\n```", Encoding.UTF8, "application/json"),
        });
        var responseError = await Assert.ThrowsAsync<ProviderFailureException>(() =>
            new DeepSeekLiveProviderExecutor("k", malformed, TimeSpan.FromSeconds(5)).ExecuteAsync(Plan(), Identities()));
        Assert.Equal("APR_PROVIDER_RESPONSE", responseError.Code);
    }

    [Fact]
    public async Task BodyTimeoutAndTransportAreProviderFailures()
    {
        var timeoutError = await Assert.ThrowsAsync<ProviderFailureException>(() =>
            new DeepSeekLiveProviderExecutor(
                "k",
                new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StallingContent(),
                }),
                TimeSpan.FromMilliseconds(20)).ExecuteAsync(Plan(), Identities()));
        Assert.Equal("APR_PROVIDER_TIMEOUT", timeoutError.Code);

        var transportError = await Assert.ThrowsAsync<ProviderFailureException>(() =>
            new DeepSeekLiveProviderExecutor(
                "k",
                new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ThrowingContent(),
                }),
                TimeSpan.FromSeconds(5)).ExecuteAsync(Plan(), Identities()));
        Assert.Equal("APR_PROVIDER_TRANSPORT", transportError.Code);
    }

    [Fact]
    public async Task WhitespaceOnlyModelTextIsProviderResponseFailure()
    {
        var error = await Assert.ThrowsAsync<ProviderFailureException>(() =>
            new DeepSeekLiveProviderExecutor(
                "k",
                new RecordingHandler(SuccessResponse(
                    "{\"schemaVersion\":1,\"summary\":\"   \",\"findings\":[],\"limitations\":[]}")),
                TimeSpan.FromSeconds(5)).ExecuteAsync(Plan(), Identities()));

        Assert.Equal("APR_PROVIDER_RESPONSE", error.Code);
    }

    [Fact]
    public void ProviderKeyUsesUtf8ByteBounds()
    {
        Assert.Throws<ArgumentException>(() => new DeepSeekLiveProviderExecutor(string.Concat(Enumerable.Repeat("😀", 65)), new RecordingHandler(SuccessResponse()), TimeSpan.FromSeconds(5)));
        _ = new DeepSeekLiveProviderExecutor(new string('a', 256), new RecordingHandler(SuccessResponse()), TimeSpan.FromSeconds(5));
    }

    private static ExpectedIdentities Identities() => new(
        "owner/repo", "owner/repo", 52, "workflow", "domain", "deepseek", "deepseek-v4-flash",
        DeepSeekProviderContract.AdapterId, DeepSeekProviderContract.TemplateId,
        "29b9fb2b505b72a008e703d61c7847d475119dfb72b90bbe167e7fade527a787",
        DeepSeekProviderContract.ToolDefinitionId, DeepSeekProviderContract.CacheConfigId);

    private static ProviderRequestPlan Plan() => new(
        [
            new ProviderRequestMessage("system", "template"),
            new ProviderRequestMessage("system", "policy"),
            new ProviderRequestMessage("system", "tools"),
            new ProviderRequestMessage("user", "patch text"),
        ], 50, "a".PadLeft(64, 'a'), "b".PadLeft(64, 'b'), DeepSeekProviderContract.AdapterId, DeepSeekProviderContract.RequestContractSha256);

    private static string SuccessResponse(string model = "{\"schemaVersion\":1,\"summary\":\"ok\",\"findings\":[],\"limitations\":[]}", long prompt = 2, long completion = 1, long total = 3, long hit = 2, long miss = 0, bool omitMiss = false) =>
        $"{{\"id\":\"id\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"deepseek-v4-flash\",\"choices\":[{{\"index\":0,\"message\":{{\"role\":\"assistant\",\"content\":{JsonSerializer.Serialize(model)}}},\"finish_reason\":\"stop\",\"logprobs\":null}}],\"usage\":{{\"prompt_tokens\":{prompt},\"completion_tokens\":{completion},\"total_tokens\":{total},\"prompt_cache_hit_tokens\":{hit}{(omitMiss ? "" : $",\"prompt_cache_miss_tokens\":{miss}")}}}}}";

    private static string BuildModel(int findings, int limitations)
    {
        var findingItems = string.Join(',', Enumerable.Range(0, findings).Select(index =>
            $"{{\"severity\":\"low\",\"confidence\":\"medium\",\"category\":\"correctness\",\"title\":\"f{index}\",\"body\":\"body\",\"path\":null,\"startLine\":null,\"endLine\":null}}"));
        var limitationItems = string.Join(',', Enumerable.Range(0, limitations).Select(index => JsonSerializer.Serialize($"l{index}")));
        return $"{{\"schemaVersion\":1,\"summary\":\"ok\",\"findings\":[{findingItems}],\"limitations\":[{limitationItems}]}}";
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage response;

        public RecordingHandler(string content)
            : this(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            })
        {
        }

        public RecordingHandler(HttpResponseMessage response) => this.response = response;

        public int Requests { get; private set; }
        public HttpRequestMessage? Request { get; private set; }
        public byte[] RequestBody { get; private set; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Request = request;
            RequestBody = request.Content!.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();
            return Task.FromResult(response);
        }
    }

    private sealed class StallingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new StallingStream());
    }

    private sealed class ThrowingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new ThrowingStream());
    }

    private abstract class TestStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StallingStream : TestStream
    {
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class ThrowingStream : TestStream
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException<int>(new IOException("simulated body read failure"));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("simulated body read failure"));
    }
}
