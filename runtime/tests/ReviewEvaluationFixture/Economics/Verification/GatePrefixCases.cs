using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Prefix;
using static AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification.GateContracts;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;

internal static class GatePrefixCases
{
    private static AgentTrustedRequest Trusted => new("repo", 279, "workflow", Encoding.UTF8.GetBytes("policy " + Canary),
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
        var transport = new FinishScript("finish-old");
        var outcome = await new AgentLoop(Client(transport), new ReadExecutor()).RunAsync(run, CancellationToken.None);
        Require(outcome.CompletedSessionEligible);
        // Fixed typed history for the generic prefix gate. Current Solus
        // restoration is qualified by the installed-package V2 proof.
        var traceSha256 = new string('9', 64);
        Require(AgentStableRequestMaterializer.TryMaterialize(Trusted, traceSha256, out var resumedStable));
        var authoredRun = new AgentRunRequest(Identity, resumedStable!.StablePlan, run.SessionId,
            [.. resumedStable.ControlMessages, run.InitialMessages[^1],
                new("assistant", [new ProjectToolCallContent("finish-old", AgentToolRegistry.FinishReviewName,
                    Encoding.UTF8.GetString(outcome.Review!.CanonicalBytes))]),
                new("tool", [new ProjectToolResultContent("finish-old", "{}")]),
                User("current delta " + Canary)],
            new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Adapter, run.SessionId,
                [new("reason " + Canary, "", DeepSeekAdapterContext.ReasoningFraming, null, resumedStable.ControlMessages.Length + 1, 0)]));
        var boundary = PrefixBoundary.AuthoredThreeMessageHistoryForTests(Trusted, authoredRun);
        var request = Request(authoredRun);
        var original = PrefixMeasurement.Observe(boundary, request);
        void Add(string id, string selected, params PrefixObservation[] observations) => cases.Add(GateCase.Create(id, selected,
            JsonSerializer.SerializeToUtf8Bytes(new GatePrefix([.. observations],
                [.. observations.Skip(1).Select(observations[0].Compare)], []), GateJson.Default.GatePrefix)));

        // Observe the real typed callback independently of the direct logical projection.
        {
            var replayScript = new FinishScript();
            var capture = new CapturingClient(replayScript);
            var observed = new PrefixObservingChatClient(boundary, capture);
            Require((await new AgentLoop(observed, new ReadExecutor()).RunAsync(authoredRun, default)).CompletedSessionEligible);
            Require(observed.Observations.Length == 1 && observed.Observations[0] == original && replayScript.Sends == 1);
            Require(AgentRequestWriter.Write(capture.LastRequest!).AsSpan().SequenceEqual(AgentRequestWriter.Write(request)));
            Require(request.Messages.Select(m => m.Role).SequenceEqual(["system", "user", "assistant", "tool", "user"]));
            Require(request.Continuation!.Items.Single().Readable == "reason " + Canary &&
                request.Messages[2].Contents.OfType<ProjectToolCallContent>().Single().CallId == "finish-old");
        }
        Add("l1-bootstrap-restored", "authored-history", bootstrap, original);
        var suffixA = await Suffix(authoredRun, boundary, "dynamic-a");
        var suffixB = await Suffix(authoredRun, boundary, "dynamic-b");
        Add("l1-dynamic-suffix", "dynamic", original, suffixA.Observation, suffixB.Observation);

