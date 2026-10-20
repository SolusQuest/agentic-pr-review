using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Agent.Core;

internal static class AgentRequestBounds
{
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
