using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Agent.Core;

namespace AgenticPrReview.Runtime.Agent.Loop;

// These are the complete repository-authored bytes shown to the model for an
// rejected batch before execution. Provider arguments and exceptions are never inputs
// to the writer.
internal static class AgentRecoveryFeedback
{
    internal const string RejectedArguments = "{\"_apr_rejected\":true}";
    internal const string ArgumentsInvalid =
        "{\"status\":\"error\",\"code\":\"arguments_invalid\",\"retryable\":true}";
    internal const string BatchNotExecuted =
        "{\"status\":\"error\",\"code\":\"batch_not_executed\",\"retryable\":true}";
    internal const string TrackedPathMissing =
        "{\"status\":\"error\",\"code\":\"path_not_tracked\",\"domain\":\"current_tracked_files\",\"retryable\":true}";
    internal const string ChangedPathMissing =
        "{\"status\":\"error\",\"code\":\"path_not_tracked\",\"domain\":\"current_changed_files\",\"retryable\":true}";
    internal const string CursorInvalid =
        "{\"status\":\"error\",\"code\":\"cursor_invalid\",\"action\":\"restart_listing\",\"retryable\":true}";

    internal static string? ForPreflight(PreparedAgentToolCall call, string failure) => (call, failure) switch
    {
        (PreparedReadFileCall or PreparedSearchTextCall { Arguments.Path: not null },
            AgentFailureCodes.ToolPathNotTracked) => TrackedPathMissing,
        (PreparedReadDiffCall, AgentFailureCodes.ToolPathNotTracked) => ChangedPathMissing,
        (PreparedListFilesCall { Arguments.After: not null } or
            PreparedListChangedFilesCall { Arguments.After: not null }, AgentFailureCodes.ToolCursorInvalid) => CursorInvalid,
        _ => null,
    };

    internal static bool IsRejectedError(string name, string value) =>
        StringComparer.Ordinal.Equals(value, ArgumentsInvalid) ||
        name == AgentToolRegistry.ListFilesName && IsCanonicalPathError(value) ||
        name is AgentToolRegistry.ReadFileName or AgentToolRegistry.SearchTextName && value == TrackedPathMissing ||
        name == AgentToolRegistry.ReadDiffName && value == ChangedPathMissing ||
        name is AgentToolRegistry.ListFilesName or AgentToolRegistry.ListChangedFilesName && value == CursorInvalid;

    internal static string ListFilesPathInvalid(ListFilesPathRejection rejection) =>
        "{\"status\":\"error\",\"code\":\"list_files_path_invalid\",\"path_field\":\"" +
        Field(rejection.Field) + "\",\"path_rule\":\"" +
        Rule(rejection.Rule) + "\",\"retryable\":true}";

    internal static bool IsCanonicalError(string value) =>
        StringComparer.Ordinal.Equals(value, ArgumentsInvalid) ||
        StringComparer.Ordinal.Equals(value, BatchNotExecuted) ||
        value is TrackedPathMissing or ChangedPathMissing or CursorInvalid ||
        AllPathErrors.Contains(value);

    internal static bool IsCanonicalPathError(string value) =>
        AllPathErrors.Contains(value);

    private static readonly HashSet<string> AllPathErrors =
        Enum.GetValues<ListFilesPathField>()
            .SelectMany(field => Enum.GetValues<RepositoryPathFailure>()
                .Select(rule => new ListFilesPathRejection(field, rule)))
            .Where(rejection =>
                rejection.Field is ListFilesPathField.Both or ListFilesPathField.Unknown
                    ? rejection.Rule == RepositoryPathFailure.Unknown
                    : rejection.Rule is not RepositoryPathFailure.None and
                        not RepositoryPathFailure.Unknown)
            .Select(ListFilesPathInvalid)
            .ToHashSet(StringComparer.Ordinal);

    private static string Field(ListFilesPathField field) => field switch
    {
        ListFilesPathField.Prefix => "prefix",
        ListFilesPathField.After => "after",
        ListFilesPathField.Both => "both",
        _ => "unknown",
    };

    private static string Rule(RepositoryPathFailure rule) => rule switch
    {
        RepositoryPathFailure.Empty => "empty",
        RepositoryPathFailure.Absolute => "absolute",
        RepositoryPathFailure.TooLong => "too_long",
        RepositoryPathFailure.InvalidUnicode => "invalid_unicode",
        RepositoryPathFailure.ForbiddenCharacter => "forbidden_character",
        RepositoryPathFailure.EmptySegment => "empty_segment",
        RepositoryPathFailure.DotSegment => "dot_segment",
        RepositoryPathFailure.TrailingDotOrSpace => "trailing_dot_or_space",
        _ => "unknown",
    };
}
