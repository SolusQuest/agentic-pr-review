using AgenticPrReview.Runtime.Agent.Core;

namespace AgenticPrReview.Runtime.Agent.Chat;

internal interface IAuthoredResponseSource
{
    Task<ProjectChatResponse> GetResponseAsync(
        ProjectChatRequest request,
        CancellationToken cancellationToken);
}

// Historical report stage values and test-only authored source failures.
// No current runtime adapter or execution normalization is implemented here.
internal enum ProjectChatNormalizationReason
{
    None,
    RequestProjection,
    TransportContract,
    ProviderJson,
    ProviderRoot,
    ProviderUsage,
    ProviderChoice,
    ProviderChoiceShape,
    ProviderChoiceIndex,
    ProviderChoiceLogprobs,
    ProviderChoiceMessageMissing,
    ProviderFinishReasonLength,
    ProviderFinishReasonContentFilter,
    ProviderFinishReasonResource,
    ProviderFinishReasonAborted,
    ProviderFinishReasonOther,
    ProviderMessage,
    ProviderInternal,
    ResponseProjection,
}

internal sealed class ProjectChatNormalizationException : Exception
{
    internal ProjectChatNormalizationException()
        : this(AgentFailureCodes.ResponseInvalid)
    {
    }

    internal ProjectChatNormalizationException(string diagnosticCode)
        : this(diagnosticCode, ProjectChatNormalizationReason.None)
    {
    }

    internal ProjectChatNormalizationException(string diagnosticCode,
        ProjectChatNormalizationReason reason)
        : base("The backend response could not be normalized.")
    {
        if (diagnosticCode is not (
            AgentFailureCodes.ResponseInvalid or
            AgentFailureCodes.ContextLimit or
            AgentFailureCodes.MissingTool))
        {
            throw new ArgumentOutOfRangeException(nameof(diagnosticCode));
        }

        DiagnosticCode = diagnosticCode;
        Reason = reason;
    }

    internal string DiagnosticCode { get; }
    internal ProjectChatNormalizationReason Reason { get; }
}
