using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

// A rejection boundary, not a root-cause assertion. No text from model output,
// arguments or exceptions is retained. Counts are independent of transport sends.
internal sealed record LiveAgentDiagnostic(int ScheduleIndex, string Code, int? ModelCalls, int? ToolCalls)
{
    internal static LiveAgentDiagnostic Capture(int index, AgentDiagnostic? diagnostic)
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
        return new(index, known ? code : "unknown", diagnostic.ModelCalls, diagnostic.ToolCalls);
    }
}
