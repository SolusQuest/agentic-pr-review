using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Ledger;
using AgenticPrReview.Runtime.Prefix;

namespace AgenticPrReview.Runtime.Tests.Execution;

public sealed class AnthropicReferenceProviderExecutorTests
{
    [Fact]
    public void SendsOneClosedRequestAndMapsStructuredFinding()
    {
        string? requestBody = null;
        var handler = new CaptureHandler(request =>
        {
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {"type":"message","model":"claude-sonnet-4-6","stop_reason":"tool_use","content":[{"type":"tool_use","name":"submit_review","input":{"summary":"one finding","findings":[{"severity":"high","confidence":"high","category":"correctness","title":"Bug","body":"Broken","path":"src/a.cs","startLine":2,"endLine":2,"suggestedAction":"Fix it"}],"limitations":[]}}],"usage":{"input_tokens":12,"cache_read_input_tokens":3,"cache_creation_input_tokens":0,"output_tokens":8}}
                """, Encoding.UTF8, "application/json")
            };
        });
        var executor = new AnthropicReferenceProviderExecutor(new HttpClient(handler), () => "secret-sentinel");
        var input = Input();
        var observation = executor.Execute(input, "a".PadLeft(64, 'a'), Identities(), Prefix(), false);

        Assert.NotNull(requestBody);
        using var request = JsonDocument.Parse(requestBody!);
        Assert.Equal("claude-sonnet-4-6", request.RootElement.GetProperty("model").GetString());
        Assert.Single(request.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal("submit_review", request.RootElement.GetProperty("tool_choice").GetProperty("name").GetString());
        Assert.Equal("ephemeral", request.RootElement.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString());
        Assert.DoesNotContain("secret-sentinel", requestBody!, StringComparison.Ordinal);
        Assert.Single(observation.Findings);
        Assert.Equal("src/a.cs", observation.Findings[0].Path);
        Assert.Equal("hit", observation.CacheStatus);
    }

    [Fact]
    public void StatelessRequiresBothProviderCacheCounters()
    {
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {"type":"message","model":"claude-sonnet-4-6","stop_reason":"tool_use","content":[{"type":"tool_use","name":"submit_review","input":{"summary":"ok","findings":[],"limitations":[]}}],"usage":{"input_tokens":12,"output_tokens":8}}
            """, Encoding.UTF8, "application/json")
        });
        var executor = new AnthropicReferenceProviderExecutor(new HttpClient(handler), () => "secret-sentinel");
        var exception = Assert.Throws<RuntimeFailure>(() => executor.Execute(Input(), "a".PadLeft(64, 'a'), Identities(), Prefix(), true));
        Assert.Equal("APR_STATELESS_PROOF_MISSING", exception.Code);
    }

    [Fact]
    public void StatelessProofRequiresMarkerFreeRequestAndBindsRequestHash()
    {
        string? requestBody = null;
        var handler = new CaptureHandler(request =>
        {
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {"type":"message","model":"claude-sonnet-4-6","stop_reason":"tool_use","content":[{"type":"tool_use","name":"submit_review","input":{"summary":"ok","findings":[],"limitations":[]}}],"usage":{"input_tokens":12,"cache_read_input_tokens":0,"cache_creation_input_tokens":0,"output_tokens":8}}
                """, Encoding.UTF8, "application/json")
            };
        });
        var executor = new AnthropicReferenceProviderExecutor(new HttpClient(handler), () => "secret-sentinel");
        var observation = executor.Execute(Input(), "a".PadLeft(64, 'a'), Identities(), Prefix(), true);

        Assert.NotNull(requestBody);
        Assert.DoesNotContain("cache_control", requestBody!, StringComparison.Ordinal);
        var requestSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestBody!))).ToLowerInvariant();
        Assert.Equal(
            requestSha256,
            observation.Capability["statelessProof"]!.AsObject()["requestSha256"]!.GetValue<string>());
    }

    private static ExpectedIdentities Identities() => new(
        "owner/repo", "owner/repo", 1, "workflow", "trusted", "anthropic", "claude-sonnet-4-6",
        "a".PadLeft(64, 'a'), "b".PadLeft(64, 'b'), "c".PadLeft(64, 'c'), "d".PadLeft(64, 'd'), "e".PadLeft(64, 'e'));

    private static PrefixMaterialization Prefix()
    {
        static byte[] Block(string role, string text)
        {
            var bytes = Encoding.UTF8.GetBytes($"{{\"content\":[{{\"text\":{JsonSerializer.Serialize(text)},\"type\":\"text\"}}],\"role\":{JsonSerializer.Serialize(role)}}}");
            var framed = new byte[4 + bytes.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)bytes.Length);
            bytes.CopyTo(framed, 4);
            return framed;
        }
        var stable = Block("system", "stable");
        var dynamic = Block("user", "dynamic");
        return new PrefixMaterialization(stable.ToImmutableArray(), stable.ToImmutableArray(), dynamic.ToImmutableArray(), dynamic.ToImmutableArray(), 1, "a".PadLeft(64, 'a'), "b".PadLeft(64, 'b'), "c".PadLeft(64, 'c'), "d".PadLeft(64, 'd'), "e".PadLeft(64, 'e'), "f".PadLeft(64, 'f'), "0".PadLeft(64, '0'));
    }

    private static ReviewInput Input() => new(
        JsonDocument.Parse("1").RootElement.Clone(), null,
        new RuntimeHost(new RuntimeRepository("owner", "repo"), new RuntimeReview("bootstrap", "b", "h", null, "test"), null),
        new RuntimeSubject(new RuntimePullRequest(JsonDocument.Parse("1").RootElement.Clone(), "Title", "Body", "main", "h", false), [], null, null),
        new RuntimePreviousState(false, null, null, [], null), new RuntimeCommentEvidence([]));

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
