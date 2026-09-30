using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;

// Closed diagnostic attribution only. Never retains arguments, paths or their hashes.
internal sealed record EconomicsToolRejection(string Tool, string Category)
{
    internal static EconomicsToolRejection? Capture(string? diagnostic, LiveToolRejectionProjection? projection)
    {
        if (projection is null || projection.FailureCode != diagnostic) return null;
        var value = new EconomicsToolRejection(projection.Tool, projection.Category);
        return value.IsValid(diagnostic) ? value : null;
    }

    internal bool IsValid(string? diagnostic) => diagnostic switch
    {
        AgentFailureCodes.ToolPathNotTracked =>
            Tool is AgentToolRegistry.ReadFileName or AgentToolRegistry.SearchTextName or AgentToolRegistry.ReadDiffName &&
            Category == LiveToolRejectionProjector.PathNotTracked,
        AgentFailureCodes.ToolArgumentsInvalid =>
            Tool is AgentToolRegistry.ReadFileName or AgentToolRegistry.SearchTextName or AgentToolRegistry.ReadDiffName or
                AgentToolRegistry.ListFilesName or AgentToolRegistry.ListChangedFilesName &&
            (Category is LiveToolRejectionProjector.InvalidJson or LiveToolRejectionProjector.InvalidContract ||
                Tool == AgentToolRegistry.ListFilesName && Category is
                    LiveToolRejectionProjector.ListInputInvalid or LiveToolRejectionProjector.ListNormalizationInvalid or
                    LiveToolRejectionProjector.ListShapeInvalid or LiveToolRejectionProjector.ListPathInvalid or
                    LiveToolRejectionProjector.ListSpellingInvalid),
        _ => false,
    };
}
