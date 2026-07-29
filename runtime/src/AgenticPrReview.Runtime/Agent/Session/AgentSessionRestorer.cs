using System.Collections.Immutable;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Agent.Session;

internal static class AgentSessionRestorer
{
    internal static AgentSessionRestoreResult Restore(
        AgentSessionRestoreInput input)
    {
        if (input.ExplicitReset)
        {
            return AgentSessionRestoreResult.Failure(
                AgentSessionCodes.ResetExplicit);
        }

        switch (input.LocatorFamily)
        {
            case AgentSessionLocatorFamily.Absent:
                return AgentSessionRestoreResult.Failure(
                    input.Intent == AgentSessionRestoreIntent.Automatic
                        ? AgentSessionCodes.BootstrapAbsent
                        : AgentSessionCodes.ExplicitMissing);
            case AgentSessionLocatorFamily.NonCurrent:
                return AgentSessionRestoreResult.Failure(
                    input.Intent == AgentSessionRestoreIntent.Automatic
                        ? AgentSessionCodes.BootstrapIncompatible
                        : AgentSessionCodes.ExplicitIncompatible);
            case AgentSessionLocatorFamily.Current:
                break;
            default:
                return AgentSessionRestoreResult.Failure(
                    AgentSessionCodes.CurrentMalformed);
        }

        if (input.Plaintext is null)
        {
            return AgentSessionRestoreResult.Failure(
                AgentSessionCodes.CurrentMalformed);
        }

        if (!AgentSessionCodec.TryParseEnvelope(
                input.Plaintext,
                out var parsed,
                out var parseFailure))
        {
            return AgentSessionRestoreResult.Failure(parseFailure);
        }

        if (!AgentSessionValidation.TryValidateEnvelopeRoot(
                parsed!.Root,
                out var envelopeFailure))
        {
            return AgentSessionRestoreResult.Failure(envelopeFailure);
        }

        if (!TryValidateScope(input, parsed, out var scopeFailure))
        {
            return AgentSessionRestoreResult.Failure(scopeFailure);
        }

        if (!TryValidateProducer(input, parsed))
        {
            return AgentSessionRestoreResult.Failure(
                AgentSessionCodes.ScopeMismatch);
        }

        if (!TryValidateTransition(input, parsed.Root))
        {
            return AgentSessionRestoreResult.Failure(
                AgentSessionCodes.TransitionRejected);
        }

        if (!AgentSessionCodec.TryConvertEnvelope(
                parsed,
                out var artifact,
                out var conversionFailure))
        {
            return AgentSessionRestoreResult.Failure(conversionFailure);
        }

        var document = artifact!.Document;
        if (!AgentSessionValidation.TryValidateRoot(
                document,
                out var rootFailure))
        {
            return AgentSessionRestoreResult.Failure(rootFailure);
        }

        if (!AgentSessionValidation.TryValidateRecords(
                document,
                input.ContinuationCodec,
                out var recordFailure))
        {
            return AgentSessionRestoreResult.Failure(recordFailure);
        }

        if (!AgentStableRequestMaterializer.TryMaterialize(
                input.TrustedRequest,
                artifact.SessionSha256,
                out var stable))
        {
            return AgentSessionRestoreResult.Failure(
                AgentSessionCodes.ScopeMismatch);
        }

        if (!AgentSessionRequestReconstruction.TryReconstructHistory(
                document,
                input.ContinuationCodec,
                stable!.ControlMessages.Length,
                out var history,
                out var continuation,
                out var reconstructionFailure))
        {
            return AgentSessionRestoreResult.Failure(
                reconstructionFailure ?? AgentSessionCodes.RecordInvalid);
        }

        if (!TryValidateCurrentContext(
                input.CurrentReviewContext,
                out var currentContext))
        {
            return AgentSessionRestoreResult.Failure(
                AgentSessionCodes.RecordInvalid);
        }

        var messages = stable!.ControlMessages
            .Concat(history!)
            .Append(currentContext!)
            .ToArray();
        var runRequest = new AgentRunRequest(
            input.CurrentReviewedIdentity,
            stable.StablePlan,
            input.SessionId,
            messages,
            continuation);
        if (!TryValidateReconstructedRequest(runRequest))
        {
            return AgentSessionRestoreResult.Failure(
                AgentSessionCodes.RecordInvalid);
        }

        return AgentSessionRestoreResult.Success(runRequest, artifact);
    }

