using System.Net;
using System.Text;
using System.Text.Json;
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
                Coordinates,
                predecessorCommentId: null),
            "proof-bot");
        var release = Comment(
            11,
            TrustedProofControlMarker.CreateBody("release", Coordinates, 10),
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
        Assert.Contains(handler.Requests, request =>
            request.Path.Contains(
                "/collaborators/maintainer/permission",
                StringComparison.Ordinal));
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

    private static TrustedProofIssueComment Comment(
        long id,
        string body,
        string login)
    {
        var timestamp = DateTimeOffset.Parse("2026-08-21T00:00:00Z");
        return new(id, body, new(login), timestamp, timestamp);
    }

    private sealed class ControlHandler(
        IEnumerable<TrustedProofIssueComment> initial) : HttpMessageHandler
    {
        private readonly Dictionary<long, TrustedProofIssueComment> comments =
            initial.ToDictionary(comment => comment.Id);

        internal List<(HttpMethod Method, string Path)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add((request.Method, path));
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
                comments.Remove(deleteId);
                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(
                HttpStatusCode.BadRequest));
        }

        private static Task<HttpResponseMessage> Json<T>(T value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
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
    }
}
