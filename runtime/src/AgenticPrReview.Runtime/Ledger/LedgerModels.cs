using System.Collections.Immutable;

namespace AgenticPrReview.Runtime.Ledger;

public sealed class LedgerModel
{
    public int SchemaVersion { get; init; }
    public int PrefixContractVersion { get; init; }
    public LedgerHeader Header { get; init; } = null!;
    public ImmutableArray<LedgerRecord> Records { get; init; } = ImmutableArray<LedgerRecord>.Empty;
}

public sealed class LedgerHeader
{
    public string Kind { get; init; } = null!;
    public string SessionEpoch { get; init; } = null!;
    public string LedgerEpoch { get; init; } = null!;
    public long StateGeneration { get; init; }
    public string PredecessorLedgerSha256 { get; init; } = null!;
    public string? PredecessorLedgerEpoch { get; init; }
    public long? PredecessorStateGeneration { get; init; }
    public string? PredecessorManifestSha256 { get; init; }
    public string? ResetReason { get; init; }
    public string? RecoveryReason { get; init; }
    public string Repository { get; init; } = null!;
    public string HeadRepository { get; init; } = null!;
    public int PullRequest { get; init; }
    public string WorkflowIdentity { get; init; } = null!;
    public string TrustedExecutionDomain { get; init; } = null!;
    public string ProviderId { get; init; } = null!;
    public string ModelId { get; init; } = null!;
    public string AdapterId { get; init; } = null!;
    public string TemplateId { get; init; } = null!;
    public string PolicyId { get; init; } = null!;
    public string ToolDefinitionId { get; init; } = null!;
    public string CacheConfigId { get; init; } = null!;
}

public abstract class LedgerRecord
{
    public string Role { get; init; } = null!;
    public string InteractionId { get; init; } = null!;
    public long InteractionOrdinal { get; init; }
}

public sealed class ReviewContextRecord : LedgerRecord
{
    public string SubjectDigest { get; init; } = null!;
    public string CacheContractDigest { get; init; } = null!;
    public string ReviewedHeadSha { get; init; } = null!;
    public string ReviewedBaseSha { get; init; } = null!;
    public ImmutableArray<LedgerChangedFile> ChangedFiles { get; init; } = ImmutableArray<LedgerChangedFile>.Empty;
}

public sealed class ReviewOutcomeRecord : LedgerRecord
{
    public string Summary { get; init; } = null!;
    public ImmutableArray<LedgerFinding> Findings { get; init; } = ImmutableArray<LedgerFinding>.Empty;
    public ImmutableArray<string> Limitations { get; init; } = ImmutableArray<string>.Empty;
}

public sealed class LedgerChangedFile
{
    public string Path { get; init; } = null!;
    public string? PreviousPath { get; init; }
    public string Status { get; init; } = null!;
    public long Additions { get; init; }
    public long Deletions { get; init; }
    public long Changes { get; init; }
    public LedgerBoundedPatch? Patch { get; init; }
}

public sealed class LedgerBoundedPatch
{
    public string Sha256 { get; init; } = null!;
    public bool Truncated { get; init; }
    public long MaxChars { get; init; }
}

public sealed class LedgerFinding
{
    public string Severity { get; init; } = null!;
    public string Confidence { get; init; } = null!;
    public string Category { get; init; } = null!;
    public string Title { get; init; } = null!;
    public string Body { get; init; } = null!;
    public string? Path { get; init; }
    public long? StartLine { get; init; }
    public long? EndLine { get; init; }
    public string? Evidence { get; init; }
    public string? SuggestedAction { get; init; }
    public string? InlinePreference { get; init; }
}

public sealed class ValidatedContextSource
{
    public string SubjectDigest { get; init; } = null!;
    public string ReviewedHeadSha { get; init; } = null!;
    public string ReviewedBaseSha { get; init; } = null!;
    public ImmutableArray<LedgerChangedFile> ChangedFiles { get; init; } = ImmutableArray<LedgerChangedFile>.Empty;
    /// <summary>
    /// Bounded current-call evidence for the provider-neutral dynamic request
    /// plan. It is intentionally not part of the persisted ledger record.
    /// </summary>
    public CurrentReviewEvidence? CurrentEvidence { get; init; }
}

public sealed class CurrentReviewEvidence
{
    public string Subject { get; init; } = string.Empty;
    public ImmutableArray<CurrentEvidenceFile> Files { get; init; } = ImmutableArray<CurrentEvidenceFile>.Empty;
}

public sealed class CurrentEvidenceFile
{
    public string Path { get; init; } = string.Empty;
    public string? Patch { get; init; }
}

public sealed class ValidatedOutcomeSource
{
    public string Summary { get; init; } = null!;
    public ImmutableArray<LedgerFinding> Findings { get; init; } = ImmutableArray<LedgerFinding>.Empty;
    public ImmutableArray<string> Limitations { get; init; } = ImmutableArray<string>.Empty;
}

public abstract record ExpectedTransition(ExpectedIdentities Identities);

public sealed record BootstrapTransition(
    ExpectedIdentities Identities, string SessionEpoch, string LedgerEpoch, long StateGeneration)
    : ExpectedTransition(Identities);

public sealed record ContinuationTransition(
    ExpectedIdentities Identities,
    string SessionEpoch, string LedgerEpoch,
    string PredecessorLedgerSha256, string PredecessorLedgerEpoch,
    long PredecessorStateGeneration, long StateGeneration)
    : ExpectedTransition(Identities);

public sealed record ResetTransition(
    ExpectedIdentities Identities,
    string SessionEpoch, string LedgerEpoch,
    string PredecessorLedgerSha256, string PredecessorManifestSha256,
    string PredecessorLedgerEpoch, long PredecessorStateGeneration,
    long StateGeneration, string ResetReason)
    : ExpectedTransition(Identities);

public sealed record RecoveryRootTransition(
    ExpectedIdentities Identities,
    string SessionEpoch, string LedgerEpoch,
    long StateGeneration, string RecoveryReason)
    : ExpectedTransition(Identities);

public sealed record ExpectedIdentities(
    string Repository, string HeadRepository, int PullRequest,
    string WorkflowIdentity, string TrustedExecutionDomain,
    string ProviderId, string ModelId,
    string AdapterId, string TemplateId, string PolicyId,
    string ToolDefinitionId, string CacheConfigId);
