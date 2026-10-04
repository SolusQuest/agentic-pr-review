using System.Net;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Inline;
using AgenticPrReview.Runtime.Tests.Agent.Loop;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed partial class ActionHostCompositionTests
{
    [Fact]
    public async Task RealStickyHttpReceivesTheJournalAbsoluteDeadline()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = FullLaunch(scenario.Launch);
        var github = new FullPathGitHubFactory(scenario.Transport.PullRequest);
        var clock = new DeadlineTestClock(LocatorTestData.Now);
        using var caller = new CancellationTokenSource();
        using var cleanup = new CancellationTokenSource();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var factory = new BoundedGitHubPublisherTransportFactory(() => new DeadlineHttpHandler(async (request, token) =>
        {
            calls.Add(request.Method.Method);
            if (request.Method == HttpMethod.Get) return DeadlineJson(HttpStatusCode.OK, "[]");
            entered.TrySetResult(token);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cleanup.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            throw new InvalidOperationException();
        }));
        var run = new ActionHostComposition(new ActionHostCompositionDependencies(
            scenario.EventReader, scenario.Factory, github, github,
            new FullPathStateDependencies(FullPathStore(launch), github), factory,
            new FullPathProviderFactory(), clock, StagingPath)).RunAsync(launch, caller.Token);
        try
        {
            var observed = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            Assert.False(observed.IsCancellationRequested);
            clock.Advance(TimeSpan.FromMinutes(4));
            var completion = await run.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(observed.IsCancellationRequested);
            Assert.Equal(ActionHostStatus.OutcomeAmbiguous, completion.Status);
            Assert.NotEqual(ActionHostStateDisposition.Accepted, completion.Summary.StateDisposition);
            Assert.Single(calls.Where(method => method == "POST"));
            Assert.Equal("POST", calls[^1]);
        }
        finally
        {
            cleanup.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData("batch-send", false)]
    [InlineData("batch-readback", false)]
    [InlineData("batch-send", true)]
    [InlineData("batch-readback", true)]
    [InlineData("batch-body", true)]
    [InlineData("individual-send", true)]
    [InlineData("individual-body", true)]
    [InlineData("individual-readback", true)]
    [InlineData("batch-complete", true)]
    public async Task RealInlineHttpCannotOutliveFinalization(string stage, bool cancelCaller)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = FullLaunch(scenario.Launch);
        var github = new FullPathGitHubFactory(scenario.Transport.PullRequest, withInlineFile: true);
        var store = FullPathStore(launch);
        var publisher = SuccessfulPublisher(82);
        var provider = new FullPathProviderFactory(withFinding: true);
        var clock = new DeadlineTestClock(LocatorTestData.Now);
        using var caller = new CancellationTokenSource();
        using var release = new CancellationTokenSource();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        object? completedComment = null;
        var calls = new List<string>();
        var getCount = 0;
        var handler = new DeadlineHttpHandler(async (request, token) =>
        {
            calls.Add(request.Method.Method);
            if (request.Method == HttpMethod.Get && ++getCount == 1)
            {
                clock.Advance(TimeSpan.FromSeconds(239));
                return DeadlineJson(HttpStatusCode.OK, "[]");
            }
            if (stage.StartsWith("individual", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/reviews", StringComparison.Ordinal))
                    return DeadlineJson(HttpStatusCode.UnprocessableEntity, """
                        {"message":"Validation Failed","documentation_url":"https://docs.github.com/rest/pulls/reviews#create-a-review-for-a-pull-request","errors":[{"resource":"PullRequestReview","field":"comments","code":"invalid"}]}
                        """);
                if (request.Method == HttpMethod.Get && getCount == 2) return DeadlineJson(HttpStatusCode.OK, "[]");
                if (request.Method == HttpMethod.Post && stage == "individual-readback")
                {
                    using var sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                    var fields = sent.RootElement;
                    var api = $"https://api.github.com/repos/{ActionHostAuthorizationScenario.RepositoryName}/pulls";
                    return DeadlineJson(HttpStatusCode.Created, JsonSerializer.Serialize(new
                    {
                        id = 99, pull_request_review_id = 1, url = api + "/comments/99",
                        pull_request_url = api + $"/{ActionHostAuthorizationScenario.PullRequestNumber}",
                        html_url = $"https://github.com/{ActionHostAuthorizationScenario.RepositoryName}/pull/{ActionHostAuthorizationScenario.PullRequestNumber}#discussion_r99",
                        body = fields.GetProperty("body").GetString(), path = fields.GetProperty("path").GetString(),
                        line = fields.GetProperty("line").GetInt32(), side = fields.GetProperty("side").GetString(),
                        commit_id = fields.GetProperty("commit_id").GetString(),
                    }));
                }
            }
            if (stage == "batch-complete" && request.Method == HttpMethod.Get)
                return DeadlineJson(HttpStatusCode.OK, JsonSerializer.Serialize(new[] { completedComment }));
            if (request.Method == HttpMethod.Post && stage is "batch-readback" or "batch-complete")
            {
                var api = request.RequestUri!.GetLeftPart(UriPartial.Authority) +
                    $"/repos/{ActionHostAuthorizationScenario.RepositoryName}/pulls/{ActionHostAuthorizationScenario.PullRequestNumber}";
                if (stage == "batch-complete")
                {
                    using var sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                    var fields = sent.RootElement.GetProperty("comments")[0];
                    completedComment = new
                    {
                        id = 99, pull_request_review_id = 1,
                        url = $"https://api.github.com/repos/{ActionHostAuthorizationScenario.RepositoryName}/pulls/comments/99",
                        pull_request_url = api,
                        html_url = $"https://github.com/{ActionHostAuthorizationScenario.RepositoryName}/pull/{ActionHostAuthorizationScenario.PullRequestNumber}#discussion_r99",
                        body = fields.GetProperty("body").GetString(), path = fields.GetProperty("path").GetString(),
                        line = fields.GetProperty("line").GetInt32(), side = fields.GetProperty("side").GetString(),
                        commit_id = sent.RootElement.GetProperty("commit_id").GetString(),
                    };
                    entered.TrySetResult(token);
                    await continueWrite.Task.WaitAsync(token);
                }
                return DeadlineJson(HttpStatusCode.Created, JsonSerializer.Serialize(new
                {
                    id = 1, url = api + "/reviews/1", pull_request_url = api,
                    html_url = $"https://github.com/{ActionHostAuthorizationScenario.RepositoryName}/pull/{ActionHostAuthorizationScenario.PullRequestNumber}#pullrequestreview-1",
                    commit_id = scenario.Transport.PullRequest.HeadSha,
                }));
            }
            if (stage.EndsWith("body", StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StreamContent(new DeadlineBodyStream(entered, release.Token)),
                };
                response.Content.Headers.ContentType = new("application/json");
                return response;
            }
            entered.TrySetResult(token);
            using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(token, release.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, cleanup.Token);
            throw new InvalidOperationException();
        });
        var run = new ActionHostComposition(new ActionHostCompositionDependencies(
            scenario.EventReader, scenario.Factory, github, github,
            new FullPathStateDependencies(store, github), publisher, provider, clock, StagingPath,
            new PostAcceptanceInlinePublisherHook(new BoundedGitHubPublisherTransportFactory(() => handler))))
            .RunAsync(launch, caller.Token);
        try
        {
            var observed = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancelCaller) caller.Cancel();
            Assert.False(observed.IsCancellationRequested);
            if (stage == "batch-complete") continueWrite.SetResult();
            else clock.Advance(TimeSpan.FromSeconds(1));
            var completion = await run.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(stage != "batch-complete", observed.IsCancellationRequested);
            Assert.Equal(ActionHostStateDisposition.Accepted, completion.Summary.StateDisposition);
            Assert.Equal(stage == "batch-complete" ? ActionHostStatus.Reviewed : ActionHostStatus.ReviewedWithInlineWarnings, completion.Status);
            var expected = stage == "individual-readback" ? new[] { "GET", "POST", "GET", "POST", "GET" }
                : stage.StartsWith("individual", StringComparison.Ordinal)
                ? new[] { "GET", "POST", "GET", "POST" }
                : stage is "batch-readback" or "batch-complete" ? new[] { "GET", "POST", "GET" } : new[] { "GET", "POST" };
            Assert.Equal(expected, calls);
            var recovered = await new ActionHostComposition(new ActionHostCompositionDependencies(
                scenario.EventReader, scenario.Factory, github, github,
                new FullPathStateDependencies(store, github), publisher, provider,
                new FrozenLocatorTimeProvider(LocatorTestData.Now), StagingPath, new ConsumingInlineHook()))
                .RunAsync(WithoutProviderKey(launch), default);
            Assert.Equal(ActionHostStateDisposition.Accepted, recovered.Summary.StateDisposition);
            Assert.Equal(1, provider.Runs);
            Assert.Single(publisher.Transport.Bodies);
        }
        finally
        {
            release.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static HttpResponseMessage DeadlineJson(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class DeadlineHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class DeadlineBodyStream(TaskCompletionSource<CancellationToken> entered, CancellationToken release) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult(cancellationToken);
            using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, release);
            await Task.Delay(Timeout.InfiniteTimeSpan, cleanup.Token);
            return 0;
        }
    }
}
