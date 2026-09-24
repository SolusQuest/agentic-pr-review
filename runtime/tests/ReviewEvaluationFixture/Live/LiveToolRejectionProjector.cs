using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

// Only fixed repository-owned values leave this projection. It observes a
// response but never changes AgentLoop admission or retains provider content.
internal sealed record LiveToolRejectionProjection(string FailureCode, string Tool, string Category)
{
    internal static LiveToolRejectionProjection Unknown(string code) =>
        new(code, "unknown", "unknown");
}

internal static class LiveToolRejectionProjector
{
    internal const string InvalidJson = "arguments_json_invalid";
    internal const string InvalidContract = "arguments_contract_invalid";
    internal const string PathNotTracked = "path_not_tracked";

    internal static LiveToolRejectionProjection? Project(
        ProjectChatResponse response,
        SnapshotToolExecutor executor)
    {
        try
        {
            return ProjectCore(response, executor);
        }
        catch
        {
            // Diagnostics must never turn a valid Agent response into a failure.
            return null;
        }
    }

    private static LiveToolRejectionProjection? ProjectCore(
        ProjectChatResponse response,
        SnapshotToolExecutor executor)
    {
        if (response.Message?.Contents is not { Length: > 0 } contents ||
            contents.Length > AgentLimits.PartsPerMessage)
            return null;
        var calls = contents.OfType<ProjectToolCallContent>().ToArray();
        if (calls.Length is < 1 or > AgentLimits.ToolCallsPerResponse)
            return null;

        // AgentLoop parses every call before preflighting any call. Keep those
        // phases separate so a later malformed call cannot be misattributed to
        // an earlier valid call's untracked path.
        var prepared = new List<PreparedAgentToolCall>(calls.Length);
        foreach (var call in calls)
        {
            if (!TryPrepare(call, out var tool, out var parsed))
                return null;
            if (parsed is null)
                return new(AgentFailureCodes.ToolArgumentsInvalid, tool,
                    ArgumentCategory(call.ArgumentsJson));
            prepared.Add(parsed);
        }

        foreach (var call in prepared)
        {
            var failure = executor.Preflight(call);
            if (failure is null) continue;
            if (failure == AgentFailureCodes.ToolPathNotTracked)
                return new(AgentFailureCodes.ToolPathNotTracked, KnownTool(call.Name), PathNotTracked);
            return null;
        }
        return null;
    }

    private static string ArgumentCategory(string json)
    {
        try
        {
            using var _ = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            return InvalidContract;
        }
        catch (JsonException)
        {
            return InvalidJson;
        }
    }

    // Return the registry constant, never the provider's original name.
    private static string KnownTool(string name) => name switch
    {
        AgentToolRegistry.ListFilesName => AgentToolRegistry.ListFilesName,
        AgentToolRegistry.ListChangedFilesName => AgentToolRegistry.ListChangedFilesName,
        AgentToolRegistry.ReadDiffName => AgentToolRegistry.ReadDiffName,
        AgentToolRegistry.ReadFileName => AgentToolRegistry.ReadFileName,
        AgentToolRegistry.SearchTextName => AgentToolRegistry.SearchTextName,
        _ => "unknown",
    };

    private static bool TryPrepare(ProjectToolCallContent call, out string tool,
        out PreparedAgentToolCall? prepared)
    {
        prepared = null;
        tool = KnownTool(call.Name);
        switch (call.Name)
        {
            case AgentToolRegistry.ListFilesName:
                if (AgentToolArguments.TryListFilesProvider(call.ArgumentsJson, out var list))
                    prepared = new PreparedListFilesCall(call.CallId, list!);
                return true;
            case AgentToolRegistry.ListChangedFilesName:
                if (AgentToolArguments.TryListChangedFilesProvider(call.ArgumentsJson, out var changed))
                    prepared = new PreparedListChangedFilesCall(call.CallId, changed!);
                return true;
            case AgentToolRegistry.ReadDiffName:
                if (AgentToolArguments.TryReadDiffProvider(call.ArgumentsJson, out var diff))
                    prepared = new PreparedReadDiffCall(call.CallId, diff!);
                return true;
            case AgentToolRegistry.ReadFileName:
                if (AgentToolArguments.TryReadFileProvider(call.ArgumentsJson, out var read))
                    prepared = new PreparedReadFileCall(call.CallId, read!);
                return true;
            case AgentToolRegistry.SearchTextName:
                if (AgentToolArguments.TrySearchTextProvider(call.ArgumentsJson, out var search))
                    prepared = new PreparedSearchTextCall(call.CallId, search!);
                return true;
            default:
                return false;
        }
    }
}
