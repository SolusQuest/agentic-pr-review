using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Prefix;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

public sealed class R6PrefixProjectionTests
{
    private const string Canary = "private-prefix-canary-275";
    private static AgentTrustedRequest Trusted => new("repo", 275, "workflow", Encoding.UTF8.GetBytes("policy " + Canary),
        "build", DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Adapter);
    private static ReviewedIdentity Identity => new("repo", 275, new('0', 40), new('1', 40));
    private static ProjectChatMessage User(string value) => new("user", [new ProjectTextContent(value)]);
    private static ProjectChatRequest Request(AgentRunRequest run) => new(run.InitialMessages,
        AgentToolRegistry.Definitions.ToArray(), run.Continuation, ThinkingRequired: true);

    [Fact]
    public async Task ActualLoopRestoreAndTypedCallbackAgreeWithIndependentLogicalSegmentContract()
    {
        var (restored, boundary) = await Fixture();
        var run = restored.RunRequest!;
        using var transport = new FinishScript();
        var observer = new PrefixObservingChatClient(boundary, Client(transport));
        var outcome = await new AgentLoop(observer, new NoTools()).RunAsync(run, CancellationToken.None);
        Assert.True(outcome.CompletedSessionEligible);
        var observation = Assert.Single(observer.Observations);
        Assert.Equal(1, observation.ControlMessages);
        Assert.Equal(3, observation.HistoricalMessages);
        Assert.Equal(0, observation.Domain.Generation);
        Assert.Equal(restored.Artifact!.SessionSha256, observation.Domain.AcceptedSessionSha256);
        Assert.Equal(1, observation.Logical.Control.Count);
        Assert.Equal(4, observation.Logical.History.Count); // three messages + one positional continuation item
        Assert.Equal(1, observation.Logical.Dynamic.Count);
        Assert.Equal(PrefixObservation.LogicalDomain, observation.ObservationDomain);
        Assert.Equal("unknown", observation.CacheObservation);
        // Inspect the actual typed callback independently; no HTTP projection is present.
        var messages = transport.LastRequest!.Messages;
        Assert.True(messages.Select(m => m.Role).SequenceEqual(["system", "user", "assistant", "tool", "user"]));
        Assert.True(transport.LastRequest.Continuation!.Items.Single().Readable == "reason " + Canary);
        Assert.True(messages[2].Contents.OfType<ProjectToolCallContent>().Single().CallId == "finish-old");
        var result = Assert.IsType<ProjectToolResultContent>(Assert.Single(messages[3].Contents));
        Assert.True(result.CallId == "finish-old" && result.Result == "{}");
        Assert.True(AgentRequestWriter.Write(Request(run)).AsSpan().SequenceEqual(transport.LastBody));
        Assert.Equal(transport.LastBody!.Length, observation.Logical.Whole.Bytes);
        Assert.Equal(PrefixMeasurement.Observe(boundary, Request(run)), observation);
        AssertSafe(observation);
    }

    [Fact]
    public async Task ValidSuffixGrowthAndDynamicCallIdsPreserveFixedHistoricalPrefix()
    {
        var (restored, boundary) = await Fixture();
        var request = Request(restored.RunRequest!);
        var before = PrefixMeasurement.Observe(boundary, request);
        var firstRun = await RunSuffix(restored.RunRequest!, boundary, "dynamic-a");
        var secondRun = await RunSuffix(restored.RunRequest!, boundary, "dynamic-b");
        var first = firstRun.Observations[1];
        var second = secondRun.Observations[1];
        Assert.Equal(before, firstRun.Observations[0]);
        Assert.Equal(before, secondRun.Observations[0]);
        foreach (var next in new[] { first, second })
        {
            Assert.Equal(new("compared", true), before.Compare(next));
            Assert.NotEqual(before.Logical.Whole, next.Logical.Whole);
            Assert.NotEqual(before.Logical.Dynamic, next.Logical.Dynamic);
        }
        Assert.Equal(new("compared", true), first.Compare(second));
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("tools")]
    [InlineData("tool-order")]
    [InlineData("old-content")]
    [InlineData("old-id")]
    [InlineData("role")]
    [InlineData("order")]
    [InlineData("reasoning")]
    [InlineData("reasoning-id-deadline")]
    public async Task SameBoundaryMutationsChangeRelevantActualSegments(string mutation)
    {
        var (restored, boundary) = await Fixture();
        var request = Request(restored.RunRequest!);
        var original = PrefixMeasurement.Observe(boundary, request);
        var messages = request.Messages.ToArray();
        var tools = request.Tools.ToArray();
        var continuation = request.Continuation!;
        switch (mutation)
        {
            case "policy": messages[0] = new("system", [new ProjectTextContent("changed policy")]); break;
            case "tools": tools[0] = tools[0] with { Description = "changed declaration" }; break;
            case "tool-order": Array.Reverse(tools); break;
            case "old-content": messages[1] = User("changed prior context"); break;
            case "role": messages[1] = messages[1] with { Role = "system" }; break;
            case "order": (messages[0], messages[1]) = (messages[1], messages[0]); break;
            case "old-id":
                messages[2] = new("assistant", [new ProjectToolCallContent("changed-old", "finish_review", "{\"summary\":\"done\",\"findings\":[]}")]);
                messages[3] = new("tool", [new ProjectToolResultContent("changed-old", "{}")]); break;
            default:
                continuation = continuation with { Items = [continuation.Items[0] with {
                    Readable = mutation == "reasoning" ? "changed reasoning" : "run_id=new deadline=tomorrow" }] }; break;
        }
        var changed = PrefixMeasurement.Observe(boundary, request with { Messages = messages, Tools = tools, Continuation = continuation });
        Assert.Equal(new("compared", false), original.Compare(changed));
        if (mutation is "tools" or "tool-order")
        {
            Assert.NotEqual(original.Logical.Settings, changed.Logical.Settings);
        }
        else if (mutation is "policy" or "order")
        {
            Assert.NotEqual(original.Logical.Control, changed.Logical.Control);
        }
        else
        {
            Assert.NotEqual(original.Logical.History, changed.Logical.History);
        }
    }

