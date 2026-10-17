using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Tools;
using SolusAgent.Tools.Api;

namespace AgenticPrReview.Runtime.Solus;

// Static complete success shapes. APR result admission still checks canonical
// bytes, snapshot identity, hashes, paging and the exact returned-line set.
internal static class SolusToolSchemas
{
    private const string Text = "{\"type\":\"string\"}";
    private const string NullableText = "{\"type\":[\"string\",\"null\"]}";
    private const string Flag = "{\"type\":\"boolean\"}";
    private const string Positive = "{\"type\":\"integer\",\"minimum\":1,\"maximum\":2147483647}";
    private const string Count = "{\"type\":\"integer\",\"minimum\":0,\"maximum\":2147483647}";
    private const string NullableLine = "{\"type\":[\"integer\",\"null\"],\"minimum\":1,\"maximum\":2147483647}";
    private static readonly string Identity = Object(
        ("repository_id", Text), ("review_target", "{\"type\":\"integer\",\"minimum\":1}"),
        ("base_sha", Text), ("head_sha", Text));

    internal static ToolDescriptor Descriptor(string name)
    {
        var definition = AgentToolRegistry.Definitions.Single(item => item.Name == name);
        return new(name, definition.Description, ToolSchema.Parse(definition.SchemaJson),
            ToolSchema.Parse(Result(name)), "apr_snapshot_" + name, ToolEffect.ReadOnly,
            Cr1DevelopmentProfile.ToolArgumentsBytes, Cr1DevelopmentProfile.ToolResultBytes);
    }

    private static string Result(string name) => name switch
    {
        AgentToolRegistry.ListFilesName => Object(
            ("status", Enum("ok")), ("reviewed_identity", Identity),
            ("prefix", NullableText), ("after", NullableText),
            ("paths", Array(Text, AgentLimits.ListFilesEntries)), ("truncated", Flag),
            ("next_after", NullableText), ("observation_id", Text)),
        AgentToolRegistry.ListChangedFilesName => Object(
            ("status", Enum("ok")), ("reviewed_identity", Identity), ("after", NullableText),
            ("changes", Array(Object(("path", Text), ("previous_path", NullableText),
                ("status", Enum("added", "removed", "modified", "changed", "renamed", "copied")),
                ("additions", Count), ("deletions", Count), ("changes", Count),
                ("patch_status", Enum("available", "unavailable", "binary")),
                ("patch_sha256", NullableText), ("source_truncated", Flag)), AgentLimits.ListChangedFilesEntries)),
            ("truncated", Flag), ("next_after", NullableText), ("observation_id", Text)),
        AgentToolRegistry.ReadFileName => Object(
            ("status", Enum("ok", "start_after_eof")), ("reviewed_identity", Identity),
            ("path", Text), ("raw_sha256", Text), ("requested_start_line", Positive),
            ("requested_line_count", Integer(1, AgentLimits.ReadFileLines)),
            ("returned_start_line", NullableLine), ("returned_end_line", NullableLine),
            ("lines", Array(Object(("line", Positive), ("text", Text)), AgentLimits.ReadFileLines)),
            ("truncated", Flag), ("truncation_reason", NullableText), ("observation_id", Text)),
        AgentToolRegistry.SearchTextName => Object(
            ("status", Enum("ok")), ("reviewed_identity", Identity), ("query_sha256", Text),
            ("path", NullableText), ("files_scanned", Integer(0, AgentLimits.SearchFiles)),
            ("raw_bytes_scanned", Integer(0, AgentLimits.SearchRawBytes)),
            ("skipped_invalid_utf8", Count), ("skipped_binary", Count), ("skipped_lone_cr", Count),
            ("skipped_oversized", Count),
            ("matches", Array(Object(("path", Text), ("raw_sha256", Text), ("line", Positive),
                ("text", Text)), AgentLimits.SearchMatches)),
            ("truncated", Flag), ("truncation_reason", NullableText), ("observation_id", Text)),
        AgentToolRegistry.ReadDiffName => Object(
            ("status", Enum("ok", "empty", "eof", "unavailable", "binary")), ("reviewed_identity", Identity),
            ("path", Text), ("patch_sha256", NullableText), ("source_truncated", Flag),
            ("requested_start_hunk", Positive), ("requested_hunk_count", Integer(1, AgentLimits.ReadDiffHunks)),
            ("returned_start_hunk", NullableLine), ("returned_end_hunk", NullableLine),
            ("hunks", Array(Object(("old_start", Count), ("old_count", Count),
                ("new_start", Count), ("new_count", Count),
                ("lines", Array(Object(("kind", Enum("context", "addition", "deletion", "no_newline")),
                    ("old_line", NullableLine), ("new_line", NullableLine), ("text", Text)),
                    AgentLimits.DiffLinesPerHunk))), AgentLimits.ReadDiffHunks)),
            ("truncated", Flag), ("next_start_hunk", NullableLine), ("observation_id", Text)),
        _ => throw new ArgumentException("APR_SOLUS_UNKNOWN_TOOL"),
    };

    // Every argument to these writers is static source-authored schema material.
    private static string Quote(string value) => "\"" + value + "\"";
    private static string Enum(params string[] values) =>
        "{\"type\":\"string\",\"enum\":[" + string.Join(',', values.Select(Quote)) + "]}";
    private static string Integer(int min, int max) =>
        FormattableString.Invariant($"{{\"type\":\"integer\",\"minimum\":{min},\"maximum\":{max}}}");
    private static string Array(string item, int maximum) =>
        "{\"type\":\"array\",\"items\":" + item + ",\"maxItems\":" +
        maximum.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
    private static string Object(params (string Name, string Schema)[] properties) =>
        "{\"type\":\"object\",\"properties\":{" +
        string.Join(',', properties.Select(p => Quote(p.Name) + ":" + p.Schema)) +
        "},\"required\":[" + string.Join(',', properties.Select(p => Quote(p.Name))) +
        "],\"additionalProperties\":false}";
}
