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
using static AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification.GateContracts;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;

internal static class GatePrefixCases
{
    private static AgentSessionTrustedRequest Trusted => new("repo", 279, "workflow", Encoding.UTF8.GetBytes("policy " + Canary),
        "build", DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Adapter);
    private static ReviewedIdentity Identity => new("repo", 279, new('0', 40), new('1', 40));
    private static ProjectChatMessage User(string value) => new("user", [new ProjectTextContent(value)]);
    private static ProjectChatRequest Request(AgentRunRequest run) => new(run.InitialMessages,
        AgentToolRegistry.Definitions.ToArray(), run.Continuation, ThinkingRequired: true);

    internal static async Task RunAsync(List<GateCase> cases)
    {
        Require(AgentStableRequestMaterializer.TryMaterialize(Trusted, null, out var stable));
        var run = new AgentRunRequest(Identity, stable!.StablePlan, "session-279", [.. stable.ControlMessages, User("old context " + Canary)]);
        var bootstrap = PrefixMeasurement.Observe(PrefixBoundary.Bootstrap(Trusted, run), Request(run));
        using var transport = new FinishTransport("finish-old");
        var outcome = await new AgentLoop(Client(transport), new ReadExecutor()).RunAsync(run, CancellationToken.None);
        Require(outcome.CompletedSessionEligible);
        var built = AgentSessionBuilder.Build(new(run, outcome, Trusted, run.InitialMessages.Length - 1,
            DeepSeekReasoningContinuationCodec.Instance, null, AgentSessionHeadTransition.SameHead));
        Require(built.Succeeded);
        var artifact = built.Artifact!;
        var restored = AgentSessionRestorer.Restore(new(AgentSessionLocatorFamily.Current, AgentSessionRestoreIntent.Automatic,
            false, artifact.Plaintext, new(0, artifact.SessionSha256, new('e', 64), Identity.BaseSha, Identity.HeadSha, null),
            Trusted, run.SessionId, Identity, User("current delta " + Canary), AgentSessionHeadTransition.SameHead,
            DeepSeekReasoningContinuationCodec.Instance));
        Require(restored.Succeeded);
        var boundary = PrefixBoundary.Restored(Trusted, restored);
        var request = Request(restored.RunRequest!);
        var original = PrefixMeasurement.Observe(boundary, request);
        void Add(string id, string selected, params PrefixObservation[] observations) => cases.Add(GateCase.Create(id, selected,
            JsonSerializer.SerializeToUtf8Bytes(new GatePrefix([.. observations],
                [.. observations.Skip(1).Select(observations[0].Compare)], []), GateJson.Default.GatePrefix)));

        // Observe the actual backend request as well as the direct projection.
        using (var replayTransport = new FinishTransport())
        {
            var observed = new PrefixObservingChatClient(boundary, Client(replayTransport));
            Require((await new AgentLoop(observed, new ReadExecutor()).RunAsync(restored.RunRequest!, default)).CompletedSessionEligible);
            Require(observed.Observations.Length == 1 && observed.Observations[0] == original);
            var written = DeepSeekRequestWriter.Write(MinimalChatClient.Materialize(request));
            Require(written.Body.AsSpan().SequenceEqual(replayTransport.LastBody));
            using var wire = JsonDocument.Parse(replayTransport.LastBody!);
            var messages = wire.RootElement.GetProperty("messages");
            Require(messages.EnumerateArray().Select(m => m.GetProperty("role").GetString())
                .SequenceEqual(["system", "user", "assistant", "tool", "user"]));
            Require(messages[2].GetProperty("reasoning_content").GetString() == "reason " + Canary &&
                messages[2].GetProperty("tool_calls")[0].GetProperty("id").GetString() == "finish-old");
        }
        Add("p1-bootstrap-restored", "actual-restore", bootstrap, original);
        var suffixA = await Suffix(restored.RunRequest!, boundary, "dynamic-a");
        var suffixB = await Suffix(restored.RunRequest!, boundary, "dynamic-b");
        Add("p1-dynamic-suffix", "dynamic", original, suffixA.Observation, suffixB.Observation);

        var changedMessages = request.Messages.ToArray();
        changedMessages[0] = new("system", [new ProjectTextContent("changed policy")]);
        Add("p1-control", "policy", original, PrefixMeasurement.Observe(boundary, request with { Messages = changedMessages }));
        changedMessages = request.Messages.ToArray(); changedMessages[1] = User("changed prior context");
        Add("p1-history", "old-content", original, PrefixMeasurement.Observe(boundary, request with { Messages = changedMessages }));
        var changedTools = request.Tools.ToArray(); changedTools[0] = changedTools[0] with { Description = "changed declaration" };
        Add("p1-settings", "tools", original, PrefixMeasurement.Observe(boundary, request with { Tools = changedTools }));
        var items = suffixA.Request.Continuation!.Items;
        var swapped = suffixA.Request with { Continuation = suffixA.Request.Continuation with
        { Items = [items[0] with { Readable = items[1].Readable }, items[1] with { Readable = items[0].Readable }] } };
        Add("p1-continuation-position", "position", suffixA.Observation, PrefixMeasurement.Observe(boundary, swapped));
        var reordered = suffixA.Request with { Continuation = suffixA.Request.Continuation with { Items = items.Reverse().ToArray() } };
        Add("p1-logical-only", "logical-order", suffixA.Observation, PrefixMeasurement.Observe(boundary, reordered));
        Add("p1-domain", "session-domain", original, original with { Domain = original.Domain with { SessionSha256 = Hash('c') } });

        var item = request.Continuation!.Items[0];
        var rejections = 0;
        foreach (var invalid in new[] { item with { MessagePosition = 1 }, item with { MessagePosition = 99 },
            item with { ContentPosition = 99 }, item with { AssociatedCallId = "missing" } })
        {
            try { _ = PrefixMeasurement.Observe(boundary, request with { Continuation = request.Continuation with { Items = [invalid] } }); }
            catch (PrefixObservationException error) { Require(error.ToString() == "prefix_observation_invalid" && error.InnerException is null); rejections++; }
        }
        using var rejectedTransport = new FinishTransport();
        var refused = false;
        try
        {
            await Client(rejectedTransport).GetResponseAsync(request with
            { Continuation = request.Continuation with { Items = [item with { ContentPosition = 1 }] } }, default);
        }
        catch (ProjectChatNormalizationException) { refused = true; }
        Require(refused && rejections == 4 && rejectedTransport.Sends == 0);
        cases.Add(Scalar("p1-invalid-position", "invalid-position", "positions_rejected", [rejections, rejectedTransport.Sends]));
    }

