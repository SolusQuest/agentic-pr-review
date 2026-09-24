using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using System.Text.Json.Serialization;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

// A rejection boundary, not a root-cause assertion. No text from model output,
// arguments or exceptions is retained. Counts are independent of transport sends.
internal sealed record LiveAgentDiagnostic(int ScheduleIndex, string Code, int? ModelCalls, int? ToolCalls,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Tool = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Category = null)
{
    internal static LiveAgentDiagnostic Capture(int index, AgentDiagnostic? diagnostic,
        LiveToolRejectionProjection? rejection = null)
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
                LiveToolRejectionProjector.InvalidJson or LiveToolRejectionProjector.InvalidContract,
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
        return this == Capture(ScheduleIndex, diagnostic, rejection);
    }
}
