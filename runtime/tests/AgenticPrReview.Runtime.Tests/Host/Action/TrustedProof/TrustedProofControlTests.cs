using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.TrustedProof;

public sealed class TrustedProofControlTests
{
    private static readonly TrustedProofControlCoordinates Coordinates = new(
        "SolusQuest/agentic-pr-review",
        42,
        147,
        new string('e', 40),
        new string('1', 64),
        new string('a', 40),
        new string('b', 40),
        new string('f', 64),
        900,
        2);
    private static readonly TrustedProofControlCoordinates ProducerCoordinates =
        Coordinates with { RunId = 899, RunAttempt = 1 };

    [Fact]
    public void MarkerRoundTripsAsOneCanonicalBody()
    {
        var body = TrustedProofControlMarker.CreateBody(
            "ready",
            Coordinates,
            predecessorCommentId: null);

        Assert.True(TrustedProofControlMarker.TryParse(body, out var marker));
        Assert.True(marker!.Matches(Coordinates));
        Assert.Equal("ready", marker.Kind);
        Assert.False(TrustedProofControlMarker.TryParse(body + " ", out _));
        Assert.False(TrustedProofControlMarker.TryParse(
            body.Replace(Coordinates.OperationId, new string('2', 64)),
            out _));
    }

    [Fact]
    public async Task StaleSignalIsValueFreeOneShotAndReleaseBound()
    {
        var signal = new TrustedProofStaleSignal();
        var wait = signal.SignalReadyAndWaitForReleaseAsync(
            CancellationToken.None).AsTask();

        await signal.Ready;
        Assert.False(wait.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await signal.SignalReadyAndWaitForReleaseAsync(
                CancellationToken.None));
        signal.Release();
        await wait;
    }

