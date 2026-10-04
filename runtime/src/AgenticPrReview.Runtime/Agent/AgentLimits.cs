using System.Collections.Immutable;

namespace AgenticPrReview.Runtime.Agent;

internal readonly record struct AgentLimit(
    int Ordinal,
    string Name,
    long Value,
    string Unit);

internal enum AgentLimitProfile
{
    Current = 0,
    Output8192 = 1,
    Output65536 = 2,
}

// This authority is supplied by trusted composition. Agent code only knows
// the selected adapter identity and cumulative limits, not provider policy.
internal sealed record AgentLimitAuthority(string AdapterId, AgentLimitProfile Profile, ReviewTokenBudget? TokenBudget = null)
{
    internal static bool TryResolve(string adapterId, AgentLimitAuthority? authority, out AgentLimitProfile profile)
    {
        profile = authority?.Profile ?? AgentLimitProfile.Current;
        return (authority is null || StringComparer.Ordinal.Equals(adapterId, authority.AdapterId)) &&
            profile is AgentLimitProfile.Current or AgentLimitProfile.Output8192 or AgentLimitProfile.Output65536 &&
            (authority?.TokenBudget is null || profile == AgentLimitProfile.Current && authority.TokenBudget.IsValid);
    }
}

internal static class AgentLimits
{
    internal const int ModelCalls = 64;
    internal const int ToolCalls = 512;
    internal const int ToolCallsPerResponse = 16;
    internal const int ConcurrentToolCalls = 1;
    internal const int DeadlineSeconds = 300;
    // Current input without a validated partition is conservatively debited here.
    internal const long InputTokens = 2_000_000;
    internal const long CachedInputTokens = 38_000_000;
    internal const long OutputTokens = 524_288;
    internal const long RetainedInputTokens = 262_144;
    internal const long RetainedOutputTokens = 32_768;
    internal const long RetainedCombinedTokens = 294_912;
    internal const long Output8192Tokens = 65_536;
    internal const long Combined8192Tokens = 327_680;
    internal const long Output65536Tokens = 524_288;
    internal const long Combined65536Tokens = 786_432;
    internal const int RequestBytes = 8 * 1024 * 1024;
    internal const int ResponseBytes = 2 * 1024 * 1024;
    internal const int Messages = 4096;
    internal const int PartsPerMessage = 32;
    internal const int PartsTotal = 8192;
    internal const int ContentBytes = 1024 * 1024;
    internal const int ToolArgumentsBytes = 8 * 1024;
    internal const int ToolResultBytes = 64 * 1024;
    internal const int ToolResultsTotalBytes = 8 * 1024 * 1024;
    internal const int ReadFileRawBytes = 1024 * 1024;
    internal const int ReadFileLines = 800;
    internal const int SearchFiles = 100;
    internal const int SearchRawBytes = 8 * 1024 * 1024;
    internal const int SearchFileBytes = 256 * 1024;
    internal const int SearchMatches = 100;
    internal const int PathBytes = 1_024;
    internal const int QueryBytes = 4_096;
    internal const int Findings = 20;
    internal const int SummaryBytes = 8 * 1024;
    internal const int FindingTitleBytes = 512;
    internal const int FindingMessageBytes = 16 * 1024;
    internal const int EvidencePerFinding = 8;
    internal const int TerminalBytes = 256 * 1024;
    internal const int SessionRecords = 8192;
    // Field limits and the complete framed SESSION remain authoritative too.
    internal const int SessionRecordBytes = 16 * 1024 * 1024;
    internal const int ContinuationItemBytes = 1024 * 1024;
    internal const int ContinuationTotalBytes = 8 * 1024 * 1024;
    internal const int SessionPlaintextBytes = 16 * 1024 * 1024;
    internal const int StateEnvelopeBytes = 32 * 1024 * 1024;
    internal const int AcceptedCandidates = 2;
    internal const int CandidateMetadataBytes = 16 * 1024;
    internal const int CandidateEnvelopeTotalBytes = AcceptedCandidates * StateEnvelopeBytes;
    internal const int StateScopeTotalBytes =
        CandidateEnvelopeTotalBytes + StateEnvelopeBytes + CandidateMetadataBytes;
    internal const int TrackedFiles = 20_000;
    internal const int TrackedFilesMetadataBytes = 8 * 1024 * 1024;
    internal const int ListFilesEntries = 100;
    internal const int ListChangedFilesEntries = 100;
    internal const int ChangedFiles = 500;
    internal const int ChangedFilesMetadataBytes = 256 * 1024;
    internal const int ReadDiffHunks = 20;
    internal const int DiffHunksPerFile = 200;
    internal const int DiffLinesPerHunk = 1_000;
    internal const int DiffLineTextBytes = 4_096;
    internal const int DiffSourceBytesPerFile = 2 * 1024 * 1024;
    internal const int DiffSnapshotBytes = 32 * 1024 * 1024;

