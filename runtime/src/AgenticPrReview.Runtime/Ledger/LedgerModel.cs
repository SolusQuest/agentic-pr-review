using System.Collections.Immutable;

namespace AgenticPrReview.Runtime.Ledger;

/// <summary>
/// Immutable ProviderSessionLedgerV1 model. Construct only through
/// <see cref="LedgerParser"/> or <see cref="LedgerBuilder"/>; both paths
/// defensive-copy every source array before returning.
/// </summary>
public sealed class LedgerModel
{
    public int SchemaVersion { get; init; }
    public int PrefixContractVersion { get; init; }
    public LedgerHeader Header { get; init; } = null!;
    public ImmutableArray<LedgerRecord> Records { get; init; } = ImmutableArray<LedgerRecord>.Empty;
}

/// <summary>
/// Header carries all identities and generation provenance for one of the four
/// transition kinds: <c>bootstrap</c>, <c>continuation</c>, <c>reset</c>,
/// <c>recovery_root</c>. Field presence per kind is governed by the schema
/// oneOf; ExpectedTransition subtypes mirror the shape.
/// </summary>
public sealed class LedgerHeader
{
    public string Kind { get; init; } = null!;                          // bootstrap | continuation | reset | recovery_root

    // Session scope (host-authoritative; carried verbatim)
    public string SessionEpoch { get; init; } = null!;                  // EpochId
    public string LedgerEpoch { get; init; } = null!;                   // EpochId
    public long StateGeneration { get; init; }
    public string PredecessorLedgerSha256 { get; init; } = null!;       // Sha256Hex or "bootstrap"
    public string? PredecessorLedgerEpoch { get; init; }                // EpochId; absent for bootstrap/recovery_root
    public long? PredecessorStateGeneration { get; init; }              // absent for bootstrap/recovery_root
    public string? PredecessorManifestSha256 { get; init; }             // only present for reset
    public string? ResetReason { get; init; }                           // only present for reset
    public string? RecoveryReason { get; init; }                        // only present for recovery_root

    public string Repository { get; init; } = null!;
    public string HeadRepository { get; init; } = null!;
    public int PullRequest { get; init; }
    public string WorkflowIdentity { get; init; } = null!;
    public string TrustedExecutionDomain { get; init; } = null!;

    // Cache-contract identity (host-authoritative; carried verbatim)
    public string ProviderId { get; init; } = null!;
    public string ModelId { get; init; } = null!;
    public string AdapterId { get; init; } = null!;
    public string TemplateId { get; init; } = null!;
    public string PolicyId { get; init; } = null!;
    public string ToolDefinitionId { get; init; } = null!;
    public string CacheConfigId { get; init; } = null!;
}

/// <summary>
/// One entry in the ordered <see cref="LedgerModel.Records"/> array. Discriminated
/// by <see cref="Role"/> = <c>review_context</c> or <c>review_outcome</c>.
/// </summary>
public abstract class LedgerRecord
{
    public string Role { get; init; } = null!;                          // review_context | review_outcome
    public string InteractionId { get; init; } = null!;                 // Sha256Hex
    public long InteractionOrdinal { get; init; }
}

public sealed class ReviewContextRecord : LedgerRecord
{
    public string SubjectDigest { get; init; } = null!;                 // Sha256Hex; host-supplied, ledger echoes verbatim
    public string CacheContractDigest { get; init; } = null!;           // Sha256Hex; ledger-computed at Build time
    public string ReviewedHeadSha { get; init; } = null!;               // GitSha (40 or 64 hex)
    public string ReviewedBaseSha { get; init; } = null!;               // GitSha (40 or 64 hex)
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
    public string Path { get; init; } = null!;                          // safeRelativePath
    public string? PreviousPath { get; init; }                          // safeRelativePath; ABSENT when null
    public string Status { get; init; } = null!;                        // added | removed | modified | renamed | copied | changed | unchanged
    public long Additions { get; init; }
    public long Deletions { get; init; }
    public long Changes { get; init; }
    public LedgerBoundedPatch? Patch { get; init; }                     // ABSENT when null
}