    [Fact]
    public async Task ContinuationPlacementAcrossHistoricalAndCurrentAssistantsCannotHideMutation()
    {
        var (restored, boundary) = await Fixture();
        var request = (await RunSuffix(restored.RunRequest!, boundary, "dynamic-a")).Request;
        var original = PrefixMeasurement.Observe(boundary, request);
        var items = request.Continuation!.Items;
        var swapped = request with { Continuation = request.Continuation with { Items = [
            items[0] with { Readable = items[1].Readable }, items[1] with { Readable = items[0].Readable }] } };
        var changed = PrefixMeasurement.Observe(boundary, swapped);
        Assert.NotEqual(original.Logical.History, changed.Logical.History);
        Assert.Equal(new("compared", false), original.Compare(changed));
        // Logical traces retain original continuation array order without normalization.
        var reordered = PrefixMeasurement.Observe(boundary, request with { Continuation = request.Continuation with {
            Items = items.Reverse().ToArray() } });
        Assert.NotEqual(original.Logical.Whole, reordered.Logical.Whole);
    }

    [Fact]
    public async Task InvalidContinuationPositionsAndAssociationsAreRejectedWithoutRawDiagnostics()
    {
        var (restored, boundary) = await Fixture();
        var request = Request(restored.RunRequest!);
        var item = request.Continuation!.Items[0];
        foreach (var invalid in new[] { item with { MessagePosition = 1 }, item with { MessagePosition = 99 },
            item with { ContentPosition = 99 }, item with { AssociatedCallId = "missing" } })
        {
            var error = Assert.Throws<PrefixObservationException>(() => PrefixMeasurement.Observe(boundary,
                request with { Continuation = request.Continuation with { Items = [invalid] } }));
            Assert.Equal("prefix_observation_invalid", error.ToString());
            Assert.Null(error.InnerException);
        }
        // Position 1 is observable logically, but the fixed legacy script requires first-position framing.
        var badReplay = request with { Continuation = request.Continuation with { Items = [item with { ContentPosition = 1 }] } };
        var projected = PrefixMeasurement.Observe(boundary, badReplay);
        Assert.NotEqual(PrefixMeasurement.Observe(boundary, request).Logical.History, projected.Logical.History);
        using var transport = new FinishScript();
        await Assert.ThrowsAsync<ProjectChatNormalizationException>(() => Client(transport).GetResponseAsync(badReplay, CancellationToken.None));
        Assert.Equal(0, transport.Sends);
    }

    [Fact]
    public async Task LogicalMetadataMutationIsObservableAndMalformedReplayIsNotCacheEvidence()
    {
        var (restored, boundary) = await Fixture();
        var request = Request(restored.RunRequest!);
        var original = PrefixMeasurement.Observe(boundary, request);
        var malformed = request with { Continuation = request.Continuation! with {
            Items = [request.Continuation.Items[0] with { Opaque = Canary }] } };
        var changed = PrefixMeasurement.Observe(boundary, malformed);
        Assert.Equal(new("compared", false), original.Compare(changed));
        Assert.NotEqual(original.Logical.History, changed.Logical.History);
        Assert.Equal("unknown", changed.CacheObservation);
        using var transport = new FinishScript();
        await Assert.ThrowsAsync<ProjectChatNormalizationException>(() => Client(transport).GetResponseAsync(malformed, CancellationToken.None));
        Assert.Equal(0, transport.Sends);
    }

