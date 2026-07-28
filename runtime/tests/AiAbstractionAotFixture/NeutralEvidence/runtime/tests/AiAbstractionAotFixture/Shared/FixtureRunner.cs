using System.Text;
using AgenticPrReview.Runtime.Agent.Chat;

namespace AgenticPrReview.Runtime.AiAbstractionFixture;

internal static class FixtureRunner
{
    internal static async Task<FirstRunResult> RunFirstAsync(
        FirstFixtureInput input,
        ICandidateHarness harness,
        string scenario,
        string[] canaries,
        CancellationToken cancellationToken)
    {
        var tools = input.Tools
            .Select(tool => new ProjectToolDefinition(
                tool.Name,
                tool.Description,
                tool.SchemaJson))
            .ToArray();
        RequireOrderedTools(tools);
        var records = new List<LogicalRecord>
        {
            new("instruction", "system", input.Instructions, null, null, null),
            new("request", "user", input.UserRequest, null, null, null),
        };

        var request = new ProjectChatRequest(
            MessagesFrom(records),
            tools,
            Continuation: null);
        var first = await harness.ChatClient.GetResponseAsync(request, cancellationToken);
        var firstTurn = ValidateTurn(first, FixtureConstants.ReadCallId);
        var readResult = ToolValidation.ExecuteRead(firstTurn.Call, input, scenario);
        AddTurn(records, firstTurn, readResult);
        var continuation = ToContinuation(input, harness, firstTurn.Reasoning);

        request = new ProjectChatRequest(MessagesFrom(records), tools, continuation);
        var second = await harness.ChatClient.GetResponseAsync(request, cancellationToken);
        var secondTurn = ValidateTurn(second, FixtureConstants.SearchCallId);
        ValidateSameContinuation(
            firstTurn.Reasoning,
            secondTurn.Reasoning,
            compareAssociation: false);
        var searchResult = ToolValidation.ExecuteSearch(secondTurn.Call, input);
        AddTurn(records, secondTurn, searchResult);

        request = new ProjectChatRequest(MessagesFrom(records), tools, continuation);
        var terminal = await harness.ChatClient.GetResponseAsync(request, cancellationToken);
        var terminalTurn = ValidateTurn(terminal, FixtureConstants.FinishCallId);
        ValidateSameContinuation(
            firstTurn.Reasoning,
            terminalTurn.Reasoning,
            compareAssociation: false);
        var review = ToolValidation.ExecuteFinish(
            terminalTurn.Call,
            FixtureConstants.FinishCallId,
            FixtureConstants.TerminalSummary);
        AddTerminal(records, terminalTurn);

        var recordArray = records.ToArray();
        var projectionBytes = FixtureJson.Serialize(harness.Probe.Requests.ToArray());
        var recordsBytes = FixtureJson.Serialize(recordArray);
        var storedContinuation = new StoredContinuation(
            firstTurn.Reasoning.Text,
            firstTurn.Reasoning.Opaque,
            firstTurn.Reasoning.Framing,
            firstTurn.Reasoning.AssociatedCallId,
            firstTurn.Reasoning.MessagePosition,
            firstTurn.Reasoning.Position,
            FixtureHash.Text(firstTurn.Reasoning.Text),
            FixtureHash.Text(firstTurn.Reasoning.Opaque));
        var state = new ProofState(
            FixtureConstants.ProofFormat,
            harness.CandidateName,
            input.ProviderId,
            input.ModelId,
            harness.AdapterId,
            input.SessionId,
            recordArray,
            storedContinuation,
            review,
            FixtureHash.Bytes(projectionBytes));
        var evidence = new FirstEvidence(
            "first",
            FixtureHash.Bytes(recordsBytes),
            FixtureHash.Bytes(projectionBytes),
            FixtureConstants.PrefixIdentity,
            tools.Select(tool => tool.Name).ToArray(),
            [FixtureConstants.ReadCallId, FixtureConstants.SearchCallId, FixtureConstants.FinishCallId],
            [FixtureHash.Text(readResult), FixtureHash.Text(searchResult)],
            ToEvidence(storedContinuation),
            review.Summary);

        var stateBytes = FixtureJson.Serialize(state);
        var evidenceBytes = FixtureJson.Serialize(evidence);
        CanaryGuard.EnsureAbsent(canaries, stateBytes, evidenceBytes, projectionBytes);
        return new FirstRunResult(state, evidence);
    }

