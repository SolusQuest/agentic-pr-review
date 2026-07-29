using System.Collections.Immutable;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;

namespace AgenticPrReview.Runtime.Agent.Session;

internal static class AgentSessionFormat
{
    internal const string Namespace = "agentic-pr-review/agent-session";
    internal const string Discriminator = "r2-current-1";
    internal const string Magic = "APRSES01";
    internal const int FramingBytes = 12;
    internal const int MaximumCompletedRuns = 64;
}

internal static class AgentSessionCodes
{
    internal const string BootstrapAbsent = "session_bootstrap_absent";
    internal const string BootstrapIncompatible = "session_bootstrap_incompatible";
    internal const string ResetExplicit = "session_reset_explicit";
    internal const string ExplicitMissing = "session_explicit_missing";
    internal const string ExplicitIncompatible = "session_explicit_incompatible";
    internal const string CurrentMalformed = "session_current_malformed";
    internal const string CurrentOversized = "session_current_oversized";
    internal const string ScopeMismatch = "session_scope_mismatch";
    internal const string TransitionRejected = "session_transition_rejected";
    internal const string RecordInvalid = "session_record_invalid";
    internal const string ClassificationInvalid = "session_classification_invalid";
    internal const string AssociationInvalid = "session_association_invalid";
    internal const string ContinuationInvalid = "session_continuation_invalid";
    internal const string ConstructionLimit = "session_construction_limit";
    internal const string Restored = "session_restored";
}

internal enum AgentSessionLocatorFamily
{
    Absent,
    NonCurrent,
    Current,
}

internal enum AgentSessionRestoreIntent
{
    Automatic,
    Explicit,
}

internal enum AgentSessionHeadTransition
{
    SameHead,
    VerifiedAhead,
    Unknown,
    Diverged,
    Unrelated,
}

internal sealed record AgentSessionTrustedRequest(
    string RepositoryId,
    long ReviewTarget,
    string WorkflowIdentity,
    byte[] TrustedPolicyBytes,
    ProjectChatMessage[] ControlMessages,
    string BuildId,
    string ProviderId,
    string ModelId,
    string AdapterId);

internal sealed record AgentSessionMaterializedStableRequest(
    StableAgentPlan StablePlan,
    ProjectChatMessage[] ControlMessages);

internal sealed record AgentSessionPredecessor(
    byte[] Plaintext,
    string SessionSha256,
    string EnvelopeSha256,
    long Generation,
    string ProducerBaseSha,
    string ProducerHeadSha,
    string? PredecessorStateSha256);

internal sealed record AgentSessionBuildInput(
    AgentRunRequest Run,
    AgentRunOutcome Outcome,
    AgentSessionTrustedRequest TrustedRequest,
    int CurrentReviewContextIndex,
    IAgentContinuationCodec ContinuationCodec,
    AgentSessionPredecessor? Predecessor,
    AgentSessionHeadTransition Transition);

internal sealed record AgentSessionAcceptedState(
    long Generation,
    string SessionSha256,
    string EnvelopeSha256,
    string ProducerBaseSha,
    string ProducerHeadSha,
    string? PredecessorStateSha256);

internal sealed record AgentSessionRestoreInput(
    AgentSessionLocatorFamily LocatorFamily,
    AgentSessionRestoreIntent Intent,
    bool ExplicitReset,
    byte[]? Plaintext,
    AgentSessionAcceptedState? AcceptedState,
    AgentSessionTrustedRequest TrustedRequest,
    string SessionId,
    ReviewedIdentity CurrentReviewedIdentity,
    ProjectChatMessage CurrentReviewContext,
    AgentSessionHeadTransition Transition,
    IAgentContinuationCodec ContinuationCodec);

internal sealed record AgentSessionArtifact(
    byte[] Plaintext,
    string SessionSha256,
    AgentSessionDocument Document);

internal sealed record AgentSessionBuildResult(
    string? FailureCode,
    AgentSessionArtifact? Artifact)
{
    internal bool Succeeded => FailureCode is null && Artifact is not null;

    internal static AgentSessionBuildResult Success(AgentSessionArtifact artifact) =>
        new(null, artifact);

    internal static AgentSessionBuildResult Failure(string code) =>
        new(code, null);
}

internal sealed record AgentSessionRestoreResult(
    string Code,
    AgentRunRequest? RunRequest,
    AgentSessionArtifact? Artifact)
{
    internal bool Succeeded =>
        StringComparer.Ordinal.Equals(Code, AgentSessionCodes.Restored) &&
        RunRequest is not null &&
        Artifact is not null;

    internal static AgentSessionRestoreResult Success(
        AgentRunRequest runRequest,
        AgentSessionArtifact artifact) =>
        new(AgentSessionCodes.Restored, runRequest, artifact);

    internal static AgentSessionRestoreResult Failure(string code) =>
        new(code, null, null);
}

