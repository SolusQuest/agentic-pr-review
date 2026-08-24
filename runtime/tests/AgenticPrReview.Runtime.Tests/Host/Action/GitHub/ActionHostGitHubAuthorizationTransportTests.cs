using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.GitHub;

public sealed class ActionHostGitHubAuthorizationTransportTests
{
    [Fact]
    public async Task RepositoryReadUsesExactOriginPathAndHeaders()
    {
        var handler = new CapturingHandler(_ => JsonResponse("""
            {
              "id": 42,
              "full_name": "SolusQuest/agentic-pr-review",
              "default_branch": "main"
            }
            """));
        using var transport =
            ActionHostGitHubAuthorizationTransport.CreateForTesting(
                "token-canary",
                handler);

        var result = await transport.GetRepositoryAsync(
            "SolusQuest/agentic-pr-review",
            CancellationToken.None);

        Assert.NotNull(result.Value);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            "https://api.github.com/repos/SolusQuest/agentic-pr-review",
            request.Uri);
        Assert.Equal("Bearer token-canary", request.Header("Authorization"));
        Assert.Equal(ActionHostGitHubAuthorizationPolicy.UserAgent,
            request.Header("User-Agent"));
        Assert.Equal(ActionHostGitHubAuthorizationPolicy.Accept,
            request.Header("Accept"));
        Assert.Equal(ActionHostGitHubAuthorizationPolicy.ApiVersion,
            request.Header("X-GitHub-Api-Version"));
        Assert.Equal(4, request.Headers.Count);
    }

    [Fact]
    public async Task ContentsReadAcceptsGitHubWrappedCanonicalBase64()
    {
        var workflow = Encoding.UTF8.GetBytes(
            "name: proof\npermissions: {}\n");
        var unwrapped = Convert.ToBase64String(workflow);
        var wrapped = string.Join(
            "\n",
            Enumerable.Range(0, (unwrapped.Length + 7) / 8)
                .Select(index => unwrapped.Substring(
                    index * 8,
                    Math.Min(8, unwrapped.Length - index * 8)))) + "\n";
        var body = ContentResponse(
            wrapped,
            workflow.Length,
            GitBlobSha(workflow));
        var handler = new CapturingHandler(_ => JsonResponse(body));
        using var transport =
            ActionHostGitHubAuthorizationTransport.CreateForTesting(
                "token-canary",
                handler);

        var result = await transport.GetWorkflowSourceAsync(
            "SolusQuest/agentic-pr-review",
            ".github/workflows/r4-trusted-proof.yml",
            new string('a', 40),
            CancellationToken.None);

        Assert.Equal(workflow, result.Value!.Bytes);
        Assert.EndsWith(
            "/contents/.github/workflows/r4-trusted-proof.yml?ref=" +
                new string('a', 40),
            handler.Requests[0].Uri,
            StringComparison.Ordinal);
    }

    public static TheoryData<string> InvalidBase64 => new()
    {
        "YWJj ZA==",
        "YWJj\tZA==",
        "YWJj_ZA=",
        "YWJjZA",
        "YW\rJjZA==",
    };

    [Theory]
    [MemberData(nameof(InvalidBase64))]
    public async Task ContentsReadRejectsNonCanonicalBase64(string content)
    {
        var handler = new CapturingHandler(_ => JsonResponse(
            ContentResponse(content, 4, new string('a', 40))));
        using var transport =
            ActionHostGitHubAuthorizationTransport.CreateForTesting(
                "token-canary",
                handler);

        var result = await transport.GetWorkflowSourceAsync(
            "SolusQuest/agentic-pr-review",
            ".github/workflows/r4-trusted-proof.yml",
            new string('b', 40),
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ActionHostGitHubFailure.InvalidResponse, result.Failure);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ContentsReadRejectsSizeOrBlobIdentityMismatch(
        bool wrongSize,
        bool wrongBlob)
    {
        var workflow = Encoding.UTF8.GetBytes("permissions: {}\n");
        var content = Convert.ToBase64String(workflow);
        var handler = new CapturingHandler(_ => JsonResponse(ContentResponse(
            content,
            wrongSize ? workflow.Length + 1 : workflow.Length,
            wrongBlob ? new string('a', 40) : GitBlobSha(workflow))));
        using var transport =
            ActionHostGitHubAuthorizationTransport.CreateForTesting(
                "token-canary",
                handler);

        var result = await transport.GetWorkflowSourceAsync(
            "SolusQuest/agentic-pr-review",
            ".github/workflows/r4-trusted-proof.yml",
            new string('b', 40),
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ActionHostGitHubFailure.InvalidResponse, result.Failure);
    }

    [Fact]
    public async Task ForbiddenResponseFailsWithoutRetry()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(
            HttpStatusCode.Forbidden));
        using var transport =
            ActionHostGitHubAuthorizationTransport.CreateForTesting(
                "token-canary",
                handler);

        var result = await transport.GetRepositoryAsync(
            "SolusQuest/agentic-pr-review",
            CancellationToken.None);

        Assert.Equal(ActionHostGitHubFailure.Forbidden, result.Failure);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CommitAssociationUsesDocumentedPaginatedEndpoint()
    {
        var handler = new CapturingHandler(_ => JsonResponse("""
            [
              {
                "id": 1000,
                "number": 147,
                "state": "open",
                "draft": false,
                "merged_at": null,
                "base": {
                  "ref": "main",
                  "sha": "dddddddddddddddddddddddddddddddddddddddd",
                  "repo": {
                    "id": 42,
                    "full_name": "SolusQuest/agentic-pr-review"
                  }
                },
                "head": {
                  "sha": "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                  "repo": {
                    "id": 42,
                    "full_name": "SolusQuest/agentic-pr-review"
                  }
                }
              }
            ]
            """));
        using var transport =
            ActionHostGitHubAuthorizationTransport.CreateForTesting(
                "token-canary",
                handler);

        var result = await transport.GetCommitPullRequestsAsync(
            "SolusQuest/agentic-pr-review",
            new string('c', 40),
            2,
            100,
            CancellationToken.None);

        Assert.True(result.Value!.IsComplete);
        Assert.Equal(147, Assert.Single(result.Value.PullRequests).Number);
        Assert.EndsWith(
            "/commits/" + new string('c', 40) +
                "/pulls?per_page=100&page=2",
            handler.Requests[0].Uri,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExactAttemptReadAcceptsThePinnedBareWorkflowPathShape()
    {
        var handler = new CapturingHandler(_ => JsonResponse("""
            {
              "id": 900,
              "run_attempt": 2,
              "workflow_id": 72,
              "name": "R4 trusted proof",
              "path": ".github/workflows/r4-trusted-proof.yml",
              "head_branch": "main",
              "head_sha": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "event": "workflow_dispatch",
              "conclusion": null,
              "repository": {
                "id": 42,
                "full_name": "SolusQuest/agentic-pr-review"
              },
              "head_repository": {
                "id": 42,
                "full_name": "SolusQuest/agentic-pr-review"
              },
              "actor": { "id": 7, "login": "maintainer" },
              "triggering_actor": { "id": 7, "login": "maintainer" },
              "pull_requests": []
            }
            """));
        using var transport =
            ActionHostGitHubAuthorizationTransport.CreateForTesting(
                "token-canary",
                handler);

        var result = await transport.GetWorkflowRunAttemptAsync(
            "SolusQuest/agentic-pr-review",
            900,
            2,
            CancellationToken.None);

        Assert.Equal(".github/workflows/r4-trusted-proof.yml",
            result.Value!.Path);
        Assert.Empty(result.Value.PullRequests);
        Assert.EndsWith(
            "/actions/runs/900/attempts/2?exclude_pull_requests=false",
            handler.Requests[0].Uri,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExactAttemptReadAcceptsRealInlineRepositoryReferences()
    {
        var handler = new CapturingHandler(_ => JsonResponse("""
            {
              "id": 800,
              "run_attempt": 1,
              "workflow_id": 71,
              "name": "CI",
              "path": ".github/workflows/ci.yml",
              "head_branch": "feature",
              "head_sha": "cccccccccccccccccccccccccccccccccccccccc",
              "event": "pull_request",
              "conclusion": "success",
              "repository": {
                "id": 42,
                "full_name": "SolusQuest/agentic-pr-review"
              },
              "head_repository": {
                "id": 42,
                "full_name": "SolusQuest/agentic-pr-review"
              },
              "actor": { "id": 7, "login": "maintainer" },
              "triggering_actor": { "id": 7, "login": "maintainer" },
              "pull_requests": [
                {
                  "id": 1000,
                  "number": 147,
                  "base": {
                    "sha": "dddddddddddddddddddddddddddddddddddddddd",
                    "repo": {
                      "id": 42,
                      "url": "https://api.github.com/repos/SolusQuest/agentic-pr-review",
                      "name": "agentic-pr-review"
                    }
                  },
                  "head": {
                    "sha": "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                    "repo": {
                      "id": 42,
                      "url": "https://api.github.com/repos/SolusQuest/agentic-pr-review",
                      "name": "agentic-pr-review"
                    }
                  }
                }
              ]
            }
            """));
        using var transport =
            ActionHostGitHubAuthorizationTransport.CreateForTesting(
                "token-canary",
                handler);

        var result = await transport.GetWorkflowRunAttemptAsync(
            "SolusQuest/agentic-pr-review",
            800,
            1,
            CancellationToken.None);

        var reference = Assert.Single(result.Value!.PullRequests);
        Assert.Equal(42, reference.BaseRepository.Id);
        Assert.Equal("agentic-pr-review", reference.BaseRepository.Name);
        Assert.Equal(42, reference.HeadRepository.Id);
        Assert.Equal("agentic-pr-review", reference.HeadRepository.Name);
    }

    [Fact]
    public async Task CollaboratorPermissionUsesOneFixedReadEndpoint()
    {
        var handler = new CapturingHandler(_ => JsonResponse(
            "{\"permission\":\"admin\"}"));
        using var transport =
            ActionHostGitHubAuthorizationTransport.CreateForTesting(
                "token-canary",
                handler);

        var result = await transport.GetCollaboratorPermissionAsync(
            "SolusQuest/agentic-pr-review",
            "maintainer",
            CancellationToken.None);

        Assert.Equal("admin", result.Value!.Permission);
        Assert.EndsWith(
            "/collaborators/maintainer/permission",
            handler.Requests[0].Uri,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonJsonAndOversizedResponsesFailClosed()
    {
        var textHandler = new CapturingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "text/plain"),
            };
            return response;
        });
        using (var transport =
            ActionHostGitHubAuthorizationTransport.CreateForTesting(
                "token-canary",
                textHandler))
        {
            var result = await transport.GetRepositoryAsync(
                "SolusQuest/agentic-pr-review",
                CancellationToken.None);
            Assert.Equal(ActionHostGitHubFailure.InvalidResponse,
                result.Failure);
        }

        var oversizedHandler = new CapturingHandler(_ => JsonResponse(
            new string('x',
                ActionHostGitHubAuthorizationPolicy.MaximumResponseBytes + 1)));
        using var oversizedTransport =
            ActionHostGitHubAuthorizationTransport.CreateForTesting(
                "token-canary",
                oversizedHandler);
        var oversized = await oversizedTransport.GetRepositoryAsync(
            "SolusQuest/agentic-pr-review",
            CancellationToken.None);
        Assert.Equal(ActionHostGitHubFailure.ResponseTooLarge,
            oversized.Failure);
    }

    [Fact]
    public async Task RequestBudgetIsClosedAndDoesNotSendTheOverflowRead()
    {
        var handler = new CapturingHandler(_ => JsonResponse("""
            {
              "id": 42,
              "full_name": "SolusQuest/agentic-pr-review",
              "default_branch": "main"
            }
            """));
        using var transport =
            ActionHostGitHubAuthorizationTransport.CreateForTesting(
                "token-canary",
                handler);
        ActionHostGitHubResult<ActionHostGitHubRepositoryFact>? result = null;
        for (var index = 0;
            index <= ActionHostGitHubAuthorizationPolicy.MaximumRequests;
            index++)
        {
            result = await transport.GetRepositoryAsync(
                "SolusQuest/agentic-pr-review",
                CancellationToken.None);
        }

        Assert.Equal(ActionHostGitHubFailure.RequestLimitExceeded,
            result!.Failure);
        Assert.Equal(ActionHostGitHubAuthorizationPolicy.MaximumRequests,
            handler.Requests.Count);
    }

    public static TheoryData<string> InvalidHeaderCredentialSuffixes => new()
    {
        "\rX-Injected:value",
        "\nX-Injected:value",
        "\0suffix",
        "\tvalue",
        " value",
        "\u0085value",
        "é",
    };

    [Theory]
    [MemberData(nameof(InvalidHeaderCredentialSuffixes))]
    public void InvalidHeaderCredentialsFailBeforeAnyRequest(string suffix)
    {
        const string canary = "github-token-canary";
        var handler = new CapturingHandler(_ => JsonResponse("{}"));

        var exception = Assert.Throws<ActionHostGitHubCredentialException>(() =>
            ActionHostGitHubAuthorizationTransport.CreateForTesting(
                canary + suffix,
                handler));

        Assert.Empty(handler.Requests);
        Assert.DoesNotContain(canary, exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task VisibleCommandShapedCredentialUsesTypedBearerHeader()
    {
        const string token = "github-token-$(whoami)";
        var handler = new CapturingHandler(_ => JsonResponse("""
            {
              "id": 42,
              "full_name": "SolusQuest/agentic-pr-review",
              "default_branch": "main"
            }
            """));
        using var transport =
            ActionHostGitHubAuthorizationTransport.CreateForTesting(
                token,
                handler);

        var result = await transport.GetRepositoryAsync(
            "SolusQuest/agentic-pr-review",
            CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Equal("Bearer " + token,
            Assert.Single(handler.Requests).Header("Authorization"));
        Assert.DoesNotContain(token, transport.ToString(),
            StringComparison.Ordinal);
    }

    public static TheoryData<string> IncompleteEligibilityMembers => new()
    {
        "\"merged_at\": null,",
        "\"draft\": null,\n  \"merged_at\": null,",
        "\"draft\": false,",
        "\"draft\": false,\n  \"merged_at\": false,",
    };

    [Theory]
    [MemberData(nameof(IncompleteEligibilityMembers))]
    public async Task PullRequestEligibilityRequiresPresentTypedFields(
        string eligibilityMembers)
    {
        var handler = new CapturingHandler(_ => JsonResponse(
            PullRequestResponse(eligibilityMembers)));
        using var transport =
            ActionHostGitHubAuthorizationTransport.CreateForTesting(
                "token-canary",
                handler);

        var result = await transport.GetPullRequestAsync(
            "SolusQuest/agentic-pr-review",
            147,
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ActionHostGitHubFailure.InvalidResponse, result.Failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\n    \"repo\": null")]
    public async Task PullRequestReadRejectsMissingOrNullHeadRepository(
        string headRepositorySuffix)
    {
        var handler = new CapturingHandler(_ => JsonResponse(
            PullRequestResponse(
                "\"draft\": false,\n  \"merged_at\": null,",
                headRepositorySuffix)));
        using var transport =
            ActionHostGitHubAuthorizationTransport.CreateForTesting(
                "token-canary",
                handler);

        var result = await transport.GetPullRequestAsync(
            "SolusQuest/agentic-pr-review",
            147,
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ActionHostGitHubFailure.InvalidResponse, result.Failure);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PullRequestReadRejectsMissingBaseReference()
    {
        var body = PullRequestResponse(
            "\"draft\": false,\n  \"merged_at\": null,")
            .Replace("    \"ref\": \"main\",\n", string.Empty,
                StringComparison.Ordinal);
        var handler = new CapturingHandler(_ => JsonResponse(body));
        using var transport =
            ActionHostGitHubAuthorizationTransport.CreateForTesting(
                "token-canary",
                handler);

        var result = await transport.GetPullRequestAsync(
            "SolusQuest/agentic-pr-review",
            147,
            CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(ActionHostGitHubFailure.InvalidResponse, result.Failure);
    }

    public static TheoryData<string, bool> CompleteMergeStatusMembers => new()
    {
        { "null", false },
        { "\"2026-08-10T00:00:00Z\"", true },
    };

    [Theory]
    [MemberData(nameof(CompleteMergeStatusMembers))]
    public async Task PullRequestEligibilityAcceptsExplicitMergeStatus(
        string mergedAt,
        bool isMerged)
    {
        var handler = new CapturingHandler(_ => JsonResponse(
            PullRequestResponse(
                "\"draft\": false,\n  \"merged_at\": " + mergedAt + ",")));
        using var transport =
            ActionHostGitHubAuthorizationTransport.CreateForTesting(
                "token-canary",
                handler);

        var result = await transport.GetPullRequestAsync(
            "SolusQuest/agentic-pr-review",
            147,
            CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Equal(isMerged, result.Value!.MergedAt is not null);
    }

    private static HttpResponseMessage JsonResponse(string body) => new(
        HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string ContentResponse(
        string content,
        int size,
        string blobSha) => $$"""
        {
          "type": "file",
          "encoding": "base64",
          "size": {{size}},
          "name": "r4-trusted-proof.yml",
          "path": ".github/workflows/r4-trusted-proof.yml",
          "sha": "{{blobSha}}",
          "content": "{{content.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal)}}"
        }
        """;

    private static string PullRequestResponse(
        string eligibilityMembers,
        string? headRepositorySuffix = null)
    {
        headRepositorySuffix ??= """
            ,
                "repo": {
                  "id": 42,
                  "full_name": "SolusQuest/agentic-pr-review"
                }
            """;
        return $$"""
        {
          "id": 1000,
          "number": 147,
          "state": "open",
          {{eligibilityMembers}}
          "base": {
            "ref": "main",
            "sha": "dddddddddddddddddddddddddddddddddddddddd",
            "repo": {
              "id": 42,
              "full_name": "SolusQuest/agentic-pr-review"
            }
          },
          "head": {
            "sha": "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"{{headRepositorySuffix}}
          }
        }
        """;
    }

    private static string GitBlobSha(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes(
            "blob " + bytes.Length.ToString(CultureInfo.InvariantCulture) +
            "\0");
        return Convert.ToHexString(SHA1.HashData([.. header, .. bytes]))
            .ToLowerInvariant();
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
        IReadOnlyDictionary<string, string> Headers)
    {
        internal string Header(string name) => Headers[name];

        internal static CapturedRequest From(HttpRequestMessage request) => new(
            request.Method,
            request.RequestUri!.AbsoluteUri,
            request.Headers.ToDictionary(
                header => header.Key,
                header => string.Join(' ', header.Value),
                StringComparer.OrdinalIgnoreCase));
    }
}
