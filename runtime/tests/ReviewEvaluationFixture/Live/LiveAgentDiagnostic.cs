using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using System.Text.Json.Serialization;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

internal static class LiveNormalizationCategories
{
    internal const string RequestProjection = "request_projection_invalid";
    internal const string TransportContract = "transport_contract_invalid";
    internal const string ProviderJson = "provider_json_invalid";
    internal const string ProviderRoot = "provider_root_invalid";
    internal const string ProviderUsage = "provider_usage_invalid";
    internal const string ProviderChoice = "provider_choice_invalid";
    internal const string ProviderMessage = "provider_message_invalid";
    internal const string ProviderInternal = "provider_internal_invalid";
    internal const string ResponseProjection = "response_projection_invalid";
}

// A rejection boundary, not a root-cause assertion. No text from model output,
// arguments or exceptions is retained. Counts are independent of transport sends.
internal sealed record LiveAgentDiagnostic(int ScheduleIndex, string Code, int? ModelCalls, int? ToolCalls,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Tool = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Category = null)
{
    internal static LiveAgentDiagnostic Capture(int index, AgentDiagnostic? diagnostic,
        LiveToolRejectionProjection? rejection = null,
        ProjectChatNormalizationReason? normalizationReason = null)
    {
        if (diagnostic is null || diagnostic.ModelCalls is < 0 or > AgentLimits.ModelCalls ||
            diagnostic.ToolCalls is < 0 or > AgentLimits.ToolCalls)
            return new(index, "unknown", null, null);
        var code = diagnostic.Code;
        var known = AgentToolResultAdmission.IsFrozenFailureCode(code) || code is
            AgentFailureCodes.Cancelled or AgentFailureCodes.DeadlineExceeded or AgentFailureCodes.ChatFailed or
            AgentFailureCodes.ModelLimit or AgentFailureCodes.ToolLimit or AgentFailureCodes.TokenLimit or
            AgentFailureCodes.RequestTooLarge or AgentFailureCodes.ResponseTooLarge or AgentFailureCodes.UsageInvalid or
            AgentFailureCodes.ResponseInvalid or AgentFailureCodes.MissingTool or AgentFailureCodes.UnknownTool or
            AgentFailureCodes.ToolArgumentsInvalid or AgentFailureCodes.TerminalSequenceInvalid or AgentFailureCodes.TerminalInvalid;
        if (!known) return new(index, "unknown", diagnostic.ModelCalls, diagnostic.ToolCalls);
        if (code == AgentFailureCodes.ResponseInvalid &&
            normalizationReason is { } reason)
        {
            var category = NormalizationCategory(reason);
            if (category is not null)
                return new(index, code, diagnostic.ModelCalls, diagnostic.ToolCalls,
                    Category: category);
        }
        if (code is not (AgentFailureCodes.ToolArgumentsInvalid or AgentFailureCodes.ToolPathNotTracked) ||
            rejection is null)
            return new(index, code, diagnostic.ModelCalls, diagnostic.ToolCalls);

        var recognizedTool = rejection.Tool is
            AgentToolRegistry.ListFilesName or AgentToolRegistry.ListChangedFilesName or
            AgentToolRegistry.ReadDiffName or AgentToolRegistry.ReadFileName or
            AgentToolRegistry.SearchTextName;
        var recognizedCategory = code switch
        {
            AgentFailureCodes.ToolArgumentsInvalid => rejection.Category is
                LiveToolRejectionProjector.InvalidJson or LiveToolRejectionProjector.InvalidContract ||
                rejection.Tool == AgentToolRegistry.ListFilesName && rejection.Category is
                    LiveToolRejectionProjector.ListInputInvalid or
                    LiveToolRejectionProjector.ListNormalizationInvalid or
                    LiveToolRejectionProjector.ListShapeInvalid or
                    LiveToolRejectionProjector.ListPathInvalid or
                    LiveToolRejectionProjector.ListSpellingInvalid,
            AgentFailureCodes.ToolPathNotTracked =>
                rejection.Category == LiveToolRejectionProjector.PathNotTracked,
            _ => false,
        };
        if (rejection.FailureCode == code && recognizedTool && recognizedCategory)
            return new(index, code, diagnostic.ModelCalls, diagnostic.ToolCalls,
                rejection.Tool, rejection.Category);
        return new(index, code, diagnostic.ModelCalls, diagnostic.ToolCalls,
            "unknown", "unknown");
    }

    internal bool IsCanonical()
    {
        var diagnostic = ModelCalls is { } model && ToolCalls is { } tool
            ? new AgentDiagnostic(Code, model, tool)
            : null;
        var rejection = Tool is null && Category is null
            ? null
            : new LiveToolRejectionProjection(Code, Tool ?? string.Empty, Category ?? string.Empty);
        var reason = Category switch
        {
            LiveNormalizationCategories.RequestProjection => ProjectChatNormalizationReason.RequestProjection,
            LiveNormalizationCategories.TransportContract => ProjectChatNormalizationReason.TransportContract,
            LiveNormalizationCategories.ProviderJson => ProjectChatNormalizationReason.ProviderJson,
            LiveNormalizationCategories.ProviderRoot => ProjectChatNormalizationReason.ProviderRoot,
            LiveNormalizationCategories.ProviderUsage => ProjectChatNormalizationReason.ProviderUsage,
            LiveNormalizationCategories.ProviderChoice => ProjectChatNormalizationReason.ProviderChoice,
            LiveNormalizationCategories.ProviderMessage => ProjectChatNormalizationReason.ProviderMessage,
            LiveNormalizationCategories.ProviderInternal => ProjectChatNormalizationReason.ProviderInternal,
            LiveNormalizationCategories.ResponseProjection => ProjectChatNormalizationReason.ResponseProjection,
            _ => (ProjectChatNormalizationReason?)null,
        };
        return this == Capture(ScheduleIndex, diagnostic, rejection, reason);
    }

    private static string? NormalizationCategory(ProjectChatNormalizationReason reason) => reason switch
    {
        ProjectChatNormalizationReason.RequestProjection => LiveNormalizationCategories.RequestProjection,
        ProjectChatNormalizationReason.TransportContract => LiveNormalizationCategories.TransportContract,
        ProjectChatNormalizationReason.ProviderJson => LiveNormalizationCategories.ProviderJson,
        ProjectChatNormalizationReason.ProviderRoot => LiveNormalizationCategories.ProviderRoot,
        ProjectChatNormalizationReason.ProviderUsage => LiveNormalizationCategories.ProviderUsage,
        ProjectChatNormalizationReason.ProviderChoice => LiveNormalizationCategories.ProviderChoice,
        ProjectChatNormalizationReason.ProviderMessage => LiveNormalizationCategories.ProviderMessage,
        ProjectChatNormalizationReason.ProviderInternal => LiveNormalizationCategories.ProviderInternal,
        ProjectChatNormalizationReason.ResponseProjection => LiveNormalizationCategories.ResponseProjection,
        _ => null,
    };
}
