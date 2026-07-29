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
            context.SessionContext is null)
        {
            return RestrictedStateSessionAdmissionResult.Failure();
        }

        var admitted = AgentSessionStateBoundary.Admit(
            plaintext.Span,
            context.SessionContext);
        if (!admitted.Succeeded ||
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
}
