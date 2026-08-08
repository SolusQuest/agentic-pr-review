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
    string SessionId,
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
            !AgentValueDomains.IsIdentifier(run.SessionId) ||
            !TryValidateInitialMessages(
                messages,
                usedCallIds,
                out contentParts) ||
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

        if (!TryAdmitInitialContinuation(
                continuation,
                run.StablePlan,
                run.SessionId,
                messages,
                usedCallIds,
                events,
                ref continuationBytes,
                ref contentParts))
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
            catch (ProjectChatNormalizationException exception)
            {
                return Failure(
                    StopReason(started, cancellationToken) ??
                        exception.DiagnosticCode,
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
                out var admittedMessage,
                out var messageEvent,
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

            if (!TryMergeResponseContinuation(
                    continuation,
                    response.Continuation,
                    run.StablePlan,
                    run.SessionId,
                    response.Message,
                    messages.Count,
                    usedCallIds,
                    ref continuationBytes,
                    out var mergedContinuation,
                    out var continuationEvents))
            {
                return Failure(
                    AgentFailureCodes.ResponseInvalid,
                    modelCalls,
                    toolCalls,
                    events);
            }

            messages.Add(admittedMessage);
            contentParts += admittedParts;
            events.Add(messageEvent);
            events.AddRange(continuationEvents);
            continuation = mergedContinuation;

            if (terminalResponse &&
                preparedCalls[0] is PreparedFinishReviewCall terminal)
            {
                toolCalls++;
                events.Add(new AgentToolCallEvent(
                    terminal.CallId,
                    terminal.Name,
                    AgentCanonical.HashDomain(
                        AgentCanonical.TerminalDomain,
                        terminal.CanonicalArguments),
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
                return AgentRunOutcome.Success(
                    review,
                    events.ToImmutable(),
                    ToContinuationCandidate(continuation));
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

                if (execution is null)
                {
                    return Failure(
                        AgentFailureCodes.ToolIoFailed,
                        modelCalls,
                        toolCalls,
                        events);
                }

                if (!execution.Succeeded)
                {
                    return Failure(
                        AgentToolResultAdmission.IsFrozenFailureCode(
                            execution.FailureCode)
                            ? execution.FailureCode!
                            : AgentFailureCodes.ToolIoFailed,
                        modelCalls,
                        toolCalls,
                        events);
                }

                if (execution.CanonicalResult is null)
                {
                    return Failure(
                        AgentFailureCodes.ToolIoFailed,
                        modelCalls,
                        toolCalls,
                        events);
                }

                var canonicalResult = execution.CanonicalResult;
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

                if (!AgentToolResultAdmission.TryAdmit(
                        call,
                        run.ReviewedIdentity,
                        execution,
                        out var resultJson,
                        out var observation))
                {
                    return Failure(
                        AgentFailureCodes.ToolIoFailed,
                        modelCalls,
                        toolCalls,
                        events);
                }

                observations.Add(observation);
                events.Add(new AgentToolResultEvent(
                    call.CallId,
                    call.Name,
                    observation.ObservationId,
                    AgentCanonical.HashRaw(canonicalResult),
                    canonicalResult.ToImmutableArray()));
                messages.Add(new ProjectChatMessage(
                    "tool",
                    [new ProjectToolResultContent(call.CallId, resultJson)]));
                contentParts++;
            }

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
        out ProjectChatMessage admittedMessage,
        out AgentMessageEvent messageEvent,
        out int admittedParts)
    {
        preparedCalls = [];
        admittedMessage = new ProjectChatMessage("assistant", []);
        messageEvent = new AgentMessageEvent(currentMessages, "assistant", []);
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
                    when ValidTextContent(text.Text):
                    break;
                case ProjectReasoningContent reasoning
                    when ValidContinuationContent(reasoning.Text) &&
                        ValidContinuationContent(reasoning.Opaque) &&
                        ValidFramingContent(reasoning.Framing) &&
                        reasoning.MessagePosition >= 0 &&
                        reasoning.Position >= 0:
                    break;
                case ProjectToolCallContent call
                    when AgentValueDomains.IsIdentifier(call.CallId) &&
                        AgentValueDomains.IsIdentifier(call.Name) &&
                        call.ArgumentsJson is not null &&
                        Utf8Bytes(call.ArgumentsJson) <= AgentLimits.ResponseBytes:
                    toolContents.Add(call);
                    break;
                default:
                    return AgentFailureCodes.ResponseInvalid;
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
            if (!localCallIds.Add(call.CallId) || usedCallIds.Contains(call.CallId))
            {
                return AgentFailureCodes.ResponseInvalid;
            }

            switch (call.Name)
            {
                case AgentToolRegistry.ListFilesName:
                    if (!AgentToolArguments.TryListFilesProvider(
                            call.ArgumentsJson,
                            out var list))
                    {
                        return AgentFailureCodes.ToolArgumentsInvalid;
                    }

                    prepared.Add(new PreparedListFilesCall(call.CallId, list!));
                    break;
                case AgentToolRegistry.ListChangedFilesName:
                    if (!AgentToolArguments.TryListChangedFilesProvider(
                            call.ArgumentsJson,
                            out var changed))
                    {
                        return AgentFailureCodes.ToolArgumentsInvalid;
                    }

                    prepared.Add(new PreparedListChangedFilesCall(
                        call.CallId,
                        changed!));
                    break;
                case AgentToolRegistry.ReadDiffName:
                    if (!AgentToolArguments.TryReadDiffProvider(
                            call.ArgumentsJson,
                            out var diff))
                    {
                        return AgentFailureCodes.ToolArgumentsInvalid;
                    }

                    prepared.Add(new PreparedReadDiffCall(call.CallId, diff!));
                    break;
                case AgentToolRegistry.ReadFileName:
                    if (!AgentToolArguments.TryReadFileProvider(
                            call.ArgumentsJson,
                            out var read))
                    {
                        return AgentFailureCodes.ToolArgumentsInvalid;
                    }

                    prepared.Add(new PreparedReadFileCall(call.CallId, read!));
                    break;
                case AgentToolRegistry.SearchTextName:
                    if (!AgentToolArguments.TrySearchTextProvider(
                            call.ArgumentsJson,
                            out var search))
                    {
                        return AgentFailureCodes.ToolArgumentsInvalid;
                    }

                    prepared.Add(new PreparedSearchTextCall(call.CallId, search!));
                    break;
                case AgentToolRegistry.FinishReviewName:
                    if (!AgentToolArguments.TryFinishReviewProvider(
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

        foreach (var call in prepared)
        {
            if (call is PreparedFinishReviewCall)
            {
                continue;
            }

            string? preflightFailure;
            try
            {
                preflightFailure = toolExecutor.Preflight(call);
            }
            catch
            {
                return AgentFailureCodes.ToolIoFailed;
            }

            if (preflightFailure is not null)
            {
                return AgentToolResultAdmission.IsFrozenFailureCode(
                    preflightFailure)
                    ? preflightFailure
                    : AgentFailureCodes.ToolIoFailed;
            }
        }

        cumulativeInput = newInput;
        cumulativeOutput = newOutput;
        cumulativeCombined = newCombined;
        preparedCalls = prepared.MoveToImmutable();
        foreach (var callId in localCallIds)
        {
            usedCallIds.Add(callId);
        }

        var preparedById = preparedCalls.ToDictionary(
            call => call.CallId,
            StringComparer.Ordinal);
        var eventContents = message.Contents.Select(content =>
            content is ProjectToolCallContent call
                ? (ProjectChatContent)new ProjectToolCallContent(
                    call.CallId,
                    call.Name,
                    Encoding.UTF8.GetString(
                        preparedById[call.CallId].CanonicalArguments))
                : content).ToArray();
        admittedMessage = new ProjectChatMessage(
            "assistant",
            eventContents
                .Where(content => content is not ProjectReasoningContent)
                .ToArray());
        messageEvent = CreateMessageEvent(
            currentMessages,
            new ProjectChatMessage("assistant", eventContents));
        admittedParts = message.Contents.Length;
        return null;
    }

    private bool TryAdmitInitialContinuation(
        ProjectContinuation? continuation,
        StableAgentPlan stablePlan,
        string sessionId,
        IReadOnlyList<ProjectChatMessage> logicalMessages,
        IReadOnlySet<string> usedCallIds,
        ImmutableArray<AgentLogicalEvent>.Builder events,
        ref long aggregateBytes,
        ref int contentParts)
    {
        if (continuation is null)
        {
            return true;
        }

        if (!ValidContinuationScope(continuation, stablePlan, sessionId))
        {
            return false;
        }

        if (continuation.Items.Any(item => item is null))
        {
            return false;
        }

        var slots = new HashSet<(int Message, int Content)>();
        var itemsByMessage = continuation.Items
            .GroupBy(item => item.MessagePosition)
            .ToDictionary(group => group.Key, group => group.Count());
        foreach (var entry in itemsByMessage)
        {
            if (entry.Key < 0 ||
                entry.Key >= logicalMessages.Count ||
                !StringComparer.Ordinal.Equals(
                    logicalMessages[entry.Key].Role,
                    "assistant") ||
                logicalMessages[entry.Key].Contents.Length + entry.Value >
                    AgentLimits.PartsPerMessage)
            {
                return false;
            }
        }

        foreach (var item in continuation.Items)
        {
            if (!ValidContinuationItem(item) ||
                !slots.Add((item.MessagePosition, item.ContentPosition)) ||
                !itemsByMessage.TryGetValue(item.MessagePosition, out var itemCount) ||
                item.ContentPosition >=
                    logicalMessages[item.MessagePosition].Contents.Length + itemCount ||
                !ValidContinuationAssociation(
                    item,
                    logicalMessages[item.MessagePosition],
                    usedCallIds))
            {
                return false;
            }
        }

        int newContentParts;
        try
        {
            newContentParts = checked(contentParts + continuation.Items.Length);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (newContentParts > AgentLimits.PartsTotal ||
            !TryCreateContinuationEvents(
                continuation.Items,
                aggregateBytes,
                out var newAggregate,
                out var continuationEvents))
        {
            return false;
        }

        contentParts = newContentParts;
        aggregateBytes = newAggregate;
        events.AddRange(continuationEvents);
        return true;
    }

    private bool TryMergeResponseContinuation(
        ProjectContinuation? current,
        ProjectContinuation? delta,
        StableAgentPlan stablePlan,
        string sessionId,
        ProjectChatMessage rawMessage,
        int messagePosition,
        IReadOnlySet<string> usedCallIds,
        ref long aggregateBytes,
        out ProjectContinuation? merged,
        out ImmutableArray<AgentContinuationEvent> continuationEvents)
    {
        merged = current;
        continuationEvents = [];
        var reasoning = rawMessage.Contents
            .Select((content, position) => (content, position))
            .Where(entry => entry.content is ProjectReasoningContent)
            .ToArray();
        if (delta is null)
        {
            return reasoning.Length == 0;
        }

        if (!ValidContinuationScope(delta, stablePlan, sessionId) ||
            delta.Items.Length != reasoning.Length)
        {
            return false;
        }

        var priorSlots = current?.Items.Select(item =>
            (item.MessagePosition, item.ContentPosition)).ToHashSet() ?? [];
        var deltaSlots = new HashSet<(int Message, int Content)>();
        foreach (var item in delta.Items)
        {
            if (!ValidContinuationItem(item) ||
                item.MessagePosition != messagePosition ||
                item.ContentPosition < 0 ||
                item.ContentPosition >= rawMessage.Contents.Length ||
                !priorSlots.Add((item.MessagePosition, item.ContentPosition)) ||
                !deltaSlots.Add((item.MessagePosition, item.ContentPosition)) ||
                rawMessage.Contents[item.ContentPosition] is not
                    ProjectReasoningContent responseReasoning ||
                !SameContinuationValue(item, responseReasoning) ||
                !ValidContinuationAssociation(item, rawMessage, usedCallIds))
            {
                return false;
            }
        }

        foreach (var entry in reasoning)
        {
            var responseReasoning = (ProjectReasoningContent)entry.content;
            if (responseReasoning.MessagePosition != messagePosition ||
                responseReasoning.Position != entry.position ||
                !deltaSlots.Contains((messagePosition, entry.position)))
            {
                return false;
            }
        }

        if (!TryCreateContinuationEvents(
                delta.Items,
                aggregateBytes,
                out var newAggregate,
                out continuationEvents))
        {
            return false;
        }

        merged = current is null
            ? delta
            : current with { Items = [.. current.Items, .. delta.Items] };
        aggregateBytes = newAggregate;
        return true;
    }

    private static bool ValidContinuationScope(
        ProjectContinuation continuation,
        StableAgentPlan stablePlan,
        string sessionId) =>
        continuation.Items is not null &&
        AgentValueDomains.IsUtf8(continuation.ProviderId, 1, 128) &&
        AgentValueDomains.IsUtf8(continuation.ModelId, 1, 128) &&
        AgentValueDomains.IsUtf8(continuation.AdapterId, 1, 128) &&
        AgentValueDomains.IsIdentifier(continuation.SessionId) &&
        StringComparer.Ordinal.Equals(
            continuation.ProviderId,
            stablePlan.ProviderId) &&
        StringComparer.Ordinal.Equals(
            continuation.ModelId,
            stablePlan.ModelId) &&
        StringComparer.Ordinal.Equals(
            continuation.AdapterId,
            stablePlan.AdapterId) &&
        StringComparer.Ordinal.Equals(continuation.SessionId, sessionId);

    private static bool ValidContinuationItem(ProjectContinuationItem item) =>
        item is not null &&
        item.MessagePosition >= 0 &&
        item.ContentPosition >= 0 &&
        AgentValueDomains.IsUtf8(item.Readable, 0, AgentLimits.ContentBytes) &&
        AgentValueDomains.IsUtf8(item.Opaque, 0, AgentLimits.ContentBytes) &&
        AgentValueDomains.IsUtf8(item.Framing, 1, AgentLimits.ContentBytes) &&
        (item.AssociatedCallId is null ||
            AgentValueDomains.IsIdentifier(item.AssociatedCallId));

    private static bool ValidContinuationAssociation(
        ProjectContinuationItem item,
        ProjectChatMessage message,
        IReadOnlySet<string> usedCallIds)
    {
        if (item.AssociatedCallId is null)
        {
            return true;
        }

        return usedCallIds.Contains(item.AssociatedCallId) &&
            message.Contents.OfType<ProjectToolCallContent>().Any(call =>
                StringComparer.Ordinal.Equals(
                    call.CallId,
                    item.AssociatedCallId));
    }

    private static bool SameContinuationValue(
        ProjectContinuationItem item,
        ProjectReasoningContent reasoning) =>
        StringComparer.Ordinal.Equals(item.Readable, reasoning.Text) &&
        StringComparer.Ordinal.Equals(item.Opaque, reasoning.Opaque) &&
        StringComparer.Ordinal.Equals(item.Framing, reasoning.Framing) &&
        StringComparer.Ordinal.Equals(
            item.AssociatedCallId,
            reasoning.AssociatedCallId) &&
        item.MessagePosition == reasoning.MessagePosition &&
        item.ContentPosition == reasoning.Position;

    private static bool TryCreateContinuationEvents(
        IReadOnlyList<ProjectContinuationItem> items,
        long aggregateBytes,
        out long newAggregate,
        out ImmutableArray<AgentContinuationEvent> continuationEvents)
    {
        newAggregate = aggregateBytes;
        var builder = ImmutableArray.CreateBuilder<AgentContinuationEvent>(
            items.Count);
        foreach (var item in items)
        {
            byte[] itemBytes;
            try
            {
                itemBytes = AgentRequestWriter.WriteContinuationItem(item);
                newAggregate = checked(newAggregate + itemBytes.Length);
            }
            catch (OverflowException)
            {
                continuationEvents = [];
                return false;
            }
            catch (Rfc8785CanonicalizationException)
            {
                continuationEvents = [];
                return false;
            }

            if (itemBytes.Length > AgentLimits.ContinuationItemBytes ||
                newAggregate > AgentLimits.ContinuationTotalBytes)
            {
                continuationEvents = [];
                return false;
            }

            builder.Add(new AgentContinuationEvent(
                AgentCanonical.HashRaw(Encoding.UTF8.GetBytes(item.Readable)),
                AgentCanonical.HashRaw(Encoding.UTF8.GetBytes(item.Opaque)),
                AgentCanonical.HashRaw(Encoding.UTF8.GetBytes(item.Framing)),
                item.AssociatedCallId,
                item.MessagePosition,
                item.ContentPosition));
        }

        continuationEvents = builder.MoveToImmutable();
        return true;
    }

    private static bool TryValidateInitialMessages(
        IReadOnlyList<ProjectChatMessage> messages,
        HashSet<string> usedCallIds,
        out int contentParts)
    {
        contentParts = 0;
        var pendingResults = new Queue<string>();
        foreach (var message in messages)
        {
            if (message is null ||
                message.Contents is null ||
                message.Contents.Length is < 1 or > AgentLimits.PartsPerMessage)
            {
                return false;
            }

            try
            {
                contentParts = checked(contentParts + message.Contents.Length);
            }
            catch (OverflowException)
            {
                return false;
            }

            if (contentParts > AgentLimits.PartsTotal)
            {
                return false;
            }

            if (pendingResults.Count > 0)
            {
                if (!StringComparer.Ordinal.Equals(message.Role, "tool"))
                {
                    return false;
                }

                foreach (var content in message.Contents)
                {
                    if (content is not ProjectToolResultContent result ||
                        !AgentValueDomains.IsIdentifier(result.CallId) ||
                        !AgentValueDomains.IsUtf8(
                            result.Result,
                            1,
                            AgentLimits.ToolResultBytes) ||
                        pendingResults.Count == 0 ||
                        !StringComparer.Ordinal.Equals(
                            pendingResults.Dequeue(),
                            result.CallId))
                    {
                        return false;
                    }
                }

                continue;
            }

            if (StringComparer.Ordinal.Equals(message.Role, "system") ||
                StringComparer.Ordinal.Equals(message.Role, "user"))
            {
                if (message.Contents.Any(content =>
                    content is not ProjectTextContent text ||
                    !ValidTextContent(text.Text)))
                {
                    return false;
                }

                continue;
            }

            if (!StringComparer.Ordinal.Equals(message.Role, "assistant"))
            {
                return false;
            }

            var calls = new List<ProjectToolCallContent>();
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case ProjectTextContent text when ValidTextContent(text.Text):
                        break;
                    case ProjectToolCallContent call
                        when TryValidatePriorCall(call, usedCallIds):
                        calls.Add(call);
                        break;
                    default:
                        return false;
                }
            }

            if (calls.Count is < 1 or > AgentLimits.ToolCallsPerResponse)
            {
                return false;
            }

            var terminalCount = calls.Count(call =>
                StringComparer.Ordinal.Equals(
                    call.Name,
                    AgentToolRegistry.FinishReviewName));
            if (terminalCount > 0)
            {
                if (terminalCount != 1 || calls.Count != 1)
                {
                    return false;
                }
            }

            foreach (var call in calls)
            {
                pendingResults.Enqueue(call.CallId);
            }
        }

        return pendingResults.Count == 0;
    }

    private static bool TryValidatePriorCall(
        ProjectToolCallContent call,
        HashSet<string> usedCallIds)
    {
        if (!AgentValueDomains.IsIdentifier(call.CallId) ||
            !usedCallIds.Add(call.CallId) ||
            call.ArgumentsJson is null)
        {
            return false;
        }

        PreparedAgentToolCall? prepared = null;
        switch (call.Name)
        {
            case AgentToolRegistry.ListFilesName:
                if (AgentToolArguments.TryListFilesCanonical(
                        call.ArgumentsJson,
                        out var list))
                {
                    prepared = new PreparedListFilesCall(call.CallId, list!);
                }

                break;
            case AgentToolRegistry.ListChangedFilesName:
                if (AgentToolArguments.TryListChangedFilesCanonical(
                        call.ArgumentsJson,
                        out var changed))
                {
                    prepared = new PreparedListChangedFilesCall(
                        call.CallId,
                        changed!);
                }

                break;
            case AgentToolRegistry.ReadDiffName:
                if (AgentToolArguments.TryReadDiff(
                        call.ArgumentsJson,
                        out var diff))
                {
                    prepared = new PreparedReadDiffCall(call.CallId, diff!);
                }

                break;
            case AgentToolRegistry.ReadFileName:
                if (AgentToolArguments.TryReadFile(
                    call.ArgumentsJson,
                    out var read))
                {
                    prepared = new PreparedReadFileCall(call.CallId, read!);
                }
                break;
            case AgentToolRegistry.SearchTextName:
                if (AgentToolArguments.TrySearchTextCanonical(
                    call.ArgumentsJson,
                    out var search))
                {
                    prepared = new PreparedSearchTextCall(call.CallId, search!);
                }
                break;
            case AgentToolRegistry.FinishReviewName:
                if (AgentToolArguments.TryFinishReview(
                    call.ArgumentsJson,
                    out var finish))
                {
                    prepared = new PreparedFinishReviewCall(call.CallId, finish!);
                }
                break;
        }

        return prepared is not null &&
            Encoding.UTF8.GetBytes(call.ArgumentsJson)
                .AsSpan()
                .SequenceEqual(prepared.CanonicalArguments);
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
            AgentValueDomains.IsUtf8(plan.WorkflowIdentity, 1, 256) &&
            IsLowerHex(plan.PolicySha256, 64) &&
            StringComparer.Ordinal.Equals(
                plan.ToolsetSha256,
                AgentCanonical.ToolsetSha256(AgentToolRegistry.Definitions)) &&
            StringComparer.Ordinal.Equals(
                plan.LimitsSha256,
                AgentCanonical.LimitsSha256()) &&
            AgentValueDomains.IsUtf8(plan.BuildId, 1, 256) &&
            AgentValueDomains.IsUtf8(plan.ProviderId, 1, 128) &&
            AgentValueDomains.IsUtf8(plan.ModelId, 1, 128) &&
            AgentValueDomains.IsUtf8(plan.AdapterId, 1, 128) &&
            (plan.PriorSessionSha256 is null ||
                IsLowerHex(plan.PriorSessionSha256, 64));
    }

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
                        ToolCallArgumentsSha256(call)),
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

    private static string ToolCallArgumentsSha256(
        ProjectToolCallContent call)
    {
        var bytes = Encoding.UTF8.GetBytes(call.ArgumentsJson);
        return StringComparer.Ordinal.Equals(
            call.Name,
            AgentToolRegistry.FinishReviewName)
            ? AgentCanonical.HashDomain(AgentCanonical.TerminalDomain, bytes)
            : AgentCanonical.HashRaw(bytes);
    }

    private static AgentContinuationCandidate? ToContinuationCandidate(
        ProjectContinuation? continuation) =>
        continuation is null
            ? null
            : new AgentContinuationCandidate(
                continuation.ProviderId,
                continuation.ModelId,
                continuation.AdapterId,
                continuation.SessionId,
                continuation.Items.Select(item =>
                    new AgentContinuationCandidateItem(
                        item.Readable,
                        item.Opaque,
                        item.Framing,
                        item.AssociatedCallId,
                        item.MessagePosition,
                        item.ContentPosition)).ToImmutableArray());

    private static bool ValidTextContent(string? value) =>
        value is not null &&
        Utf8Bytes(value) is >= 1 and <= AgentLimits.ContentBytes;

    private static bool ValidContinuationContent(string? value) =>
        value is not null && Utf8Bytes(value) <= AgentLimits.ContentBytes;

    private static bool ValidFramingContent(string? value) =>
        value is not null &&
        Utf8Bytes(value) is >= 1 and <= AgentLimits.ContentBytes;

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
