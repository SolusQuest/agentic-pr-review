using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Agent.Loop;

internal sealed record AgentRunRequest(
    ReviewedIdentity ReviewedIdentity,
    StableAgentPlan StablePlan,
    ProjectChatMessage[] InitialMessages,
    ProjectContinuation? Continuation = null);

internal sealed class AgentLoop(
    IProjectChatClient chatClient,
    IAgentToolExecutor toolExecutor,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    internal async Task<AgentRunOutcome> RunAsync(
        AgentRunRequest run,
        CancellationToken cancellationToken)
    {
        var started = _timeProvider.GetTimestamp();
        var messages = run.InitialMessages.ToList();
        var continuation = run.Continuation;
        var observations = new List<AgentObservation>();
        var events = ImmutableArray.CreateBuilder<AgentLogicalEvent>();
        var usedCallIds = new HashSet<string>(StringComparer.Ordinal);
        var modelCalls = 0;
        var toolCalls = 0;
        var contentParts = 0;
        long inputTokens = 0;
        long outputTokens = 0;
        long combinedTokens = 0;
        long toolResultBytes = 0;
        long continuationBytes = 0;

        if (!run.ReviewedIdentity.IsValid() ||
            !ValidStablePlan(run.StablePlan, run.ReviewedIdentity) ||
            !TryValidateInitialMessages(messages, out contentParts) ||
            messages.Count > AgentLimits.Messages)
        {
            return Failure(
                AgentFailureCodes.ResponseInvalid,
                modelCalls,
                toolCalls,
                events);
        }

        events.Add(new AgentPlanEvent(
            AgentCanonical.StablePlanSha256(run.StablePlan)));
        for (var index = 0; index < messages.Count; index++)
        {
            events.Add(CreateMessageEvent(index, messages[index]));
        }

        if (!TryAdmitContinuation(
                continuation,
                run.StablePlan,
                messages,
                events,
                ref continuationBytes))
        {
            return Failure(
                AgentFailureCodes.ResponseInvalid,
                modelCalls,
                toolCalls,
                events);
        }

        while (true)
        {
            var stop = StopReason(started, cancellationToken);
            if (stop is not null)
            {
                return Failure(stop, modelCalls, toolCalls, events);
            }

            if (modelCalls >= AgentLimits.ModelCalls)
            {
                return Failure(
                    AgentFailureCodes.ModelLimit,
                    modelCalls,
                    toolCalls,
                    events);
            }

            var request = new ProjectChatRequest(
                messages.ToArray(),
                AgentToolRegistry.Definitions.ToArray(),
                continuation,
                ThinkingRequired: true);
            byte[] requestBytes;
            try
            {
                requestBytes = AgentRequestWriter.Write(request);
            }
            catch (Rfc8785CanonicalizationException)
            {
                return Failure(
                    AgentFailureCodes.ResponseInvalid,
                    modelCalls,
                    toolCalls,
                    events);
            }

            if (requestBytes.Length > AgentLimits.RequestBytes)
            {
                return Failure(
                    AgentFailureCodes.RequestTooLarge,
                    modelCalls,
                    toolCalls,
                    events);
            }

            modelCalls++;
            ProjectChatResponse response;
            using var chatDeadline = new CancellationTokenSource(
                Remaining(started),
                _timeProvider);
            using var chatCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    chatDeadline.Token);
            try
            {
                var task = chatClient.GetResponseAsync(
                    request,
                    chatCancellation.Token);
                response = await task.WaitAsync(chatCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return Failure(
                    OperationStopReason(
                        started,
                        cancellationToken,
                        chatDeadline) ??
                        AgentFailureCodes.ChatFailed,
                    modelCalls,
                    toolCalls,
                    events);
            }
            catch
            {
                return Failure(
                    StopReason(started, cancellationToken) ??
                        AgentFailureCodes.ChatFailed,
                    modelCalls,
                    toolCalls,
                    events);
            }

            stop = StopReason(started, cancellationToken);
            if (stop is not null)
            {
                return Failure(stop, modelCalls, toolCalls, events);
            }

            var admissionFailure = AdmitResponse(
                response,
                usedCallIds,
                messages.Count,
                contentParts,
                toolCalls,
                ref inputTokens,
                ref outputTokens,
                ref combinedTokens,
                out var preparedCalls,
                out var admittedParts);
            if (admissionFailure is not null)
            {
                return Failure(
                    admissionFailure,
                    modelCalls,
                    toolCalls,
                    events);
            }

            var terminalResponse = preparedCalls[0] is PreparedFinishReviewCall;
            var additionalRecords =
                1 +
                (response.Continuation?.Items?.Length ?? 0) +
                preparedCalls.Length * 2;
            if (events.Count + additionalRecords > AgentLimits.SessionRecords)
            {
                return Failure(
                    AgentFailureCodes.ResponseInvalid,
                    modelCalls,
                    toolCalls,
                    events);
            }

            messages.Add(response.Message);
            contentParts += admittedParts;
            events.Add(CreateMessageEvent(messages.Count - 1, response.Message));
            if (!TryAdmitContinuation(
                    response.Continuation,
                    run.StablePlan,
                    messages,
                    events,
                    ref continuationBytes))
            {
                return Failure(
                    AgentFailureCodes.ResponseInvalid,
                    modelCalls,
                    toolCalls,
                    events);
            }

            if (terminalResponse &&
                preparedCalls[0] is PreparedFinishReviewCall terminal)
            {
                toolCalls++;
                events.Add(new AgentToolCallEvent(
                    terminal.CallId,
                    terminal.Name,
                    AgentCanonical.HashRaw(terminal.CanonicalArguments),
                    terminal.CanonicalArguments.ToImmutableArray()));
                if (!TerminalReviewValidator.TryValidate(
                        terminal.Arguments,
                        run.ReviewedIdentity,
                        observations,
                        out var review))
                {
                    return Failure(
                        AgentFailureCodes.TerminalInvalid,
                        modelCalls,
                        toolCalls,
                        events);
                }

                events.Add(new AgentTerminalEvent(review!.TerminalSha256));
                return AgentRunOutcome.Success(review, events.ToImmutable());
            }

            foreach (var call in preparedCalls)
            {
                stop = StopReason(started, cancellationToken);
                if (stop is not null)
                {
                    return Failure(stop, modelCalls, toolCalls, events);
                }

                toolCalls++;
                events.Add(new AgentToolCallEvent(
                    call.CallId,
                    call.Name,
                    AgentCanonical.HashRaw(call.CanonicalArguments),
                    call.CanonicalArguments.ToImmutableArray()));
                AgentToolExecution execution;
                using var toolDeadline = new CancellationTokenSource(
                    Remaining(started),
                    _timeProvider);
                using var toolCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        toolDeadline.Token);
                try
                {
                    var operation = toolExecutor.ExecuteAsync(
                        call,
                        toolCancellation.Token).AsTask();
                    execution = await operation.WaitAsync(toolCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return Failure(
                        OperationStopReason(
                            started,
                            cancellationToken,
                            toolDeadline) ??
                            AgentFailureCodes.ToolIoFailed,
                        modelCalls,
                        toolCalls,
                        events);
                }
                catch
                {
                    return Failure(
                        StopReason(started, cancellationToken) ??
                            AgentFailureCodes.ToolIoFailed,
                        modelCalls,
                        toolCalls,
                        events);
                }

                stop = StopReason(started, cancellationToken);
                if (stop is not null)
                {
                    return Failure(stop, modelCalls, toolCalls, events);
                }

                if (!execution.Succeeded)
                {
                    return Failure(
                        execution.FailureCode ?? AgentFailureCodes.ToolIoFailed,
                        modelCalls,
                        toolCalls,
                        events);
                }

                var canonicalResult = execution.CanonicalResult!;
                if (canonicalResult.Length > AgentLimits.ToolResultBytes)
                {
                    return Failure(
                        AgentFailureCodes.ToolResultLimit,
                        modelCalls,
                        toolCalls,
                        events);
                }

                try
                {
                    toolResultBytes = checked(toolResultBytes + canonicalResult.Length);
                }
                catch (OverflowException)
                {
                    return Failure(
                        AgentFailureCodes.ToolResultLimit,
                        modelCalls,
                        toolCalls,
                        events);
                }

                if (toolResultBytes > AgentLimits.ToolResultsTotalBytes)
                {
                    return Failure(
                        AgentFailureCodes.ToolResultLimit,
                        modelCalls,
                        toolCalls,
                        events);
                }

                var observation = execution.Observation!;
                observations.Add(observation);
                events.Add(new AgentToolResultEvent(
                    call.CallId,
                    call.Name,
                    observation.ObservationId,
                    AgentCanonical.HashRaw(canonicalResult),
                    canonicalResult.ToImmutableArray()));
                messages.Add(new ProjectChatMessage(
                    "tool",
                    [new ProjectToolResultContent(call.CallId, execution.ResultJson!)]));
                contentParts++;
            }

            continuation = response.Continuation;
        }
    }

    private string? AdmitResponse(
        ProjectChatResponse response,
        HashSet<string> usedCallIds,
        int currentMessages,
        int currentParts,
        int currentToolCalls,
        ref long cumulativeInput,
        ref long cumulativeOutput,
        ref long cumulativeCombined,
        out ImmutableArray<PreparedAgentToolCall> preparedCalls,
        out int admittedParts)
    {
        preparedCalls = [];
        admittedParts = 0;
        if (response is null ||
            response.CapturedResponseBodyBytes is null ||
            response.CapturedResponseBodyBytes < 0)
        {
            return AgentFailureCodes.ResponseInvalid;
        }

        if (response.CapturedResponseBodyBytes > AgentLimits.ResponseBytes)
        {
            return AgentFailureCodes.ResponseTooLarge;
        }

        if (response.Message is null)
        {
            return AgentFailureCodes.ResponseInvalid;
        }

        var message = response.Message;
        if (!StringComparer.Ordinal.Equals(message.Role, "assistant") ||
            message.Contents is null ||
            message.Contents.Length is < 1 or > AgentLimits.PartsPerMessage)
        {
            return AgentFailureCodes.ResponseInvalid;
        }

        var toolContents = new List<ProjectToolCallContent>();
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case ProjectTextContent text
                    when ValidContent(text.Text):
                    break;
                case ProjectReasoningContent reasoning
                    when ValidContent(reasoning.Text) &&
                        ValidContent(reasoning.Opaque) &&
                        ValidContent(reasoning.Framing) &&
                        reasoning.MessagePosition >= 0 &&
                        reasoning.Position >= 0:
                    break;
                case ProjectToolCallContent call
                    when ValidIdentifier(call.CallId) &&
                        ValidIdentifier(call.Name) &&
                        ValidToolArguments(call.ArgumentsJson):
                    toolContents.Add(call);
                    break;
                default:
                    return AgentFailureCodes.ResponseInvalid;
            }
        }

        if (toolContents.Count is < 1 or > AgentLimits.ToolCallsPerResponse)
        {
            return AgentFailureCodes.ResponseInvalid;
        }

        var terminalCount = toolContents.Count(call =>
            StringComparer.Ordinal.Equals(
                call.Name,
                AgentToolRegistry.FinishReviewName));
        if (terminalCount > 0 &&
            (terminalCount != 1 || toolContents.Count != 1))
        {
            return AgentFailureCodes.TerminalSequenceInvalid;
        }

        if (currentToolCalls + toolContents.Count > AgentLimits.ToolCalls)
        {
            return AgentFailureCodes.ToolLimit;
        }

        var resultMessages = terminalCount == 1 ? 0 : toolContents.Count;
        if (currentMessages + 1 + resultMessages > AgentLimits.Messages ||
            currentParts + message.Contents.Length + resultMessages >
                AgentLimits.PartsTotal)
        {
            return AgentFailureCodes.ResponseInvalid;
        }

        var localCallIds = new HashSet<string>(StringComparer.Ordinal);
        var prepared = ImmutableArray.CreateBuilder<PreparedAgentToolCall>(
            toolContents.Count);
        foreach (var call in toolContents)
        {
            if (!localCallIds.Add(call.CallId) || !usedCallIds.Add(call.CallId))
            {
                return AgentFailureCodes.ResponseInvalid;
            }

            switch (call.Name)
            {
                case AgentToolRegistry.ReadFileName:
                    if (!AgentToolArguments.TryReadFile(
                            call.ArgumentsJson,
                            out var read))
                    {
                        return AgentFailureCodes.ToolArgumentsInvalid;
                    }

                    prepared.Add(new PreparedReadFileCall(call.CallId, read!));
                    break;
                case AgentToolRegistry.SearchTextName:
                    if (!AgentToolArguments.TrySearchText(
                            call.ArgumentsJson,
                            out var search))
                    {
                        return AgentFailureCodes.ToolArgumentsInvalid;
                    }

                    prepared.Add(new PreparedSearchTextCall(call.CallId, search!));
                    break;
                case AgentToolRegistry.FinishReviewName:
                    if (!AgentToolArguments.TryFinishReview(
                            call.ArgumentsJson,
                            out var finish))
                    {
                        return AgentFailureCodes.TerminalInvalid;
                    }

                    prepared.Add(new PreparedFinishReviewCall(call.CallId, finish!));
                    break;
                default:
                    return AgentFailureCodes.UnknownTool;
            }
        }

        if (response.Usage is null ||
            response.Usage.InputTokens < 0 ||
            response.Usage.OutputTokens < 0)
        {
            return AgentFailureCodes.UsageInvalid;
        }

        long newInput;
        long newOutput;
        long newCombined;
        try
        {
            newInput = checked(cumulativeInput + response.Usage.InputTokens);
            newOutput = checked(cumulativeOutput + response.Usage.OutputTokens);
            newCombined = checked(
                cumulativeCombined +
                response.Usage.InputTokens +
                response.Usage.OutputTokens);
        }
        catch (OverflowException)
        {
            return AgentFailureCodes.UsageInvalid;
        }

        if (newInput > AgentLimits.InputTokens ||
            newOutput > AgentLimits.OutputTokens ||
            newCombined > AgentLimits.CombinedTokens)
        {
            return AgentFailureCodes.TokenLimit;
        }

        cumulativeInput = newInput;
        cumulativeOutput = newOutput;
        cumulativeCombined = newCombined;
        preparedCalls = prepared.MoveToImmutable();
        admittedParts = message.Contents.Length;
        return null;
    }

    private bool TryAdmitContinuation(
        ProjectContinuation? continuation,
        StableAgentPlan stablePlan,
        IReadOnlyList<ProjectChatMessage> logicalMessages,
        ImmutableArray<AgentLogicalEvent>.Builder events,
        ref long aggregateBytes)
    {
        if (continuation is null)
        {
            return true;
        }

        if (continuation.ProviderId is null ||
            continuation.ModelId is null ||
            continuation.AdapterId is null ||
            continuation.SessionId is null ||
            continuation.Items is null ||
            !ValidContent(continuation.ProviderId) ||
            !ValidContent(continuation.ModelId) ||
            !ValidContent(continuation.AdapterId) ||
            !ValidContent(continuation.SessionId) ||
            !StringComparer.Ordinal.Equals(
                continuation.ProviderId,
                stablePlan.ProviderId) ||
            !StringComparer.Ordinal.Equals(
                continuation.ModelId,
                stablePlan.ModelId) ||
            !StringComparer.Ordinal.Equals(
                continuation.AdapterId,
                stablePlan.AdapterId))
        {
            return false;
        }

        foreach (var item in continuation.Items)
        {
            if (item.MessagePosition < 0 ||
                item.MessagePosition >= logicalMessages.Count ||
                item.ContentPosition < 0 ||
                item.ContentPosition >
                    logicalMessages[item.MessagePosition].Contents.Length ||
                item.Readable is null ||
                item.Opaque is null ||
                item.Framing is null)
            {
                return false;
            }

            var itemBytes = AgentRequestWriter.WriteContinuationItem(item);
            var bytes = itemBytes.Length;
            if (bytes > AgentLimits.ContinuationItemBytes)
            {
                return false;
            }

            try
            {
                aggregateBytes = checked(aggregateBytes + bytes);
            }
            catch (OverflowException)
            {
                return false;
            }

            if (aggregateBytes > AgentLimits.ContinuationTotalBytes ||
                events.Count >= AgentLimits.SessionRecords)
            {
                return false;
            }

            events.Add(new AgentContinuationEvent(
                AgentCanonical.HashRaw(Encoding.UTF8.GetBytes(item.Readable)),
                AgentCanonical.HashRaw(Encoding.UTF8.GetBytes(item.Opaque)),
                AgentCanonical.HashRaw(Encoding.UTF8.GetBytes(item.Framing)),
                item.AssociatedCallId,
                item.MessagePosition,
                item.ContentPosition));
        }

        return true;
    }

    private static bool TryValidateInitialMessages(
        IReadOnlyList<ProjectChatMessage> messages,
        out int contentParts)
    {
        contentParts = 0;
        foreach (var message in messages)
        {
            if (message is null ||
                string.IsNullOrEmpty(message.Role) ||
                message.Contents is null ||
                message.Contents.Length > AgentLimits.PartsPerMessage)
            {
                return false;
            }

            contentParts += message.Contents.Length;
            if (contentParts > AgentLimits.PartsTotal)
            {
                return false;
            }

            foreach (var content in message.Contents)
            {
                if (content is not ProjectTextContent text ||
                    !ValidContent(text.Text))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool ValidStablePlan(
        StableAgentPlan plan,
        ReviewedIdentity identity)
    {
        return plan is not null &&
            StringComparer.Ordinal.Equals(
                plan.RepositoryId,
                identity.RepositoryId) &&
            plan.ReviewTarget == identity.ReviewTarget &&
            ValidNonEmpty(plan.WorkflowIdentity) &&
            IsLowerHex(plan.PolicySha256, 64) &&
            StringComparer.Ordinal.Equals(
                plan.ToolsetSha256,
                AgentCanonical.ToolsetSha256(AgentToolRegistry.Definitions)) &&
            StringComparer.Ordinal.Equals(
                plan.LimitsSha256,
                AgentCanonical.LimitsSha256()) &&
            ValidNonEmpty(plan.BuildId) &&
            ValidNonEmpty(plan.ProviderId) &&
            ValidNonEmpty(plan.ModelId) &&
            ValidNonEmpty(plan.AdapterId) &&
            (plan.PriorSessionSha256 is null ||
                IsLowerHex(plan.PriorSessionSha256, 64));
    }

    private static bool ValidNonEmpty(string? value) =>
        value is not null &&
        value.Length > 0 &&
        Utf8Bytes(value) <= AgentLimits.ContentBytes;

    private static bool IsLowerHex(string? value, int length) =>
        value is not null &&
        value.Length == length &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static AgentMessageEvent CreateMessageEvent(
        int messageIndex,
        ProjectChatMessage message)
    {
        var parts = ImmutableArray.CreateBuilder<AgentMessagePart>(
            message.Contents.Length);
        foreach (var content in message.Contents)
        {
            parts.Add(content switch
            {
                ProjectTextContent text => new AgentTextPart(text.Text),
                ProjectReasoningContent reasoning =>
                    new AgentReasoningReferencePart(
                        AgentCanonical.HashRaw(Encoding.UTF8.GetBytes(reasoning.Text)),
                        AgentCanonical.HashRaw(Encoding.UTF8.GetBytes(reasoning.Opaque)),
                        AgentCanonical.HashRaw(Encoding.UTF8.GetBytes(reasoning.Framing)),
                        reasoning.AssociatedCallId,
                        reasoning.MessagePosition,
                        reasoning.Position),
                ProjectToolCallContent call =>
                    new AgentToolCallReferencePart(
                        call.CallId,
                        call.Name,
                        AgentCanonical.HashRaw(
                            Encoding.UTF8.GetBytes(call.ArgumentsJson))),
                ProjectToolResultContent result =>
                    new AgentToolResultReferencePart(
                        result.CallId,
                        AgentCanonical.HashRaw(
                            Encoding.UTF8.GetBytes(result.Result))),
                _ => throw new InvalidOperationException(
                    "Unsupported project message content."),
            });
        }

        return new AgentMessageEvent(
            messageIndex,
            message.Role,
            parts.MoveToImmutable());
    }

    private static bool ValidContent(string? value) =>
        value is not null && Utf8Bytes(value) <= AgentLimits.ContentBytes;

    private static bool ValidIdentifier(string? value) =>
        value is not null &&
        value.Length > 0 &&
        Utf8Bytes(value) <= AgentLimits.ContentBytes;

    private static bool ValidToolArguments(string? value) =>
        value is not null &&
        Utf8Bytes(value) <= AgentLimits.ToolArgumentsBytes;

    private static int Utf8Bytes(string value)
    {
        try
        {
            return new UTF8Encoding(false, true).GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            return int.MaxValue;
        }
    }

    private string? StopReason(long started, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return AgentFailureCodes.Cancelled;
        }

        return _timeProvider.GetElapsedTime(started) >=
            TimeSpan.FromSeconds(AgentLimits.DeadlineSeconds)
            ? AgentFailureCodes.DeadlineExceeded
            : null;
    }

    private TimeSpan Remaining(long started)
    {
        var remaining =
            TimeSpan.FromSeconds(AgentLimits.DeadlineSeconds) -
            _timeProvider.GetElapsedTime(started);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private string? OperationStopReason(
        long started,
        CancellationToken callerCancellation,
        CancellationTokenSource deadlineCancellation)
    {
        if (callerCancellation.IsCancellationRequested)
        {
            return AgentFailureCodes.Cancelled;
        }

        if (deadlineCancellation.IsCancellationRequested ||
            _timeProvider.GetElapsedTime(started) >=
                TimeSpan.FromSeconds(AgentLimits.DeadlineSeconds))
        {
            return AgentFailureCodes.DeadlineExceeded;
        }

        return null;
    }

    private static AgentRunOutcome Failure(
        string code,
        int modelCalls,
        int toolCalls,
        ImmutableArray<AgentLogicalEvent>.Builder events)
    {
        events.Add(new AgentFailureEvent(code));
        return AgentRunOutcome.Failure(
            code,
            modelCalls,
            toolCalls,
            events.ToImmutable());
    }
}

internal static class AgentRequestWriter
{
    internal static byte[] Write(ProjectChatRequest request)
    {
        var writer = new Rfc8785Writer(4_096);
        writer.WriteObjectStart();
        writer.WriteProperty("messages");
        writer.WriteArrayStart();
        for (var messageIndex = 0; messageIndex < request.Messages.Length; messageIndex++)
        {
            if (messageIndex > 0)
            {
                writer.WriteComma();
            }

            WriteMessage(ref writer, request.Messages[messageIndex]);
        }

        writer.WriteArrayEnd();
        writer.WriteProperty("tools");
        writer.WriteArrayStart();
        for (var toolIndex = 0; toolIndex < request.Tools.Length; toolIndex++)
        {
            if (toolIndex > 0)
            {
                writer.WriteComma();
            }

            var tool = request.Tools[toolIndex];
            writer.WriteObjectStart();
            writer.WriteProperty("name");
            writer.WriteString(tool.Name);
            writer.WriteProperty("description");
            writer.WriteString(tool.Description);
            writer.WriteProperty("schema_json");
            writer.WriteString(tool.SchemaJson);
            writer.WriteObjectEnd();
        }

        writer.WriteArrayEnd();
        writer.WriteProperty("continuation");
        WriteContinuation(ref writer, request.Continuation);
        writer.WriteProperty("thinking_required");
        writer.WriteBoolean(request.ThinkingRequired);
        writer.WriteObjectEnd();
        return writer.ToImmutableArray().ToArray();
    }

    internal static byte[] WriteContinuationItem(ProjectContinuationItem item)
    {
        var writer = new Rfc8785Writer(256);
        WriteContinuationItem(ref writer, item);
        return writer.ToImmutableArray().ToArray();
    }

    private static void WriteMessage(
        ref Rfc8785Writer writer,
        ProjectChatMessage message)
    {
        writer.WriteObjectStart();
        writer.WriteProperty("role");
        writer.WriteString(message.Role);
        writer.WriteProperty("contents");
        writer.WriteArrayStart();
        for (var contentIndex = 0;
            contentIndex < message.Contents.Length;
            contentIndex++)
        {
            if (contentIndex > 0)
            {
                writer.WriteComma();
            }

            var content = message.Contents[contentIndex];
            writer.WriteObjectStart();
            writer.WriteProperty("kind");
            writer.WriteString(content.Kind);
            switch (content)
            {
                case ProjectTextContent text:
                    writer.WriteProperty("text");
                    writer.WriteString(text.Text);
                    break;
                case ProjectReasoningContent reasoning:
                    writer.WriteProperty("text");
                    writer.WriteString(reasoning.Text);
                    writer.WriteProperty("opaque");
                    writer.WriteString(reasoning.Opaque);
                    writer.WriteProperty("framing");
                    writer.WriteString(reasoning.Framing);
                    writer.WriteProperty("associated_call_id");
                    WriteNullableString(ref writer, reasoning.AssociatedCallId);
                    writer.WriteProperty("message_position");
                    writer.WriteNumber(reasoning.MessagePosition);
                    writer.WriteProperty("position");
                    writer.WriteNumber(reasoning.Position);
                    break;
                case ProjectToolCallContent call:
                    writer.WriteProperty("call_id");
                    writer.WriteString(call.CallId);
                    writer.WriteProperty("name");
                    writer.WriteString(call.Name);
                    writer.WriteProperty("arguments_json");
                    writer.WriteString(call.ArgumentsJson);
                    break;
                case ProjectToolResultContent result:
                    writer.WriteProperty("call_id");
                    writer.WriteString(result.CallId);
                    writer.WriteProperty("result");
                    writer.WriteString(result.Result);
                    break;
            }

            writer.WriteObjectEnd();
        }

        writer.WriteArrayEnd();
        writer.WriteObjectEnd();
    }

    private static void WriteContinuation(
        ref Rfc8785Writer writer,
        ProjectContinuation? continuation)
    {
        if (continuation is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteObjectStart();
        writer.WriteProperty("provider_id");
        writer.WriteString(continuation.ProviderId);
        writer.WriteProperty("model_id");
        writer.WriteString(continuation.ModelId);
        writer.WriteProperty("adapter_id");
        writer.WriteString(continuation.AdapterId);
        writer.WriteProperty("session_id");
        writer.WriteString(continuation.SessionId);
        writer.WriteProperty("items");
        writer.WriteArrayStart();
        for (var index = 0; index < continuation.Items.Length; index++)
        {
            if (index > 0)
            {
                writer.WriteComma();
            }

            var item = continuation.Items[index];
            WriteContinuationItem(ref writer, item);
        }

        writer.WriteArrayEnd();
        writer.WriteObjectEnd();
    }

    private static void WriteContinuationItem(
        ref Rfc8785Writer writer,
        ProjectContinuationItem item)
    {
        writer.WriteObjectStart();
        writer.WriteProperty("readable");
        writer.WriteString(item.Readable);
        writer.WriteProperty("opaque");
        writer.WriteString(item.Opaque);
        writer.WriteProperty("framing");
        writer.WriteString(item.Framing);
        writer.WriteProperty("associated_call_id");
        WriteNullableString(ref writer, item.AssociatedCallId);
        writer.WriteProperty("message_position");
        writer.WriteNumber(item.MessagePosition);
        writer.WriteProperty("content_position");
        writer.WriteNumber(item.ContentPosition);
        writer.WriteObjectEnd();
    }

    private static void WriteNullableString(
        ref Rfc8785Writer writer,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull();
        }
        else
        {
            writer.WriteString(value);
        }
    }
}
