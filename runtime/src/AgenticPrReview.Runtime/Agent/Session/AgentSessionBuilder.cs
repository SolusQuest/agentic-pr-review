using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Agent.Session;

internal static class AgentSessionBuilder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static AgentSessionBuildResult Build(
        AgentSessionBuildInput input)
    {
        try
        {
            return BuildCore(input);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            DecoderFallbackException or
            EncoderFallbackException or
            FormatException or
            InvalidOperationException or
            JsonException or
            NotSupportedException or
            OverflowException or
            Rfc8785CanonicalizationException)
        {
            return AgentSessionBuildResult.Failure(
                AgentSessionCodes.RecordInvalid);
        }
    }

    private static AgentSessionBuildResult BuildCore(
        AgentSessionBuildInput input)
    {
        if (input is null ||
            input.Run is null ||
            input.Outcome is null ||
            !input.Outcome.CompletedSessionEligible ||
            input.Outcome.Review is null ||
            input.ContinuationCodec is null ||
            !AgentValueDomains.IsIdentifier(input.Run.SessionId) ||
            !input.Run.ReviewedIdentity.IsValid())
        {
            return AgentSessionBuildResult.Failure(
                AgentSessionCodes.RecordInvalid);
        }

        if (!TryPreparePredecessor(
                input,
                out var predecessorDocument,
                out var predecessorHistory,
                out var predecessorContinuation,
                out var generation,
                out var predecessorFailure))
        {
            return AgentSessionBuildResult.Failure(predecessorFailure);
        }

        var priorSessionSha256 = input.Predecessor?.SessionSha256;
        if (!AgentStableRequestMaterializer.TryMaterialize(
                input.TrustedRequest,
                priorSessionSha256,
                out var stable) ||
            input.Run.StablePlan != stable!.StablePlan ||
            !StringComparer.Ordinal.Equals(
                input.Run.ReviewedIdentity.RepositoryId,
                stable.StablePlan.RepositoryId) ||
            input.Run.ReviewedIdentity.ReviewTarget !=
                stable.StablePlan.ReviewTarget)
        {
            return AgentSessionBuildResult.Failure(
                AgentSessionCodes.ScopeMismatch);
        }

        if (!TryCurrentReviewContext(
                input.Run,
                input.CurrentReviewContextIndex,
                out var currentContext,
                out var currentText))
        {
            return AgentSessionBuildResult.Failure(
                AgentSessionCodes.RecordInvalid);
        }

        var expectedMessages = stable.ControlMessages
            .Concat(predecessorHistory ?? [])
            .Append(currentContext!)
            .ToArray();
        if (input.CurrentReviewContextIndex != expectedMessages.Length - 1 ||
            !SameMessages(input.Run.InitialMessages, expectedMessages))
        {
            return AgentSessionBuildResult.Failure(
                AgentSessionCodes.ScopeMismatch);
        }

        if (!SameContinuation(
                input.Run.Continuation,
                predecessorContinuation))
        {
            return AgentSessionBuildResult.Failure(
                AgentSessionCodes.ContinuationInvalid);
        }

        if (!TryValidateInitialEventPrefix(
                input.Run,
                input.Outcome.Events,
                predecessorContinuation,
                out var eventIndex))
        {
            return AgentSessionBuildResult.Failure(
                AgentSessionCodes.RecordInvalid);
        }

        if (!TrySplitContinuation(
                input.Run,
                input.Outcome.Continuation,
                predecessorContinuation,
                out var continuationSuffix))
        {
            return AgentSessionBuildResult.Failure(
                AgentSessionCodes.ContinuationInvalid);
        }

        if (!TryBuildCompletedRun(
                input.Run,
                input.Outcome,
                generation,
                currentText!,
                eventIndex,
                continuationSuffix,
                input.ContinuationCodec,
                out var completedRun,
                out var runFailure))
        {
            return AgentSessionBuildResult.Failure(runFailure);
        }

        var completedRuns = predecessorDocument is null
            ? ImmutableArray.Create(completedRun!)
            : predecessorDocument.CompletedRuns.Add(completedRun!);
        var document = new AgentSessionDocument(
            AgentSessionFormat.Namespace,
            AgentSessionFormat.Discriminator,
            input.Run.SessionId,
            input.Run.StablePlan.RepositoryId,
            input.Run.StablePlan.ReviewTarget,
            input.Run.StablePlan.WorkflowIdentity,
            input.Run.StablePlan.ProviderId,
            input.Run.StablePlan.ModelId,
            input.Run.StablePlan.AdapterId,
            input.Run.StablePlan.PolicySha256,
            input.Run.StablePlan.BuildId,
            input.Run.StablePlan.ToolsetSha256,
            input.Run.StablePlan.LimitsSha256,
            input.Run.ReviewedIdentity.BaseSha,
            input.Run.ReviewedIdentity.HeadSha,
            generation,
            input.Predecessor?.EnvelopeSha256,
            input.Predecessor?.SessionSha256,
            completedRuns);
        if (ExceedsConstructionLimits(document))
        {
            return AgentSessionBuildResult.Failure(
                AgentSessionCodes.ConstructionLimit);
        }

        if (!AgentSessionValidation.TryValidateRoot(
                document,
                out var rootFailure))
        {
            return AgentSessionBuildResult.Failure(rootFailure);
        }

        if (!AgentSessionValidation.TryValidateRecords(
                document,
                input.ContinuationCodec,
                out var recordsFailure))
        {
            return AgentSessionBuildResult.Failure(recordsFailure);
        }

        if (!AgentSessionCodec.TryWrite(
                document,
                out var artifact,
                out var writeFailure))
        {
            return AgentSessionBuildResult.Failure(
                StringComparer.Ordinal.Equals(
                    writeFailure,
                    AgentSessionCodes.ConstructionLimit)
                    ? writeFailure
                    : AgentSessionCodes.RecordInvalid);
        }

        return AgentSessionBuildResult.Success(artifact!);
    }

    private static bool TryPreparePredecessor(
        AgentSessionBuildInput input,
        out AgentSessionDocument? predecessorDocument,
        out ProjectChatMessage[]? predecessorHistory,
        out ProjectContinuation? predecessorContinuation,
        out long generation,
        out string failureCode)
    {
        predecessorDocument = null;
        predecessorHistory = null;
        predecessorContinuation = null;
        generation = 0;
        failureCode = AgentSessionCodes.ScopeMismatch;
        if (input.Predecessor is null)
        {
            if (input.Run.StablePlan.PriorSessionSha256 is not null)
            {
                return false;
            }

            failureCode = string.Empty;
            return true;
        }

        var predecessor = input.Predecessor;
        if (!AgentSessionValidation.IsLowerHex(
                predecessor.SessionSha256,
                64) ||
            !AgentSessionValidation.IsLowerHex(
                predecessor.EnvelopeSha256,
                64) ||
            predecessor.Generation < 0 ||
            !AgentSessionValidation.IsLowerHex(
                predecessor.ProducerBaseSha,
                40) ||
            !AgentSessionValidation.IsLowerHex(
                predecessor.ProducerHeadSha,
                40) ||
            (predecessor.PredecessorStateSha256 is not null &&
                !AgentSessionValidation.IsLowerHex(
                    predecessor.PredecessorStateSha256,
                    64)) ||
            !AgentSessionCodec.TryParse(
                predecessor.Plaintext,
                out var artifact,
                out failureCode) ||
            !StringComparer.Ordinal.Equals(
                artifact!.SessionSha256,
                predecessor.SessionSha256) ||
            !AgentSessionValidation.TryValidateRoot(
                artifact.Document,
                out failureCode))
        {
            return false;
        }

        var document = artifact.Document;
        if (document.Generation != predecessor.Generation ||
            !StringComparer.Ordinal.Equals(
                document.ProducerBaseSha,
                predecessor.ProducerBaseSha) ||
            !StringComparer.Ordinal.Equals(
                document.ProducerHeadSha,
                predecessor.ProducerHeadSha) ||
            !StringComparer.Ordinal.Equals(
                document.PredecessorStateSha256,
                predecessor.PredecessorStateSha256) ||
            !StringComparer.Ordinal.Equals(
                document.SessionId,
                input.Run.SessionId) ||
            !AgentStableRequestMaterializer.TryMaterialize(
                input.TrustedRequest,
                document.PriorSessionSha256,
                out var storedStable) ||
            !SameRootScope(document, storedStable!.StablePlan))
        {
            failureCode = AgentSessionCodes.ScopeMismatch;
            return false;
        }

        if (!ValidTransition(
                input.Transition,
                input.Run.ReviewedIdentity,
                document))
        {
            failureCode = AgentSessionCodes.TransitionRejected;
            return false;
        }

        if (!AgentSessionValidation.TryValidateRecords(
                document,
                input.ContinuationCodec,
                out failureCode))
        {
            return false;
        }

        if (!AgentSessionRequestReconstruction.TryReconstructHistory(
                document,
                input.ContinuationCodec,
                storedStable.ControlMessages.Length,
                out predecessorHistory,
                out predecessorContinuation,
                out var reconstructionFailure))
        {
            failureCode =
                reconstructionFailure ?? AgentSessionCodes.RecordInvalid;
            return false;
        }

        try
        {
            generation = checked(document.Generation + 1);
        }
        catch (OverflowException)
        {
            failureCode = AgentSessionCodes.ConstructionLimit;
            return false;
        }

        if (generation >= AgentSessionFormat.MaximumCompletedRuns)
        {
            failureCode = AgentSessionCodes.ConstructionLimit;
            return false;
        }

        predecessorDocument = document;
        failureCode = string.Empty;
        return true;
    }

    private static bool TryCurrentReviewContext(
        AgentRunRequest run,
        int index,
        out ProjectChatMessage? context,
        out string? text)
    {
        context = null;
        text = null;
        if (run.InitialMessages is null ||
            index < 0 ||
            index >= run.InitialMessages.Length ||
            run.InitialMessages[index] is not { } message ||
            !StringComparer.Ordinal.Equals(message.Role, "user") ||
            message.Contents is null ||
            message.Contents.Length != 1 ||
            message.Contents[0] is not ProjectTextContent textContent ||
            !AgentValueDomains.IsUtf8(
                textContent.Text,
                1,
                AgentLimits.ContentBytes))
        {
            return false;
        }

        text = textContent.Text;
        context = new ProjectChatMessage(
            "user",
            [new ProjectTextContent(text)]);
        return true;
    }

    private static bool TryValidateInitialEventPrefix(
        AgentRunRequest run,
        ImmutableArray<AgentLogicalEvent> events,
        ProjectContinuation? initialContinuation,
        out int eventIndex)
    {
        eventIndex = 0;
        if (events.Length == 0 ||
            events[0] is not AgentPlanEvent plan ||
            !StringComparer.Ordinal.Equals(
                plan.StablePlanSha256,
                AgentCanonical.StablePlanSha256(run.StablePlan)))
        {
            return false;
        }

        eventIndex = 1;
        for (var messageIndex = 0;
            messageIndex < run.InitialMessages.Length;
            messageIndex++)
        {
            if (eventIndex >= events.Length ||
                events[eventIndex] is not AgentMessageEvent message ||
                !SameMessageEvent(
                    message,
                    messageIndex,
                    run.InitialMessages[messageIndex]))
            {
                return false;
            }

            eventIndex++;
        }

        if (initialContinuation is null)
        {
            return true;
        }

        foreach (var item in initialContinuation.Items)
        {
            if (eventIndex >= events.Length ||
                events[eventIndex] is not AgentContinuationEvent continuation ||
                !SameContinuationEvent(continuation, item))
            {
                return false;
            }

            eventIndex++;
        }

        return true;
    }

    private static bool TrySplitContinuation(
        AgentRunRequest run,
        AgentContinuationCandidate? outcome,
        ProjectContinuation? initial,
        out ImmutableArray<AgentContinuationCandidateItem> suffix)
    {
        suffix = [];
        var initialCount = initial?.Items.Length ?? 0;
        if (outcome is null)
        {
            return initialCount == 0 && initial is null;
        }

        if (!StringComparer.Ordinal.Equals(
                outcome.ProviderId,
                run.StablePlan.ProviderId) ||
            !StringComparer.Ordinal.Equals(
                outcome.ModelId,
                run.StablePlan.ModelId) ||
            !StringComparer.Ordinal.Equals(
                outcome.AdapterId,
                run.StablePlan.AdapterId) ||
            !StringComparer.Ordinal.Equals(
                outcome.SessionId,
                run.SessionId) ||
            outcome.Items.Length < initialCount)
        {
            return false;
        }

        for (var index = 0; index < initialCount; index++)
        {
            if (!SameContinuationValue(
                    outcome.Items[index],
                    initial!.Items[index]))
            {
                return false;
            }
        }

        suffix = outcome.Items[initialCount..];
        return true;
    }

    private static bool TryBuildCompletedRun(
        AgentRunRequest run,
        AgentRunOutcome outcome,
        long generation,
        string currentText,
        int eventIndex,
        ImmutableArray<AgentContinuationCandidateItem> continuationSuffix,
        IAgentContinuationCodec continuationCodec,
        out AgentSessionCompletedRun? completedRun,
        out string failureCode)
    {
        completedRun = null;
        failureCode = AgentSessionCodes.RecordInvalid;
        if (generation > int.MaxValue)
        {
            failureCode = AgentSessionCodes.ConstructionLimit;
            return false;
        }

        var runOrdinal = (int)generation;
        var records = ImmutableArray.CreateBuilder<AgentSessionRecord>();
        records.Add(new AgentSessionReviewContextRecord(
            RecordId(runOrdinal, 0),
            0,
            run.ReviewedIdentity,
            currentText,
            "user",
            "text",
            "untrusted_review_data"));
        var candidateBySlot =
            new Dictionary<(int Message, int Content), int>();
        for (var index = 0; index < continuationSuffix.Length; index++)
        {
            var item = continuationSuffix[index];
            if (!candidateBySlot.TryAdd(
                    (item.MessagePosition, item.ContentPosition),
                    index))
            {
                failureCode = AgentSessionCodes.ContinuationInvalid;
                return false;
            }
        }

        var consumedCandidates = new HashSet<int>();
        var messageIds = new Dictionary<int, string>();
        var expectedMessageIndex = run.InitialMessages.Length;
        var messageOrdinal = 0;
        var terminalSeen = false;
        while (eventIndex < outcome.Events.Length)
        {
            if (outcome.Events[eventIndex] is not AgentMessageEvent message ||
                message.MessageIndex != expectedMessageIndex ||
                !StringComparer.Ordinal.Equals(message.Role, "assistant") ||
                message.Contents.Length is < 1 or >
                    AgentLimits.PartsPerMessage)
            {
                return false;
            }

            eventIndex++;
            var messageId = MessageId(runOrdinal, messageOrdinal);
            messageIds.Add(message.MessageIndex, messageId);
            var contents =
                ImmutableArray.CreateBuilder<AgentSessionAssistantContent>(
                    message.Contents.Length);
            var callParts = new List<(
                int Position,
                AgentToolCallReferencePart Reference)>();
            for (var contentPosition = 0;
                contentPosition < message.Contents.Length;
                contentPosition++)
            {
                switch (message.Contents[contentPosition])
                {
                    case AgentTextPart text
                        when AgentValueDomains.IsUtf8(
                            text.Text,
                            1,
                            AgentLimits.ContentBytes):
                        contents.Add(new AgentSessionTextContent(
                            contentPosition,
                            text.Text));
                        break;
                    case AgentReasoningReferencePart reasoning:
                        if (!candidateBySlot.TryGetValue(
                                (
                                    message.MessageIndex,
                                    contentPosition
                                ),
                                out var candidateIndex) ||
                            !SameReasoningReference(
                                reasoning,
                                continuationSuffix[candidateIndex]) ||
                            !consumedCandidates.Add(candidateIndex))
                        {
                            failureCode =
                                AgentSessionCodes.ContinuationInvalid;
                            return false;
                        }

                        contents.Add(
                            new AgentSessionContinuationSlotContent(
                                contentPosition,
                                ContinuationId(
                                    runOrdinal,
                                    candidateIndex)));
                        break;
                    case AgentToolCallReferencePart call:
                        callParts.Add((contentPosition, call));
                        contents.Add(null!);
                        break;
                    default:
                        return false;
                }
            }

            var currentCandidates = continuationSuffix
                .Select((item, index) => (item, index))
                .Where(entry =>
                    entry.item.MessagePosition == message.MessageIndex)
                .ToArray();
            foreach (var entry in currentCandidates)
            {
                if (eventIndex >= outcome.Events.Length ||
                    outcome.Events[eventIndex] is not
                        AgentContinuationEvent continuationEvent ||
                    !SameContinuationEvent(
                        continuationEvent,
                        entry.item))
                {
                    failureCode = AgentSessionCodes.ContinuationInvalid;
                    return false;
                }

                eventIndex++;
            }

            if (currentCandidates.Length != message.Contents.Count(part =>
                    part is AgentReasoningReferencePart) ||
                callParts.Count is < 1 or >
                    AgentLimits.ToolCallsPerResponse)
            {
                return false;
            }

            var callEvents = new List<AgentToolCallEvent>();
            var resultEvents = new List<AgentToolResultEvent>();
            foreach (var callPart in callParts)
            {
                if (eventIndex >= outcome.Events.Length ||
                    outcome.Events[eventIndex] is not AgentToolCallEvent call ||
                    !SameCallReference(callPart.Reference, call))
                {
                    return false;
                }

                callEvents.Add(call);
                if (!TryDecodeCanonicalBytes(
                        call.CanonicalArguments.AsSpan(),
                        out var argumentsJson))
                {
                    failureCode = AgentSessionCodes.RecordInvalid;
                    return false;
                }

                contents[callPart.Position] =
                    StringComparer.Ordinal.Equals(
                        call.Name,
                        AgentToolRegistry.FinishReviewName)
                    ?
                        new AgentSessionTerminalCallContent(
                            callPart.Position,
                            call.CallId,
                            call.Name,
                            argumentsJson!,
                            call.ArgumentsSha256)
                    :
                        new AgentSessionToolCallContent(
                            callPart.Position,
                            call.CallId,
                            call.Name,
                            argumentsJson!);

                eventIndex++;
                if (!StringComparer.Ordinal.Equals(
                        call.Name,
                        AgentToolRegistry.FinishReviewName))
                {
                    if (eventIndex >= outcome.Events.Length ||
                        outcome.Events[eventIndex] is not
                            AgentToolResultEvent result ||
                        !StringComparer.Ordinal.Equals(
                            result.CallId,
                            call.CallId) ||
                        !StringComparer.Ordinal.Equals(
                            result.Name,
                            call.Name) ||
                        !StringComparer.Ordinal.Equals(
                            result.ResultSha256,
                            AgentCanonical.HashRaw(
                                result.CanonicalResult.AsSpan())) ||
                        !AgentSessionValidation.IsLowerHex(
                            result.ObservationId,
                            64))
                    {
                        return false;
                    }

                    resultEvents.Add(result);
                    eventIndex++;
                }
            }

            var terminalCalls = callEvents.Where(call =>
                StringComparer.Ordinal.Equals(
                    call.Name,
                    AgentToolRegistry.FinishReviewName)).ToArray();
            records.Add(new AgentSessionAssistantMessageRecord(
                messageId,
                records.Count,
                messageOrdinal,
                contents.MoveToImmutable(),
                "assistant",
                "provider_message",
                "provider_data"));
            messageOrdinal++;
            if (terminalCalls.Length > 0)
            {
                if (terminalSeen ||
                    terminalCalls.Length != 1 ||
                    callEvents.Count != 1 ||
                    eventIndex >= outcome.Events.Length ||
                    outcome.Events[eventIndex] is not AgentTerminalEvent terminal ||
                    !StringComparer.Ordinal.Equals(
                        terminal.TerminalSha256,
                        outcome.Review!.TerminalSha256) ||
                    !StringComparer.Ordinal.Equals(
                        terminal.TerminalSha256,
                        terminalCalls[0].ArgumentsSha256) ||
                    !terminalCalls[0].CanonicalArguments.AsSpan()
                        .SequenceEqual(outcome.Review.CanonicalBytes) ||
                    !TryFindingsJson(
                        outcome.Review.CanonicalBytes,
                        out var findingsJson))
                {
                    return false;
                }

                eventIndex++;
                if (eventIndex != outcome.Events.Length)
                {
                    return false;
                }

                records.Add(new AgentSessionReviewOutcomeRecord(
                    OutcomeId(runOrdinal),
                    records.Count,
                    messageId,
                    terminalCalls[0].CallId,
                    outcome.Review.TerminalSha256,
                    outcome.Review.Summary,
                    findingsJson!,
                    "assistant",
                    "validated_terminal",
                    "validated_terminal_data"));
                terminalSeen = true;
                break;
            }

            for (var callIndex = 0;
                callIndex < callEvents.Count;
                callIndex++)
            {
                if (callIndex >= resultEvents.Count)
                {
                    return false;
                }

                var result = resultEvents[callIndex];
                if (!TryDecodeCanonicalBytes(
                        result.CanonicalResult.AsSpan(),
                        out var resultJson))
                {
                    failureCode = AgentSessionCodes.RecordInvalid;
                    return false;
                }

                records.Add(new AgentSessionToolResultRecord(
                    RecordId(runOrdinal, records.Count),
                    records.Count,
                    messageId,
                    result.CallId,
                    result.Name,
                    result.ObservationId,
                    resultJson!,
                    "tool",
                    "tool_result",
                    "untrusted_tool_data"));
            }

            expectedMessageIndex = checked(
                expectedMessageIndex + 1 + callEvents.Count);
        }

        if (!terminalSeen ||
            consumedCandidates.Count != continuationSuffix.Length ||
            !TryEncodeContinuation(
                runOrdinal,
                continuationSuffix,
                messageIds,
                continuationCodec,
                out var continuation,
                out failureCode))
        {
            return false;
        }

        completedRun = new AgentSessionCompletedRun(
            RunId(runOrdinal),
            runOrdinal,
            run.ReviewedIdentity,
            AgentCanonical.StablePlanSha256(run.StablePlan),
            records.ToImmutable(),
            continuation!);
        failureCode = string.Empty;
        return true;
    }

    private static bool TryEncodeContinuation(
        int runOrdinal,
        ImmutableArray<AgentContinuationCandidateItem> suffix,
        IReadOnlyDictionary<int, string> messageIds,
        IAgentContinuationCodec codec,
        out AgentSessionContinuation? continuation,
        out string failureCode)
    {
        continuation = null;
        failureCode = AgentSessionCodes.ContinuationInvalid;
        if (!AgentValueDomains.IsIdentifier(codec.CodecId) ||
            !AgentValueDomains.IsIdentifier(codec.CodecDiscriminator))
        {
            return false;
        }

        var items = ImmutableArray.CreateBuilder<AgentSessionContinuationItem>(
            suffix.Length);
        long totalBytes = 0;
        for (var index = 0; index < suffix.Length; index++)
        {
            var candidate = suffix[index];
            if (!messageIds.TryGetValue(
                    candidate.MessagePosition,
                    out var messageId) ||
                !AgentContinuationCodecBoundary.TryEncode(
                    codec,
                    new AgentContinuationCodecValue(
                        candidate.Readable,
                        candidate.Opaque,
                        candidate.Framing),
                    out var encoded) ||
                encoded is null ||
                encoded.Bytes is null)
            {
                return false;
            }

            if (encoded.Bytes.Length > AgentLimits.ContinuationItemBytes)
            {
                failureCode = AgentSessionCodes.ConstructionLimit;
                return false;
            }

            if (encoded.Encoding is not ("utf8" or "base64") ||
                !AgentContinuationCodecBoundary.TryDecode(
                    codec,
                    encoded.Encoding,
                    encoded.Bytes,
                    out var decoded) ||
                decoded is null ||
                !StringComparer.Ordinal.Equals(
                    decoded.Readable,
                    candidate.Readable) ||
                !StringComparer.Ordinal.Equals(
                    decoded.Opaque,
                    candidate.Opaque) ||
                !StringComparer.Ordinal.Equals(
                    decoded.Framing,
                    candidate.Framing))
            {
                return false;
            }

            try
            {
                totalBytes = checked(totalBytes + encoded.Bytes.Length);
            }
            catch (OverflowException)
            {
                failureCode = AgentSessionCodes.ConstructionLimit;
                return false;
            }

            if (encoded.Bytes.Length > AgentLimits.ContinuationItemBytes ||
                totalBytes > AgentLimits.ContinuationTotalBytes)
            {
                failureCode = AgentSessionCodes.ConstructionLimit;
                return false;
            }

            string payload;
            try
            {
                payload = StringComparer.Ordinal.Equals(
                    encoded.Encoding,
                    "utf8")
                    ? StrictUtf8.GetString(encoded.Bytes)
                    : Convert.ToBase64String(encoded.Bytes);
            }
            catch (DecoderFallbackException)
            {
                return false;
            }

            var itemId = ContinuationId(runOrdinal, index);
            items.Add(new AgentSessionContinuationItem(
                itemId,
                encoded.Encoding,
                payload,
                encoded.Bytes.ToArray(),
                AgentSessionCodec.ContinuationPayloadSha256(
                    codec.CodecId,
                    codec.CodecDiscriminator,
                    itemId,
                    encoded.Encoding,
                    encoded.Bytes),
                messageId,
                candidate.ContentPosition,
                candidate.AssociatedCallId));
        }

        continuation = new AgentSessionContinuation(
            codec.CodecId,
            codec.CodecDiscriminator,
            items.MoveToImmutable());
        failureCode = string.Empty;
        return true;
    }

    private static bool TryDecodeCanonicalBytes(
        ReadOnlySpan<byte> bytes,
        out string? value)
    {
        value = null;
        try
        {
            value = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool ExceedsConstructionLimits(
        AgentSessionDocument document)
    {
        if (document.CompletedRuns.Length >
            AgentSessionFormat.MaximumCompletedRuns)
        {
            return true;
        }

        long entries = 0;
        long continuationBytes = 0;
        try
        {
            foreach (var run in document.CompletedRuns)
            {
                entries = checked(
                    entries +
                    run.Records.Length +
                    run.Continuation.Items.Length);
                foreach (var item in run.Continuation.Items)
                {
                    continuationBytes = checked(
                        continuationBytes + item.PayloadBytes.Length);
                    if (item.PayloadBytes.Length >
                        AgentLimits.ContinuationItemBytes)
                    {
                        return true;
                    }
                }
            }
        }
        catch (OverflowException)
        {
            return true;
        }

        return entries > AgentLimits.SessionRecords ||
            continuationBytes > AgentLimits.ContinuationTotalBytes;
    }

    private static bool SameRootScope(
        AgentSessionDocument document,
        StableAgentPlan expected) =>
        StringComparer.Ordinal.Equals(
            document.RepositoryId,
            expected.RepositoryId) &&
        document.ReviewTarget == expected.ReviewTarget &&
        StringComparer.Ordinal.Equals(
            document.WorkflowIdentity,
            expected.WorkflowIdentity) &&
        StringComparer.Ordinal.Equals(
            document.PolicySha256,
            expected.PolicySha256) &&
        StringComparer.Ordinal.Equals(
            document.ToolsetSha256,
            expected.ToolsetSha256) &&
        StringComparer.Ordinal.Equals(
            document.LimitsSha256,
            expected.LimitsSha256) &&
        StringComparer.Ordinal.Equals(
            document.BuildId,
            expected.BuildId) &&
        StringComparer.Ordinal.Equals(
            document.ProviderId,
            expected.ProviderId) &&
        StringComparer.Ordinal.Equals(
            document.ModelId,
            expected.ModelId) &&
        StringComparer.Ordinal.Equals(
            document.AdapterId,
            expected.AdapterId);

    private static bool ValidTransition(
        AgentSessionHeadTransition transition,
        ReviewedIdentity current,
        AgentSessionDocument predecessor) =>
        transition switch
        {
            AgentSessionHeadTransition.SameHead =>
                StringComparer.Ordinal.Equals(
                    current.BaseSha,
                    predecessor.ProducerBaseSha) &&
                StringComparer.Ordinal.Equals(
                    current.HeadSha,
                    predecessor.ProducerHeadSha),
            AgentSessionHeadTransition.VerifiedAhead => true,
            _ => false,
        };

    private static bool SameMessages(
        ProjectChatMessage[] actual,
        ProjectChatMessage[] expected)
    {
        try
        {
            var tools = AgentToolRegistry.Definitions.ToArray();
            return AgentRequestWriter.Write(new ProjectChatRequest(
                    actual,
                    tools,
                    null,
                    ThinkingRequired: true))
                .AsSpan()
                .SequenceEqual(AgentRequestWriter.Write(
                    new ProjectChatRequest(
                        expected,
                        tools,
                        null,
                        ThinkingRequired: true)));
        }
        catch (Rfc8785CanonicalizationException)
        {
            return false;
        }
    }

    private static bool SameContinuation(
        ProjectContinuation? actual,
        ProjectContinuation? expected)
    {
        if (actual is null || expected is null)
        {
            return actual is null && expected is null;
        }

        if (!StringComparer.Ordinal.Equals(
                actual.ProviderId,
                expected.ProviderId) ||
            !StringComparer.Ordinal.Equals(
                actual.ModelId,
                expected.ModelId) ||
            !StringComparer.Ordinal.Equals(
                actual.AdapterId,
                expected.AdapterId) ||
            !StringComparer.Ordinal.Equals(
                actual.SessionId,
                expected.SessionId) ||
            actual.Items.Length != expected.Items.Length)
        {
            return false;
        }

        return actual.Items.Select((item, index) =>
            SameProjectContinuationValue(
                item,
                expected.Items[index])).All(value => value);
    }

    private static bool SameMessageEvent(
        AgentMessageEvent messageEvent,
        int expectedIndex,
        ProjectChatMessage message)
    {
        if (messageEvent.MessageIndex != expectedIndex ||
            !StringComparer.Ordinal.Equals(
                messageEvent.Role,
                message.Role) ||
            messageEvent.Contents.Length != message.Contents.Length)
        {
            return false;
        }

        for (var index = 0; index < message.Contents.Length; index++)
        {
            var matches = (message.Contents[index], messageEvent.Contents[index])
                switch
            {
                (ProjectTextContent text, AgentTextPart part) =>
                    StringComparer.Ordinal.Equals(
                        text.Text,
                        part.Text),
                (
                    ProjectToolCallContent call,
                    AgentToolCallReferencePart part
                ) =>
                    StringComparer.Ordinal.Equals(
                        call.CallId,
                        part.CallId) &&
                    StringComparer.Ordinal.Equals(
                        call.Name,
                        part.Name) &&
                    StringComparer.Ordinal.Equals(
                        part.ArgumentsSha256,
                        ToolCallHash(call)),
                (
                    ProjectToolResultContent result,
                    AgentToolResultReferencePart part
                ) =>
                    StringComparer.Ordinal.Equals(
                        result.CallId,
                        part.CallId) &&
                    StringComparer.Ordinal.Equals(
                        part.ResultSha256,
                        AgentCanonical.HashRaw(
                            StrictUtf8.GetBytes(result.Result))),
                _ => false,
            };
            if (!matches)
            {
                return false;
            }
        }

        return true;
    }

    private static string ToolCallHash(ProjectToolCallContent call)
    {
        var bytes = StrictUtf8.GetBytes(call.ArgumentsJson);
        return StringComparer.Ordinal.Equals(
            call.Name,
            AgentToolRegistry.FinishReviewName)
            ? AgentCanonical.HashDomain(
                AgentCanonical.TerminalDomain,
                bytes)
            : AgentCanonical.HashRaw(bytes);
    }

    private static bool SameContinuationEvent(
        AgentContinuationEvent continuation,
        ProjectContinuationItem item) =>
        StringComparer.Ordinal.Equals(
            continuation.ReadableSha256,
            AgentCanonical.HashRaw(StrictUtf8.GetBytes(item.Readable))) &&
        StringComparer.Ordinal.Equals(
            continuation.OpaqueSha256,
            AgentCanonical.HashRaw(StrictUtf8.GetBytes(item.Opaque))) &&
        StringComparer.Ordinal.Equals(
            continuation.FramingSha256,
            AgentCanonical.HashRaw(StrictUtf8.GetBytes(item.Framing))) &&
        StringComparer.Ordinal.Equals(
            continuation.AssociatedCallId,
            item.AssociatedCallId) &&
        continuation.MessagePosition == item.MessagePosition &&
        continuation.ContentPosition == item.ContentPosition;

    private static bool SameContinuationEvent(
        AgentContinuationEvent continuation,
        AgentContinuationCandidateItem item) =>
        StringComparer.Ordinal.Equals(
            continuation.ReadableSha256,
            AgentCanonical.HashRaw(StrictUtf8.GetBytes(item.Readable))) &&
        StringComparer.Ordinal.Equals(
            continuation.OpaqueSha256,
            AgentCanonical.HashRaw(StrictUtf8.GetBytes(item.Opaque))) &&
        StringComparer.Ordinal.Equals(
            continuation.FramingSha256,
            AgentCanonical.HashRaw(StrictUtf8.GetBytes(item.Framing))) &&
        StringComparer.Ordinal.Equals(
            continuation.AssociatedCallId,
            item.AssociatedCallId) &&
        continuation.MessagePosition == item.MessagePosition &&
        continuation.ContentPosition == item.ContentPosition;

    private static bool SameReasoningReference(
        AgentReasoningReferencePart reference,
        AgentContinuationCandidateItem item) =>
        StringComparer.Ordinal.Equals(
            reference.ReadableSha256,
            AgentCanonical.HashRaw(StrictUtf8.GetBytes(item.Readable))) &&
        StringComparer.Ordinal.Equals(
            reference.OpaqueSha256,
            AgentCanonical.HashRaw(StrictUtf8.GetBytes(item.Opaque))) &&
        StringComparer.Ordinal.Equals(
            reference.FramingSha256,
            AgentCanonical.HashRaw(StrictUtf8.GetBytes(item.Framing))) &&
        StringComparer.Ordinal.Equals(
            reference.AssociatedCallId,
            item.AssociatedCallId) &&
        reference.MessagePosition == item.MessagePosition &&
        reference.ContentPosition == item.ContentPosition;

    private static bool SameCallReference(
        AgentToolCallReferencePart reference,
        AgentToolCallEvent call) =>
        StringComparer.Ordinal.Equals(reference.CallId, call.CallId) &&
        StringComparer.Ordinal.Equals(reference.Name, call.Name) &&
        StringComparer.Ordinal.Equals(
            reference.ArgumentsSha256,
            call.ArgumentsSha256) &&
        StringComparer.Ordinal.Equals(
            call.ArgumentsSha256,
            StringComparer.Ordinal.Equals(
                call.Name,
                AgentToolRegistry.FinishReviewName)
                ? AgentCanonical.HashDomain(
                    AgentCanonical.TerminalDomain,
                    call.CanonicalArguments.AsSpan())
                : AgentCanonical.HashRaw(
                    call.CanonicalArguments.AsSpan()));

    private static bool SameContinuationValue(
        AgentContinuationCandidateItem candidate,
        ProjectContinuationItem item) =>
        StringComparer.Ordinal.Equals(candidate.Readable, item.Readable) &&
        StringComparer.Ordinal.Equals(candidate.Opaque, item.Opaque) &&
        StringComparer.Ordinal.Equals(candidate.Framing, item.Framing) &&
        StringComparer.Ordinal.Equals(
            candidate.AssociatedCallId,
            item.AssociatedCallId) &&
        candidate.MessagePosition == item.MessagePosition &&
        candidate.ContentPosition == item.ContentPosition;

    private static bool SameProjectContinuationValue(
        ProjectContinuationItem left,
        ProjectContinuationItem right) =>
        StringComparer.Ordinal.Equals(left.Readable, right.Readable) &&
        StringComparer.Ordinal.Equals(left.Opaque, right.Opaque) &&
        StringComparer.Ordinal.Equals(left.Framing, right.Framing) &&
        StringComparer.Ordinal.Equals(
            left.AssociatedCallId,
            right.AssociatedCallId) &&
        left.MessagePosition == right.MessagePosition &&
        left.ContentPosition == right.ContentPosition;

    private static bool TryFindingsJson(
        byte[] terminalBytes,
        out string? findingsJson)
    {
        findingsJson = null;
        try
        {
            using var document = JsonDocument.Parse(terminalBytes);
            findingsJson = document.RootElement
                .GetProperty("findings")
                .GetRawText();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string RunId(int runOrdinal) =>
        string.Concat("run", runOrdinal);

    private static string RecordId(int runOrdinal, int sequence) =>
        string.Concat("r", runOrdinal, "_", sequence);

    private static string MessageId(int runOrdinal, int messageOrdinal) =>
        string.Concat("m", runOrdinal, "_", messageOrdinal);

    private static string ContinuationId(int runOrdinal, int itemOrdinal) =>
        string.Concat("c", runOrdinal, "_", itemOrdinal);

    private static string OutcomeId(int runOrdinal) =>
        string.Concat("o", runOrdinal);
}