        var changedMessages = request.Messages.ToArray();
        changedMessages[0] = new("system", [new ProjectTextContent("changed policy")]);
        Add("l1-control", "policy", original, PrefixMeasurement.Observe(boundary, request with { Messages = changedMessages }));
        changedMessages = request.Messages.ToArray(); changedMessages[1] = User("changed prior context");
        Add("l1-history", "old-content", original, PrefixMeasurement.Observe(boundary, request with { Messages = changedMessages }));
        var changedTools = request.Tools.ToArray(); changedTools[0] = changedTools[0] with { Description = "changed declaration" };
        Add("l1-settings", "tools", original, PrefixMeasurement.Observe(boundary, request with { Tools = changedTools }));
        var items = suffixA.Request.Continuation!.Items;
        var swapped = suffixA.Request with { Continuation = suffixA.Request.Continuation with
        { Items = [items[0] with { Readable = items[1].Readable }, items[1] with { Readable = items[0].Readable }] } };
        Add("l1-continuation-position", "position", suffixA.Observation, PrefixMeasurement.Observe(boundary, swapped));
        var reordered = suffixA.Request with { Continuation = suffixA.Request.Continuation with { Items = items.Reverse().ToArray() } };
        Add("l1-continuation-order", "continuation-order", suffixA.Observation, PrefixMeasurement.Observe(boundary, reordered));
        Add("l1-domain", "session-domain", original, original with { Domain = original.Domain with { SessionSha256 = Hash('c') } });

        var item = request.Continuation!.Items[0];
        var rejections = 0;
        foreach (var invalid in new[] { item with { MessagePosition = 1 }, item with { MessagePosition = 99 },
            item with { ContentPosition = 99 }, item with { AssociatedCallId = "missing" } })
        {
            try { _ = PrefixMeasurement.Observe(boundary, request with { Continuation = request.Continuation with { Items = [invalid] } }); }
            catch (PrefixObservationException error) { Require(error.ToString() == "prefix_observation_invalid" && error.InnerException is null); rejections++; }
        }
        var rejectedTransport = new FinishScript();
        var refused = false;
        try
        {
            await Client(rejectedTransport).GetResponseAsync(request with
            { Continuation = request.Continuation with { Items = [item with { ContentPosition = 1 }] } }, default);
        }
        catch (ProjectChatNormalizationException) { refused = true; }
        Require(refused && rejections == 4 && rejectedTransport.Sends == 0);
        cases.Add(Scalar("l1-invalid-position", "invalid-position", "positions_rejected", [rejections, rejectedTransport.Sends]));
    }

    private static async Task<(ProjectChatRequest Request, PrefixObservation Observation)> Suffix(
        AgentRunRequest run, PrefixBoundary boundary, string id)
    {
        var transport = new FinishScript(firstReadId: id);
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

    private static IProjectChatClient Client(FinishScript script) => script;

    // Authored fixed script; no request/response JSON provider protocol.
    private sealed class FinishScript(string id = "finish-current", string? firstReadId = null) : IProjectChatClient
    {
        internal int Sends { get; private set; }
        public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try { PrefixMeasurement.ValidateContinuation(request); }
            catch (PrefixObservationException) { throw new ProjectChatNormalizationException(); }
            if (request.Continuation?.Items.Any(i => i.ContentPosition != 0 || i.Opaque != "" ||
                i.Framing != DeepSeekAdapterContext.ReasoningFraming) == true)
                throw new ProjectChatNormalizationException();
            Sends++;
            var read = firstReadId is not null && Sends == 1;
            var arguments = read ? "{\"path\":\"a.cs\",\"start_line\":1,\"line_count\":1}" : "{\"summary\":\"done\",\"findings\":[]}";
            var reasoning = read ? "current reasoning" : "reason " + Canary;
            var position = request.Messages.Length;
            // Typed fixture: no serialized provider response body; zero is not a wire measurement.
            return Task.FromResult(new ProjectChatResponse(new("assistant", [
                new ProjectReasoningContent(reasoning, "", DeepSeekAdapterContext.ReasoningFraming, null, position, 0),
                new ProjectToolCallContent(read ? firstReadId! : id, read ? "read_file" : "finish_review", arguments)]),
                new(3, 2, new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Model, 0, 3)),
                CapturedResponseBodyBytes: 0, Continuation: new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Adapter,
                    "session-279", [new(reasoning, "", DeepSeekAdapterContext.ReasoningFraming, null, position, 0)])));
        }
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
