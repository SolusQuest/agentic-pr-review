using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;

namespace AgenticPrReview.Runtime.Solus;

internal static class SolusToolCorrection
{
    internal const string TrackedMissing = "tracked_path_missing";
    internal const string ChangedMissing = "changed_path_missing";
    internal const string CursorInvalid = "cursor_invalid";
    private static readonly IReadOnlyDictionary<ListFilesPathRejection, string> PathKeys =
        Enum.GetValues<ListFilesPathField>()
            .SelectMany(field => Enum.GetValues<RepositoryPathFailure>()
                .Select(rule => new ListFilesPathRejection(field, rule)))
            .Where(rejection => rejection.Field is ListFilesPathField.Both or ListFilesPathField.Unknown
                ? rejection.Rule == RepositoryPathFailure.Unknown
                : rejection.Rule is not RepositoryPathFailure.None and not RepositoryPathFailure.Unknown)
            .ToDictionary(rejection => rejection, rejection =>
                "list_path_" + rejection.Field.ToString().ToLowerInvariant() + "_" +
                rejection.Rule.ToString().ToLowerInvariant());

    internal static ToolArgumentCorrectionPolicy Policy { get; } = BuildPolicy();

    private static ToolArgumentCorrectionPolicy BuildPolicy()
    {
        var errors = new List<ToolArgumentError>
        {
            new(TrackedMissing, "path_not_tracked", [new("domain", "current_tracked_files")]),
            new(ChangedMissing, "path_not_tracked", [new("domain", "current_changed_files")]),
            new(CursorInvalid, "cursor_invalid", [new("action", "restart_listing")]),
        };
        foreach (var (rejection, key) in PathKeys)
        {
            // Parse repository-authored fixed bytes only, never rejected arguments.
            using var document = System.Text.Json.JsonDocument.Parse(AgentRecoveryFeedback.ListFilesPathInvalid(rejection));
            var root = document.RootElement;
            errors.Add(new(key, "list_files_path_invalid", [
                new("path_field", root.GetProperty("path_field").GetString()!),
                new("path_rule", root.GetProperty("path_rule").GetString()!)]));
        }
        return new("apr_snapshot_arguments_v1", errors, "_apr_rejected");
    }

    internal static IReadOnlyList<string> AllowedKeys(string name)
    {
        var keys = new List<string> { ToolArgumentCorrectionPolicy.ArgumentsInvalid };
        switch (name)
        {
            case AgentToolRegistry.ListFilesName:
                keys.AddRange(PathKeys.Values); keys.Add(CursorInvalid); break;
            case AgentToolRegistry.ListChangedFilesName:
                keys.Add(CursorInvalid); break;
            case AgentToolRegistry.ReadFileName:
            case AgentToolRegistry.SearchTextName:
                keys.Add(TrackedMissing); break;
            case AgentToolRegistry.ReadDiffName:
                keys.Add(ChangedMissing); break;
            default: throw new ArgumentException("APR_SOLUS_UNKNOWN_TOOL");
        }
        return keys.AsReadOnly();
    }

    internal static ToolArgumentDisposition Classify(ToolCall call, IAgentToolExecutor executor)
    {
        if (!TryParse(call, out var parsed, out var rejection))
            return ToolArgumentDisposition.Correct(rejection is { } path && PathKeys.TryGetValue(path, out var key)
                ? key : ToolArgumentCorrectionPolicy.ArgumentsInvalid);
        var failure = executor.Preflight(parsed!);
        if (failure is null) return ToolArgumentDisposition.Allow();
        var feedback = AgentRecoveryFeedback.ForPreflight(parsed!, failure);
        return feedback switch
        {
            AgentRecoveryFeedback.TrackedPathMissing => ToolArgumentDisposition.Correct(TrackedMissing),
            AgentRecoveryFeedback.ChangedPathMissing => ToolArgumentDisposition.Correct(ChangedMissing),
            AgentRecoveryFeedback.CursorInvalid => ToolArgumentDisposition.Correct(CursorInvalid),
            _ => ToolArgumentDisposition.Fail(ToolError.DomainRejected),
        };
    }

    internal static bool TryParse(ToolCall call, out PreparedAgentToolCall? parsed,
        out ListFilesPathRejection? rejection)
    {
        parsed = null; rejection = null;
        switch (call.ToolName)
        {
            case AgentToolRegistry.ListFilesName:
                if (AgentToolArguments.TryListFilesProvider(call.ArgumentsJson, out var list, out _, out rejection))
                    parsed = new PreparedListFilesCall(call.CallId, list!);
                break;
            case AgentToolRegistry.ListChangedFilesName:
                if (AgentToolArguments.TryListChangedFilesProvider(call.ArgumentsJson, out var changed))
                    parsed = new PreparedListChangedFilesCall(call.CallId, changed!);
                break;
            case AgentToolRegistry.ReadDiffName:
                if (AgentToolArguments.TryReadDiffProvider(call.ArgumentsJson, out var diff))
                    parsed = new PreparedReadDiffCall(call.CallId, diff!);
                break;
            case AgentToolRegistry.ReadFileName:
                if (AgentToolArguments.TryReadFileProvider(call.ArgumentsJson, out var read))
                    parsed = new PreparedReadFileCall(call.CallId, read!);
                break;
            case AgentToolRegistry.SearchTextName:
                if (AgentToolArguments.TrySearchTextProvider(call.ArgumentsJson, out var search))
                    parsed = new PreparedSearchTextCall(call.CallId, search!);
                break;
        }
        return parsed is not null;
    }
}
