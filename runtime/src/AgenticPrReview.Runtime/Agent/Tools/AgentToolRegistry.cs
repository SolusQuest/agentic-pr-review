using System.Collections.Immutable;
using AgenticPrReview.Runtime.Agent.Chat;

namespace AgenticPrReview.Runtime.Agent.Tools;

internal static class AgentToolRegistry
{
    internal const string ListFilesName = "list_files";
    internal const string ListChangedFilesName = "list_changed_files";
    internal const string ReadDiffName = "read_diff";
    internal const string ReadFileName = "read_file";
    internal const string SearchTextName = "search_text";
    internal const string FinishReviewName = "finish_review";

    internal const string ListFilesDescription =
        "List tracked repository paths from the reviewed snapshot in ordinal order, " +
        "one bounded page at a time. Use {} to start an unfiltered listing. If " +
        "truncated is true, call list_files again with after set to next_after and " +
        "the same prefix, if any, until truncated is false. prefix and after are " +
        "optional repository-relative path strings; omit either field when unused " +
        "and never pass null or an empty string. Use read_file to read file contents. " +
        "This listing contains no source lines; its observation_id cannot ground finding evidence.";
    internal const string ListChangedFilesDescription =
        "List bounded changed-file metadata from the reviewed snapshot in ordinal path order. " +
        "This metadata contains no source lines; its observation_id cannot ground finding evidence. " +
        "Use read_diff or read_file to obtain evidence for a finding.";
    internal const string ReadDiffDescription =
        "Read bounded complete diff hunks for one changed path in the reviewed snapshot. " +
        "Use the exact path from the current list_changed_files result, not previous_path. " +
        "A tracked but unchanged file is not a changed path; a removed file can still have a diff. " +
        "Historical changed paths do not establish membership in the current changed-file set.";
    internal const string ReadFileDescription =
        "Read a bounded line range from one tracked UTF-8 file in the reviewed snapshot. " +
        "When current path membership is unknown, use list_files first and copy the exact path. " +
        "A path in accepted history may have been removed or renamed after the head changed; " +
        "historical observations do not establish current tracked-file membership.";
    internal const string SearchTextDescription =
        "Search for a case-sensitive literal in tracked UTF-8 files in the reviewed snapshot. " +
        "Omit path to search the current tracked files. If specifying path, use an exact current " +
        "tracked path; use list_files first when membership is unknown. Historical paths may " +
        "have been removed or renamed and do not establish current membership.";
    internal const string FinishReviewDescription =
        "Finish the review with validated grounded findings. For each evidence item, copy observation_id " +
        "from the successful read_file, read_diff, or search_text result that returned that exact path " +
        "and every line in the cited range. Do not combine an observation_id from one result with paths " +
        "or lines from another. read_diff evidence uses new-side context or addition line numbers, never " +
        "deleted old-side lines. list_files and list_changed_files provide metadata only and cannot " +
        "ground evidence. If the needed lines were not returned, read them before finishing. " +
        "Return findings: [] when there are no supported findings; do not invent evidence.";

    internal const string ListFilesSchema =
        "{\"type\":\"object\",\"properties\":{\"prefix\":{\"type\":\"string\"},\"after\":{\"type\":\"string\"}},\"additionalProperties\":false}";
    internal const string ListChangedFilesSchema =
        "{\"type\":\"object\",\"properties\":{\"after\":{\"type\":\"string\"}},\"additionalProperties\":false}";
    internal const string ReadDiffSchema =
        "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"start_hunk\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":2147483647},\"hunk_count\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":20}},\"required\":[\"path\"],\"additionalProperties\":false}";
    internal const string ReadFileSchema =
        "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"start_line\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":2147483647},\"line_count\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":400}},\"required\":[\"path\"],\"additionalProperties\":false}";
    internal const string SearchTextSchema =
        "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"path\":{\"type\":\"string\"}},\"required\":[\"query\"],\"additionalProperties\":false}";
    internal const string FinishReviewSchema =
        "{\"type\":\"object\",\"properties\":{\"summary\":{\"type\":\"string\"},\"findings\":{\"type\":\"array\",\"maxItems\":20,\"items\":{\"type\":\"object\",\"properties\":{\"severity\":{\"type\":\"string\",\"enum\":[\"critical\",\"high\",\"medium\",\"low\"]},\"title\":{\"type\":\"string\"},\"message\":{\"type\":\"string\"},\"evidence\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":8,\"items\":{\"type\":\"object\",\"properties\":{\"observation_id\":{\"type\":\"string\"},\"path\":{\"type\":\"string\"},\"start_line\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":2147483647},\"end_line\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":2147483647}},\"required\":[\"observation_id\",\"path\",\"start_line\",\"end_line\"],\"additionalProperties\":false}}},\"required\":[\"severity\",\"title\",\"message\",\"evidence\"],\"additionalProperties\":false}}},\"required\":[\"summary\",\"findings\"],\"additionalProperties\":false}";

    internal static ImmutableArray<ProjectToolDefinition> Definitions { get; } =
    [
        new(ListFilesName, ListFilesDescription, ListFilesSchema),
        new(
            ListChangedFilesName,
            ListChangedFilesDescription,
            ListChangedFilesSchema),
        new(ReadDiffName, ReadDiffDescription, ReadDiffSchema),
        new(SearchTextName, SearchTextDescription, SearchTextSchema),
        new(ReadFileName, ReadFileDescription, ReadFileSchema),
        new(FinishReviewName, FinishReviewDescription, FinishReviewSchema),
    ];
}