    private static bool TryValidateScope(
        AgentSessionRestoreInput input,
        AgentSessionParsedEnvelope parsed,
        out string failureCode)
    {
        failureCode = AgentSessionCodes.ScopeMismatch;
        var root = parsed.Root;
        if (!AgentStableRequestMaterializer.TryMaterialize(
                input.TrustedRequest,
                root.PriorSessionSha256,
                out var storedStable) ||
            !StringComparer.Ordinal.Equals(
                root.SessionId,
                input.SessionId) ||
            !StringComparer.Ordinal.Equals(
                root.RepositoryId,
                storedStable!.StablePlan.RepositoryId) ||
            root.ReviewTarget != storedStable.StablePlan.ReviewTarget ||
            !StringComparer.Ordinal.Equals(
                root.WorkflowIdentity,
                storedStable.StablePlan.WorkflowIdentity) ||
            !StringComparer.Ordinal.Equals(
                root.ProviderId,
                storedStable.StablePlan.ProviderId) ||
            !StringComparer.Ordinal.Equals(
                root.ModelId,
                storedStable.StablePlan.ModelId) ||
            !StringComparer.Ordinal.Equals(
                root.AdapterId,
                storedStable.StablePlan.AdapterId) ||
            !StringComparer.Ordinal.Equals(
                root.PolicySha256,
                storedStable.StablePlan.PolicySha256) ||
            !StringComparer.Ordinal.Equals(
                root.BuildId,
                storedStable.StablePlan.BuildId) ||
            !StringComparer.Ordinal.Equals(
                root.ToolsetSha256,
                storedStable.StablePlan.ToolsetSha256) ||
            !StringComparer.Ordinal.Equals(
                root.LimitsSha256,
                storedStable.StablePlan.LimitsSha256) ||
            !StringComparer.Ordinal.Equals(
                input.CurrentReviewedIdentity.RepositoryId,
                root.RepositoryId) ||
            input.CurrentReviewedIdentity.ReviewTarget != root.ReviewTarget ||
            !input.CurrentReviewedIdentity.IsValid())
        {
            return false;
        }

        failureCode = string.Empty;
        return true;
    }

    private static bool TryValidateProducer(
        AgentSessionRestoreInput input,
        AgentSessionParsedEnvelope parsed)
    {
        var accepted = input.AcceptedState;
        var root = parsed.Root;
        return accepted is not null &&
            accepted.Generation == root.Generation &&
            AgentSessionValidation.IsLowerHex(
                accepted.SessionSha256,
                64) &&
            StringComparer.Ordinal.Equals(
                accepted.SessionSha256,
                parsed.SessionSha256) &&
            AgentSessionValidation.IsLowerHex(
                accepted.EnvelopeSha256,
                64) &&
            StringComparer.Ordinal.Equals(
                accepted.ProducerBaseSha,
                root.ProducerBaseSha) &&
            StringComparer.Ordinal.Equals(
                accepted.ProducerHeadSha,
                root.ProducerHeadSha) &&
            StringComparer.Ordinal.Equals(
                accepted.PredecessorStateSha256,
                root.PredecessorStateSha256);
    }

    private static bool TryValidateTransition(
        AgentSessionRestoreInput input,
        AgentSessionEnvelopeRootDto root) =>
        input.Transition switch
        {
            AgentSessionHeadTransition.SameHead =>
                StringComparer.Ordinal.Equals(
                    input.CurrentReviewedIdentity.BaseSha,
                    root.ProducerBaseSha) &&
                StringComparer.Ordinal.Equals(
                    input.CurrentReviewedIdentity.HeadSha,
                    root.ProducerHeadSha),
            AgentSessionHeadTransition.VerifiedAhead => true,
            _ => false,
        };

    private static bool TryValidateCurrentContext(
        ProjectChatMessage message,
        out ProjectChatMessage? cloned)
    {
        cloned = null;
        if (message is null ||
            !StringComparer.Ordinal.Equals(message.Role, "user") ||
            message.Contents is null ||
            message.Contents.Length != 1 ||
            message.Contents[0] is not ProjectTextContent text ||
            !AgentValueDomains.IsUtf8(
                text.Text,
                1,
                AgentLimits.ContentBytes))
        {
            return false;
        }

        cloned = new ProjectChatMessage(
            "user",
            [new ProjectTextContent(text.Text)]);
        return true;
    }