    private static ImmutableArray<AgentLimit> RetainedRegistry { get; } =
    [
        new(1, "model_calls", ModelCalls, "count"),
        new(2, "tool_calls", ToolCalls, "count"),
        new(3, "tool_calls_per_response", ToolCallsPerResponse, "count"),
        new(4, "concurrent_tool_calls", ConcurrentToolCalls, "count"),
        new(5, "deadline_seconds", DeadlineSeconds, "seconds"),
        new(6, "input_tokens", RetainedInputTokens, "tokens"),
        new(7, "output_tokens", RetainedOutputTokens, "tokens"),
        new(8, "combined_tokens", RetainedCombinedTokens, "tokens"),
        new(9, "request_bytes", RequestBytes, "bytes"),
        new(10, "response_bytes", ResponseBytes, "bytes"),
        new(11, "messages", Messages, "count"),
        new(12, "parts_per_message", PartsPerMessage, "count"),
        new(13, "parts_total", PartsTotal, "count"),
        new(14, "content_bytes", ContentBytes, "bytes"),
        new(15, "tool_arguments_bytes", ToolArgumentsBytes, "bytes"),
        new(16, "tool_result_bytes", ToolResultBytes, "bytes"),
        new(17, "tool_results_total_bytes", ToolResultsTotalBytes, "bytes"),
        new(18, "read_file_raw_bytes", ReadFileRawBytes, "bytes"),
        new(19, "read_file_lines", ReadFileLines, "lines"),
        new(20, "search_files", SearchFiles, "count"),
        new(21, "search_raw_bytes", SearchRawBytes, "bytes"),
        new(22, "search_file_bytes", SearchFileBytes, "bytes"),
        new(23, "search_matches", SearchMatches, "count"),
        new(24, "path_bytes", PathBytes, "bytes"),
        new(25, "query_bytes", QueryBytes, "bytes"),
        new(26, "findings", Findings, "count"),
        new(27, "summary_bytes", SummaryBytes, "bytes"),
        new(28, "finding_title_bytes", FindingTitleBytes, "bytes"),
        new(29, "finding_message_bytes", FindingMessageBytes, "bytes"),
        new(30, "evidence_per_finding", EvidencePerFinding, "count"),
        new(31, "terminal_bytes", TerminalBytes, "bytes"),
        new(32, "session_records", SessionRecords, "count"),
        new(33, "session_record_bytes", SessionRecordBytes, "bytes"),
        new(34, "continuation_item_bytes", ContinuationItemBytes, "bytes"),
        new(35, "continuation_total_bytes", ContinuationTotalBytes, "bytes"),
        new(36, "session_plaintext_bytes", SessionPlaintextBytes, "bytes"),
        new(37, "state_envelope_bytes", StateEnvelopeBytes, "bytes"),
        new(38, "accepted_candidates", AcceptedCandidates, "count"),
        new(39, "candidate_metadata_bytes", CandidateMetadataBytes, "bytes"),
        new(40, "candidate_envelope_total_bytes", CandidateEnvelopeTotalBytes, "bytes"),
        new(41, "state_scope_total_bytes", StateScopeTotalBytes, "bytes"),
        new(42, "tracked_files", TrackedFiles, "count"),
        new(43, "tracked_files_metadata_bytes", TrackedFilesMetadataBytes, "bytes"),
        new(44, "list_files_entries", ListFilesEntries, "count"),
        new(45, "list_changed_files_entries", ListChangedFilesEntries, "count"),
        new(46, "changed_files", ChangedFiles, "count"),
        new(47, "changed_files_metadata_bytes", ChangedFilesMetadataBytes, "bytes"),
        new(48, "read_diff_hunks", ReadDiffHunks, "count"),
        new(49, "diff_hunks_per_file", DiffHunksPerFile, "count"),
        new(50, "diff_lines_per_hunk", DiffLinesPerHunk, "lines"),
        new(51, "diff_line_text_bytes", DiffLineTextBytes, "bytes"),
        new(52, "diff_source_bytes_per_file", DiffSourceBytesPerFile, "bytes"),
        new(53, "diff_snapshot_bytes", DiffSnapshotBytes, "bytes"),
    ];