public sealed class LedgerBoundedPatch
{
    public string Sha256 { get; init; } = null!;                        // Sha256Hex
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
    public string? Path { get; init; }                                  // required JSON property; may be explicit null
    public long? StartLine { get; init; }
    public long? EndLine { get; init; }
    public string? Evidence { get; init; }
    public string? SuggestedAction { get; init; }
    public string? InlinePreference { get; init; }
}

// ---------------------------------------------------------------------------
// Builder source DTOs

public sealed class ValidatedContextSource
{
    public string SubjectDigest { get; init; } = null!;                 // Sha256Hex; caller-computed (host-supplied pass-through)
    public string ReviewedHeadSha { get; init; } = null!;               // GitSha
    public string ReviewedBaseSha { get; init; } = null!;               // GitSha
    public ImmutableArray<LedgerChangedFile> ChangedFiles { get; init; } = ImmutableArray<LedgerChangedFile>.Empty;
}

public sealed class ValidatedOutcomeSource
{
    public string Summary { get; init; } = null!;
    public ImmutableArray<LedgerFinding> Findings { get; init; } = ImmutableArray<LedgerFinding>.Empty;
    public ImmutableArray<string> Limitations { get; init; } = ImmutableArray<string>.Empty;
}

// ---------------------------------------------------------------------------
// Interaction identity + expected identities

public sealed record InteractionIdentity(string InteractionId, long InteractionOrdinal);

// Session-scope + cache-contract expected identities.
public sealed record ExpectedIdentities(
    string Repository,
    string HeadRepository,
    int PullRequest,
    string WorkflowIdentity,
    string TrustedExecutionDomain,
    string ProviderId,
    string ModelId,
    string AdapterId,
    string TemplateId,
    string PolicyId,
    string ToolDefinitionId,
    string CacheConfigId);

// ---------------------------------------------------------------------------
// Expected transition requests. Base carries the identities and kind guard;
// each derived record introduces its own positional epoch/generation fields.

public abstract record ExpectedTransition(ExpectedIdentities Identities, string SessionEpoch)
{
    public abstract string Kind { get; }
    public abstract long GetStateGeneration();
    public abstract string GetLedgerEpoch();
}

public sealed record BootstrapTransition(
    ExpectedIdentities Identities,
    string SessionEpoch,
    long StateGeneration,
    string LedgerEpoch)
    : ExpectedTransition(Identities, SessionEpoch)
{
    public override string Kind => "bootstrap";
    public override long GetStateGeneration() => StateGeneration;
    public override string GetLedgerEpoch() => LedgerEpoch;
}

public sealed record ContinuationTransition(
    ExpectedIdentities Identities,
    string SessionEpoch,
    string PredecessorLedgerSha256,
    long PredecessorStateGeneration,
    string PredecessorLedgerEpoch,
    long StateGeneration,
    string LedgerEpoch)
    : ExpectedTransition(Identities, SessionEpoch)
{
    public override string Kind => "continuation";
    public override long GetStateGeneration() => StateGeneration;
    public override string GetLedgerEpoch() => LedgerEpoch;
}

public sealed record ResetTransition(
    ExpectedIdentities Identities,
    string SessionEpoch,
    string PredecessorLedgerSha256,
    string PredecessorManifestSha256,
    long PredecessorStateGeneration,
    string PredecessorLedgerEpoch,
    long StateGeneration,
    string LedgerEpoch,
    string ResetReason)
    : ExpectedTransition(Identities, SessionEpoch)
{
    public override string Kind => "reset";
    public override long GetStateGeneration() => StateGeneration;
    public override string GetLedgerEpoch() => LedgerEpoch;
}

// Recovery-root has no predecessor fields; stateGeneration is const 0 per schema.
public sealed record RecoveryRootTransition(
    ExpectedIdentities Identities,
    string SessionEpoch,
    string LedgerEpoch,
    string RecoveryReason)
    : ExpectedTransition(Identities, SessionEpoch)
{
    public override string Kind => "recovery_root";
    public override long GetStateGeneration() => 0L;
    public override string GetLedgerEpoch() => LedgerEpoch;
}

