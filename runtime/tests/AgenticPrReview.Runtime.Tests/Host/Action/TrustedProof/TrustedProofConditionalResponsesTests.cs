using System.Collections;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

namespace AgenticPrReview.Runtime.Tests.Host.Action.TrustedProof;

public sealed class TrustedProofConditionalResponsesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConditionalReadsKeepEveryWireAndSecondaryChargeAndRefundOnlyActual304(bool enabled)
    {
        var wire = 0;
        var primary = 0;
        using var budget = Budget(request =>
        {
            wire++;
            var unchanged = request.Headers.IfNoneMatch.Any(value => value.Tag == "\"one\"");
            if (!unchanged) primary++;
            return Reply("{\"head\":\"one\"}", "\"one\"", 1000 - primary,
                unchanged ? HttpStatusCode.NotModified : HttpStatusCode.OK);
        }, enabled);
        using var client = new HttpClient(budget.CreateOtherGitHubHandler());
        for (var index = 0; index < 2; index++)
        {
            using var response = await client.SendAsync(Request());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("{\"head\":\"one\"}", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(2, wire);
        Assert.Equal(enabled ? 1 : 2, primary);
        using var receipt = Receipt(budget);
        var other = receipt.RootElement.GetProperty("host_other_github_rest");
        Assert.Equal(2, other.GetProperty("raw").GetInt32());
        Assert.Equal(2, other.GetProperty("secondary_points").GetInt32());
        Assert.Equal(primary, other.GetProperty("primary").GetInt32());
        Assert.Equal(enabled ? 1 : 0, other.GetProperty("not_modified").GetInt32());
    }

    [Fact]
    public async Task ChangedHeadAndPermissionRevocationCannotReturnTheCachedHead()
    {
        var wire = 0;
        using var budget = Budget(request =>
        {
            wire++;
            if (wire == 1) return Reply("{\"head\":\"old\"}", "\"old\"");
            if (wire == 2)
            {
                Assert.Equal("\"old\"", Assert.Single(request.Headers.IfNoneMatch).Tag);
                return Reply("{\"head\":\"new\"}", "\"new\"");
            }
            Assert.Equal("\"new\"", Assert.Single(request.Headers.IfNoneMatch).Tag);
            return Reply("{\"message\":\"permission revoked\"}", null, status: HttpStatusCode.Forbidden);
        });
        using var client = new HttpClient(budget.CreateOtherGitHubHandler());
        using var first = await client.SendAsync(Request());
        using var changed = await client.SendAsync(Request());
        Assert.Equal("{\"head\":\"new\"}", await changed.Content.ReadAsStringAsync());
        using var denied = await client.SendAsync(Request());
        Assert.False(denied.IsSuccessStatusCode);
        Assert.DoesNotContain("old", await denied.Content.ReadAsStringAsync());
        Assert.Equal(3, wire);
        Assert.Empty(CachedBodies(budget));
    }

    [Theory]
    [InlineData("credential")]
    [InlineData("accept")]
    [InlineData("api-version")]
    [InlineData("head-selector")]
    [InlineData("query")]
    public async Task RepresentationIdentitySeparatesCredentialsHeadersUriAndRequestMode(string variation)
    {
        var wire = 0;
        using var budget = Budget(request =>
        {
            wire++;
            Assert.Empty(request.Headers.IfNoneMatch);
            return Reply($"{{\"representation\":{wire}}}", "\"shared-tag\"");
        });
        using var ordinary = new HttpClient(budget.CreateOtherGitHubHandler());
        using var head = new HttpClient(budget.CreateHeadSourceHandler());
        using var initial = await ordinary.SendAsync(Request());
        using var request = Request(variation == "query" ? "/repos/o/r/pulls/1?mode=other" : "/repos/o/r/pulls/1");
        if (variation == "credential") request.Headers.Authorization = new("Bearer", "different-principal");
        if (variation == "accept") request.Headers.Accept.Add(new("application/vnd.github+json"));
        if (variation == "api-version") request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await (variation == "head-selector" ? head : ordinary).SendAsync(request);
        Assert.Equal("{\"representation\":2}", await response.Content.ReadAsStringAsync());
        Assert.Equal(2, wire);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnseenOrContradictory304FailsClosedAndNeverRevivesAnOldBody(bool unseen)
    {
        var wire = 0;
        using var budget = Budget(_ =>
        {
            wire++;
            return !unseen && wire == 1 ? Reply("{\"private\":\"old\"}", "\"old\"") :
                Reply("", "\"different\"", status: HttpStatusCode.NotModified);
        });
        using var client = new HttpClient(budget.CreateOtherGitHubHandler());
        if (!unseen) { using var initial = await client.SendAsync(Request()); }
        using var rejected = await client.SendAsync(Request());
        Assert.Equal(HttpStatusCode.BadGateway, rejected.StatusCode);
        Assert.DoesNotContain("private", await rejected.Content.ReadAsStringAsync());
        using var closed = await client.SendAsync(Request());
        Assert.Equal(HttpStatusCode.TooManyRequests, closed.StatusCode);
        Assert.Equal(unseen ? 1 : 2, wire);
        Assert.Empty(CachedBodies(budget));
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("partial")]
    [InlineData("invalid-json")]
    public async Task ExceptionalAndPartialResponsesCannotReuseThePreviousRepresentation(string failure)
    {
        var wire = 0;
        using var budget = Budget(_ =>
        {
            wire++;
            if (wire == 1) return Reply("{\"private\":\"old\"}", "\"old\"");
            if (failure == "transport") throw new HttpRequestException("controlled transport failure");
            var response = Reply(failure == "invalid-json" ? "{invalid" : "{}", "\"new\"");
            if (failure == "partial") response.Content = new PartialContent();
            return response;
        });
        using var client = new HttpClient(budget.CreateOtherGitHubHandler());
        using var initial = await client.SendAsync(Request());
        await Assert.ThrowsAnyAsync<Exception>(() => client.SendAsync(Request()));
        using var closed = await client.SendAsync(Request());
        Assert.Equal(HttpStatusCode.TooManyRequests, closed.StatusCode);
        Assert.Equal(2, wire);
        Assert.Empty(CachedBodies(budget));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedAndNoStoreResponsesAreNeverRetained(bool noStore)
    {
        var wire = 0;
        using var budget = Budget(request =>
        {
            wire++;
            Assert.Empty(request.Headers.IfNoneMatch);
            var response = Reply(noStore ? "{}" : "{\"data\":\"" + new string('x', 65_537) + "\"}", "\"tag\"");
            if (noStore) response.Headers.CacheControl = new() { NoStore = true };
            return response;
        });
        using var client = new HttpClient(budget.CreateOtherGitHubHandler());
        using var first = await client.SendAsync(Request());
        using var second = await client.SendAsync(Request());
        Assert.True(first.IsSuccessStatusCode && second.IsSuccessStatusCode);
        Assert.Equal(2, wire);
        Assert.Empty(CachedBodies(budget));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EntryAndByteEvictionWipeOwnedBodiesAndRequireAnotherWireRead(bool byteBound)
    {
        var wire = 0;
        var bodies = new List<byte[]>();
        using var budget = Budget(request =>
        {
            wire++;
            Assert.Empty(request.Headers.IfNoneMatch);
            return Reply(byteBound ? "{\"data\":\"" + new string('x', 65_525) + "\"}" : "{}", "\"tag\"");
        });
        using var client = new HttpClient(budget.CreateOtherGitHubHandler());
        var count = byteBound ? 17 : 33;
        for (var index = 0; index < count; index++)
        {
            using var response = await client.SendAsync(Request("/repos/o/r/pulls/" + index));
            if (index == 0) bodies.AddRange(CachedBodies(budget));
        }
        Assert.All(Assert.Single(bodies), value => Assert.Equal((byte)0, value));
        Assert.True(CachedBodies(budget).Count <= 32);
        Assert.True(CachedBodies(budget).Sum(body => body.Length) <= 1_048_576);
        using var reread = await client.SendAsync(Request("/repos/o/r/pulls/0"));
        Assert.Equal(count + 1, wire);
    }

    [Fact]
    public async Task InvocationAndReconstructedResponseDisposalWipeTheirOwnedBuffers()
    {
        using var budget = Budget(request => Reply("{\"data\":\"owned\"}", "\"tag\"", status:
            request.Headers.IfNoneMatch.Count > 0 ? HttpStatusCode.NotModified : HttpStatusCode.OK));
        using var client = new HttpClient(budget.CreateOtherGitHubHandler());
        using var initial = await client.SendAsync(Request());
        var retained = Assert.Single(CachedBodies(budget));
        var reused = await client.SendAsync(Request());
        Assert.Equal("{\"data\":\"owned\"}", await reused.Content.ReadAsStringAsync());
        var contentBytes = Assert.IsType<byte[]>(reused.Content.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(field => field.FieldType == typeof(byte[])).GetValue(reused.Content));
        reused.Dispose();
        Assert.All(contentBytes, value => Assert.Equal((byte)0, value));
        Assert.Contains(retained, value => value != 0);
        budget.Dispose();
        Assert.All(retained, value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public async Task InvalidFreshRateHeadersCannotBeHiddenByCachedHeadersOrBody()
    {
        var wire = 0;
        using var budget = Budget(_ =>
        {
            var response = Reply("{}", "\"tag\"", status: ++wire == 1 ? HttpStatusCode.OK : HttpStatusCode.NotModified);
            if (wire == 2)
            {
                response.Headers.Remove("x-ratelimit-remaining");
                response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "invalid");
            }
            return response;
        });
        using var client = new HttpClient(budget.CreateOtherGitHubHandler());
        using var first = await client.SendAsync(Request());
        using var invalid = await client.SendAsync(Request());
        Assert.Equal(HttpStatusCode.TooManyRequests, invalid.StatusCode);
        Assert.Equal(2, wire);
        Assert.Empty(CachedBodies(budget));
    }

    private static TrustedProofGitHubRequestBudget Budget(Func<HttpRequestMessage, HttpResponseMessage> send, bool enabled = true) =>
        new(256, 1, () => new Origin(send), enableConditionalGet: enabled);

    [Fact]
    public async Task MalformedRepresentationHeaderIdentityIsRejectedBeforeWire()
    {
        var wire = 0;
        using var budget = Budget(_ => { wire++; return Reply("{}", "\"tag\""); });
        using var client = new HttpClient(budget.CreateOtherGitHubHandler());
        using var request = Request();
        request.Headers.TryAddWithoutValidation("X-Representation", "\ud800");
        await Assert.ThrowsAsync<EncoderFallbackException>(() => client.SendAsync(request));
        Assert.Equal(0, wire);
        Assert.Empty(CachedBodies(budget));
    }

    private static HttpRequestMessage Request(string path = "/repos/o/r/pulls/1")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com" + path);
        request.Headers.Authorization = new("Bearer", "controlled-principal");
        return request;
    }

    private static HttpResponseMessage Reply(string body, string? etag, int remaining = 900, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (status == HttpStatusCode.NotModified) { response.Content.Dispose(); response.Content = null; }
        if (etag is not null) response.Headers.ETag = new(etag);
        response.Headers.TryAddWithoutValidation("x-ratelimit-limit", "1000");
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", remaining.ToString(System.Globalization.CultureInfo.InvariantCulture));
        response.Headers.TryAddWithoutValidation("x-ratelimit-reset", "4102444800");
        return response;
    }

    private static JsonDocument Receipt(TrustedProofGitHubRequestBudget budget)
    {
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        budget.WriteReceipt(writer);
        return JsonDocument.Parse(writer.ToString()["APR_R4_E2P_GITHUB_REQUEST_BUDGET ".Length..]);
    }

    private static IReadOnlyList<byte[]> CachedBodies(TrustedProofGitHubRequestBudget budget)
    {
        var cache = typeof(TrustedProofGitHubRequestBudget).GetField("_conditionalResponses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(budget)!;
        var entries = (IDictionary)cache.GetType().GetField("entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        return entries.Values.Cast<object>().Select(entry => (byte[])entry.GetType().GetProperty("Body")!.GetValue(entry)!).ToArray();
    }

    private sealed class Origin(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = send(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class PartialContent : HttpContent
    {
        internal PartialContent() { Headers.ContentType = new("application/json"); }
        protected override bool TryComputeLength(out long length) { length = 20; return true; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync("{\"part\":"u8.ToArray());
            throw new IOException("controlled partial body");
        }
    }
}
