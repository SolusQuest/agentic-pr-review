using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Quality;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.LiveAgentVerifierFixture;

internal sealed class VerifierScriptException(string code) : Exception
{ internal string Code { get; } = code; }

// Historical fixed-script observer. There is no socket, HTTP handler, endpoint,
// credential input or generic provider request/response implementation here.
internal sealed class VerifierHistoricalChat(VerifierScenario scenario, R3QualityCase testCase,
    ReviewedIdentity identity, string? expectedHistory, IReadOnlyList<byte[]> publicInputs,
    DeepSeekAdapterContext context, VerifierTypedObservationOwner observations) : IProjectChatClient, IDisposable
{
    private readonly VerifierAuthoredChatScript script = new(scenario, testCase, identity, expectedHistory, context);
    private int calls;
    private string? firstHash, failure;
    internal VerifierTypedProof Proof => new(failure is null && calls == script.ExpectedRequests && script.Completed,
        calls, 1, scenario == VerifierScenario.ContinuationRestore ? firstHash : null,
        script.ExpectedTerminalSha256, script.PriorFactSha256, script.HistoricalMessagesSha256,
        script.ExactReplayValidated, script.ReplayMutationMatrixValidated, failure,
        scenario == VerifierScenario.CanaryRouting && script.CanaryRoutesValidated, observations.Snapshot);

    public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
    {
        var ordinal = calls++;
        var observation = observations.Begin(request);
        var outcome = "failed";
        try
        {
            token.ThrowIfCancellationRequested();
            VerifierLogicalOracle.Validate(request, publicInputs, scenario);
            firstHash ??= LiveAgentFreshProcessDomain.RawSha256(AgentRequestWriter.Write(request));
            if (request.Accounting is { } capture && !capture.TryBeginDispatch()) throw new VerifierScriptException("typed_dispatch_refused");
            var response = script.Respond(ordinal, request);
            request.Accounting?.RecordUsage(ProviderUsageObservation.Create(3, 2, 1, 2, 5));
            outcome = "succeeded";
            return Task.FromResult(response);
        }
        catch (OperationCanceledException) { outcome = "cancelled"; failure = "typed_cancelled"; throw; }
        catch (VerifierScriptException error) { failure = error.Code; throw; }
        catch (ProjectChatNormalizationException) { failure = "typed_response_invalid"; throw; }
        catch { failure = "typed_script_failed"; throw; }
        finally { observations.Complete(observation, outcome); }
    }
    public void Dispose() { }
}

internal static class VerifierLogicalOracle
{
    internal static void Validate(ProjectChatRequest request, IReadOnlyList<byte[]> publicInputs, VerifierScenario? scenario = null)
    {
        if (!request.ThinkingRequired || request.Messages.Length == 0) throw new VerifierScriptException("typed_input_shape_invalid");
        if (!request.Tools.SequenceEqual(AgentToolRegistry.Definitions)) throw new VerifierScriptException("typed_tool_contract_invalid");
        var text = Encoding.UTF8.GetString(AgentRequestWriter.Write(request));
        var forbidden = new[] { "APR111_PROVIDER_SECRET_CANARY", "APR111_STATE_KEY_CANARY", "APR111_GITHUB_CANARY",
            "APR111_ACTIONS_CANARY", "APR111_UNRELATED_WORKFLOW_CANARY", VerifierCanaries.PublicResult };
        var untrusted = new[] { VerifierCanaries.Repository, VerifierCanaries.Path, VerifierCanaries.Prompt };
        if (forbidden.Any(value => text.Contains(value, StringComparison.Ordinal)) ||
            scenario != VerifierScenario.CanaryRouting && untrusted.Any(value => text.Contains(value, StringComparison.Ordinal)) ||
            publicInputs.Any(input => Encoding.UTF8.GetString(input).Contains("APR111_RANDOM_", StringComparison.Ordinal)))
            throw new VerifierScriptException("typed_canary_invalid");
    }
}

