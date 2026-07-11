namespace AgenticPrReview.Runtime;

public sealed record ReviewInput(
    int ProtocolVersion,
    string? RequestedRuntimeVersion,
    RuntimeHost Host,
    RuntimeSubject Subject,
    RuntimePreviousState PreviousState,
    RuntimeCommentEvidence CommentEvidence);

public sealed record RuntimeHost(RuntimeRepository Repository, RuntimeReview Review, RuntimeOptions? Options);
public sealed record RuntimeRepository(string Owner, string Name);
public sealed record RuntimeReview(string Phase, string BaseSha, string HeadSha, string? StateKey, string RuntimeProvider);
public sealed record RuntimeOptions(
    string? ToolMode,
    int? MaxFindings,
    int? MaxPatchChars,
    int? MaxContextChars,
    int? MaxReviewChars,
    RuntimeInlineCommentsPolicy? InlineComments);
public sealed record RuntimeInlineCommentsPolicy(bool? Enabled, int? MaxComments, string? MinSeverity, string? MinConfidence);

public sealed record RuntimeSubject(
    RuntimePullRequest PullRequest,
    RuntimeChangedFile[] ChangedFiles,
    RuntimeContextDocument[]? ContextDocuments,
    string? PolicyText);
public sealed record RuntimePullRequest(int Number, string Title, string Body, string BaseRef, string HeadRef, bool Draft);
public sealed record RuntimeChangedFile(
    string Path,
    string? PreviousPath,
    string Status,
    int Additions,
    int Deletions,
    int Changes,
    RuntimePatch? Patch);
public sealed record RuntimePatch(string Text, bool Truncated, string Sha256, int MaxChars);
public sealed record RuntimeContextDocument(string Name, string Text);

public sealed record RuntimePreviousState(
    bool Present,
    string? ReviewedHeadSha,
    string? Phase,
    string[] FindingFingerprints,
    RuntimeLineage? Lineage);
public sealed record RuntimeLineage(int ReviewCount);
public sealed record RuntimeCommentEvidence(string[] ExistingFindingFingerprints);