    internal static bool TryValidateReconstructedRequest(
        AgentRunRequest runRequest)
    {
        try
        {
            var requestBytes = AgentRequestWriter.Write(
                new ProjectChatRequest(
                    runRequest.InitialMessages,
                    AgentToolRegistry.Definitions.ToArray(),
                    runRequest.Continuation,
                    ThinkingRequired: true));
            var contentParts = runRequest.InitialMessages.Sum(message =>
                message.Contents.Length) +
                (runRequest.Continuation?.Items.Length ?? 0);
            return runRequest.InitialMessages.Length <= AgentLimits.Messages &&
                contentParts <= AgentLimits.PartsTotal &&
                requestBytes.Length <= AgentLimits.RequestBytes;
        }
        catch (Exception exception) when (
            exception is OverflowException or
            Rfc8785CanonicalizationException)
        {
            return false;
        }
    }
}

internal static class AgentSessionRequestReconstruction
{
    internal static bool TryReconstructHistory(
        AgentSessionDocument document,
        IAgentContinuationCodec continuationCodec,
        int stablePrefixMessageCount,
        out ProjectChatMessage[]? history,
        out ProjectContinuation? continuation,
        out string? failureCode)
    {
        history = null;
        continuation = null;
        failureCode = AgentSessionCodes.RecordInvalid;
        var messages = new List<ProjectChatMessage>();
        var items = new List<ProjectContinuationItem>();
        foreach (var run in document.CompletedRuns)
        {
            var messagePositions =
                new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var record in run.Records)
            {
                switch (record)
                {
                    case AgentSessionReviewContextRecord context:
                        messages.Add(new ProjectChatMessage(
                            "user",
                            [new ProjectTextContent(context.Text)]));
                        break;
                    case AgentSessionAssistantMessageRecord assistant:
                        var absolutePosition = checked(
                            stablePrefixMessageCount + messages.Count);
                        messagePositions.Add(assistant.Id, absolutePosition);
                        messages.Add(new ProjectChatMessage(
                            "assistant",
                            assistant.Contents
                                .Where(content =>
                                    content is not
                                        AgentSessionContinuationSlotContent)
                                .Select(ToProjectContent)
                                .ToArray()));
                        break;
                    case AgentSessionToolResultRecord result:
                        messages.Add(new ProjectChatMessage(
                            "tool",
                            [
                                new ProjectToolResultContent(
                                    result.CallId,
                                    result.ResultJson),
                            ]));
                        break;
                    case AgentSessionReviewOutcomeRecord:
                        break;
                    default:
                        return false;
                }
            }

            foreach (var item in run.Continuation.Items)
            {
                if (!messagePositions.TryGetValue(
                        item.MessageId,
                        out var messagePosition) ||
                    !AgentContinuationCodecBoundary.TryDecode(
                        continuationCodec,
                        item.Encoding,
                        item.PayloadBytes,
                        out var value) ||
                    value is null)
                {
                    failureCode = AgentSessionCodes.ContinuationInvalid;
                    return false;
                }

                items.Add(new ProjectContinuationItem(
                    value.Readable,
                    value.Opaque,
                    value.Framing,
                    item.AssociatedCallId,
                    messagePosition,
                    item.ContentPosition));
            }
        }

        if (messages.Count + stablePrefixMessageCount >= AgentLimits.Messages)
        {
            return false;
        }

        history = messages.ToArray();
        continuation = new ProjectContinuation(
            document.ProviderId,
            document.ModelId,
            document.AdapterId,
            document.SessionId,
            items.ToArray());
        failureCode = null;
        return true;
    }

    private static ProjectChatContent ToProjectContent(
        AgentSessionAssistantContent content) =>
        content switch
        {
            AgentSessionTextContent text =>
                new ProjectTextContent(text.Text),
            AgentSessionToolCallContent call =>
                new ProjectToolCallContent(
                    call.CallId,
                    call.Name,
                    call.ArgumentsJson),
            AgentSessionTerminalCallContent terminal =>
                new ProjectToolCallContent(
                    terminal.CallId,
                    terminal.Name,
                    terminal.ArgumentsJson),
            _ => throw new InvalidOperationException(
                "Unsupported durable assistant content."),
        };
}
