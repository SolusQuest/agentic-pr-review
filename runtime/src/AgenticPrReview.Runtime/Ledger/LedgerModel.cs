using System.Collections.Immutable;

namespace AgenticPrReview.Runtime.Ledger;

/// <summary>
/// Immutable, provider-neutral ProviderSessionLedgerV1 model. Construct only
/// through <see cref="LedgerParser"/> or <see cref="LedgerBuilder"/>; both
/// paths defensive-copy every source array before returning.
/// </summary>
public sealed record LedgerModel(
    int SchemaVersion,
    int PrefixContractVersion,
    LedgerHeader Header,
    ImmutableArray<LedgerRecord> Records);

/// <summary>
/// Header carries all identities and generation provenance for one of the four
/// transition kinds: bootstrap, continuation, reset, recovery.
/// </summary>
public sealed record LedgerHeader(
    string Kind,
    // Session scope (host-authoritative; carried verbatim)
    string Repository,
    string HeadRepository,
    int PullRequest,
    string WorkflowIdentity,
    string TrustedExecutionDomain,
    string SessionEpoch,
    // Cache-contract identity (host-authoritative; carried verbatim)
    string ProviderId,
    string ModelId,
    string AdapterId,
    string TemplateId,
    string PolicyId,
    string ToolDefinitionId,
    string CacheConfigId,
    // Generation / transition provenance
    int StateGeneration,
    int LedgerEpoch,
    string PredecessorLedgerSha256,
    int? PredecessorStateGeneration,
    string? PredecessorManifestSha256,
    string? ResetReason,
    string? RecoveryReason);

/// <summary>
/// One entry in the ordered records array. Exactly one of
/// <see cref="Context"/> or <see cref="Outcome"/> is non-null.
/// </summary>
public sealed record LedgerRecord(ReviewContextRecord? Context, ReviewOutcomeRecord? Outcome)
{
    public string Role => Context is not null ? "review_context" : "review_outcome";
    public string InteractionId => Context?.InteractionId ?? Outcome!.InteractionId;
    public int InteractionOrdinal => Context?.InteractionOrdinal ?? Outcome!.InteractionOrdinal;
}

public sealed record ReviewContextRecord(
    string InteractionId,
    int InteractionOrdinal,
    string ReviewedHeadSha,
    string ReviewedBaseSha,
    string SubjectDigest,
    string CacheContractDigest,
    ImmutableArray<ChangedFileEntry> ChangedFiles);

public sealed record ReviewOutcomeRecord(
    string InteractionId,
    int InteractionOrdinal,
    string Summary,
    ImmutableArray<LedgerFinding> Findings,
    ImmutableArray<string> Limitations);

public sealed record ChangedFileEntry(
    string Path,
    string? PreviousPath,
    string Status,
    int Additions,
    int Deletions,
    int Changes,
    ChangedFilePatch? Patch);

public sealed record ChangedFilePatch(string Sha256, bool Truncated, int MaxChars);

public sealed record LedgerFinding(
    string Severity,
    string Confidence,
    string Category,
    string Title,
    string Body,
    string? Path,
    int? StartLine,
    int? EndLine,
    string? Evidence,
    string? SuggestedAction,
    string? InlinePreference);

/// <summary>
/// Deeply immutable validated ledger snapshot. Once constructed, the byte
/// buffer, model, and hash are guaranteed to agree; callers cannot obtain a
/// mutable reference to any internal buffer.
/// </summary>
public sealed class ValidatedLedger
{
    private readonly byte[] canonicalBytes;
    private readonly SafeByteMemoryManager memoryManager;

    internal ValidatedLedger(LedgerModel model, byte[] canonicalBytes, string contentSha256)
    {
        // Defensive copy: the constructor owns the sole reference to the byte buffer.
        this.canonicalBytes = new byte[canonicalBytes.Length];
        Buffer.BlockCopy(canonicalBytes, 0, this.canonicalBytes, 0, canonicalBytes.Length);
        this.memoryManager = new SafeByteMemoryManager(this.canonicalBytes);
        Model = model;
        ContentSha256 = contentSha256;
    }

    public LedgerModel Model { get; }

    /// <summary>
    /// Canonical UTF-8 bytes of the ledger. The returned <see cref="ReadOnlyMemory{Byte}"/>
    /// is backed by a <see cref="System.Buffers.MemoryManager{Byte}"/> whose
    /// <c>TryGetArray</c> refuses to expose the underlying array, so
    /// <c>MemoryMarshal.TryGetArray</c> returns <c>false</c> and callers cannot
    /// obtain a mutable segment aliasing the ledger's internal storage. Use
    /// <see cref="ToCanonicalByteArray"/> when a mutable copy is required.
    /// </summary>
    public ReadOnlyMemory<byte> CanonicalBytes => this.memoryManager.Memory;

    /// <summary>
    /// Returns a freshly allocated copy of the canonical bytes. Each call
    /// allocates; mutating the returned array cannot affect this ledger.
    /// </summary>
    public byte[] ToCanonicalByteArray()
    {
        var copy = new byte[this.canonicalBytes.Length];
        Buffer.BlockCopy(this.canonicalBytes, 0, copy, 0, this.canonicalBytes.Length);
        return copy;
    }

