using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;

namespace AgenticPrReview.Runtime.Agent.Session;

internal sealed record AgentSessionStateAdmissionContext(
    AgentSessionTrustedRequest TrustedRequest,
    string SessionId,
    ReviewedIdentity CurrentReviewedIdentity,
    ProjectChatMessage CurrentReviewContext,
    AgentSessionHeadTransition Transition,
    IAgentContinuationCodec ContinuationCodec,
    string? EnvelopeSha256);

internal sealed record AgentSessionStateAdmittedValue(
    AgentRunRequest RunRequest,
    AgentSessionArtifact Artifact);

internal sealed record AgentSessionStateScope(
    string RepositoryId,
    string WorkflowIdentity,
    long ReviewTarget,
    string SessionId,
    string ProviderId,
    string ModelId,
    string AdapterId,
    string PolicySha256,
    string LimitsSha256,
    string ToolsetSha256,
    string BuildId);

internal sealed record AgentSessionStateAdmissionResult(
    AgentSessionStateAdmittedValue? Value,
    AgentSessionStateScope? Scope,
    string? ProducerBaseSha,
    string? ProducerHeadSha,
    long Generation,
    string? PredecessorEnvelopeSha256,
    string? SessionSha256)
{
    internal bool Succeeded =>
        Value is not null &&
        Scope is not null &&
        ProducerBaseSha is not null &&
        ProducerHeadSha is not null &&
        SessionSha256 is not null;

    internal static AgentSessionStateAdmissionResult Failure() =>
        new(null, null, null, null, 0, null, null);
}

internal static class AgentSessionStateBoundary
{
    private const string UnpreparedEnvelopeSha256 =
        "0000000000000000000000000000000000000000000000000000000000000000";

    internal static AgentSessionStateAdmissionResult Admit(
        ReadOnlySpan<byte> plaintext,
        AgentSessionStateAdmissionContext context)
    {
        if (context is null ||
            !AgentSessionCodec.TryParseEnvelope(
                plaintext,
                out var parsed,
                out _) ||
            !AgentSessionValidation.TryValidateEnvelopeRoot(
                parsed!.Root,
                out _))
        {
            return AgentSessionStateAdmissionResult.Failure();
        }

        var root = parsed.Root;
        var accepted = new AgentSessionAcceptedState(
            root.Generation,
            parsed.SessionSha256,
            context.EnvelopeSha256 ?? UnpreparedEnvelopeSha256,
            root.ProducerBaseSha!,
            root.ProducerHeadSha!,
            root.PredecessorStateSha256);
        var restored = AgentSessionRestorer.Restore(
            new AgentSessionRestoreInput(
                AgentSessionLocatorFamily.Current,
                AgentSessionRestoreIntent.Explicit,
                ExplicitReset: false,
                plaintext.ToArray(),
                accepted,
                context.TrustedRequest,
                context.SessionId,
                context.CurrentReviewedIdentity,
                context.CurrentReviewContext,
                context.Transition,
                context.ContinuationCodec));
        if (!restored.Succeeded)
        {
            return AgentSessionStateAdmissionResult.Failure();
        }

        return new AgentSessionStateAdmissionResult(
            new AgentSessionStateAdmittedValue(
                restored.RunRequest!,
                restored.Artifact!),
            new AgentSessionStateScope(
                root.RepositoryId!,
                root.WorkflowIdentity!,
                root.ReviewTarget,
                root.SessionId!,
                root.ProviderId!,
                root.ModelId!,
                root.AdapterId!,
                root.PolicySha256!,
                root.LimitsSha256!,
                root.ToolsetSha256!,
                root.BuildId!),
            root.ProducerBaseSha,
            root.ProducerHeadSha,
            root.Generation,
            root.PredecessorStateSha256,
            parsed.SessionSha256);
    }
}