    [Fact]
    public async Task DispatchVerificationIsNonMutatingAndPermissionBound()
    {
        var ready = Comment(
            10,
            TrustedProofControlMarker.CreateBody(
                "ready",
                ProducerCoordinates,
                predecessorCommentId: null),
            "proof-bot");
        var release = Comment(
            11,
            TrustedProofControlMarker.CreateBody(
                "release",
                ProducerCoordinates,
                10),
            "maintainer");
        var handler = new ControlHandler([ready, release]);
        var transport = TrustedProofControlTransport.Create(
            Coordinates,
            "github-token-canary",
            handler);

        var exit = await TrustedProofControlService.RunAsync(
            ["verify-completed"],
            Coordinates,
            transport,
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.All(handler.Requests, request =>
            Assert.Equal(HttpMethod.Get, request.Method));
        Assert.All(handler.Requests, request =>
            Assert.Equal("trusted_control_rest", request.Domain));
        Assert.Contains(handler.Requests, request =>
            request.Path.Contains(
                "/collaborators/maintainer/permission",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task BaseDriftBetweenSelectionAndCreateStopsBeforeMutation()
    {
        var handler = new ControlHandler(
            [],
            pullRequestStates: [true, false]);
        var transport = TrustedProofControlTransport.Create(
            Coordinates,
            "github-token-canary",
            handler);

        Assert.Equal(1, await TrustedProofControlService.RunAsync(
            ["hold"],
            Coordinates,
            transport,
            CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count(request =>
            request.Method == HttpMethod.Get &&
            request.Path.EndsWith("/pulls/147", StringComparison.Ordinal)));
        Assert.DoesNotContain(handler.Requests, request =>
            request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task FixturePullRequestPreflightIsTaggedAsControlOnly()
    {
        var budget = new TrustedProofControlRequestBudget(maximumRequests: 1);
        var handler = new DomainRecordingHandler();

        var result = await TrustedProofControlTransport.ReadFixturePullRequestAsync(
            Coordinates.Repository,
            Coordinates.PullRequestNumber,
            "github-token-canary",
            handler,
            budget,
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, budget.Consumed);
        Assert.Equal("trusted_control_rest", handler.Domain);
    }

    [Fact]
    public async Task BaseShaDriftBetweenSelectionAndCreateStopsBeforeMutation()
    {
        var handler = new ControlHandler(
            [],
            pullRequestBaseShas:
            [Coordinates.WorkflowSha, new string('f', 40)]);
        var transport = TrustedProofControlTransport.Create(
            Coordinates,
            "github-token-canary",
            handler);

        Assert.Equal(1, await TrustedProofControlService.RunAsync(
            ["hold"],
            Coordinates,
            transport,
            CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count(request =>
            request.Method == HttpMethod.Get &&
            request.Path.EndsWith("/pulls/147", StringComparison.Ordinal)));
        Assert.DoesNotContain(handler.Requests, request =>
            request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task DispatchRejectsCurrentRunAndConflictingOperationFamily()
    {
        var currentReady = Comment(
            10,
            TrustedProofControlMarker.CreateBody(
                "ready",
                Coordinates,
                predecessorCommentId: null),
            "proof-bot");
        var currentRelease = Comment(
            11,
            TrustedProofControlMarker.CreateBody("release", Coordinates, 10),
            "maintainer");
        var currentHandler = new ControlHandler([currentReady, currentRelease]);
        var currentTransport = TrustedProofControlTransport.Create(
            Coordinates,
            "github-token-canary",
            currentHandler);
        Assert.Equal(1, await TrustedProofControlService.RunAsync(
            ["verify-completed"],
            Coordinates,
            currentTransport,
            CancellationToken.None));

        var conflicting = ProducerCoordinates with
        {
            PayloadSha256 = new string('0', 64),
        };
        var conflictHandler = new ControlHandler([
            Comment(
                12,
                TrustedProofControlMarker.CreateBody(
                    "ready",
                    conflicting,
                    predecessorCommentId: null),
                "proof-bot"),
        ]);
        var conflictTransport = TrustedProofControlTransport.Create(
            Coordinates,
            "github-token-canary",
            conflictHandler);
        Assert.Equal(1, await TrustedProofControlService.RunAsync(
            ["verify-completed"],
            Coordinates,
            conflictTransport,
            CancellationToken.None));
    }

    [Fact]
    public async Task OversizedGitHubResponseFailsClosed()
    {
        var transport = TrustedProofControlTransport.Create(
            Coordinates,
            "github-token-canary",
            new OversizedControlHandler());

        Assert.Equal(1, await TrustedProofControlService.RunAsync(
            ["verify-completed"],
            Coordinates,
            transport,
            CancellationToken.None));
    }

    [Fact]
    public async Task HoldPollingCannotExceedSharedRequestBudget()
    {
        var ready = Comment(
            10,
            TrustedProofControlMarker.CreateBody(
                "ready",
                Coordinates,
                predecessorCommentId: null),
            "proof-bot");
        var handler = new ControlHandler([ready]);
        var budget = new TrustedProofControlRequestBudget(maximumRequests: 6);
        var transport = TrustedProofControlTransport.Create(
            Coordinates,
            "github-token-canary",
            handler,
            budget);
        var delays = new List<TimeSpan>();

        var exit = await TrustedProofControlService.RunAsync(
            ["hold"],
            Coordinates,
            transport,
            CancellationToken.None,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        Assert.Equal(1, exit);
        Assert.Equal(6, budget.Consumed);
        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public async Task ListChargesEveryPageAgainstSharedRequestBudget()
    {
        var handler = new FullPageControlHandler();
        var budget = new TrustedProofControlRequestBudget(maximumRequests: 10);
        using var transport = TrustedProofControlTransport.Create(
            Coordinates,
            "github-token-canary",
            handler,
            budget);

        Assert.Null(await transport.ListAsync(CancellationToken.None));
        Assert.Equal(10, budget.Consumed);
        Assert.Equal(10, handler.Requests);
    }

    [Theory]
    [InlineData(403, true)]
    [InlineData(429, false)]
    public async Task MutationRateLimitsAreClassifiedAndStopFurtherRequests(
        int statusCode,
        bool exhaustedHeader)
    {
        var handler = new RateLimitedControlHandler(
            (HttpStatusCode)statusCode,
            exhaustedHeader);
        using var transport = TrustedProofControlTransport.Create(
            Coordinates,
            "github-token-canary",
            handler);

        var creation = await transport.CreateAsync("body", CancellationToken.None);
        var deletion = await transport.DeleteAsync(10, CancellationToken.None);

        Assert.Equal(TrustedProofMutationOutcome.RateLimited, creation.Outcome);
        Assert.Equal(TrustedProofMutationOutcome.RateLimited, deletion);
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NotModified)]
    public async Task MalformedSuccessOr304ResponseDoesNotReachControlConsumer(
        HttpStatusCode status)
    {
        var handler = new MalformedSuccessControlHandler(status);
        var budget = new TrustedProofControlRequestBudget(maximumRequests: 4);
        using var transport = TrustedProofControlTransport.Create(
            Coordinates,
            "github-token-canary",
            handler,
            budget);

        Assert.Null(await transport.ListAsync(CancellationToken.None));
        Assert.True(budget.IsRateLimited);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public void PollingBackoffIsBoundedAtOneMinute()
    {
        var delays = new List<TimeSpan>();
        var current = TimeSpan.FromSeconds(2);
        for (var index = 0; index < 8; index++)
        {
            delays.Add(current);
            current = TrustedProofControlService.NextPollDelay(current);
        }

        Assert.Equal(
            [2, 4, 8, 16, 32, 60, 60, 60],
            delays.Select(delay => delay.TotalSeconds));
    }

    [Fact]
    public async Task ReservedMalformedControlCommentFailsClosed()
    {
        var malformed = Comment(
            12,
            TrustedProofControlMarker.Prefix + "{}" +
                TrustedProofControlMarker.Suffix,
            "someone");
        var handler = new ControlHandler([malformed]);
        var transport = TrustedProofControlTransport.Create(
            Coordinates,
            "github-token-canary",
            handler);

        Assert.Equal(1, await TrustedProofControlService.RunAsync(
            ["verify-completed"],
            Coordinates,
            transport,
            CancellationToken.None));
    }

    [Fact]
    public async Task CleanupDeletesOnlyExactOwnedCommentsAndProvesAbsence()
    {
        var owned = Comment(
            10,
            TrustedProofControlMarker.CreateBody(
                "ready",
                Coordinates,
                predecessorCommentId: null),
            "proof-bot");
        var unrelated = Comment(99, "unrelated", "someone");
        var handler = new ControlHandler([owned, unrelated]);
        var transport = TrustedProofControlTransport.Create(
            Coordinates,
            "github-token-canary",
            handler);

        var exit = await TrustedProofControlService.RunAsync(
            ["cleanup"],
            Coordinates,
            transport,
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Delete &&
            request.Path.EndsWith("/10", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, request =>
            request.Method == HttpMethod.Delete &&
            request.Path.EndsWith("/99", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(
        (int)TrustedProofMutationOutcome.OutcomeUnknown,
        (int)TrustedProofMutationOutcome.MissingIdempotent,
        false,
        "reconciled-committed")]
    [InlineData(
        (int)TrustedProofMutationOutcome.OutcomeUnknown,
        (int)TrustedProofMutationOutcome.Committed,
        false,
        "reconciled-committed")]
    [InlineData(
        (int)TrustedProofMutationOutcome.OutcomeUnknown,
        (int)TrustedProofMutationOutcome.OutcomeUnknown,
        false,
        "reconciled-committed")]
    [InlineData(
        (int)TrustedProofMutationOutcome.KnownNotSent,
        (int)TrustedProofMutationOutcome.MissingIdempotent,
        false,
        "reconciled-missing")]
    [InlineData(
        (int)TrustedProofMutationOutcome.KnownNotSent,
        (int)TrustedProofMutationOutcome.Committed,
        false,
        "reconciled-committed")]
    [InlineData(
        (int)TrustedProofMutationOutcome.OutcomeUnknown,
        (int)TrustedProofMutationOutcome.OutcomeUnknown,
        true,
        null)]
    public void CleanupClassificationRetainsInitialRetryAndFinalPresence(
        int initialOutcome,
        int retryOutcome,
        bool finalPresence,
        string? expected)
    {
        Assert.Equal(expected, TrustedProofControlService.ClassifyCleanupOutcome(
            (TrustedProofMutationOutcome)initialOutcome,
            (TrustedProofMutationOutcome)retryOutcome,
            finalPresence));
    }

    [Fact]
    public async Task CleanupReconcilesUnknownThenMissingAfterFinalAbsence()
    {
        var owned = Comment(
            10,
            TrustedProofControlMarker.CreateBody(
                "ready",
                Coordinates,
                predecessorCommentId: null),
            "proof-bot");
        var handler = new ControlHandler(
            [owned],
            [
                new(HttpStatusCode.InternalServerError, Remove: true),
                new(HttpStatusCode.NotFound, Remove: false),
            ]);
        var transport = TrustedProofControlTransport.Create(
            Coordinates,
            "github-token-canary",
            handler);

        var exit = await TrustedProofControlService.RunAsync(
            ["cleanup"],
            Coordinates,
            transport,
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(2, handler.Requests.Count(request =>
            request.Method == HttpMethod.Delete));
    }

    [Fact]
    public async Task CleanupFailsWhenTwoUnknownDeletesLeaveCommentPresent()
    {
        var owned = Comment(
            10,
            TrustedProofControlMarker.CreateBody(
                "ready",
                Coordinates,
                predecessorCommentId: null),
            "proof-bot");
        var handler = new ControlHandler(
            [owned],
            [
                new(HttpStatusCode.InternalServerError, Remove: false),
                new(HttpStatusCode.InternalServerError, Remove: false),
            ]);
        var transport = TrustedProofControlTransport.Create(
            Coordinates,
            "github-token-canary",
            handler);

        var exit = await TrustedProofControlService.RunAsync(
            ["cleanup"],
            Coordinates,
            transport,
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(2, handler.Requests.Count(request =>
            request.Method == HttpMethod.Delete));
    }

    private static TrustedProofIssueComment Comment(
        long id,
        string body,
        string login)
    {
        var timestamp = DateTimeOffset.Parse("2026-08-21T00:00:00Z")
            .AddSeconds(id);
        return new(id, body, new(login), timestamp, timestamp);
    }

    private sealed record DeleteStep(HttpStatusCode Status, bool Remove);

    private sealed class ControlHandler : HttpMessageHandler
    {
        private readonly Dictionary<long, TrustedProofIssueComment> comments;
        private readonly Queue<DeleteStep> deleteSteps;
        private readonly Queue<bool> pullRequestStates;
        private readonly Queue<string> pullRequestBaseShas;

        internal ControlHandler(
            IEnumerable<TrustedProofIssueComment> initial,
            IEnumerable<DeleteStep>? deleteSteps = null,
            IEnumerable<bool>? pullRequestStates = null,
            IEnumerable<string>? pullRequestBaseShas = null)
        {
            comments = initial.ToDictionary(comment => comment.Id);
            this.deleteSteps = new Queue<DeleteStep>(deleteSteps ?? []);
            this.pullRequestStates = new Queue<bool>(
                pullRequestStates ?? [true]);
            this.pullRequestBaseShas = new Queue<string>(
                pullRequestBaseShas ?? [Coordinates.WorkflowSha]);
        }

        internal List<(HttpMethod Method, string Path, string? Domain)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.PathAndQuery;
            request.Options.TryGetValue(
                TrustedProofOperationRequestAccounting.WitnessDomainOption,
                out string? domain);
            Requests.Add((request.Method, path, domain));
            if (request.Method == HttpMethod.Get &&
                path.EndsWith("/pulls/147", StringComparison.Ordinal))
            {
                var current = pullRequestStates.Count > 1
                    ? pullRequestStates.Dequeue()
                    : pullRequestStates.Peek();
                var baseSha = pullRequestBaseShas.Count > 1
                    ? pullRequestBaseShas.Dequeue()
                    : pullRequestBaseShas.Peek();
                return Json(new TrustedProofPullRequest
                {
                    Number = Coordinates.PullRequestNumber,
                    State = "open",
                    Draft = false,
                    MergedAt = null,
                    Base = new()
                    {
                        Ref = current ? "main" : "release",
                        Sha = baseSha,
                        Repository = new()
                        {
                            Id = Coordinates.RepositoryId,
                            FullName = Coordinates.Repository,
                        },
                    },
                    Head = new()
                    {
                        Ref = "r4-trusted-proof/" + Coordinates.OperationId,
                        Sha = Coordinates.FixtureHeadSha,
                        Repository = new()
                        {
                            Id = Coordinates.RepositoryId,
                            FullName = Coordinates.Repository,
                        },
                    },
                });
            }

            if (request.Method == HttpMethod.Get &&
                path.Contains("/comments?", StringComparison.Ordinal))
            {
                return Json(comments.Values.OrderBy(comment => comment.Id).ToArray());
            }

            if (request.Method == HttpMethod.Get &&
                path.Contains("/collaborators/", StringComparison.Ordinal))
            {
                return Json(new TrustedProofPermission("write"));
            }

            if (request.Method == HttpMethod.Get &&
                long.TryParse(path.Split('/').Last(), out var getId) &&
                comments.TryGetValue(getId, out var comment))
            {
                return Json(comment);
            }

            if (request.Method == HttpMethod.Delete &&
                long.TryParse(path.Split('/').Last(), out var deleteId))
            {
                var step = deleteSteps.Count == 0
                    ? new DeleteStep(HttpStatusCode.NoContent, Remove: true)
                    : deleteSteps.Dequeue();
                if (step.Remove)
                {
                    comments.Remove(deleteId);
                }

                return Task.FromResult(new HttpResponseMessage(
                    step.Status));
            }

            return Task.FromResult(new HttpResponseMessage(
                HttpStatusCode.BadRequest));
        }

        private static Task<HttpResponseMessage> Json<T>(T value)
        {
            var node = JsonNode.Parse(JsonSerializer.Serialize(value))!;
            AddGitHubFields(node);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(node);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
                {
                    Headers =
                    {
                        ContentType = new("application/json"),
                    },
                },
            });
        }

        private static void AddGitHubFields(JsonNode node)
        {
            if (node is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        AddGitHubFields(item);
                    }
                }

                return;
            }

            if (node is not JsonObject value)
            {
                return;
            }

            if (value.ContainsKey("id") && value.ContainsKey("body"))
            {
                value["url"] = "https://api.github.com/issues/comments/10";
                value["node_id"] = "IC_test";
                value["author_association"] = "MEMBER";
                value["reactions"] = new JsonObject { ["total_count"] = 0 };
                if (value["user"] is JsonObject user)
                {
                    user["id"] = 100;
                    user["avatar_url"] = "https://avatars.example.test/100";
                }
            }
            else if (value.ContainsKey("permission"))
            {
                value["role_name"] = "write";
                value["user"] = new JsonObject { ["login"] = "maintainer" };
            }
        }
    }

    private sealed class DomainRecordingHandler : HttpMessageHandler
    {
        internal string? Domain { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.Options.TryGetValue(
                TrustedProofOperationRequestAccounting.WitnessDomainOption,
                out string? domain);
            Domain = domain;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class OversizedControlHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    new string('x', 512 * 1024 + 1),
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }

    private sealed class FullPageControlHandler : HttpMessageHandler
    {
        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            var page = int.Parse(
                request.RequestUri!.Query.Split('&').Single(part =>
                    part.StartsWith("page=", StringComparison.Ordinal))["page=".Length..],
                System.Globalization.CultureInfo.InvariantCulture);
            return Json(Enumerable.Range(1, 100).Select(index => Comment(
                page * 1_000 + index,
                "ordinary",
                "proof-bot")).ToArray());
        }
    }

    private sealed class RateLimitedControlHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode statusCode;
        private readonly bool exhaustedHeader;

        internal RateLimitedControlHandler(
            HttpStatusCode statusCode,
            bool exhaustedHeader)
        {
            this.statusCode = statusCode;
            this.exhaustedHeader = exhaustedHeader;
        }

        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            var response = new HttpResponseMessage(statusCode);
            if (exhaustedHeader)
            {
                response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
            }

            return Task.FromResult(response);
        }
    }

    private sealed class MalformedSuccessControlHandler(HttpStatusCode statusCode) :
        HttpMessageHandler
    {
        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("[]")),
            };
            response.Headers.TryAddWithoutValidation(
                "x-ratelimit-remaining", "malformed");
            response.Content.Headers.ContentType = new("application/json");
            return Task.FromResult(response);
        }
    }

    private static Task<HttpResponseMessage> Json<T>(T value)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(value))!;
        AddGitHubFields(node);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(node);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
            {
                Headers =
                {
                    ContentType = new("application/json"),
                },
            },
        });
    }

    private static void AddGitHubFields(JsonNode node)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    AddGitHubFields(item);
                }
            }

            return;
        }

        if (node is not JsonObject value)
        {
            return;
        }

        if (value.ContainsKey("id") && value.ContainsKey("body"))
        {
            value["url"] = "https://api.github.com/issues/comments/10";
            value["node_id"] = "IC_test";
            value["author_association"] = "MEMBER";
            value["reactions"] = new JsonObject { ["total_count"] = 0 };
            if (value["user"] is JsonObject user)
            {
                user["id"] = 100;
                user["avatar_url"] = "https://avatars.example.test/100";
            }
        }
    }
}