internal sealed class VerifierAuthoredChatScript
{
    private const string RandomPrefix = "APR111_RANDOM_";
    private readonly VerifierScenario scenario;
    private readonly R3QualityCase testCase;
    private readonly string? expectedHistorySha256;
    private readonly DeepSeekAdapterContext context;
    private ProjectChatRequest current = null!;
    private string? randomFact;
    internal VerifierAuthoredChatScript(VerifierScenario scenario, R3QualityCase testCase, ReviewedIdentity identity,
        string? expectedHistorySha256, DeepSeekAdapterContext context)
    {
        this.scenario = scenario; this.testCase = testCase; this.expectedHistorySha256 = expectedHistorySha256; this.context = context;
        if (scenario == VerifierScenario.ContinuationSeed)
            randomFact = RandomPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    }
    internal VerifierAuthoredChatScript(VerifierScenario scenario, R3QualityCase testCase)
        : this(scenario, testCase, testCase.ReviewedIdentity, null,
            new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Adapter, "unit-session")) { }

    internal int ExpectedRequests => scenario switch
    {
        VerifierScenario.MustFind or VerifierScenario.QualityFailedAfterCommit => 3,
        VerifierScenario.MustNotFind or VerifierScenario.PublicResultCanary => 4,
        VerifierScenario.ContinuationSeed or VerifierScenario.ContinuationRestore or VerifierScenario.ProviderHttpFailure or
            VerifierScenario.ProviderMalformedResponse or VerifierScenario.TerminalUngrounded => 1,
        VerifierScenario.CanaryRouting or VerifierScenario.ToolArgumentsInvalid => 2,
        VerifierScenario.OuterAuthorizationDenied or VerifierScenario.InnerAuthorizationDenied or
            VerifierScenario.TransitionFromHeadInvalid or VerifierScenario.LineageTampered => 0,
        _ => throw new InvalidOperationException(),
    };
    internal bool Completed { get; private set; }
    internal string? ExpectedTerminalSha256 { get; private set; }
    internal string? PriorFactSha256 => randomFact is null ? null : LiveAgentFreshProcessDomain.RawSha256(Encoding.UTF8.GetBytes(randomFact));
    internal string? HistoricalMessagesSha256 { get; private set; }
    internal bool ExactReplayValidated { get; private set; }
    internal bool ReplayMutationMatrixValidated { get; private set; }
    internal bool CanaryRoutesValidated { get; private set; }

    internal ProjectChatResponse Respond(int index, ProjectChatRequest request)
    {
        current = request;
        var logical = AgentRequestWriter.Write(request);
        return scenario switch
        {
            VerifierScenario.MustFind or VerifierScenario.QualityFailedAfterCommit => MustFind(index, logical),
            VerifierScenario.MustNotFind or VerifierScenario.PublicResultCanary => MustNotFind(index, logical),
            VerifierScenario.ContinuationSeed => Seed(index, request),
            VerifierScenario.ContinuationRestore => Restore(index, request),
            VerifierScenario.CanaryRouting => Canary(index, request),
            VerifierScenario.ProviderHttpFailure => throw new VerifierScriptException("script_failure_injected"),
            VerifierScenario.ProviderMalformedResponse => throw new ProjectChatNormalizationException(),
            VerifierScenario.ToolArgumentsInvalid => ToolArgumentsRecovery(index, request),
            VerifierScenario.TerminalUngrounded => Finish("Return an ungrounded terminal finding.",
                "{\"summary\":\"invalid\",\"findings\":[{\"severity\":\"high\",\"title\":\"ungrounded\",\"message\":\"invalid\",\"evidence\":[{\"observation_id\":\"" +
                new string('0', 64) + "\",\"path\":\"src/CacheGate.cs\",\"start_line\":1,\"end_line\":1}]}]}"),
            _ => throw new InvalidOperationException(),
        };
    }

    private ProjectChatResponse ToolArgumentsRecovery(int index, ProjectChatRequest request)
    {
        if (index == 0) return Tool("Attempt an invalid tool call.", "negative_invalid_arguments", AgentToolRegistry.ReadFileName,
            "{\"path\":\"src/CacheGate.cs\",\"unknown\":true}");
        var assistants = request.Messages.Where(m => m.Role == "assistant").ToArray();
        var tools = request.Messages.Where(m => m.Role == "tool").ToArray();
        if (index != 1 || assistants.Length != 1 || tools.Length != 1 ||
            assistants[0].Contents is not [ProjectRecoveryToolCallContent call] || call.CallId != "negative_invalid_arguments" ||
            call.Name != AgentToolRegistry.ReadFileName || call.ArgumentsJson != AgentRecoveryFeedback.RejectedArguments || !call.Rejected ||
            tools[0].Contents is not [ProjectToolErrorContent result] || result.CallId != call.CallId ||
            result.Result != AgentRecoveryFeedback.ArgumentsInvalid ||
            Encoding.UTF8.GetString(AgentRequestWriter.Write(request)).Contains("unknown\\\":true", StringComparison.Ordinal))
            throw new VerifierScriptException("recovery_feedback_invalid");
        throw new VerifierScriptException("recovery_followup_injected");
    }

    private ProjectChatResponse MustFind(int index, byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var messages = document.RootElement.GetProperty("messages");
        return index switch
        {
            0 => Tool(
                "Inspect changed-file metadata first.",
                "must_find_changed",
                AgentToolRegistry.ListChangedFilesName,
                "{}"),
            1 => Tool(
                "Read the exact changed diff.",
                "must_find_diff",
                AgentToolRegistry.ReadDiffName,
                "{\"path\":\"src/CacheGate.cs\",\"start_hunk\":1,\"hunk_count\":20}"),
            2 => Finish(
                "Report the grounded null-forgiving regression.",
                string.Concat(
                    "{\"summary\":\"Grounded synthetic defect.\",",
                    "\"findings\":[{\"severity\":\"high\",",
                    "\"title\":\"Null-forgiving regression\",",
                    "\"message\":\"The change hides a nullable failure.\",",
                    "\"evidence\":[{\"observation_id\":\"",
                    LastObservation(messages, "must_find_diff"),
                    "\",\"path\":\"src/CacheGate.cs\",",
                    "\"start_line\":4,\"end_line\":4}]}]}")),
            _ => throw new VerifierScriptException("script_index_invalid"),
        };
    }

    private ProjectChatResponse MustNotFind(int index, byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        return index switch
        {
            0 => Tool(
                "List the bounded repository paths.",
                "must_not_list",
                AgentToolRegistry.ListFilesName,
                "{}"),
            1 => Tool(
                "Search for the safe marker.",
                "must_not_search",
                AgentToolRegistry.SearchTextName,
                "{\"query\":\"APR110_SAFE_K2R8\",\"path\":\"src/ReadyGate.cs\"}"),
            2 => Tool(
                "Read the safe implementation.",
                "must_not_read",
                AgentToolRegistry.ReadFileName,
                "{\"path\":\"src/ReadyGate.cs\",\"start_line\":1,\"line_count\":400}"),
            3 => Finish(
                "No actionable defect is present.",
                "{\"summary\":\"The bounded change is safe.\",\"findings\":[]}"),
            _ => throw new VerifierScriptException("script_index_invalid"),
        };
    }

    private ProjectChatResponse Seed(int index, ProjectChatRequest request)
    {
        if (index != 0 || randomFact is null || request.Messages.Length != 2 ||
            request.Messages[0].Role != "system" || !ExactUser(request.Messages[1], testCase.ProcessOneContext) ||
            request.Messages.Any(m => Raw(m).Contains(randomFact, StringComparison.Ordinal)))
            throw new VerifierScriptException("seed_history_invalid");
        var response = Finish("", SeedFinishArguments(randomFact));
        var admitted = response.Message with { Contents = response.Message.Contents.Where(c => c is not ProjectReasoningContent).ToArray() };
        HistoricalMessagesSha256 = HistoryHash([.. request.Messages, admitted,
            new("tool", [new ProjectToolResultContent("seed_finish", "{}")])], response.Continuation!);
        return response;
    }

    private ProjectChatResponse Restore(int index, ProjectChatRequest request)
    {
        if (index != 0) throw new VerifierScriptException("script_index_invalid");
        randomFact = ValidateRestored(request);
        var item = request.Continuation!.Items[0];
        ProjectChatRequest Message(int at, ProjectChatMessage message)
        { var messages = request.Messages.ToArray(); messages[at] = message; return request with { Messages = messages }; }
        var seed = (ProjectToolCallContent)request.Messages[2].Contents.Single();
        ProjectChatRequest Call(ProjectToolCallContent call) => Message(2, new("assistant", [call]));
        ProjectChatRequest Continuation(ProjectContinuationItem value) => request with
        { Continuation = request.Continuation with { Items = [value] } };
        ProjectChatRequest[] mutations =
        [
            Message(2, request.Messages[2] with { Role = "user" }),
            Message(1, new("user", [new ProjectTextContent("changed initial request")])),
            request with { Continuation = null },
            request with { Continuation = request.Continuation with { Items = [] } },
            Continuation(item with { Readable = "changed reasoning" }),
            Continuation(item with { Opaque = "changed opaque" }),
            Continuation(item with { Framing = "changed framing" }),
            Continuation(item with { MessagePosition = 1 }),
            Continuation(item with { ContentPosition = 1 }),
            Continuation(item with { AssociatedCallId = "changed" }),
            Call(seed with { CallId = "changed" }),
            Call(seed with { Name = AgentToolRegistry.ReadDiffName }),
            Call(seed with { ArgumentsJson = "{}" }),
            Message(3, new("tool", [new ProjectToolResultContent("seed_finish", "{\"changed\":true}")])),
            Message(4, new("user", [new ProjectTextContent("changed current request")])),
        ];
        foreach (var mutation in mutations)
        {
            var refused = false;
            try { _ = ValidateRestored(mutation); }
            catch (VerifierScriptException) { refused = true; }
            if (!refused) throw new VerifierScriptException("restore_prefix_mutation_accepted");
        }
        ExactReplayValidated = true; ReplayMutationMatrixValidated = true;
        return Finish("Use only the restored prior fact.", "{\"summary\":\"Restored " +
            ((R3QualityContinuationExpectation)testCase.Expectation).PriorOnlyMarker + " and " + randomFact + ".\",\"findings\":[]}");
    }

    private string ValidateRestored(ProjectChatRequest request)
    {
        var values = request.Messages;
        if (values.Length != 5 || !values.Select(m => m.Role).SequenceEqual(["system", "user", "assistant", "tool", "user"]) ||
            values.Any(m => m.Contents.Any(c => c is ProjectReasoningContent)) ||
            !ExactUser(values[1], testCase.ProcessOneContext) || !ExactUser(values[4], testCase.InitialContext) ||
            values[2].Contents is not [ProjectToolCallContent call] || call.CallId != "seed_finish" || call.Name != AgentToolRegistry.FinishReviewName ||
            values[3].Contents is not [ProjectToolResultContent result] || result.CallId != call.CallId || result.Result != "{}" ||
            request.Continuation is not { } continuation || continuation.Items is not [var item] ||
            continuation.ProviderId != context.ProviderId || continuation.ModelId != context.ModelId ||
            continuation.AdapterId != context.AdapterId || continuation.SessionId != context.SessionId ||
            item.Readable != "" || item.Opaque != "" || item.Framing != DeepSeekAdapterContext.ReasoningFraming ||
            item.AssociatedCallId is not null || item.MessagePosition != 2 || item.ContentPosition != 0)
            throw new VerifierScriptException("restore_history_shape_invalid");
        string? fact;
        try
        {
            using var arguments = JsonDocument.Parse(call.ArgumentsJson);
            var summary = arguments.RootElement.GetProperty("summary").GetString()!;
            var prefix = "Seeded " + ((R3QualityContinuationExpectation)testCase.Expectation).PriorOnlyMarker + " and ";
            fact = summary.StartsWith(prefix, StringComparison.Ordinal) && summary.EndsWith(".", StringComparison.Ordinal)
                ? summary[prefix.Length..^1] : null;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new VerifierScriptException("restore_history_shape_invalid"); }
        if (fact is null || fact.Length != RandomPrefix.Length + 64 || !fact.StartsWith(RandomPrefix, StringComparison.Ordinal) ||
            fact.AsSpan(RandomPrefix.Length).ToArray().Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
            call.ArgumentsJson != SeedFinishArguments(fact) || new[] { values[0], values[1], values[3], values[4] }
                .Any(m => Raw(m).Contains(fact, StringComparison.Ordinal)))
            throw new VerifierScriptException("restore_history_shape_invalid");
        var actual = HistoryHash(values[..4], continuation);
        if (!LiveAgentFreshProcessDomain.IsSha256(expectedHistorySha256) || actual != expectedHistorySha256)
            throw new VerifierScriptException("restore_history_digest_invalid");
        HistoricalMessagesSha256 = actual;
        return fact;
    }

    private ProjectChatResponse Canary(int index, ProjectChatRequest request)
    {
        var values = request.Messages;
        var path = "src/" + VerifierCanaries.Path + ".cs";
        if (index == 0)
        {
            if (values.Length != 2 || new[] { VerifierCanaries.Repository, VerifierCanaries.Path, VerifierCanaries.Prompt }
                    .Any(c => Raw(values[0]).Contains(c, StringComparison.Ordinal)) ||
                !Raw(values[1]).Contains(VerifierCanaries.Prompt, StringComparison.Ordinal))
                throw new VerifierScriptException("canary_initial_route_invalid");
            return Tool("Read only the untrusted canary path.", "canary_read", AgentToolRegistry.ReadFileName,
                "{\"path\":\"" + path + "\",\"start_line\":1,\"line_count\":400}");
        }
        if (index != 1 || values.Length != 4) throw new VerifierScriptException("script_index_invalid");
        var system = Raw(values[0]); var assistant = Raw(values[^2]); var tool = Raw(values[^1]);
        if (new[] { VerifierCanaries.Repository, VerifierCanaries.Path, VerifierCanaries.Prompt }.Any(c => system.Contains(c, StringComparison.Ordinal)) ||
            !assistant.Contains(VerifierCanaries.Path, StringComparison.Ordinal) || assistant.Contains(VerifierCanaries.Repository, StringComparison.Ordinal) ||
            assistant.Contains(VerifierCanaries.Prompt, StringComparison.Ordinal) ||
            new[] { VerifierCanaries.Repository, VerifierCanaries.Path, VerifierCanaries.Prompt }.Any(c => !tool.Contains(c, StringComparison.Ordinal)))
            throw new VerifierScriptException("canary_tool_route_invalid");
        CanaryRoutesValidated = true;
        return Finish("Complete without promoting untrusted canaries.", "{\"summary\":\"Canary routing remained untrusted.\",\"findings\":[]}");
    }

    private ProjectChatResponse Finish(string reasoning, string arguments)
    {
        ExpectedTerminalSha256 = AgentCanonical.HashDomain(AgentCanonical.TerminalDomain, Encoding.UTF8.GetBytes(arguments));
        Completed = true;
        var id = VerifierScenarioDomain.ProviderBehavior(scenario) switch
        {
            VerifierScenario.MustFind => "must_find_finish", VerifierScenario.MustNotFind => "must_not_finish",
            VerifierScenario.ContinuationSeed => "seed_finish", VerifierScenario.ContinuationRestore => "restore_finish",
            VerifierScenario.CanaryRouting => "canary_finish", _ => throw new InvalidOperationException(),
        };
        return Tool(reasoning, id, AgentToolRegistry.FinishReviewName, arguments);
    }

    private ProjectChatResponse Tool(string reasoning, string id, string name, string arguments)
    {
        var position = current.Messages.Length;
        // Typed fixture: no serialized provider response body; zero is not a wire measurement.
        return new(new("assistant", [new ProjectReasoningContent(reasoning, "", DeepSeekAdapterContext.ReasoningFraming, null, position, 0),
            new ProjectToolCallContent(id, name, arguments)]),
            new(3, 2, new(context.ProviderId, context.ModelId, context.ModelId, 1, 2)), CapturedResponseBodyBytes: 0,
            Continuation: new(context.ProviderId, context.ModelId, context.AdapterId, context.SessionId,
                [new(reasoning, "", DeepSeekAdapterContext.ReasoningFraming, null, position, 0)]));
    }
    private static string LastObservation(JsonElement messages, string callId)
    {
        var results = messages.EnumerateArray().Where(m => m.GetProperty("role").GetString() == "tool")
            .SelectMany(m => m.GetProperty("contents").EnumerateArray()).Where(c => c.GetProperty("kind").GetString() == "tool_result" &&
                c.GetProperty("call_id").GetString() == callId).ToArray();
        if (results.Length != 1) throw new VerifierScriptException("observation_missing");
        using var result = JsonDocument.Parse(results[0].GetProperty("result").GetString()!);
        return result.RootElement.GetProperty("observation_id").GetString() ?? throw new VerifierScriptException("observation_missing");
    }
    private string SeedFinishArguments(string fact) => "{\"summary\":\"Seeded " +
        ((R3QualityContinuationExpectation)testCase.Expectation).PriorOnlyMarker + " and " + fact + ".\",\"findings\":[]}";
    private static bool ExactUser(ProjectChatMessage message, string? content) => message.Role == "user" &&
        message.Contents is [ProjectTextContent text] && content is not null && text.Text == content;
    private static string Raw(ProjectChatMessage message) => Encoding.UTF8.GetString(AgentRequestWriter.Write(new([message], [], null)));
    private static string HistoryHash(ProjectChatMessage[] messages, ProjectContinuation continuation) =>
        AgentCanonical.HashDomain("apr.historical.typed-seed-history.v1", AgentRequestWriter.Write(new(messages, [], continuation, ThinkingRequired: true)));
}