    public string ContentSha256 { get; }
    public int ByteLength => this.canonicalBytes.Length;
}

/// <summary>
/// Backing store for <see cref="ValidatedLedger.CanonicalBytes"/>. Refuses to
/// expose the underlying array through <c>MemoryMarshal.TryGetArray</c>, so
/// callers can only read through <see cref="ReadOnlyMemory{Byte}"/> /
/// <see cref="ReadOnlySpan{Byte}"/> without gaining a mutable alias.
/// </summary>
internal sealed class SafeByteMemoryManager : System.Buffers.MemoryManager<byte>
{
    private readonly byte[] buffer;

    public SafeByteMemoryManager(byte[] buffer) => this.buffer = buffer;

    public override Span<byte> GetSpan() => this.buffer;

    public override System.Buffers.MemoryHandle Pin(int elementIndex = 0) =>
        throw new NotSupportedException("Pinning is not supported for ledger canonical bytes.");

    public override void Unpin() { }

    protected override bool TryGetArray(out ArraySegment<byte> segment)
    {
        segment = default;
        return false;
    }

    protected override void Dispose(bool disposing) { }
}

// ---------------------------------------------------------------------------
// Builder source DTOs

public sealed record ValidatedContextSource(
    string ReviewedHeadSha,
    string ReviewedBaseSha,
    ImmutableArray<ValidatedChangedFileSource> ChangedFiles);

public sealed record ValidatedChangedFileSource(
    string Path,
    string? PreviousPath,
    string Status,
    int Additions,
    int Deletions,
    int Changes,
    ValidatedPatchSource? Patch);

public sealed record ValidatedPatchSource(string Sha256, bool Truncated, int MaxChars);

public sealed record ValidatedOutcomeSource(
    string Summary,
    ImmutableArray<ValidatedFindingSource> Findings,
    ImmutableArray<string> Limitations);

public sealed record ValidatedFindingSource(
    string Severity,
    string Confidence,
    string Category,
    string Title,
    string Body,
    string? Path,
    int? StartLine,
    int? EndLine,
    string? Evidence,
    string? SuggestedAction,
    string? InlinePreference);

public sealed record InteractionIdentity(string InteractionId, int InteractionOrdinal);

// Session-scope + cache-contract expected identities.
public sealed record ExpectedIdentities(
    string Repository,
    string HeadRepository,
    int PullRequest,
    string WorkflowIdentity,
    string TrustedExecutionDomain,
    string SessionEpoch,
    string ProviderId,
    string ModelId,
    string AdapterId,
    string TemplateId,
    string PolicyId,
    string ToolDefinitionId,
    string CacheConfigId);

// Typed transition requests. Base carries only the identities and kind; each
// derived record introduces its own positional StateGeneration / LedgerEpoch
// so record init semantics work without abstract-property overriding.
public abstract record ExpectedTransition(ExpectedIdentities Identities)
{
    public abstract string Kind { get; }
    public abstract int GetStateGeneration();
    public abstract int GetLedgerEpoch();
}

public sealed record BootstrapTransition(ExpectedIdentities Identities, int StateGeneration, int LedgerEpoch)
    : ExpectedTransition(Identities)
{
    public override string Kind => "bootstrap";
    public override int GetStateGeneration() => StateGeneration;
    public override int GetLedgerEpoch() => LedgerEpoch;
}

public sealed record ContinuationTransition(
    ExpectedIdentities Identities,
    string PredecessorLedgerSha256,
    int PredecessorStateGeneration,
    int StateGeneration,
    int LedgerEpoch)
    : ExpectedTransition(Identities)
{
    public override string Kind => "continuation";
    public override int GetStateGeneration() => StateGeneration;
    public override int GetLedgerEpoch() => LedgerEpoch;
}

public sealed record ResetTransition(
    ExpectedIdentities Identities,
    string PredecessorLedgerSha256,
    string PredecessorManifestSha256,
    int PredecessorStateGeneration,
    int StateGeneration,
    int LedgerEpoch,
    string ResetReason)
    : ExpectedTransition(Identities)
{
    public override string Kind => "reset";
    public override int GetStateGeneration() => StateGeneration;
    public override int GetLedgerEpoch() => LedgerEpoch;
}

public sealed record RecoveryTransition(
    ExpectedIdentities Identities,
    int StateGeneration,
    int LedgerEpoch,
    string RecoveryReason)
    : ExpectedTransition(Identities)
{
    public override string Kind => "recovery";
    public override int GetStateGeneration() => StateGeneration;
    public override int GetLedgerEpoch() => LedgerEpoch;
}

// Outcomes
public readonly record struct ParseOutcome(ValidatedLedger? Ledger, LedgerDiagnostic? Failure);
public readonly record struct ProjectionOutcome<T>(T? Record, LedgerDiagnostic? Failure) where T : class;
public readonly record struct BuildOutcome(ValidatedLedger? Ledger, LedgerDiagnostic? Failure);
public readonly record struct TransitionOutcome(ValidatedLedger? Candidate, LedgerDiagnostic? Failure);

