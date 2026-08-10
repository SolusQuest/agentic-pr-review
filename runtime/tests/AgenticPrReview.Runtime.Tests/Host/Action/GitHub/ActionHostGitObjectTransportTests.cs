using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.GitHub;

public sealed class ActionHostGitObjectTransportTests
{
    [Fact]
    public async Task ExactObjectEndpointsAreGetOnlyAndTreeIsNonRecursive()
    {
        var commitSha = new string('a', 40);
        var treeSha = new string('b', 40);
        var blobBytes = Encoding.UTF8.GetBytes("policy\n");
        var blobSha = GitBlobSha(blobBytes);
        var responses = new Queue<HttpResponseMessage>(
        [
            JsonResponse(
                $"{{\"sha\":\"{commitSha}\",\"tree\":{{\"sha\":\"{treeSha}\"}}}}"),
            JsonResponse(
                $"{{\"sha\":\"{treeSha}\",\"truncated\":false,\"tree\":[" +
                $"{{\"path\":\"config.json\",\"mode\":\"100644\"," +
                $"\"type\":\"blob\",\"sha\":\"{blobSha}\"," +
                $"\"size\":{blobBytes.Length}}}]}}"),
            JsonResponse(BlobResponse(blobBytes, blobSha)),
        ]);
        var handler = new CapturingHandler(_ => responses.Dequeue());
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var commit = await transport.GetCommitObjectAsync(
            "SolusQuest/agentic-pr-review",
            commitSha,
            CancellationToken.None);
        var tree = await transport.GetTreeObjectAsync(
            "SolusQuest/agentic-pr-review",
            treeSha,
            CancellationToken.None);
        var blob = await transport.GetBlobObjectAsync(
            "SolusQuest/agentic-pr-review",
            blobSha,
            ActionHostGitBlobReadBudget.TrustedConfig,
            CancellationToken.None);

        Assert.NotNull(commit.Value);
        Assert.NotNull(tree.Value);
        Assert.Equal(blobBytes.Length,
            Assert.Single(tree.Value!.Entries).Size);
        Assert.Equal(blobBytes, blob.Value!.Bytes);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer token-canary",
                request.Header("Authorization"));
            Assert.Equal(ActionHostGitHubAuthorizationPolicy.UserAgent,
                request.Header("User-Agent"));
            Assert.Equal(ActionHostGitHubAuthorizationPolicy.Accept,
                request.Header("Accept"));
        });
        Assert.EndsWith("/git/commits/" + commitSha,
            handler.Requests[0].Uri, StringComparison.Ordinal);
        Assert.EndsWith("/git/trees/" + treeSha,
            handler.Requests[1].Uri, StringComparison.Ordinal);
        Assert.DoesNotContain("recursive", handler.Requests[1].Uri,
            StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("/git/blobs/" + blobSha,
            handler.Requests[2].Uri, StringComparison.Ordinal);
        Assert.DoesNotContain("token-canary", transport.ToString(),
            StringComparison.Ordinal);
    }

    public static TheoryData<string, string> MissingRequiredMembers => new()
    {
        { "commit", "{\"tree\":{\"sha\":\"{sha}\"}}" },
        { "commit", "{\"sha\":\"{sha}\"}" },
        { "commit", "{\"sha\":\"{sha}\",\"tree\":{}}" },
        { "tree", "{\"sha\":\"{sha}\",\"truncated\":false,\"tree\":[{\"mode\":\"100644\",\"type\":\"blob\",\"sha\":\"{sha}\"}]}" },
        { "tree", "{\"sha\":\"{sha}\",\"truncated\":false,\"tree\":[{\"path\":\"a\",\"type\":\"blob\",\"sha\":\"{sha}\"}]}" },
        { "tree", "{\"sha\":\"{sha}\",\"truncated\":false,\"tree\":[{\"path\":\"a\",\"mode\":\"100644\",\"sha\":\"{sha}\"}]}" },
        { "tree", "{\"sha\":\"{sha}\",\"truncated\":false,\"tree\":[{\"path\":\"a\",\"mode\":\"100644\",\"type\":\"blob\"}]}" },
        { "blob", "{\"size\":0,\"encoding\":\"base64\",\"content\":\"\"}" },
        { "blob", "{\"sha\":\"{sha}\",\"encoding\":\"base64\",\"content\":\"\"}" },
        { "blob", "{\"sha\":\"{sha}\",\"size\":0,\"content\":\"\"}" },
        { "blob", "{\"sha\":\"{sha}\",\"size\":0,\"encoding\":\"base64\"}" },
    };

    [Theory]
    [MemberData(nameof(MissingRequiredMembers))]
    public async Task ExactObjectDtosRequireEveryExternalMember(
        string kind,
        string template)
    {
        var sha = kind == "blob"
            ? GitBlobSha([])
            : new string('a', 40);
        var body = template.Replace("{sha}", sha,
            StringComparison.Ordinal);
        var handler = new CapturingHandler(_ => JsonResponse(body));
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var failure = kind switch
        {
            "commit" => (await transport.GetCommitObjectAsync(
                "SolusQuest/agentic-pr-review",
                sha,
                CancellationToken.None)).Failure,
            "tree" => (await transport.GetTreeObjectAsync(
                "SolusQuest/agentic-pr-review",
                sha,
                CancellationToken.None)).Failure,
            "blob" => (await transport.GetBlobObjectAsync(
                "SolusQuest/agentic-pr-review",
                sha,
                ActionHostGitBlobReadBudget.TrustedConfig,
                CancellationToken.None)).Failure,
            _ => throw new InvalidOperationException(),
        };

        Assert.Equal(ActionHostGitObjectFailure.InvalidResponse, failure);
    }

    public static TheoryData<string> IncompleteTrees => new()
    {
        "{\"sha\":\"{sha}\",\"tree\":[]}",
        "{\"sha\":\"{sha}\",\"truncated\":null,\"tree\":[]}",
        "{\"sha\":\"{sha}\",\"truncated\":\"false\",\"tree\":[]}",
        "{\"sha\":\"{sha}\",\"truncated\":true,\"tree\":[]}",
        "{\"sha\":\"{sha}\",\"truncated\":false,\"tree\":null}",
        "{\"truncated\":false,\"tree\":[]}",
    };

    [Theory]
    [MemberData(nameof(IncompleteTrees))]
    public async Task TreeCompletenessRequiresExplicitPositiveEvidence(
        string template)
    {
        var treeSha = new string('a', 40);
        var body = template.Replace("{sha}", treeSha,
            StringComparison.Ordinal);
        var handler = new CapturingHandler(_ => JsonResponse(body));
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var result = await transport.GetTreeObjectAsync(
            "SolusQuest/agentic-pr-review",
            treeSha,
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ActionHostGitObjectFailure.InvalidResponse,
            result.Failure);
    }

    [Fact]
    public async Task ExplicitCompleteEmptyTreeIsAccepted()
    {
        var treeSha = new string('a', 40);
        var handler = new CapturingHandler(_ => JsonResponse(
            $"{{\"sha\":\"{treeSha}\",\"truncated\":false,\"tree\":[]}}"));
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var result = await transport.GetTreeObjectAsync(
            "SolusQuest/agentic-pr-review",
            treeSha,
            CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Empty(result.Value!.Entries);
    }

    [Fact]
    public async Task TreeRejectsRootMismatchAndDuplicateEntryNames()
    {
        var treeSha = new string('a', 40);
        var entrySha = new string('b', 40);
        var responses = new Queue<HttpResponseMessage>(
        [
            JsonResponse(
                $"{{\"sha\":\"{entrySha}\",\"truncated\":false,\"tree\":[]}}"),
            JsonResponse(
                $"{{\"sha\":\"{treeSha}\",\"truncated\":false,\"tree\":[" +
                $"{{\"path\":\"same\",\"mode\":\"100644\",\"type\":\"blob\",\"sha\":\"{entrySha}\"}}," +
                $"{{\"path\":\"same\",\"mode\":\"100644\",\"type\":\"blob\",\"sha\":\"{entrySha}\"}}]}}"),
        ]);
        var handler = new CapturingHandler(_ => responses.Dequeue());
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var mismatched = await transport.GetTreeObjectAsync(
            "SolusQuest/agentic-pr-review",
            treeSha,
            CancellationToken.None);
        var duplicate = await transport.GetTreeObjectAsync(
            "SolusQuest/agentic-pr-review",
            treeSha,
            CancellationToken.None);

        Assert.Equal(ActionHostGitObjectFailure.InvalidResponse,
            mismatched.Failure);
        Assert.Equal(ActionHostGitObjectFailure.InvalidResponse,
            duplicate.Failure);
    }

    public static TheoryData<string> InvalidBase64 => new()
    {
        "YWJj ZA==",
        "YWJj\tZA==",
        "YWJj_ZA=",
        "YWJjZA",
        "YW\rJjZA==",
        "\nYWJjZA==",
        "\r\nYWJjZA==",
    };

    [Theory]
    [MemberData(nameof(InvalidBase64))]
    public async Task BlobRejectsNonCanonicalBase64(string content)
    {
        var sha = new string('a', 40);
        var handler = new CapturingHandler(_ => JsonResponse($$"""
            {"sha":"{{sha}}","size":4,"encoding":"base64","content":{{Json(content)}}}
            """));
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var result = await transport.GetBlobObjectAsync(
            "SolusQuest/agentic-pr-review",
            sha,
            ActionHostGitBlobReadBudget.TrustedConfig,
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ActionHostGitObjectFailure.InvalidResponse,
            result.Failure);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task BlobAcceptsCanonicalWrappedBase64(string newline)
    {
        var bytes = Encoding.UTF8.GetBytes("wrapped-policy-bytes");
        var sha = GitBlobSha(bytes);
        var encoded = Convert.ToBase64String(bytes);
        var wrapped = string.Join(newline,
            Enumerable.Range(0, encoded.Length / 4)
                .Select(index => encoded.Substring(index * 4, 4)));
        var handler = new CapturingHandler(_ => JsonResponse(
            BlobResponse(bytes, sha, wrapped)));
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var result = await transport.GetBlobObjectAsync(
            "SolusQuest/agentic-pr-review",
            sha,
            ActionHostGitBlobReadBudget.TrustedConfig,
            CancellationToken.None);

        Assert.Equal(bytes, result.Value!.Bytes);
    }

    [Fact]
    public async Task BlobDecodedBudgetIsInclusiveAndRejectsPlusOne()
    {
        var acceptedBytes = new byte[16 * 1024];
        Array.Fill<byte>(acceptedBytes, 0x61);
        var rejectedBytes = new byte[acceptedBytes.Length + 1];
        Array.Fill<byte>(rejectedBytes, 0x62);
        var responses = new Queue<HttpResponseMessage>(
        [
            JsonResponse(BlobResponse(
                acceptedBytes,
                GitBlobSha(acceptedBytes))),
            JsonResponse(BlobResponse(
                rejectedBytes,
                GitBlobSha(rejectedBytes))),
        ]);
        var handler = new CapturingHandler(_ => responses.Dequeue());
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var accepted = await transport.GetBlobObjectAsync(
            "SolusQuest/agentic-pr-review",
            GitBlobSha(acceptedBytes),
            ActionHostGitBlobReadBudget.TrustedConfig,
            CancellationToken.None);
        var rejected = await transport.GetBlobObjectAsync(
            "SolusQuest/agentic-pr-review",
            GitBlobSha(rejectedBytes),
            ActionHostGitBlobReadBudget.TrustedConfig,
            CancellationToken.None);

        Assert.Equal(acceptedBytes, accepted.Value!.Bytes);
        Assert.Null(rejected.Value);
        Assert.Equal(ActionHostGitObjectFailure.InvalidResponse,
            rejected.Failure);
    }

    [Fact]
    public async Task InstructionsDecodedBudgetIsInclusiveAndRejectsPlusOne()
    {
        var acceptedBytes = Enumerable.Repeat((byte)0x63, 64 * 1024)
            .ToArray();
        var rejectedBytes = Enumerable.Repeat(
            (byte)0x64,
            acceptedBytes.Length + 1).ToArray();
        var responses = new Queue<HttpResponseMessage>(
        [
            JsonResponse(BlobResponse(
                acceptedBytes,
                GitBlobSha(acceptedBytes))),
            JsonResponse(BlobResponse(
                rejectedBytes,
                GitBlobSha(rejectedBytes))),
        ]);
        var handler = new CapturingHandler(_ => responses.Dequeue());
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var accepted = await transport.GetBlobObjectAsync(
            "SolusQuest/agentic-pr-review",
            GitBlobSha(acceptedBytes),
            ActionHostGitBlobReadBudget.TrustedInstructions,
            CancellationToken.None);
        var rejected = await transport.GetBlobObjectAsync(
            "SolusQuest/agentic-pr-review",
            GitBlobSha(rejectedBytes),
            ActionHostGitBlobReadBudget.TrustedInstructions,
            CancellationToken.None);

        Assert.Equal(acceptedBytes, accepted.Value!.Bytes);
        Assert.Equal(ActionHostGitObjectFailure.InvalidResponse,
            rejected.Failure);
    }

    [Fact]
    public async Task SharedEnvelopeCarriesOneMiBDecodedBlob()
    {
        var bytes = new byte[1024 * 1024];
        Array.Fill<byte>(bytes, 0x5a);
        var sha = GitBlobSha(bytes);
        var handler = new CapturingHandler(_ => JsonResponse(
            BlobResponse(bytes, sha)));
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var result = await transport.GetBlobObjectAsync(
            "SolusQuest/agentic-pr-review",
            sha,
            ActionHostGitBlobReadBudget.MaximumSupported,
            CancellationToken.None);

        Assert.Equal(bytes.Length, result.Value!.Bytes.Length);
        Assert.InRange(result.CapturedResponseBytes,
            bytes.Length,
            ActionHostGitBlobReadBudget.MaximumSupported
                .MaximumResponseBytes);
    }

    [Fact]
    public async Task EncodedCapDoesNotChargeCanonicalLineBreaks()
    {
        var bytes = new byte[930 * 1024];
        Array.Fill<byte>(bytes, 0x5b);
        var sha = GitBlobSha(bytes);
        var encoded = Convert.ToBase64String(bytes);
        var wrapped = WrapEveryQuantum(encoded);
        Assert.True(wrapped.Length >
            ActionHostGitBlobReadBudget.MaximumSupported
                .MaximumEncodedCharacters);
        var handler = new CapturingHandler(_ => JsonResponse(
            BlobResponse(bytes, sha, wrapped)));
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var result = await transport.GetBlobObjectAsync(
            "SolusQuest/agentic-pr-review",
            sha,
            ActionHostGitBlobReadBudget.MaximumSupported,
            CancellationToken.None);

        Assert.Equal(bytes, result.Value!.Bytes);
        Assert.InRange(result.CapturedResponseBytes,
            bytes.Length,
            ActionHostGitBlobReadBudget.MaximumSupported
                .MaximumResponseBytes);
    }

    [Fact]
    public async Task ResponseCapFailsBeforeCompleteRetention()
    {
        var handler = new CapturingHandler(_ => JsonResponse(
            new string(' ',
                ActionHostGitBlobReadBudget.TrustedConfig
                    .MaximumResponseBytes + 1)));
        using var transport = ActionHostGitObjectTransport.CreateForTesting(
            "token-canary",
            handler);

        var result = await transport.GetBlobObjectAsync(
            "SolusQuest/agentic-pr-review",
            new string('a', 40),
            ActionHostGitBlobReadBudget.TrustedConfig,
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ActionHostGitObjectFailure.ResponseTooLarge,
            result.Failure);
    }

    private static HttpResponseMessage JsonResponse(string body) => new(
        HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string BlobResponse(
        byte[] bytes,
        string sha,
        string? content = null) => $$"""
        {"sha":"{{sha}}","size":{{bytes.Length}},"encoding":"base64","content":{{Json(content ?? Convert.ToBase64String(bytes))}}}
        """;

    private static string Json(string value) =>
        "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string WrapEveryQuantum(string encoded)
    {
        var wrapped = new StringBuilder(
            encoded.Length + encoded.Length / 4);
        for (var index = 0; index < encoded.Length; index += 4)
        {
            if (index > 0)
            {
                wrapped.Append('\n');
            }

            wrapped.Append(encoded, index, 4);
        }

        return wrapped.ToString();
    }

    private static string GitBlobSha(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes(
            "blob " + bytes.Length.ToString(CultureInfo.InvariantCulture) +
            "\0");
        return Convert.ToHexStringLower(
            SHA1.HashData([.. header, .. bytes]));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _reply;

        internal CapturingHandler(
            Func<HttpRequestMessage, HttpResponseMessage> reply)
        {
            _reply = reply;
        }

        internal List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(CapturedRequest.From(request));
            return Task.FromResult(_reply(request));
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Uri,
        IReadOnlyDictionary<string, string> Headers)
    {
        internal string Header(string name) => Headers[name];

        internal static CapturedRequest From(HttpRequestMessage request) =>
            new(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.ToDictionary(
                    header => header.Key,
                    header => string.Join(' ', header.Value),
                    StringComparer.OrdinalIgnoreCase));
    }
}
