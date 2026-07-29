using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticPrReview.Runtime.Agent.Session;

internal sealed class AgentSessionRootDto
{
    [JsonPropertyName("namespace")]
    public string? Namespace { get; set; }

    [JsonPropertyName("discriminator")]
    public string? Discriminator { get; set; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    [JsonPropertyName("repository_id")]
    public string? RepositoryId { get; set; }

    [JsonPropertyName("review_target")]
    public long ReviewTarget { get; set; }

    [JsonPropertyName("workflow_identity")]
    public string? WorkflowIdentity { get; set; }

    [JsonPropertyName("provider_id")]
    public string? ProviderId { get; set; }

    [JsonPropertyName("model_id")]
    public string? ModelId { get; set; }

    [JsonPropertyName("adapter_id")]
    public string? AdapterId { get; set; }

    [JsonPropertyName("policy_sha256")]
    public string? PolicySha256 { get; set; }

    [JsonPropertyName("build_id")]
    public string? BuildId { get; set; }

    [JsonPropertyName("toolset_sha256")]
    public string? ToolsetSha256 { get; set; }

    [JsonPropertyName("limits_sha256")]
    public string? LimitsSha256 { get; set; }

    [JsonPropertyName("producer_base_sha")]
    public string? ProducerBaseSha { get; set; }

    [JsonPropertyName("producer_head_sha")]
    public string? ProducerHeadSha { get; set; }

    [JsonPropertyName("generation")]
    public long Generation { get; set; }

    [JsonPropertyName("predecessor_state_sha256")]
    public string? PredecessorStateSha256 { get; set; }

    [JsonPropertyName("prior_session_sha256")]
    public string? PriorSessionSha256 { get; set; }

    [JsonPropertyName("completed_runs")]
    public AgentSessionRunDto[]? CompletedRuns { get; set; }
}

internal sealed class AgentSessionRunDto
{
    [JsonPropertyName("run_id")]
    public string? RunId { get; set; }

    [JsonPropertyName("run_ordinal")]
    public int RunOrdinal { get; set; }

    [JsonPropertyName("reviewed_identity")]
    public AgentSessionReviewedIdentityDto? ReviewedIdentity { get; set; }

    [JsonPropertyName("stable_plan_sha256")]
    public string? StablePlanSha256 { get; set; }

    [JsonPropertyName("records")]
    public JsonElement[]? Records { get; set; }

    [JsonPropertyName("continuation")]
    public AgentSessionContinuationDto? Continuation { get; set; }
}

internal sealed class AgentSessionReviewedIdentityDto
{
    [JsonPropertyName("repository_id")]
    public string? RepositoryId { get; set; }

    [JsonPropertyName("review_target")]
    public long ReviewTarget { get; set; }

    [JsonPropertyName("base_sha")]
    public string? BaseSha { get; set; }

    [JsonPropertyName("head_sha")]
    public string? HeadSha { get; set; }
}

internal sealed class AgentSessionReviewContextDto
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("sequence")]
    public int Sequence { get; set; }

    [JsonPropertyName("reviewed_identity")]
    public AgentSessionReviewedIdentityDto? ReviewedIdentity { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("framing")]
    public string? Framing { get; set; }

    [JsonPropertyName("classification")]
    public string? Classification { get; set; }
}

internal sealed class AgentSessionAssistantMessageDto
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("sequence")]
    public int Sequence { get; set; }

    [JsonPropertyName("message_ordinal")]
    public int MessageOrdinal { get; set; }

    [JsonPropertyName("contents")]
    public JsonElement[]? Contents { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("framing")]
    public string? Framing { get; set; }

    [JsonPropertyName("classification")]
    public string? Classification { get; set; }
}

internal sealed class AgentSessionToolResultDto
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("sequence")]
    public int Sequence { get; set; }

    [JsonPropertyName("source_message_id")]
    public string? SourceMessageId { get; set; }

    [JsonPropertyName("call_id")]
    public string? CallId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("observation_id")]
    public string? ObservationId { get; set; }

    [JsonPropertyName("result_json")]
    public string? ResultJson { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("framing")]
    public string? Framing { get; set; }

    [JsonPropertyName("classification")]
    public string? Classification { get; set; }
}

internal sealed class AgentSessionReviewOutcomeDto
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("sequence")]
    public int Sequence { get; set; }

    [JsonPropertyName("terminal_message_id")]
    public string? TerminalMessageId { get; set; }

    [JsonPropertyName("terminal_call_id")]
    public string? TerminalCallId { get; set; }

    [JsonPropertyName("terminal_sha256")]
    public string? TerminalSha256 { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("findings_json")]
    public string? FindingsJson { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("framing")]
    public string? Framing { get; set; }

    [JsonPropertyName("classification")]
    public string? Classification { get; set; }
}

