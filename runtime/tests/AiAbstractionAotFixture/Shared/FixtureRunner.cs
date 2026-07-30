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
            new("instruction", "system", input.Instructions, null, null, null, 0, 0),
            new("request", "user", input.UserRequest, null, null, null, 1, 0),
        };
        var continuations = new List<StoredContinuation>();

        var request = new ProjectChatRequest(
            MessagesFrom(records),
            tools,
            Continuation: null);
        var first = await harness.ChatClient.GetResponseAsync(request, cancellationToken);
        var firstTurn = ValidateTurn(
            first,
            FixtureConstants.ReadCallId,
            request.Messages.Length);
        var readResult = ToolValidation.ExecuteRead(firstTurn.Call, input, scenario);
        AddTurn(records, firstTurn, readResult);
        continuations.Add(ToStoredContinuation(firstTurn.Reasoning));

        request = new ProjectChatRequest(
            MessagesFrom(records),
            tools,
            ToContinuation(input, harness, continuations));
        var second = await harness.ChatClient.GetResponseAsync(request, cancellationToken);
        var secondTurn = ValidateTurn(
            second,
            FixtureConstants.SearchCallId,
            request.Messages.Length);
        ValidateSameContinuationPayload(firstTurn.Reasoning, secondTurn.Reasoning);
        var searchResult = ToolValidation.ExecuteSearch(secondTurn.Call, input);
        AddTurn(records, secondTurn, searchResult);
        continuations.Add(ToStoredContinuation(secondTurn.Reasoning));

        request = new ProjectChatRequest(
            MessagesFrom(records),
            tools,
            ToContinuation(input, harness, continuations));
        var terminal = await harness.ChatClient.GetResponseAsync(request, cancellationToken);
        var terminalTurn = ValidateTurn(
            terminal,
            FixtureConstants.FinishCallId,
            request.Messages.Length);
        ValidateSameContinuationPayload(firstTurn.Reasoning, terminalTurn.Reasoning);
        var review = ToolValidation.ExecuteFinish(
            terminalTurn.Call,
            FixtureConstants.FinishCallId,
            FixtureConstants.TerminalSummary);
        AddTerminal(records, terminalTurn);
        continuations.Add(ToStoredContinuation(terminalTurn.Reasoning));

        var recordArray = records.ToArray();
        var projectionBytes = FixtureJson.Serialize(harness.Probe.Requests.ToArray());
        var recordsBytes = FixtureJson.Serialize(recordArray);
        var storedContinuations = continuations.ToArray();
        var state = new ProofState(
            FixtureConstants.ProofFormat,
            harness.CandidateName,
            input.ProviderId,
            input.ModelId,
            harness.AdapterId,
            input.SessionId,
            recordArray,
            storedContinuations,
            review);
        var evidence = new FirstEvidence(
            "first",
            FixtureHash.Bytes(recordsBytes),
            FixtureHash.Bytes(projectionBytes),
            FixtureConstants.PrefixIdentity,
            tools.Select(tool => tool.Name).ToArray(),
            [FixtureConstants.ReadCallId, FixtureConstants.SearchCallId, FixtureConstants.FinishCallId],
            [FixtureHash.Text(readResult), FixtureHash.Text(searchResult)],
            storedContinuations.Select(ToEvidence).ToArray(),
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
        var records = state.Records.ToList();
        var nextMessagePosition =
            state.Records.Max(record => record.MessagePosition) + 1;
        records.Add(new LogicalRecord(
            "instruction",
            "system",
            input.Instructions,
            null,
            null,
            null,
            nextMessagePosition,
            0));
        records.Add(new LogicalRecord(
            "request",
            "user",
            input.UserRequest,
            null,
            null,
            null,
            nextMessagePosition + 1,
            0));
        var continuation = new ProjectContinuation(
            state.ProviderId,
            state.ModelId,
            state.AdapterId,
            state.SessionId,
            state.Continuations.Select(item => new ProjectContinuationItem(
                item.Readable,
                item.Opaque,
                item.Framing,
                item.AssociatedCallId,
                item.MessagePosition,
                item.ContentPosition)).ToArray());
        var request = new ProjectChatRequest(
            MessagesFrom(records),
            tools,
            continuation);
        var response = await harness.ChatClient.GetResponseAsync(request, cancellationToken);
        var terminalTurn = ValidateTurn(
            response,
            FixtureConstants.ResumeFinishCallId,
            request.Messages.Length);
        ValidateSameContinuationPayload(
            new ProjectReasoningContent(
                state.Continuations[0].Readable,
                state.Continuations[0].Opaque,
                state.Continuations[0].Framing,
                FixtureConstants.ResumeFinishCallId,
                terminalTurn.Reasoning.MessagePosition,
                terminalTurn.Reasoning.Position),
            terminalTurn.Reasoning);
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
            FixtureHash.Text(input.Instructions),
            FixtureHash.Text(input.UserRequest),
            FixtureConstants.PrefixIdentity,
            state.Continuations.Select(ToEvidence).ToArray(),
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
        string expectedCallId,
        int expectedMessagePosition)
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
            reasoning.MessagePosition != expectedMessagePosition ||
            reasoning.Position != 1)
        {
            throw new FixtureFailure("APR_AI_CONTINUATION");
        }
        return new ValidatedTurn(
            text.Text,
            reasoning,
            new ProjectToolCall(call.CallId, call.Name, call.ArgumentsJson));
    }

    private static void ValidateSameContinuationPayload(
        ProjectReasoningContent expected,
        ProjectReasoningContent actual)
    {
        if (actual.Text != expected.Text ||
            actual.Opaque != expected.Opaque ||
            actual.Framing != expected.Framing)
        {
            throw new FixtureFailure("APR_AI_CONTINUATION");
        }
    }

    private static ProjectContinuation ToContinuation(
        FirstFixtureInput input,
        ICandidateHarness harness,
        IEnumerable<StoredContinuation> continuations) => new(
            input.ProviderId,
            input.ModelId,
            harness.AdapterId,
            input.SessionId,
            continuations.Select(item => new ProjectContinuationItem(
                item.Readable,
                item.Opaque,
                item.Framing,
                item.AssociatedCallId,
                item.MessagePosition,
                item.ContentPosition)).ToArray());

    private static StoredContinuation ToStoredContinuation(
        ProjectReasoningContent reasoning) => new(
            reasoning.Text,
            reasoning.Opaque,
            reasoning.Framing,
            reasoning.AssociatedCallId,
            reasoning.MessagePosition,
            reasoning.Position,
            FixtureHash.Text(reasoning.Text),
            FixtureHash.Text(reasoning.Opaque));

    private static void AddTurn(
        List<LogicalRecord> records,
        ValidatedTurn turn,
        string result)
    {
        var messagePosition = turn.Reasoning.MessagePosition;
        records.Add(new LogicalRecord(
            "assistant_text",
            "assistant",
            turn.Text,
            null,
            null,
            null,
            messagePosition,
            0));
        records.Add(new LogicalRecord(
            "tool_call",
            "assistant",
            null,
            turn.Call.CallId,
            turn.Call.Name,
            turn.Call.ArgumentsJson,
            messagePosition,
            2));
        records.Add(new LogicalRecord(
            "tool_result",
            "tool",
            null,
            turn.Call.CallId,
            turn.Call.Name,
            result,
            messagePosition + 1,
            0));
    }

    private static void AddTerminal(
        List<LogicalRecord> records,
        ValidatedTurn terminal)
    {
        var messagePosition = terminal.Reasoning.MessagePosition;
        records.Add(new LogicalRecord(
            "assistant_text",
            "assistant",
            terminal.Text,
            null,
            null,
            null,
            messagePosition,
            0));
        records.Add(new LogicalRecord(
            "terminal_call",
            "assistant",
            null,
            terminal.Call.CallId,
            terminal.Call.Name,
            terminal.Call.ArgumentsJson,
            messagePosition,
            2));
    }

    private static ProjectChatMessage[] MessagesFrom(IEnumerable<LogicalRecord> records) =>
        records
            .OrderBy(record => record.MessagePosition)
            .ThenBy(record => record.ContentPosition)
            .GroupBy(record => record.MessagePosition)
            .Select((group, expectedMessagePosition) =>
            {
                if (group.Key != expectedMessagePosition ||
                    group.Select(record => record.Role)
                        .Distinct(StringComparer.Ordinal).Count() != 1)
                {
                    throw new FixtureFailure("APR_AI_STATE_RECORD");
                }
                return new ProjectChatMessage(
                    group.First().Role,
                    group.Select(record => record.Kind switch
                    {
                        "instruction" or "request" or "assistant_text" =>
                            (ProjectChatContent)new ProjectTextContent(record.Text!),
                        "tool_call" or "terminal_call" =>
                            new ProjectToolCallContent(
                                record.CallId!,
                                record.ToolName!,
                                record.Result!),
                        "tool_result" =>
                            new ProjectToolResultContent(
                                record.CallId!,
                                record.Result!),
                        _ => throw new FixtureFailure("APR_AI_STATE_RECORD"),
                    }).ToArray());
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
        if (string.IsNullOrWhiteSpace(input.Instructions) ||
            string.IsNullOrWhiteSpace(input.UserRequest) ||
            state.Records.Length == 0 ||
            state.Records[0].Kind != "instruction")
        {
            throw new FixtureFailure("APR_AI_RESTORE_INPUT");
        }

        var messages = state.Records
            .OrderBy(record => record.MessagePosition)
            .ThenBy(record => record.ContentPosition)
            .GroupBy(record => record.MessagePosition)
            .ToArray();
        if (!state.Records.SequenceEqual(state.Records
            .OrderBy(record => record.MessagePosition)
            .ThenBy(record => record.ContentPosition)))
        {
            throw new FixtureFailure("APR_AI_ASSOCIATION");
        }
        if (messages.Length != 7 ||
            !messages.Select(group => group.Key).SequenceEqual(Enumerable.Range(0, 7)))
        {
            throw new FixtureFailure("APR_AI_ASSOCIATION");
        }
        ValidateMessage(messages[0], "system", ("instruction", 0));
        ValidateMessage(messages[1], "user", ("request", 0));
        ValidateMessage(
            messages[2],
            "assistant",
            ("assistant_text", 0),
            ("tool_call", 2));
        ValidateMessage(messages[3], "tool", ("tool_result", 0));
        ValidateMessage(
            messages[4],
            "assistant",
            ("assistant_text", 0),
            ("tool_call", 2));
        ValidateMessage(messages[5], "tool", ("tool_result", 0));
        ValidateMessage(
            messages[6],
            "assistant",
            ("assistant_text", 0),
            ("terminal_call", 2));
        var expectedCalls = new[]
        {
            (Message: 2, CallId: FixtureConstants.ReadCallId, Tool: "read_file", HasResult: true),
            (Message: 4, CallId: FixtureConstants.SearchCallId, Tool: "search_text", HasResult: true),
            (Message: 6, CallId: FixtureConstants.FinishCallId, Tool: "finish_review", HasResult: false),
        };
        var seenCalls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var expected in expectedCalls)
        {
            var call = messages[expected.Message].SingleOrDefault(record =>
                record.Kind is "tool_call" or "terminal_call");
            if (call is null ||
                call.CallId != expected.CallId ||
                call.ToolName != expected.Tool ||
                call.ContentPosition != 2 ||
                !seenCalls.Add(call.CallId))
            {
                throw new FixtureFailure("APR_AI_ASSOCIATION");
            }
            if (expected.HasResult)
            {
                var result = messages[expected.Message + 1].SingleOrDefault();
                if (result is null ||
                    result.Kind != "tool_result" ||
                    result.CallId != call.CallId ||
                    result.ToolName != call.ToolName ||
                    result.ContentPosition != 0)
                {
                    throw new FixtureFailure("APR_AI_ASSOCIATION");
                }
            }
        }
        if (state.Continuations.Length != expectedCalls.Length)
        {
            throw new FixtureFailure("APR_AI_CONTINUATION");
        }
        foreach (var (item, index) in state.Continuations.Select(
            (item, index) => (item, index)))
        {
            var expected = expectedCalls[index];
            if (FixtureHash.Text(item.Readable) != item.ReadableSha256 ||
                FixtureHash.Text(item.Opaque) != item.OpaqueSha256 ||
                item.Readable != FixtureConstants.ReadableContinuation ||
                item.Opaque != FixtureConstants.OpaqueContinuation ||
                item.Framing != FixtureConstants.ReasoningFraming ||
                item.MessagePosition != expected.Message ||
                item.ContentPosition != 1 ||
                item.AssociatedCallId != expected.CallId)
            {
                throw new FixtureFailure("APR_AI_CONTINUATION");
            }
        }
    }

    private static void ValidateMessage(
        IGrouping<int, LogicalRecord> message,
        string role,
        params (string Kind, int Position)[] expected)
    {
        var records = message.ToArray();
        if (records.Length != expected.Length ||
            records.Any(record => record.Role != role))
        {
            throw new FixtureFailure("APR_AI_ASSOCIATION");
        }
        for (var index = 0; index < expected.Length; index++)
        {
            if (records[index].Kind != expected[index].Kind ||
                records[index].ContentPosition != expected[index].Position)
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