    [Fact]
    public async Task ComparisonDomainInvalidatesEqualBytesAndRecordsActualStablePlanIdentity()
    {
        var (restored, boundary) = await Fixture();
        var observation = PrefixMeasurement.Observe(boundary, Request(restored.RunRequest!));
        foreach (var domain in new[] {
            observation.Domain with { SourceCommit = new('a', 40) }, observation.Domain with { SourceTree = new('b', 40) },
            observation.Domain with { SourceClean = !observation.Domain.SourceClean },
            observation.Domain with { Generation = 1 }, observation.Domain with { SessionSha256 = new('c', 64) },
            observation.Domain with { AcceptedSessionSha256 = new('d', 64) } })
            Assert.Equal(new("incomparable", null), observation.Compare(observation with { Domain = domain }));
        var trusted = Trusted;
        var original = Bootstrap(trusted);
        var baseline = PrefixMeasurement.Observe(PrefixBoundary.Bootstrap(trusted, original), Request(original));
        foreach (var changed in new[] { trusted with { BuildId = "new-build" }, trusted with { ModelId = "new-model" },
            trusted with { AdapterId = "new-adapter" }, trusted with { ProviderId = "new-provider" },
            trusted with { TrustedPolicyBytes = "new policy"u8.ToArray() } })
        {
            var run = Bootstrap(changed);
            var next = PrefixMeasurement.Observe(PrefixBoundary.Bootstrap(changed, run), Request(run));
            Assert.NotEqual(baseline.Domain.StablePlanSha256, next.Domain.StablePlanSha256);
            Assert.Equal(new("incomparable", null), baseline.Compare(next));
        }
    }

    [Fact]
    public async Task BoundsEmptyHistoryAndFailurePathsRetainOnlySafeEvidence()
    {
        var run = Bootstrap(Trusted);
        var boundary = PrefixBoundary.Bootstrap(Trusted, run);
        var request = Request(run);
        var empty = PrefixMeasurement.Observe(boundary, request);
        Assert.Equal(0, empty.HistoricalMessages);
        Assert.Equal(0, empty.Logical.History.Count);
        var growing = await RunSuffix(run, boundary, "new-call");
        Assert.Equal(new("compared", true), empty.Compare(growing.Observations[1]));
        AssertSafe(empty);
        foreach (var invalid in new[] {
            request with { Messages = [] }, request with { Messages = [request.Messages[0]] },
            request with { Messages = Enumerable.Repeat(User("x"), AgentLimits.Messages + 1).ToArray() },
            request with { Messages = [request.Messages[0], User(new('x', AgentLimits.RequestBytes + 1))] },
            request with { Tools = [] }, request with { ThinkingRequired = false },
            request with { Messages = [request.Messages[0], User("\ud800")] } })
            Assert.Throws<PrefixObservationException>(() => PrefixMeasurement.Observe(boundary, invalid));
        Assert.Throws<PrefixObservationException>(() => PrefixBoundary.Bootstrap(Trusted, run with {
            InitialMessages = [.. run.InitialMessages, User("unowned history")] }));
        Assert.Throws<PrefixObservationException>(() => PrefixBoundary.Restored(Trusted, AgentSessionRestoreResult.Failure("bad")));
        var client = new PrefixObservingChatClient(boundary, new FailingClient());
        for (var index = 0; index < AgentLimits.ModelCalls; index++)
            await Assert.ThrowsAsync<IOException>(() => client.GetResponseAsync(request, CancellationToken.None));
        Assert.Equal(AgentLimits.ModelCalls, client.Observations.Length);
        Assert.Throws<PrefixObservationException>(() => { _ = client.GetResponseAsync(request, CancellationToken.None); });
        foreach (var observation in client.Observations) AssertSafe(observation);
        var cancelled = new PrefixObservingChatClient(boundary, new FailingClient());
        Assert.Throws<OperationCanceledException>(() => { _ = cancelled.GetResponseAsync(request, new CancellationToken(true)); });
        Assert.Empty(cancelled.Observations);
    }