    private static async Task<(ProjectChatRequest Request, PrefixObservation Observation)> Suffix(
        AgentRunRequest run, PrefixBoundary boundary, string id)
    {
        using var transport = new FinishTransport(firstReadId: id);
        var capture = new CapturingClient(Client(transport));
        var observer = new PrefixObservingChatClient(boundary, capture);
        var executor = new ReadExecutor();
        var outcome = await new AgentLoop(observer, executor).RunAsync(run, default);
        Require(outcome.CompletedSessionEligible && transport.Sends == 2 && observer.Observations.Length == 2 && executor.Executions == 1);
        var request = capture.LastRequest!;
        var call = request.Messages[^2].Contents.Single() as ProjectToolCallContent;
        var result = request.Messages[^1].Contents.Single() as ProjectToolResultContent;
        Require(call?.CallId == id && result?.CallId == id);
        Require(AgentToolArguments.TryReadFileProvider(call!.ArgumentsJson, out var arguments));
        Require(AgentToolResultAdmission.TryAdmit(new PreparedReadFileCall(id, arguments!), Identity,
            executor.Execution!, out var canonical, out _) && result!.Result == canonical);
        return (request, observer.Observations[1]);
    }

    private static IProjectChatClient Client(FinishTransport transport) => DeepSeekChatBackend.CreateClient(
        new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Adapter, "session-279"), transport);

    private sealed class FinishTransport(string id = "finish-current", string? firstReadId = null) : IDeepSeekTransport
    {
        internal byte[]? LastBody { get; private set; }
        internal int Sends { get; private set; }
        public Task<DeepSeekTransportResult> SendAsync(ReadOnlyMemory<byte> requestBody, CancellationToken cancellationToken)
        {
            Sends++; LastBody = requestBody.ToArray();
            var read = firstReadId is not null && Sends == 1;
            var arguments = read ? "{\"path\":\"a.cs\",\"start_line\":1,\"line_count\":1}" : "{\"summary\":\"done\",\"findings\":[]}";
            using var response = new MemoryStream();
            using (var writer = new Utf8JsonWriter(response))
            {
                writer.WriteStartObject(); writer.WriteString("model", DeepSeekAdapterContext.Model);
                writer.WriteStartArray("choices"); writer.WriteStartObject(); writer.WriteNumber("index", 0);
                writer.WriteString("finish_reason", "tool_calls"); writer.WriteStartObject("message");
                writer.WriteString("role", "assistant"); writer.WriteString("content", "");
                writer.WriteString("reasoning_content", read ? "current reasoning" : "reason " + Canary);
                writer.WriteStartArray("tool_calls"); writer.WriteStartObject(); writer.WriteString("id", read ? firstReadId : id);
                writer.WriteString("type", "function"); writer.WriteStartObject("function");
                writer.WriteString("name", read ? "read_file" : "finish_review"); writer.WriteString("arguments", arguments);
                writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndArray();
                writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndArray();
                writer.WriteStartObject("usage"); writer.WriteNumber("prompt_tokens", 3); writer.WriteNumber("completion_tokens", 2);
                writer.WriteNumber("total_tokens", 5); writer.WriteNumber("prompt_cache_hit_tokens", 0);
                writer.WriteNumber("prompt_cache_miss_tokens", 3); writer.WriteEndObject(); writer.WriteEndObject();
            }
            return Task.FromResult(DeepSeekTransportResult.Success(response.ToArray()));
        }
        public void Dispose() { LastBody = null; }
    }

    private sealed class CapturingClient(IProjectChatClient inner) : IProjectChatClient
    {
        internal ProjectChatRequest? LastRequest { get; private set; }
        public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
        { LastRequest = request; return inner.GetResponseAsync(request, token); }
    }

    private sealed class ReadExecutor : IAgentToolExecutor
    {
        internal int Executions { get; private set; }
        internal AgentToolExecution? Execution { get; private set; }
        public string? Preflight(PreparedAgentToolCall call) => null;
        public ValueTask<AgentToolExecution> ExecuteAsync(PreparedAgentToolCall call, CancellationToken token)
        {
            Require(call is PreparedReadFileCall);
            var read = (PreparedReadFileCall)call;
            Executions++;
            var result = new ReadFileResult("ok", Identity, read.Arguments.Path,
                AgentCanonical.HashRaw(Encoding.UTF8.GetBytes("line " + Canary)), 1, 1, 1, 1,
                [new ReadFileLine(1, "line " + Canary)], false, null, null);
            var observationId = AgentCanonical.HashDomain(AgentCanonical.ReadObservationDomain,
                ReadFileResultWriter.Write(result, includeObservationId: false));
            var bytes = ReadFileResultWriter.Write(result with { ObservationId = observationId });
            Execution = new(true, null, Encoding.UTF8.GetString(bytes), bytes,
                new(observationId, Identity, ImmutableDictionary<string, ImmutableHashSet<int>>.Empty
                    .WithComparers(StringComparer.Ordinal).Add(read.Arguments.Path, ImmutableHashSet.Create(1))));
            return ValueTask.FromResult(Execution);
        }
    }
}
