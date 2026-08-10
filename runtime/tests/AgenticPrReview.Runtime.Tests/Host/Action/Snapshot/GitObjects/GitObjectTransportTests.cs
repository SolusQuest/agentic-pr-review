using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.ActionHost.Snapshot.GitObjects;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Snapshot.GitObjects;

public sealed class GitObjectTransportTests
{
    [Fact]
    public async Task ExactCommitAndNonRecursiveTreeRequestsUseFrozenAuthority()
    {
        var invocation = await AuthorizedInvocation();
        var treeSha = new string('1', 40);
        var handler = new CapturingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path.EndsWith(
                "/git/commits/" + ActionHostAuthorizationScenario.HeadSha,
                StringComparison.Ordinal)
                ? JsonResponse($$"""
                    {
                      "sha": "{{ActionHostAuthorizationScenario.HeadSha}}",
                      "tree": { "sha": "{{treeSha}}" },
                      "forward_compatible": true
                    }
                    """)
                : JsonResponse($$"""
                    {
                      "sha": "{{treeSha}}",
                      "truncated": false,
                      "tree": []
                    }
                    """);
        });
        using var transport = ReviewedGitObjectTransport.CreateForTesting(
            invocation,
            Token(),
            ProductionBudget(),
            handler);

        var commit = await transport.GetCommitAsync(CancellationToken.None);
        var tree = await transport.GetTreeAsync(
            treeSha,
            CancellationToken.None);

        Assert.Equal(treeSha, commit.Value!.TreeSha);
        Assert.Empty(tree.Value!.Entries);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.github.com", request.Origin);
            Assert.Equal(string.Empty, request.Query);
            Assert.Equal("Bearer token-canary",
                request.Header("Authorization"));
            Assert.Equal("2026-03-10",
                request.Header("X-GitHub-Api-Version"));
        });
        Assert.DoesNotContain(
            "recursive",
            handler.Requests[1].Uri,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RawBlobStreamsToStageAndAcceptsVariableContentType()
    {
        var invocation = await AuthorizedInvocation();
        var bytes = "text\0and-binary"u8.ToArray();
        var sha = GitBlobSha(bytes);
        var handler = new CapturingHandler(_ => new HttpResponseMessage(
            HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
            {
                Headers =
                {
                    ContentType = new MediaTypeHeaderValue("text/plain")
                    {
                        CharSet = "utf-8",
                    },
                },
            },
        });
        using var transport = ReviewedGitObjectTransport.CreateForTesting(
            invocation,
            Token(),
            ProductionBudget(),
            handler);
        var parent = CreateTemporaryDirectory();
        try
        {
            var staging = Assert.IsType<ReviewedBlobStagingLease>(
                ReviewedBlobStagingLease.TryCreate(parent));
            var result = await transport.StageBlobAsync(
                sha,
                bytes.Length,
                staging,
                CancellationToken.None);

            var staged = Assert.IsType<ReviewedStagedBlob>(result.Value);
            using var copied = new MemoryStream();
            Assert.True(await staged.CopyVerifiedToAsync(
                copied,
                CancellationToken.None));
            Assert.Equal(bytes, copied.ToArray());
            Assert.Equal("application/vnd.github.raw+json",
                Assert.Single(handler.Requests).Header("Accept"));
            Assert.True(staging.Cleanup());
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task MissingTruncatedAndDuplicatePropertiesFailClosed()
    {
        var invocation = await AuthorizedInvocation();
        var sha = new string('2', 40);
        foreach (var body in new[]
        {
            $$"""{"sha":"{{sha}}","tree":[]}""",
            $$"""{"sha":"{{sha}}","truncated":false,"truncated":false,"tree":[]}""",
            $$"""{"sha":"{{sha}}","truncated":true,"tree":[]}""",
        })
        {
            using var transport = ReviewedGitObjectTransport.CreateForTesting(
                invocation,
                Token(),
                ProductionBudget(),
                new CapturingHandler(_ => JsonResponse(body)));
            var result = await transport.GetTreeAsync(
                sha,
                CancellationToken.None);
            Assert.Null(result.Value);
            Assert.Equal(ReviewedGitObjectFailure.InvalidResponse,
                result.Failure);
        }
    }

    [Fact]
    public async Task CommitAndTreeResponsesMustMatchTheRequestedObjectSha()
    {
        var invocation = await AuthorizedInvocation();
        var treeSha = new string('4', 40);
        using (var commitTransport =
               ReviewedGitObjectTransport.CreateForTesting(
                   invocation,
                   Token(),
                   ProductionBudget(),
                   new CapturingHandler(_ => JsonResponse($$"""
                       {
                         "sha": "{{new string('5', 40)}}",
                         "tree": { "sha": "{{treeSha}}" }
                       }
                       """))))
        {
            var commit = await commitTransport.GetCommitAsync(
                CancellationToken.None);
            Assert.Null(commit.Value);
            Assert.Equal(ReviewedGitObjectFailure.InvalidResponse,
                commit.Failure);
        }

        using var treeTransport =
            ReviewedGitObjectTransport.CreateForTesting(
                invocation,
                Token(),
                ProductionBudget(),
                new CapturingHandler(_ => JsonResponse($$"""
                    {
                      "sha": "{{new string('6', 40)}}",
                      "truncated": false,
                      "tree": []
                    }
                    """)));
        var tree = await treeTransport.GetTreeAsync(
            treeSha,
            CancellationToken.None);
        Assert.Null(tree.Value);
        Assert.Equal(ReviewedGitObjectFailure.InvalidResponse, tree.Failure);
    }

    [Fact]
    public async Task ObjectResponseByteBoundaryAcceptsCapAndRejectsCapPlusOne()
    {
        var invocation = await AuthorizedInvocation();
        var treeSha = new string('7', 40);
        var json = Encoding.UTF8.GetBytes($$$"""
            {"sha":"{{{ActionHostAuthorizationScenario.HeadSha}}}","tree":{"sha":"{{{treeSha}}}"}}
            """);
        var atCap = new byte[ReviewedContentLimits.GitObjectResponseBytes];
        json.CopyTo(atCap, 0);
        Array.Fill(atCap, (byte)' ', json.Length,
            atCap.Length - json.Length);
        using (var accepted = ReviewedGitObjectTransport.CreateForTesting(
                   invocation,
                   Token(),
                   ProductionBudget(),
                   new CapturingHandler(_ => JsonBytesResponse(atCap))))
        {
            var result = await accepted.GetCommitAsync(
                CancellationToken.None);
            Assert.NotNull(result.Value);
        }

        var overCap = new byte[ReviewedContentLimits.GitObjectResponseBytes + 1];
        using var rejected = ReviewedGitObjectTransport.CreateForTesting(
            invocation,
            Token(),
            ProductionBudget(),
            new CapturingHandler(_ => JsonBytesResponse(overCap)));
        var overflow = await rejected.GetCommitAsync(CancellationToken.None);
        Assert.Null(overflow.Value);
        Assert.Equal(ReviewedGitObjectFailure.UnsupportedSize,
            overflow.Failure);
    }

    [Fact]
    public async Task RequestOverflowIsRejectedBeforeASecondSend()
    {
        var invocation = await AuthorizedInvocation();
        var treeSha = new string('3', 40);
        var handler = new CapturingHandler(_ => JsonResponse($$"""
            {
              "sha": "{{ActionHostAuthorizationScenario.HeadSha}}",
              "tree": { "sha": "{{treeSha}}" }
            }
            """));
        var budget = ReviewedContentBudget.Create(
            new ReviewedContentLimitProfile(
                1,
                ReviewedContentLimits.GitObjectResponseBytes,
                ReviewedContentLimits.AggregateResponseBytes,
                TimeSpan.FromMinutes(5)),
            TimeProvider.System);
        using var transport = ReviewedGitObjectTransport.CreateForTesting(
            invocation,
            Token(),
            budget,
            handler);

        Assert.NotNull((await transport.GetCommitAsync(
            CancellationToken.None)).Value);
        var overflow = await transport.GetTreeAsync(
            treeSha,
            CancellationToken.None);

        Assert.Equal(ReviewedGitObjectFailure.UnsupportedSize,
            overflow.Failure);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RateLimitIsNotRetriedAndReturnsNoValue()
    {
        var invocation = await AuthorizedInvocation();
        var handler = new CapturingHandler(_ => new HttpResponseMessage(
            (HttpStatusCode)429));
        using var transport = ReviewedGitObjectTransport.CreateForTesting(
            invocation,
            Token(),
            ProductionBudget(),
            handler);

        var result = await transport.GetCommitAsync(CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ReviewedGitObjectFailure.RateLimited, result.Failure);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void SourceGeneratedJsonRootsExistWithoutReflectionFallback()
    {
        Assert.NotNull(ReviewedGitObjectJsonContext.Default
            .ReviewedGitCommitDocument);
        Assert.NotNull(ReviewedGitObjectJsonContext.Default
            .ReviewedGitTreeDocument);
        Assert.False(ReviewedGitObjectJsonContext.Default.Options
            .PropertyNameCaseInsensitive);
        Assert.False(ReviewedGitObjectJsonContext.Default.Options
            .AllowDuplicateProperties);
    }

    private static async Task<ActionHostAuthorizer.AuthorizedInvocation>
        AuthorizedInvocation()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var result = await scenario.CreateAuthorizer().AuthorizeAsync(
            scenario.Launch,
            CancellationToken.None);
        return Assert.IsType<ActionHostAuthorizer.AuthorizedInvocation>(
            result.Invocation);
    }

    private static ActionHostGitHubToken Token()
    {
        Assert.True(ActionHostGitHubToken.TryCreate(
            "token-canary",
            out var token));
        return token!;
    }

    private static ReviewedContentBudget ProductionBudget() =>
        ReviewedContentBudget.Create(
            ReviewedContentLimits.Production,
            TimeProvider.System);

    private static HttpResponseMessage JsonResponse(string body) => new(
        HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage JsonBytesResponse(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/json")
        {
            CharSet = "utf-8",
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        };
    }

    private static string GitBlobSha(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes(
            "blob " + bytes.Length.ToString(CultureInfo.InvariantCulture) +
            "\0");
        return Convert.ToHexString(SHA1.HashData([.. header, .. bytes]))
            .ToLowerInvariant();
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "apr-h4-transport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

        internal CapturingHandler(
            Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _response = response;
        }

        internal List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(CapturedRequest.From(request));
            return Task.FromResult(_response(request));
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Uri,
        string Origin,
        string Query,
        IReadOnlyDictionary<string, string> Headers)
    {
        internal string Header(string name) => Headers[name];

        internal static CapturedRequest From(HttpRequestMessage request) => new(
            request.Method,
            request.RequestUri!.AbsoluteUri,
            request.RequestUri.GetLeftPart(UriPartial.Authority),
            request.RequestUri.Query,
            request.Headers.ToDictionary(
                static header => header.Key,
                static header => string.Join(' ', header.Value),
                StringComparer.OrdinalIgnoreCase));
    }
}
