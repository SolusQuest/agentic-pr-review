using System.Collections.Immutable;
using AgenticPrReview.Runtime.Agent.Chat;

namespace AgenticPrReview.Runtime.Agent.Tools;

internal static class AgentToolRegistry
{
    internal const string ListFilesName = "list_files";
    internal const string ReadFileName = "read_file";
    internal const string SearchTextName = "search_text";
    internal const string FinishReviewName = "finish_review";

    internal const string ListFilesDescription =
        "List tracked repository paths from the reviewed snapshot in ordinal order.";
    internal const string ReadFileDescription =
        "Read a bounded line range from one tracked UTF-8 file in the reviewed snapshot.";
    internal const string SearchTextDescription =
        "Search for a case-sensitive literal in tracked UTF-8 files in the reviewed snapshot.";
    internal const string FinishReviewDescription =
        "Finish the review with validated grounded findings.";

    internal const string ListFilesSchema =
        "{\"type\":\"object\",\"properties\":{\"prefix\":{\"type\":\"string\"},\"after\":{\"type\":\"string\"}},\"additionalProperties\":false}";
    internal const string ReadFileSchema =
        "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"start_line\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":2147483647},\"line_count\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":400}},\"required\":[\"path\"],\"additionalProperties\":false}";
    internal const string SearchTextSchema =
        "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"path\":{\"type\":\"string\"}},\"required\":[\"query\"],\"additionalProperties\":false}";
    internal const string FinishReviewSchema =
        "{\"type\":\"object\",\"properties\":{\"summary\":{\"type\":\"string\"},\"findings\":{\"type\":\"array\",\"maxItems\":20,\"items\":{\"type\":\"object\",\"properties\":{\"severity\":{\"type\":\"string\",\"enum\":[\"critical\",\"high\",\"medium\",\"low\"]},\"title\":{\"type\":\"string\"},\"message\":{\"type\":\"string\"},\"evidence\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":8,\"items\":{\"type\":\"object\",\"properties\":{\"observation_id\":{\"type\":\"string\"},\"path\":{\"type\":\"string\"},\"start_line\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":2147483647},\"end_line\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":2147483647}},\"required\":[\"observation_id\",\"path\",\"start_line\",\"end_line\"],\"additionalProperties\":false}}},\"required\":[\"severity\",\"title\",\"message\",\"evidence\"],\"additionalProperties\":false}}},\"required\":[\"summary\",\"findings\"],\"additionalProperties\":false}";

    internal static ImmutableArray<ProjectToolDefinition> Definitions { get; } =
    [
        new(ListFilesName, ListFilesDescription, ListFilesSchema),
        new(SearchTextName, SearchTextDescription, SearchTextSchema),
        new(ReadFileName, ReadFileDescription, ReadFileSchema),
        new(FinishReviewName, FinishReviewDescription, FinishReviewSchema),
    ];
}