    internal static ImmutableArray<AgentLimit> Registry { get; } = CurrentRegistry(ReviewTokenBudget.Default);

    private static ImmutableArray<AgentLimit> CurrentRegistry(ReviewTokenBudget budget) =>
        RetainedRegistry.Select(row => row.Ordinal switch
        {
            6 => row with { Name = "uncached_input_tokens", Value = budget.UncachedInputTokens },
            7 => row with { Name = "cached_input_tokens", Value = budget.CachedInputTokens },
            8 => row with { Name = "output_tokens", Value = budget.OutputTokens },
            _ => row,
        }).ToImmutableArray();

    internal static ImmutableArray<AgentLimit> RegistryFor(AgentLimitProfile profile, ReviewTokenBudget? budget = null) => profile switch
    {
        AgentLimitProfile.Current => budget is null ? Registry : budget.IsValid
            ? CurrentRegistry(budget) : throw new ArgumentOutOfRangeException(nameof(budget)),
        AgentLimitProfile.Output8192 => CandidateRegistry,
        AgentLimitProfile.Output65536 => Output65536Registry,
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    internal static long OutputTokensFor(AgentLimitProfile profile) => profile switch
    {
        AgentLimitProfile.Current => OutputTokens,
        AgentLimitProfile.Output8192 => Output8192Tokens,
        AgentLimitProfile.Output65536 => Output65536Tokens,
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    internal static long CombinedTokensFor(AgentLimitProfile profile) => profile switch
    {
        AgentLimitProfile.Current => throw new ArgumentOutOfRangeException(nameof(profile)),
        AgentLimitProfile.Output8192 => Combined8192Tokens,
        AgentLimitProfile.Output65536 => Combined65536Tokens,
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    private static ImmutableArray<AgentLimit> CandidateRegistry { get; } = RetainedRegistry.Select(row => row.Name switch
    {
        "output_tokens" => row with { Value = Output8192Tokens },
        "combined_tokens" => row with { Value = Combined8192Tokens },
        _ => row,
    }).ToImmutableArray();

    private static ImmutableArray<AgentLimit> Output65536Registry { get; } = RetainedRegistry.Select(row => row.Name switch
    {
        "output_tokens" => row with { Value = Output65536Tokens },
        "combined_tokens" => row with { Value = Combined65536Tokens },
        _ => row,
    }).ToImmutableArray();
}

// Trusted composition only; balances are transient and never part of this identity.
internal sealed record ReviewTokenBudget(long UncachedInputTokens, long CachedInputTokens, long OutputTokens)
{
    internal static ReviewTokenBudget Default { get; } = new(
        AgentLimits.InputTokens, AgentLimits.CachedInputTokens, AgentLimits.OutputTokens);
    internal bool IsValid => UncachedInputTokens is > 0 and <= AgentLimits.InputTokens &&
        CachedInputTokens is > 0 and <= AgentLimits.CachedInputTokens &&
        OutputTokens is > 0 and <= AgentLimits.OutputTokens;
}