    internal static async Task<ResumeRunResult> RunResumeAsync(
        ResumeFixtureInput input,
        ProofState state,
        ICandidateHarness harness,
        string[] canaries,
        CancellationToken cancellationToken)
    {
        ValidateRestore(input, state, harness);
        var tools = input.Tools
            .Select(tool => new ProjectToolDefinition(
                tool.Name,
                tool.Description,
                tool.SchemaJson))
            .ToArray();
        RequireOrderedTools(tools);
        var priorFact = FindPriorFact(state.Records);
        var continuation = new ProjectContinuation(
            state.ProviderId,
            state.ModelId,
            state.AdapterId,
            state.SessionId,
            state.Continuation.Readable,
            state.Continuation.Opaque,
            state.Continuation.Framing,
            state.Continuation.AssociatedCallId,
            state.Continuation.MessagePosition,
            state.Continuation.ContentPosition);
        var request = new ProjectChatRequest(
            MessagesFrom(state.Records),
            tools,
            continuation);
        var response = await harness.ChatClient.GetResponseAsync(request, cancellationToken);
        var terminalTurn = ValidateTurn(response, FixtureConstants.ResumeFinishCallId);
        ValidateSameContinuation(
            new ProjectReasoningContent(
                state.Continuation.Readable,
                state.Continuation.Opaque,
                state.Continuation.Framing,
                FixtureConstants.ResumeFinishCallId,
                state.Continuation.MessagePosition,
                state.Continuation.ContentPosition),
            terminalTurn.Reasoning,
            compareAssociation: false);
        var review = ToolValidation.ExecuteFinish(
            terminalTurn.Call,
            FixtureConstants.ResumeFinishCallId,
            FixtureConstants.ResumeTerminalSummary);

        var recordsBytes = FixtureJson.Serialize(state.Records);
        var projectionBytes = FixtureJson.Serialize(harness.Probe.Requests.ToArray());
        var evidence = new ResumeEvidence(
            "resume",
            FixtureHash.Bytes(recordsBytes),
            FixtureHash.Bytes(projectionBytes),
            FixtureHash.Text(priorFact),
            FixtureConstants.PrefixIdentity,
            ToEvidence(state.Continuation),
            review.Summary);
        var evidenceBytes = FixtureJson.Serialize(evidence);
        CanaryGuard.EnsureAbsent(
            canaries,
            recordsBytes,
            projectionBytes,
            evidenceBytes);
        return new ResumeRunResult(evidence);
    }

    private static void RequireOrderedTools(ProjectToolDefinition[] tools)
    {
        var names = tools.Select(tool => tool.Name).ToArray();
        if (!names.SequenceEqual(["read_file", "search_text", "finish_review"]))
        {
            throw new FixtureFailure("APR_AI_TOOL_ORDER");
        }
        if (tools.Any(tool =>
            string.IsNullOrWhiteSpace(tool.Description) ||
            string.IsNullOrWhiteSpace(tool.SchemaJson)))
        {
            throw new FixtureFailure("APR_AI_TOOL_SCHEMA");
        }
    }

    private static ValidatedTurn ValidateTurn(
        ProjectChatResponse response,
        string expectedCallId)
    {
        if (response.Message.Role != "assistant")
        {
            throw new FixtureFailure("APR_AI_PROVIDER_ROLE");
        }
        if (response.Message.Contents.Length != 3 ||
            response.Message.Contents[0] is not ProjectTextContent text ||
            response.Message.Contents[1] is not ProjectReasoningContent reasoning ||
            response.Message.Contents[2] is not ProjectToolCallContent call)
        {
            throw new FixtureFailure("APR_AI_ORDERING");
        }
        if (Encoding.UTF8.GetByteCount(reasoning.Text) > FixtureConstants.MaxContinuationBytes ||
            Encoding.UTF8.GetByteCount(reasoning.Opaque) > FixtureConstants.MaxContinuationBytes)
        {
            throw new FixtureFailure("APR_AI_CONTINUATION_OVERSIZED");
        }
        if (reasoning.Text != FixtureConstants.ReadableContinuation ||
            reasoning.Opaque != FixtureConstants.OpaqueContinuation ||
            reasoning.Framing != FixtureConstants.ReasoningFraming ||
            reasoning.AssociatedCallId != expectedCallId ||
            reasoning.MessagePosition != 2 ||
            reasoning.Position != 1)
        {
            throw new FixtureFailure("APR_AI_CONTINUATION");
        }
        return new ValidatedTurn(
            text.Text,
            reasoning,
            new ProjectToolCall(call.CallId, call.Name, call.ArgumentsJson));
    }

    private static void ValidateSameContinuation(
        ProjectReasoningContent expected,
        ProjectReasoningContent actual,
        bool compareAssociation = true)
    {
        if (actual.Text != expected.Text ||
            actual.Opaque != expected.Opaque ||
            actual.Framing != expected.Framing ||
            actual.MessagePosition != expected.MessagePosition ||
            actual.Position != expected.Position ||
            (compareAssociation && actual.AssociatedCallId != expected.AssociatedCallId))
        {
            throw new FixtureFailure("APR_AI_CONTINUATION");
        }
    }

