using System.Text;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Agent.Core;

internal static class AgentStableRequestMaterializer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool TryMaterialize(
        AgentTrustedRequest trusted,
        string? priorSessionSha256,
        out AgentMaterializedStableRequest? materialized)
    {
        materialized = null;
        if (trusted is null ||
            trusted.TrustedPolicyBytes is null ||
            trusted.TrustedPolicyBytes.Length > AgentLimits.RequestBytes ||
            !AgentValueDomains.IsUtf8(trusted.RepositoryId, 1, 128) ||
            trusted.ReviewTarget < 1 ||
            !AgentValueDomains.IsUtf8(trusted.WorkflowIdentity, 1, 256) ||
            !AgentValueDomains.IsUtf8(trusted.BuildId, 1, 256) ||
            !AgentValueDomains.IsUtf8(trusted.ProviderId, 1, 128) ||
            !AgentValueDomains.IsUtf8(trusted.ModelId, 1, 128) ||
            !AgentValueDomains.IsUtf8(trusted.AdapterId, 1, 128) ||
            !AgentLimitAuthority.TryResolve(trusted.AdapterId, trusted.LimitAuthority, out var limitProfile) ||
            (priorSessionSha256 is not null &&
                !IsPriorStateHash(priorSessionSha256)) ||
            !TryMaterializeControlMessages(
                trusted.TrustedPolicyBytes,
                out var controlMessages))
        {
            return false;
        }

        var plan = new StableAgentPlan(
            trusted.RepositoryId,
            trusted.ReviewTarget,
            trusted.WorkflowIdentity,
            AgentCanonical.HashRaw(trusted.TrustedPolicyBytes),
            AgentCanonical.ToolsetSha256(AgentToolRegistry.Definitions),
            AgentCanonical.LimitsSha256(limitProfile, trusted.LimitAuthority?.TokenBudget,
                trusted.LimitAuthority?.ModelCalls ?? AgentLimits.ModelCalls,
                trusted.LimitAuthority?.TimeoutSeconds ?? AgentLimits.DeadlineSeconds),
            trusted.BuildId,
            trusted.ProviderId,
            trusted.ModelId,
            trusted.AdapterId,
            priorSessionSha256);
        materialized = new AgentMaterializedStableRequest(
            plan,
            controlMessages!);
        return true;
    }

    private static bool TryMaterializeControlMessages(
        byte[] trustedPolicyBytes,
        out ProjectChatMessage[]? messages)
    {
        messages = null;
        string policyText;
        try
        {
            policyText = StrictUtf8.GetString(trustedPolicyBytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (!AgentValueDomains.IsUtf8(
                policyText,
                1,
                AgentLimits.ContentBytes))
        {
            return false;
        }

        messages =
        [
            new ProjectChatMessage(
                "system",
                [new ProjectTextContent(policyText)]),
        ];
        return true;
    }
    private static bool IsPriorStateHash(string value) =>
        value.Length == 64 &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

}

