using AgenticPrReview.Runtime.Agent.Session;

namespace AgenticPrReview.Runtime.Host.State;

internal sealed class AgentSessionRestrictedStateAdmission
    : IRestrictedStateSessionAdmission
{
    public RestrictedStateSessionAdmissionResult Admit(
        AuthorizedStateAccess access,
        ReadOnlyMemory<byte> plaintext,
        RestrictedStateSessionAdmissionContext context)
    {
        if (access is null ||
            context is null ||
            context.SessionContext is null ||
            !RestrictedStateValidation.IsLowerHex(
                context.ProducerBaseSha,
                40) ||
            !RestrictedStateValidation.IsLowerHex(
                context.ProducerHeadSha,
                40) ||
            context.Generation is < 0 or >
                RestrictedStateFormat.MaximumGeneration ||
            ((context.Generation == 0 &&
                    context.PredecessorEnvelopeSha256 is not null) ||
                (context.Generation > 0 &&
                    !RestrictedStateValidation.IsLowerHex(
                        context.PredecessorEnvelopeSha256,
                        64))))
        {
            return RestrictedStateSessionAdmissionResult.Failure();
        }

        var admitted = AgentSessionStateBoundary.Admit(
            plaintext.Span,
            context.SessionContext);
        if (!admitted.Succeeded ||
            !MatchesScope(admitted.Scope!, access.Scope) ||
            admitted.Generation != context.Generation ||
            !StringComparer.Ordinal.Equals(
                admitted.ProducerBaseSha,
                context.ProducerBaseSha) ||
            !StringComparer.Ordinal.Equals(
                admitted.ProducerHeadSha,
                context.ProducerHeadSha) ||
            !StringComparer.Ordinal.Equals(
                admitted.PredecessorEnvelopeSha256,
                context.PredecessorEnvelopeSha256))
        {
            return RestrictedStateSessionAdmissionResult.Failure();
        }

        return RestrictedStateSessionAdmissionResult.Success(
            new RestrictedStateAdmittedSession(
                plaintext.ToArray(),
                admitted.SessionSha256!,
                admitted.ProducerBaseSha!,
                admitted.ProducerHeadSha!,
                admitted.Generation,
                admitted.PredecessorEnvelopeSha256,
                admitted.Value!));
    }

    private static bool MatchesScope(
        AgentSessionStateScope admitted,
        RestrictedStateScope authorized) =>
        StringComparer.Ordinal.Equals(
            admitted.RepositoryId,
            authorized.RepositoryId) &&
        StringComparer.Ordinal.Equals(
            admitted.WorkflowIdentity,
            authorized.WorkflowIdentity) &&
        admitted.ReviewTarget == authorized.ReviewTarget &&
        StringComparer.Ordinal.Equals(
            admitted.SessionId,
            authorized.SessionId) &&
        StringComparer.Ordinal.Equals(
            admitted.ProviderId,
            authorized.ProviderId) &&
        StringComparer.Ordinal.Equals(
            admitted.ModelId,
            authorized.ModelId) &&
        StringComparer.Ordinal.Equals(
            admitted.AdapterId,
            authorized.AdapterId) &&
        StringComparer.Ordinal.Equals(
            admitted.PolicySha256,
            authorized.PolicySha256) &&
        StringComparer.Ordinal.Equals(
            admitted.LimitsSha256,
            authorized.LimitsSha256) &&
        StringComparer.Ordinal.Equals(
            admitted.ToolsetSha256,
            authorized.ToolsetSha256) &&
        StringComparer.Ordinal.Equals(
            admitted.BuildId,
            authorized.BuildId);
}