    private static ProjectContinuation ToContinuation(
        FirstFixtureInput input,
        ICandidateHarness harness,
        ProjectReasoningContent reasoning) => new(
            input.ProviderId,
            input.ModelId,
            harness.AdapterId,
            input.SessionId,
            reasoning.Text,
            reasoning.Opaque,
            reasoning.Framing,
            reasoning.AssociatedCallId,
            reasoning.MessagePosition,
            reasoning.Position);

    private static void AddTurn(
        List<LogicalRecord> records,
        ValidatedTurn turn,
        string result)
    {
        records.Add(new LogicalRecord(
            "assistant_text",
            "assistant",
            turn.Text,
            null,
            null,
            null));
        records.Add(new LogicalRecord(
            "tool_call",
            "assistant",
            null,
            turn.Call.CallId,
            turn.Call.Name,
            turn.Call.ArgumentsJson));
        records.Add(new LogicalRecord(
            "tool_result",
            "tool",
            null,
            turn.Call.CallId,
            turn.Call.Name,
            result));
    }

    private static void AddTerminal(
        List<LogicalRecord> records,
        ValidatedTurn terminal)
    {
        records.Add(new LogicalRecord(
            "assistant_text",
            "assistant",
            terminal.Text,
            null,
            null,
            null));
        records.Add(new LogicalRecord(
            "terminal_call",
            "assistant",
            null,
            terminal.Call.CallId,
            terminal.Call.Name,
            terminal.Call.ArgumentsJson));
    }

    private static ProjectChatMessage[] MessagesFrom(IEnumerable<LogicalRecord> records) =>
        records.Select(record => record.Kind switch
        {
            "instruction" or "request" or "assistant_text" =>
                new ProjectChatMessage(
                    record.Role,
                    [new ProjectTextContent(record.Text!)]),
            "tool_call" or "terminal_call" =>
                new ProjectChatMessage(
                    record.Role,
                    [new ProjectToolCallContent(
                        record.CallId!,
                        record.ToolName!,
                        record.Result!)]),
            "tool_result" =>
                new ProjectChatMessage(
                    record.Role,
                    [new ProjectToolResultContent(
                        record.CallId!,
                        record.Result!)]),
            _ => throw new FixtureFailure("APR_AI_STATE_RECORD"),
        }).ToArray();

    private static void ValidateRestore(
        ResumeFixtureInput input,
        ProofState state,
        ICandidateHarness harness)
    {
        if (state.Format != FixtureConstants.ProofFormat ||
            state.Candidate != harness.CandidateName ||
            state.ProviderId != input.ProviderId ||
            state.ModelId != input.ModelId ||
            state.AdapterId != harness.AdapterId ||
            state.SessionId != input.SessionId)
        {
            throw new FixtureFailure("APR_AI_RESTORE_BINDING");
        }
        if (FixtureHash.Text(state.Continuation.Readable) != state.Continuation.ReadableSha256 ||
            FixtureHash.Text(state.Continuation.Opaque) != state.Continuation.OpaqueSha256 ||
            state.Continuation.Readable != FixtureConstants.ReadableContinuation ||
            state.Continuation.Opaque != FixtureConstants.OpaqueContinuation ||
            state.Continuation.Framing != FixtureConstants.ReasoningFraming ||
            state.Continuation.MessagePosition != 2 ||
            state.Continuation.ContentPosition != 1)
        {
            throw new FixtureFailure("APR_AI_CONTINUATION");
        }

        var calls = state.Records
            .Where(record => record.Kind is "tool_call" or "terminal_call")
            .Select(record => record.CallId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var result in state.Records.Where(record => record.Kind == "tool_result"))
        {
            if (result.CallId is null || !calls.Contains(result.CallId))
            {
                throw new FixtureFailure("APR_AI_ASSOCIATION");
            }
        }
    }

    private static string FindPriorFact(LogicalRecord[] records)
    {
        const string priorFact = "PRIOR_ONLY_FACT_81_9f6d3a";
        if (!records.Any(record =>
            record.Kind == "tool_result" &&
            record.Result?.Contains(priorFact, StringComparison.Ordinal) == true))
        {
            throw new FixtureFailure("APR_AI_RESTORE_ORACLE");
        }
        return priorFact;
    }

    private static ContinuationEvidence ToEvidence(StoredContinuation continuation) => new(
        continuation.ReadableSha256,
        continuation.OpaqueSha256,
        continuation.Framing,
        continuation.AssociatedCallId,
        continuation.MessagePosition,
        continuation.ContentPosition);

    private static ContinuationEvidence ToEvidence(ProjectReasoningContent continuation) => new(
        FixtureHash.Text(continuation.Text),
        FixtureHash.Text(continuation.Opaque),
        continuation.Framing,
        continuation.AssociatedCallId,
        continuation.MessagePosition,
        continuation.Position);
}

internal sealed record ValidatedTurn(
    string Text,
    ProjectReasoningContent Reasoning,
    ProjectToolCall Call);
