using System.Net;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;

// Internal synthetic proof only. The real HTTP request writer runs, but this handler
// has no network implementation. Captures remain private and never enter a report.
internal sealed class EconomicsCredentialProbe(ReplayScript script) : HttpMessageHandler
{
    internal const string Provider = "APR278_SYNTHETIC_PROVIDER_ONLY";
    internal static readonly IReadOnlyDictionary<string, string> Canaries = new Dictionary<string, string>
    {
        [LiveEnvironmentSecretSource.ProviderVariable] = Provider,
        ["GITHUB_TOKEN"] = "APR278_SYNTHETIC_GITHUB",
        ["ACTIONS_RUNTIME_TOKEN"] = "APR278_SYNTHETIC_ACTIONS",
        ["AGENTIC_REVIEW_R3_STATE_KEY_B64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("APR278_SYNTHETIC_STATE_KEY_00000")),
        ["APR278_UNRELATED_SECRET"] = "APR278_SYNTHETIC_UNRELATED",
        ["RUNNER_TOKEN"] = "APR278_SYNTHETIC_RUNNER",
        ["NPM_TOKEN"] = "APR278_SYNTHETIC_PACKAGE",
        ["AWS_SECRET_ACCESS_KEY"] = "APR278_SYNTHETIC_CLOUD",
        ["SIGNING_KEY"] = "APR278_SYNTHETIC_SIGNING",
    };
    private readonly ReplayTransport replay = new(script, ReplayFault.None);
    private readonly List<Capture> captures = [];
    private sealed record Capture(string Uri, string Method, Dictionary<string, string[]> Headers, byte[] Body);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (captures.Count >= EconomicsLiveLimits.Calls || request.Content is null) throw new IOException();
        // Snapshot headers at handler entry: buffering content can materialize its length header.
        var headers = request.Headers.Concat(request.Content.Headers)
            .ToDictionary(header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        var body = await request.Content.ReadAsByteArrayAsync(token);
        if (body.Length > DeepSeekTransportPolicy.RequestBodyMaxBytes) throw new IOException();
        captures.Add(new(request.RequestUri?.AbsoluteUri ?? "", request.Method.Method, headers, body));
        var response = await replay.SendAsync(body, token);
        if (!response.HasBody) throw new IOException();
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(response.Body.ToArray()) };
    }

    internal EconomicsCredentialProof Verify(byte[] key, byte[]? restored, byte[] completed, string stateRoot)
    {
        if (captures.Count is < 1 or > EconomicsLiveLimits.Calls ||
            Canaries.Keys.Any(name => Environment.GetEnvironmentVariable(name) is not null)) throw new IOException();
        foreach (var capture in captures)
        {
            if (capture.Uri != DeepSeekTransportPolicy.Endpoint || capture.Method != "POST" ||
                !capture.Headers.Keys.Order(StringComparer.Ordinal).SequenceEqual(new[] { "Authorization", "Content-Type" }) ||
                !capture.Headers["Authorization"].SequenceEqual(new[] { "Bearer " + Provider }) ||
                !capture.Headers["Content-Type"].SequenceEqual(new[] { "application/json" })) throw new IOException();
            AssertProtected(Encoding.UTF8.GetBytes(capture.Uri), key);
            foreach (var header in capture.Headers.Where(header => header.Key != "Authorization"))
                AssertProtected(Encoding.UTF8.GetBytes(header.Key + string.Join("\n", header.Value)), key);
            AssertProtected(capture.Body, key);
        }
        if (restored is not null) AssertProtected(restored, key);
        AssertProtected(completed, key);
        var files = Directory.EnumerateFiles(Path.Combine(stateRoot, "state"), "*", SearchOption.AllDirectories).Take(17).ToArray();
        if (files.Length is < 1 or > 16) throw new IOException();
        long total = 0;
        foreach (var file in files)
        {
            var length = new FileInfo(file).Length;
            total = checked(total + length);
            if (length > 8 * 1024 * 1024 || total > 32 * 1024 * 1024) throw new IOException();
            var bytes = File.ReadAllBytes(file);
            try { AssertProtected(bytes, key); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        return new(captures.Count, true, restored is not null, true, files.Length);
    }

    internal static void AssertProtected(ReadOnlySpan<byte> bytes, byte[] key)
    {
        if (key.Length != 32 || bytes.IndexOf(key) >= 0) throw new IOException();
        foreach (var value in Canaries.Values.Append(Convert.ToBase64String(key)).Append(Convert.ToHexString(key))
            .Append(Convert.ToHexString(key).ToLowerInvariant()))
            if (bytes.IndexOf(Encoding.UTF8.GetBytes(value)) >= 0) throw new IOException();
        // Also check the configured state canary's decoded key, not just its environment spelling.
        if (bytes.IndexOf(Convert.FromBase64String(Canaries["AGENTIC_REVIEW_R3_STATE_KEY_B64"])) >= 0) throw new IOException();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var capture in captures) CryptographicOperations.ZeroMemory(capture.Body);
            foreach (var request in replay.Requests) CryptographicOperations.ZeroMemory(request);
            captures.Clear(); replay.Requests.Clear(); replay.Dispose();
        }
        base.Dispose(disposing);
    }
}
