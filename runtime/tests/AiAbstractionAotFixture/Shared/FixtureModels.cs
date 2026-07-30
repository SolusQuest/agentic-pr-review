using System.Text.Json.Serialization;

namespace AgenticPrReview.Runtime.AiAbstractionFixture;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FirstFixtureInput(
    string Instructions,
    FixtureTool[] Tools,
    string UserRequest,
    FixtureFile[] Files,
    string SearchQuery,
    string ProviderId,
    string ModelId,
    string SessionId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ResumeFixtureInput(
    string Instructions,
    FixtureTool[] Tools,
    string UserRequest,
    string ProviderId,
    string ModelId,
    string SessionId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FixtureTool(
    string Name,
    string Description,
    string SchemaJson);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FixtureFile(
    string Path,
    string Content);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ProofState(
    int Format,
    string Candidate,
    string ProviderId,
    string ModelId,
    string AdapterId,
    string SessionId,
    LogicalRecord[] Records,
    StoredContinuation[] Continuations,
    TerminalReview Review);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LogicalRecord(
    string Kind,
    string Role,
    string? Text,
    string? CallId,
    string? ToolName,
    string? Result,
    int MessagePosition,
    int ContentPosition);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record StoredContinuation(
    string Readable,
    string Opaque,
    string Framing,
    string? AssociatedCallId,
    int MessagePosition,
    int ContentPosition,
    string ReadableSha256,
    string OpaqueSha256);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TerminalReview(
    string Summary,
    string[] Findings);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FirstEvidence(
    string Phase,
    string LogicalRecordsSha256,
    string ProviderProjectionSha256,
    string PrefixIdentity,
    string[] OrderedTools,
    string[] ToolCalls,
    string[] ToolResultSha256,
    ContinuationEvidence[] Continuations,
    string TerminalSummary);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ResumeEvidence(
    string Phase,
    string RestoredRecordsSha256,
    string ProviderProjectionSha256,
    string PriorFactSha256,
    string CurrentInstructionsSha256,
    string CurrentRequestSha256,
    string PrefixIdentity,
    ContinuationEvidence[] Continuations,
    string TerminalSummary);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CombinedEvidence(
    FirstEvidence First,
    ResumeEvidence Resume);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ContinuationEvidence(
    string ReadableSha256,
    string OpaqueSha256,
    string Framing,
    string? AssociatedCallId,
    int MessagePosition,
    int ContentPosition);

internal sealed record FirstRunResult(
    ProofState State,
    FirstEvidence Evidence);

internal sealed record ResumeRunResult(
    ResumeEvidence Evidence);