internal interface IAgentContinuationCodec
{
    string CodecId { get; }

    string CodecDiscriminator { get; }

    bool TryEncode(
        AgentContinuationCodecValue value,
        out AgentContinuationEncodedPayload? payload);

    bool TryDecode(
        string encoding,
        ReadOnlySpan<byte> payload,
        out AgentContinuationCodecValue? value);
}

internal sealed record AgentContinuationCodecValue(
    string Readable,
    string Opaque,
    string Framing);

internal sealed record AgentContinuationEncodedPayload(
    string Encoding,
    byte[] Bytes);

internal sealed record AgentSessionDocument(
    string Namespace,
    string Discriminator,
    string SessionId,
    string RepositoryId,
    long ReviewTarget,
    string WorkflowIdentity,
    string ProviderId,
    string ModelId,
    string AdapterId,
    string PolicySha256,
    string BuildId,
    string ToolsetSha256,
    string LimitsSha256,
    string ProducerBaseSha,
    string ProducerHeadSha,
    long Generation,
    string? PredecessorStateSha256,
    string? PriorSessionSha256,
    ImmutableArray<AgentSessionCompletedRun> CompletedRuns);

internal sealed record AgentSessionCompletedRun(
    string RunId,
    int RunOrdinal,
    ReviewedIdentity ReviewedIdentity,
    string StablePlanSha256,
    ImmutableArray<AgentSessionRecord> Records,
    AgentSessionContinuation Continuation);

internal abstract record AgentSessionRecord(
    string Kind,
    string Id,
    int Sequence,
    string Role,
    string Framing,
    string Classification);

internal sealed record AgentSessionReviewContextRecord(
    string Id,
    int Sequence,
    ReviewedIdentity ReviewedIdentity,
    string Text,
    string Role,
    string Framing,
    string Classification)
    : AgentSessionRecord(
        "review_context",
        Id,
        Sequence,
        Role,
        Framing,
        Classification);

internal sealed record AgentSessionAssistantMessageRecord(
    string Id,
    int Sequence,
    int MessageOrdinal,
    ImmutableArray<AgentSessionAssistantContent> Contents,
    string Role,
    string Framing,
    string Classification)
    : AgentSessionRecord(
        "assistant_message",
        Id,
        Sequence,
        Role,
        Framing,
        Classification);

internal sealed record AgentSessionToolResultRecord(
    string Id,
    int Sequence,
    string SourceMessageId,
    string CallId,
    string Name,
    string ObservationId,
    string ResultJson,
    string Role,
    string Framing,
    string Classification)
    : AgentSessionRecord(
        "tool_result",
        Id,
        Sequence,
        Role,
        Framing,
        Classification);

internal sealed record AgentSessionReviewOutcomeRecord(
    string Id,
    int Sequence,
    string TerminalMessageId,
    string TerminalCallId,
    string TerminalSha256,
    string Summary,
    string FindingsJson,
    string Role,
    string Framing,
    string Classification)
    : AgentSessionRecord(
        "review_outcome",
        Id,
        Sequence,
        Role,
        Framing,
        Classification);

internal abstract record AgentSessionAssistantContent(
    string Kind,
    int ContentPosition);

internal sealed record AgentSessionTextContent(
    int ContentPosition,
    string Text)
    : AgentSessionAssistantContent("text", ContentPosition);

internal sealed record AgentSessionContinuationSlotContent(
    int ContentPosition,
    string ContinuationItemId)
    : AgentSessionAssistantContent("continuation_slot", ContentPosition);

internal sealed record AgentSessionToolCallContent(
    int ContentPosition,
    string CallId,
    string Name,
    string ArgumentsJson)
    : AgentSessionAssistantContent("tool_call", ContentPosition);

internal sealed record AgentSessionTerminalCallContent(
    int ContentPosition,
    string CallId,
    string Name,
    string ArgumentsJson,
    string ArgumentsSha256)
    : AgentSessionAssistantContent("terminal_call", ContentPosition);

internal sealed record AgentSessionContinuation(
    string CodecId,
    string CodecDiscriminator,
    ImmutableArray<AgentSessionContinuationItem> Items);

internal sealed record AgentSessionContinuationItem(
    string ItemId,
    string Encoding,
    string Payload,
    byte[] PayloadBytes,
    string PayloadSha256,
    string MessageId,
    int ContentPosition,
    string? AssociatedCallId);
