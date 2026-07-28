using System.Collections.Immutable;

namespace AgenticPrReview.Runtime.Agent;

internal readonly record struct AgentLimit(
    int Ordinal,
    string Name,
    long Value,
    string Unit);

internal static class AgentLimits
{
    internal const int ModelCalls = 8;
    internal const int ToolCalls = 24;
    internal const int ToolCallsPerResponse = 8;
    internal const int ConcurrentToolCalls = 1;
    internal const int DeadlineSeconds = 300;
    internal const long InputTokens = 262_144;
    internal const long OutputTokens = 32_768;
    internal const long CombinedTokens = 294_912;
    internal const int RequestBytes = 1 * 1024 * 1024;
    internal const int ResponseBytes = 1 * 1024 * 1024;
    internal const int Messages = 64;
    internal const int PartsPerMessage = 32;
    internal const int PartsTotal = 256;
    internal const int ContentBytes = 64 * 1024;
    internal const int ToolArgumentsBytes = 8 * 1024;
    internal const int ToolResultBytes = 32 * 1024;
    internal const int ToolResultsTotalBytes = 256 * 1024;
    internal const int ReadFileRawBytes = 64 * 1024;
    internal const int ReadFileLines = 400;
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
    internal const int SessionRecords = 256;
    internal const int SessionRecordBytes = 512 * 1024;
    internal const int ContinuationItemBytes = 64 * 1024;
    internal const int ContinuationTotalBytes = 256 * 1024;
    internal const int SessionPlaintextBytes = 1 * 1024 * 1024;
    internal const int StateEnvelopeBytes = 2 * 1024 * 1024;
    internal const int AcceptedCandidates = 2;
    internal const int CandidateMetadataBytes = 16 * 1024;
    internal const int CandidateEnvelopeTotalBytes = 4 * 1024 * 1024;
    internal const int StateScopeTotalBytes = 6 * 1024 * 1024;

    internal static ImmutableArray<AgentLimit> Registry { get; } =
    [
        new(1, "model_calls", ModelCalls, "count"),
        new(2, "tool_calls", ToolCalls, "count"),
        new(3, "tool_calls_per_response", ToolCallsPerResponse, "count"),
        new(4, "concurrent_tool_calls", ConcurrentToolCalls, "count"),
        new(5, "deadline_seconds", DeadlineSeconds, "seconds"),
        new(6, "input_tokens", InputTokens, "tokens"),
        new(7, "output_tokens", OutputTokens, "tokens"),
        new(8, "combined_tokens", CombinedTokens, "tokens"),
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
    ];
}