internal sealed class AgentSessionTextContentDto
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("content_position")]
    public int ContentPosition { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

internal sealed class AgentSessionContinuationSlotDto
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("content_position")]
    public int ContentPosition { get; set; }

    [JsonPropertyName("continuation_item_id")]
    public string? ContinuationItemId { get; set; }
}

internal sealed class AgentSessionToolCallDto
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("content_position")]
    public int ContentPosition { get; set; }

    [JsonPropertyName("call_id")]
    public string? CallId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments_json")]
    public string? ArgumentsJson { get; set; }
}

internal sealed class AgentSessionTerminalCallDto
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("content_position")]
    public int ContentPosition { get; set; }

    [JsonPropertyName("call_id")]
    public string? CallId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments_json")]
    public string? ArgumentsJson { get; set; }

    [JsonPropertyName("arguments_sha256")]
    public string? ArgumentsSha256 { get; set; }
}

internal sealed class AgentSessionContinuationDto
{
    [JsonPropertyName("codec_id")]
    public string? CodecId { get; set; }

    [JsonPropertyName("codec_discriminator")]
    public string? CodecDiscriminator { get; set; }

    [JsonPropertyName("items")]
    public AgentSessionContinuationItemDto[]? Items { get; set; }
}

internal sealed class AgentSessionContinuationItemDto
{
    [JsonPropertyName("item_id")]
    public string? ItemId { get; set; }

    [JsonPropertyName("encoding")]
    public string? Encoding { get; set; }

    [JsonPropertyName("payload")]
    public string? Payload { get; set; }

    [JsonPropertyName("payload_sha256")]
    public string? PayloadSha256 { get; set; }

    [JsonPropertyName("message_id")]
    public string? MessageId { get; set; }

    [JsonPropertyName("content_position")]
    public int ContentPosition { get; set; }

    [JsonPropertyName("associated_call_id")]
    public string? AssociatedCallId { get; set; }
}

internal sealed class AgentSessionReadFileResultDto
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("reviewed_identity")]
    public AgentSessionReviewedIdentityDto? ReviewedIdentity { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("raw_sha256")]
    public string? RawSha256 { get; set; }

    [JsonPropertyName("requested_start_line")]
    public int RequestedStartLine { get; set; }

    [JsonPropertyName("requested_line_count")]
    public int RequestedLineCount { get; set; }

    [JsonPropertyName("returned_start_line")]
    public int? ReturnedStartLine { get; set; }

    [JsonPropertyName("returned_end_line")]
    public int? ReturnedEndLine { get; set; }

    [JsonPropertyName("lines")]
    public AgentSessionReadFileLineDto[]? Lines { get; set; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonPropertyName("truncation_reason")]
    public string? TruncationReason { get; set; }

    [JsonPropertyName("observation_id")]
    public string? ObservationId { get; set; }
}

internal sealed class AgentSessionReadFileLineDto
{
    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

internal sealed class AgentSessionSearchTextResultDto
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("reviewed_identity")]
    public AgentSessionReviewedIdentityDto? ReviewedIdentity { get; set; }

    [JsonPropertyName("query_sha256")]
    public string? QuerySha256 { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("files_scanned")]
    public int FilesScanned { get; set; }

    [JsonPropertyName("raw_bytes_scanned")]
    public long RawBytesScanned { get; set; }

    [JsonPropertyName("skipped_invalid_utf8")]
    public int SkippedInvalidUtf8 { get; set; }

    [JsonPropertyName("skipped_binary")]
    public int SkippedBinary { get; set; }

    [JsonPropertyName("skipped_lone_cr")]
    public int SkippedLoneCr { get; set; }

    [JsonPropertyName("skipped_oversized")]
    public int SkippedOversized { get; set; }

    [JsonPropertyName("matches")]
    public AgentSessionSearchMatchDto[]? Matches { get; set; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonPropertyName("truncation_reason")]
    public string? TruncationReason { get; set; }

    [JsonPropertyName("observation_id")]
    public string? ObservationId { get; set; }
}

internal sealed class AgentSessionSearchMatchDto
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("raw_sha256")]
    public string? RawSha256 { get; set; }

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AgentSessionRootDto))]
[JsonSerializable(typeof(AgentSessionReviewContextDto))]
[JsonSerializable(typeof(AgentSessionAssistantMessageDto))]
[JsonSerializable(typeof(AgentSessionToolResultDto))]
[JsonSerializable(typeof(AgentSessionReviewOutcomeDto))]
[JsonSerializable(typeof(AgentSessionTextContentDto))]
[JsonSerializable(typeof(AgentSessionContinuationSlotDto))]
[JsonSerializable(typeof(AgentSessionToolCallDto))]
[JsonSerializable(typeof(AgentSessionTerminalCallDto))]
[JsonSerializable(typeof(AgentSessionReadFileResultDto))]
[JsonSerializable(typeof(AgentSessionSearchTextResultDto))]
internal sealed partial class AgentSessionJsonContext : JsonSerializerContext;