    private static void AssertSafe(PrefixObservation observation)
    {
        var text = observation.ToString();
        Assert.False(text.Contains(Canary, StringComparison.Ordinal));
        Assert.False(text.Contains("finish-old", StringComparison.Ordinal));
        Assert.False(text.Contains("reasoning_content", StringComparison.Ordinal));
        foreach (var projection in new[] { observation.Logical })
            foreach (var segment in new[] { projection.Control, projection.History, projection.Dynamic, projection.Settings, projection.Whole })
            {
                Assert.Matches("^[0-9a-f]{64}$", segment.Sha256);
                Assert.InRange(segment.Bytes, 0, AgentLimits.RequestBytes);
                Assert.InRange(segment.Count, 0, AgentLimits.PartsTotal + AgentLimits.Messages);
            }
    }

    private static AgentRunRequest Bootstrap(AgentTrustedRequest trusted)
    {
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var stable));
        return new(Identity, stable!.StablePlan, "session-275", [.. stable.ControlMessages, User("old context " + Canary)]);
    }

    private static async Task<(AgentSessionRestoreResult Restored, PrefixBoundary Boundary)> Fixture()
    {
        var trusted = Trusted;
        var run = Bootstrap(trusted);
        using var transport = new FinishScript("finish-old");
        var outcome = await new AgentLoop(Client(transport), new NoTools()).RunAsync(run, CancellationToken.None);
        Assert.True(outcome.CompletedSessionEligible);
        var built = AgentSessionBuilder.Build(new(run, outcome, trusted, run.InitialMessages.Length - 1,
            DeepSeekReasoningContinuationCodec.Instance, null, ReviewedHeadTransition.SameHead));
        Assert.True(built.Succeeded);
        var artifact = built.Artifact!;
        var restored = AgentSessionRestorer.Restore(new(AgentSessionLocatorFamily.Current, AgentSessionRestoreIntent.Automatic,
            false, artifact.Plaintext, new(0, artifact.SessionSha256, new('e', 64), Identity.BaseSha, Identity.HeadSha, null),
            trusted, run.SessionId, Identity, User("current delta " + Canary), ReviewedHeadTransition.SameHead,
            DeepSeekReasoningContinuationCodec.Instance));
        Assert.True(restored.Succeeded);
        return (restored, PrefixBoundary.Restored(trusted, restored));
    }

    [Theory]
    [InlineData(true, null, 0)]
    [InlineData(false, AgentFailureCodes.ToolIoFailed, 1)]
    public async Task InvalidArgumentsOrUnadmittedResultsCannotSupplyPositiveSuffixEvidence(
        bool invalidArguments, string? expectedCode, int expectedExecutions)
    {
        var (restored, boundary) = await Fixture();
        using var transport = new FinishScript(firstReadId: "dynamic", invalidArguments: invalidArguments);
        var executor = new SyntheticReadExecutor(malformedResult: !invalidArguments);
        var observer = new PrefixObservingChatClient(boundary, Client(transport));
        var outcome = await new AgentLoop(observer, executor).RunAsync(restored.RunRequest!, CancellationToken.None);
        Assert.Equal(invalidArguments, outcome.CompletedSessionEligible);
        Assert.Equal(expectedCode, outcome.Diagnostic?.Code);
        Assert.Equal(expectedExecutions, executor.Executions);
        Assert.Equal(invalidArguments ? 2 : 1, transport.Sends);
        Assert.Equal(invalidArguments ? 2 : 1,
            observer.Observations.Length);
        // The recovery request carries only fixed errors, never a tool
        // observation capable of grounding the rejected suffix.
        Assert.Empty(outcome.Events.OfType<AgentToolResultEvent>());
    }

    private static async Task<(ProjectChatRequest Request, ImmutableArray<PrefixObservation> Observations)> RunSuffix(
        AgentRunRequest run, PrefixBoundary boundary, string id)
    {
        using var transport = new FinishScript(firstReadId: id);
        var capture = new CapturingClient(Client(transport));
        var observer = new PrefixObservingChatClient(boundary, capture);
        var executor = new SyntheticReadExecutor();
        var outcome = await new AgentLoop(observer, executor).RunAsync(run, CancellationToken.None);
        Assert.True(outcome.CompletedSessionEligible);
        Assert.Equal(2, transport.Sends);
        Assert.Equal(2, observer.Observations.Length);
        Assert.Equal(1, executor.Executions);
        // Independently bind the second actual request to production argument/result admission.
        var request = capture.LastRequest!;
        var call = Assert.IsType<ProjectToolCallContent>(Assert.Single(request.Messages[^2].Contents));
        var result = Assert.IsType<ProjectToolResultContent>(Assert.Single(request.Messages[^1].Contents));
        Assert.True(call.CallId == id && result.CallId == id);
        Assert.True(AgentToolArguments.TryReadFileProvider(call.ArgumentsJson, out var arguments));
        Assert.True(AgentToolResultAdmission.TryAdmit(new PreparedReadFileCall(id, arguments!), Identity,
            executor.Execution!, out var canonical, out _));
        Assert.True(result.Result == canonical);
        return (request, observer.Observations);
    }

    private static IProjectChatClient Client(FinishScript script) => script;

    private sealed class FinishScript(string id = "finish-current", string? firstReadId = null,
        bool invalidArguments = false) : IProjectChatClient, IDisposable
    {
        internal byte[]? LastBody { get; private set; }
        internal ProjectChatRequest? LastRequest { get; private set; }
        internal int Sends { get; private set; }
        public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try { PrefixMeasurement.ValidateContinuation(request); }
            catch (PrefixObservationException) { throw new ProjectChatNormalizationException(); }
            if (request.Continuation?.Items.Any(i => i.ContentPosition != 0 || i.Opaque != "" ||
                i.Framing != DeepSeekReasoningContinuationCodec.FramingName) == true)
                throw new ProjectChatNormalizationException();
            Sends++; LastRequest = request; LastBody = AgentRequestWriter.Write(request);
            var read = firstReadId is not null && Sends == 1;
            var arguments = read ? invalidArguments
                ? "{\"path\":\"a.cs\",\"start_line\":1,\"end_line\":1}"
                : "{\"path\":\"a.cs\",\"start_line\":1,\"line_count\":1}"
                : "{\"summary\":\"done\",\"findings\":[]}";
            var reasoning = read ? "current reasoning" : "reason " + Canary;
            var position = request.Messages.Length;
            // Typed fixture: no serialized provider response body; zero is not a wire measurement.
            return Task.FromResult(new ProjectChatResponse(new("assistant", [
                new ProjectReasoningContent(reasoning, "", DeepSeekReasoningContinuationCodec.FramingName, null, position, 0),
                new ProjectToolCallContent(read ? firstReadId! : id, read ? "read_file" : "finish_review", arguments)]),
                new(3, 2, new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Model, 0, 3)),
                CapturedResponseBodyBytes: 0, Continuation: new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Adapter,
                    "session-275", [new(reasoning, "", DeepSeekReasoningContinuationCodec.FramingName, null, position, 0)])));
        }
        public void Dispose() { LastBody = null; LastRequest = null; }
    }

    // Test-private capture only; neither the observer nor any public output retains requests.
    private sealed class CapturingClient(IProjectChatClient inner) : IProjectChatClient
    {
        internal ProjectChatRequest? LastRequest { get; private set; }
        public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
        { LastRequest = request; return inner.GetResponseAsync(request, token); }
    }

    private sealed class SyntheticReadExecutor(bool malformedResult = false) : IAgentToolExecutor
    {
        internal int Executions { get; private set; }
        internal AgentToolExecution? Execution { get; private set; }
        public string? Preflight(PreparedAgentToolCall call) => null;
        public ValueTask<AgentToolExecution> ExecuteAsync(PreparedAgentToolCall call, CancellationToken token)
        {
            var read = Assert.IsType<PreparedReadFileCall>(call);
            Executions++;
            var result = new ReadFileResult("ok", Identity, read.Arguments.Path,
                AgentCanonical.HashRaw(Encoding.UTF8.GetBytes("line " + Canary)), 1, 1, 1, 1,
                [new ReadFileLine(1, "line " + Canary)], false, null, null);
            var observationId = AgentCanonical.HashDomain(AgentCanonical.ReadObservationDomain,
                ReadFileResultWriter.Write(result, includeObservationId: false));
            var bytes = malformedResult ? "{\"lines\":[]}"u8.ToArray() :
                ReadFileResultWriter.Write(result with { ObservationId = observationId });
            Execution = new(true, null, Encoding.UTF8.GetString(bytes), bytes,
                new(observationId, Identity, ImmutableDictionary<string, ImmutableHashSet<int>>.Empty
                    .WithComparers(StringComparer.Ordinal).Add(read.Arguments.Path, ImmutableHashSet.Create(1))));
            return ValueTask.FromResult(Execution);
        }
    }

    private sealed class NoTools : IAgentToolExecutor
    {
        public string? Preflight(PreparedAgentToolCall call) => null;
        public ValueTask<AgentToolExecution> ExecuteAsync(PreparedAgentToolCall call, CancellationToken token) => throw new InvalidOperationException();
    }

    private sealed class FailingClient : IProjectChatClient
    {
        public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token) =>
            Task.FromException<ProjectChatResponse>(new IOException("synthetic_failure"));
    }
}