// ---------------------------------------------------------------------------
// Outcomes

public sealed record ParseOutcome(ValidatedLedger? Ledger, ImmutableArray<LedgerDiagnostic> Diagnostics);

public sealed record TransitionOutcome(ImmutableArray<LedgerDiagnostic> Diagnostics);

public sealed record BuildOutcome<TRecord>(TRecord? Record, ImmutableArray<LedgerDiagnostic> Diagnostics)
    where TRecord : class;

public sealed record CandidateOutcome(ValidatedLedger? Candidate, ImmutableArray<LedgerDiagnostic> Diagnostics);

// ---------------------------------------------------------------------------
// ValidatedLedger: deeply immutable snapshot minted only by trusted internal
// callers (parser, builder). External code cannot fabricate one.

public sealed class ValidatedLedger
{
    private readonly byte[] canonicalBytes;
    private readonly LedgerModel privateModel;

    /// <summary>
    /// Deep-immutable snapshot. The constructor takes ownership of a fresh
    /// canonical byte buffer, then rebuilds the model from those bytes so
    /// the internally stored <see cref="Model"/> reference is entirely
    /// derived from the private byte buffer. The caller's <paramref name="model"/>
    /// argument is used only as a schema-typed skeleton; any mutation of
    /// caller-supplied collections after construction cannot alter the
    /// snapshot exposed through <see cref="Model"/> or <see cref="CanonicalBytes"/>.
    /// </summary>
    internal ValidatedLedger(LedgerModel model, byte[] canonicalBytes, string contentSha256)
    {
        this.canonicalBytes = new byte[canonicalBytes.Length];
        Buffer.BlockCopy(canonicalBytes, 0, this.canonicalBytes, 0, canonicalBytes.Length);
        // Blocker #8 fix: derive the private/public Model from the private
        // canonical byte buffer, not from the caller-supplied `model`
        // parameter. This makes the immutability structural (not conventional)
        // and closes the door on any accidental aliasing of caller-supplied
        // sub-collections into the ledger's stable snapshot.
        //
        // The re-parse is a strict RFC 8785 canonical form re-read of the
        // exact bytes we just deep-copied; no external state, no schema
        // validation (the parser has already validated the bytes before
        // handing them to us). The caller's `model` is ignored for storage
        // purposes but retained in the signature to keep the parser/builder
        // call sites unchanged for callers that only mint after full parse.
        _ = model;
        using var doc = System.Text.Json.JsonDocument.Parse(this.canonicalBytes);
        var rebuilt = LedgerDeserializer.Deserialize(doc.RootElement);
        this.privateModel = rebuilt;
        Model = rebuilt;
        ContentSha256 = contentSha256;
        ByteLength = canonicalBytes.Length;
    }

    public LedgerModel Model { get; }
    public ImmutableArray<byte> CanonicalBytes => ImmutableArray.Create(this.canonicalBytes);
    public string ContentSha256 { get; }
    public long ByteLength { get; }

    /// <summary>
    /// Internal snapshot used by LedgerAppend / LedgerBuilder for byte-level
    /// comparisons without paying an allocation cost.
    /// </summary>
    internal ReadOnlySpan<byte> CanonicalBytesUnsafeSpan => this.canonicalBytes;

    internal LedgerModel PrivateModel => this.privateModel;

    public byte[] ToCanonicalByteArray()
    {
        var copy = new byte[this.canonicalBytes.Length];
        Buffer.BlockCopy(this.canonicalBytes, 0, copy, 0, copy.Length);
        return copy;
    }

    public int CopyCanonicalBytesTo(Span<byte> destination)
    {
        if (destination.Length < this.canonicalBytes.Length)
            throw new ArgumentException("Destination too short.", nameof(destination));
        this.canonicalBytes.AsSpan().CopyTo(destination);
        return this.canonicalBytes.Length;
    }
}
