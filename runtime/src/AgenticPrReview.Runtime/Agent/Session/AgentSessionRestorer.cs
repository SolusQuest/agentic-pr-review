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

        if (!AgentSessionCodec.TryParse(
                input.Plaintext,
                out var artifact,
                out var parseFailure))
        {
            return AgentSessionRestoreResult.Failure(parseFailure);
        }

        var document = artifact!.Document;
        if (!AgentSessionValidation.TryValidateRoot(
                document,
                out var rootFailure))
        {
            return AgentSessionRestoreResult.Failure(rootFailure);
        }

        if (!TryValidateScope(input, artifact, out var scopeFailure))
        {
            return AgentSessionRestoreResult.Failure(scopeFailure);
        }

        if (!TryValidateProducer(input, artifact))
        {
            return AgentSessionRestoreResult.Failure(
                AgentSessionCodes.ScopeMismatch);
        }

        if (!TryValidateTransition(input, document))
        {
            return AgentSessionRestoreResult.Failure(
                AgentSessionCodes.TransitionRejected);
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
        AgentSessionArtifact artifact,
        out string failureCode)
    {
        failureCode = AgentSessionCodes.ScopeMismatch;
        var document = artifact.Document;
        if (!AgentStableRequestMaterializer.TryMaterialize(
                input.TrustedRequest,
                document.PriorSessionSha256,
                out var storedStable) ||
            !StringComparer.Ordinal.Equals(
                document.SessionId,
                input.SessionId) ||
            !StringComparer.Ordinal.Equals(
                document.RepositoryId,
                storedStable!.StablePlan.RepositoryId) ||
            document.ReviewTarget != storedStable.StablePlan.ReviewTarget ||
            !StringComparer.Ordinal.Equals(
                document.WorkflowIdentity,
                storedStable.StablePlan.WorkflowIdentity) ||
            !StringComparer.Ordinal.Equals(
                document.ProviderId,
                storedStable.StablePlan.ProviderId) ||
            !StringComparer.Ordinal.Equals(
                document.ModelId,
                storedStable.StablePlan.ModelId) ||
            !StringComparer.Ordinal.Equals(
                document.AdapterId,
                storedStable.StablePlan.AdapterId) ||
            !StringComparer.Ordinal.Equals(
                document.PolicySha256,
                storedStable.StablePlan.PolicySha256) ||
            !StringComparer.Ordinal.Equals(
                document.BuildId,
                storedStable.StablePlan.BuildId) ||
            !StringComparer.Ordinal.Equals(
                document.ToolsetSha256,
                storedStable.StablePlan.ToolsetSha256) ||
            !StringComparer.Ordinal.Equals(
                document.LimitsSha256,
                storedStable.StablePlan.LimitsSha256) ||
            !StringComparer.Ordinal.Equals(
                input.CurrentReviewedIdentity.RepositoryId,
                document.RepositoryId) ||
            input.CurrentReviewedIdentity.ReviewTarget != document.ReviewTarget ||
            !input.CurrentReviewedIdentity.IsValid())
        {
            return false;
        }

        failureCode = string.Empty;
        return true;
    }

    private static bool TryValidateProducer(
        AgentSessionRestoreInput input,
        AgentSessionArtifact artifact)
    {
        var accepted = input.AcceptedState;
        var document = artifact.Document;
        return accepted is not null &&
            accepted.Generation == document.Generation &&
            AgentSessionValidation.IsLowerHex(
                accepted.SessionSha256,
                64) &&
            StringComparer.Ordinal.Equals(
                accepted.SessionSha256,
                artifact.SessionSha256) &&
            AgentSessionValidation.IsLowerHex(
                accepted.EnvelopeSha256,
                64) &&
            StringComparer.Ordinal.Equals(
                accepted.ProducerBaseSha,
                document.ProducerBaseSha) &&
            StringComparer.Ordinal.Equals(
                accepted.ProducerHeadSha,
                document.ProducerHeadSha) &&
            StringComparer.Ordinal.Equals(
                accepted.PredecessorStateSha256,
                document.PredecessorStateSha256);
    }

    private static bool TryValidateTransition(
        AgentSessionRestoreInput input,
        AgentSessionDocument document) =>
        input.Transition switch
        {
            AgentSessionHeadTransition.SameHead =>
                StringComparer.Ordinal.Equals(
                    input.CurrentReviewedIdentity.BaseSha,
                    document.ProducerBaseSha) &&
                StringComparer.Ordinal.Equals(
                    input.CurrentReviewedIdentity.HeadSha,
                    document.ProducerHeadSha),
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

    private static bool TryValidateReconstructedRequest(
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
                    !continuationCodec.TryDecode(
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
