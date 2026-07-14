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
    long StateGeneration,
    long LedgerEpoch,
    string PredecessorLedgerSha256,
    long? PredecessorStateGeneration,
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
    // Private snapshot used by LedgerAppend / LedgerBuilder. Distinct from the
    // public LedgerModel so that any caller mutation of the public Model via
    // ImmutableCollectionsMarshal.AsArray (documented "unsafe" API) cannot
    // affect append validation, history equality, or prefix-hash binding.
    private readonly LedgerModel privateModel;

    internal ValidatedLedger(LedgerModel model, byte[] canonicalBytes, string contentSha256)
    {
        this.canonicalBytes = new byte[canonicalBytes.Length];
        Buffer.BlockCopy(canonicalBytes, 0, this.canonicalBytes, 0, canonicalBytes.Length);
        this.privateModel = DeepClone(model);
        Model = DeepClone(model);
        ContentSha256 = contentSha256;
    }

    public LedgerModel Model { get; }

    /// <summary>
    /// Internal accessor returning the frozen snapshot captured at construction.
    /// LedgerAppend and LedgerBuilder read this instead of <see cref="Model"/>
    /// so any caller mutation of the public model — for example via
    /// <c>ImmutableCollectionsMarshal.AsArray</c> — cannot influence append
    /// validation, history equality, or the prefix-hash binding.
    /// </summary>
    internal LedgerModel PrivateModel => this.privateModel;

    private static LedgerModel DeepClone(LedgerModel model)
    {
        var records = ImmutableArray.CreateBuilder<LedgerRecord>(model.Records.Length);
        foreach (var r in model.Records) records.Add(CloneRecord(r));
        return new LedgerModel(model.SchemaVersion, model.PrefixContractVersion, model.Header, records.ToImmutable());
    }

    private static LedgerRecord CloneRecord(LedgerRecord r)
    {
        if (r.Context is ReviewContextRecord ctx)
        {
            var files = ImmutableArray.CreateBuilder<ChangedFileEntry>(ctx.ChangedFiles.Length);
            foreach (var f in ctx.ChangedFiles) files.Add(f);
            return new LedgerRecord(ctx with { ChangedFiles = files.ToImmutable() }, null);
        }
        if (r.Outcome is ReviewOutcomeRecord oc)
        {
            var findings = ImmutableArray.CreateBuilder<LedgerFinding>(oc.Findings.Length);
            foreach (var f in oc.Findings) findings.Add(f);
            var lims = ImmutableArray.CreateBuilder<string>(oc.Limitations.Length);
            foreach (var l in oc.Limitations) lims.Add(l);
            return new LedgerRecord(null, oc with { Findings = findings.ToImmutable(), Limitations = lims.ToImmutable() });
        }
        return r;
    }

    /// <summary>
    /// The lowercase-hex SHA-256 of <see cref="ToCanonicalByteArray"/> as measured
    /// at construction. Never recomputed; the underlying bytes are held by a
    /// private array and cannot be observed except through the copy accessors
    /// on this class.
    /// </summary>
    public string ContentSha256 { get; }

    /// <summary>Length in bytes of the canonical form.</summary>
    public int ByteLength => this.canonicalBytes.Length;

    /// <summary>
    /// Returns a freshly allocated copy of the canonical UTF-8 bytes. Each call
    /// allocates; mutating the returned array cannot affect the ledger.
    /// </summary>
    /// <remarks>
    /// This is the only public byte accessor. There is no <c>ReadOnlyMemory</c>
    /// or <c>ReadOnlySpan</c> property, because the standard interop helpers
    /// (<c>MemoryMarshal.TryGetArray</c>, <c>MemoryMarshal.TryGetMemoryManager</c>,
    /// or <c>MemoryMarshal.CreateSpan</c>) would otherwise let callers alias the
    /// internal buffer and violate the deep-immutability invariant.
    /// </remarks>
    public byte[] ToCanonicalByteArray()
    {
        var copy = new byte[this.canonicalBytes.Length];
        Buffer.BlockCopy(this.canonicalBytes, 0, copy, 0, this.canonicalBytes.Length);
        return copy;
    }

    /// <summary>
    /// Copies the canonical bytes into <paramref name="destination"/>. Throws
    /// <see cref="ArgumentException"/> if the destination is not long enough.
    /// This avoids the allocation of <see cref="ToCanonicalByteArray"/> while
    /// still preventing internal-buffer aliasing.
    /// </summary>
    public int CopyCanonicalBytesTo(Span<byte> destination)
    {
        if (destination.Length < this.canonicalBytes.Length)
            throw new ArgumentException("Destination too short.", nameof(destination));
        this.canonicalBytes.AsSpan().CopyTo(destination);
        return this.canonicalBytes.Length;
    }

    /// <summary>
    /// Internal accessor for use by <see cref="LedgerAppend"/> /
    /// <see cref="LedgerBuilder"/> that need to compare canonical bytes without
    /// paying the copy cost. Not exposed publicly; consumers outside the runtime
    /// assembly must use the copy-returning accessors.
    /// </summary>
    internal ReadOnlySpan<byte> CanonicalBytesUnsafeSpan => this.canonicalBytes;
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
    public abstract long GetStateGeneration();
    public abstract long GetLedgerEpoch();
}

public sealed record BootstrapTransition(ExpectedIdentities Identities, long StateGeneration, long LedgerEpoch)
    : ExpectedTransition(Identities)
{
    public override string Kind => "bootstrap";
    public override long GetStateGeneration() => StateGeneration;
    public override long GetLedgerEpoch() => LedgerEpoch;
}

public sealed record ContinuationTransition(
    ExpectedIdentities Identities,
    string PredecessorLedgerSha256,
    long PredecessorStateGeneration,
    long StateGeneration,
    long LedgerEpoch)
    : ExpectedTransition(Identities)
{
    public override string Kind => "continuation";
    public override long GetStateGeneration() => StateGeneration;
    public override long GetLedgerEpoch() => LedgerEpoch;
}

public sealed record ResetTransition(
    ExpectedIdentities Identities,
    string PredecessorLedgerSha256,
    string PredecessorManifestSha256,
    long PredecessorStateGeneration,
    long StateGeneration,
    long LedgerEpoch,
    string ResetReason)
    : ExpectedTransition(Identities)
{
    public override string Kind => "reset";
    public override long GetStateGeneration() => StateGeneration;
    public override long GetLedgerEpoch() => LedgerEpoch;
}

public sealed record RecoveryTransition(
    ExpectedIdentities Identities,
    long StateGeneration,
    long LedgerEpoch,
    string RecoveryReason)
    : ExpectedTransition(Identities)
{
    public override string Kind => "recovery";
    public override long GetStateGeneration() => StateGeneration;
    public override long GetLedgerEpoch() => LedgerEpoch;
}

// Outcomes
public readonly record struct ParseOutcome(ValidatedLedger? Ledger, LedgerDiagnostic? Failure);
public readonly record struct ProjectionOutcome<T>(T? Record, LedgerDiagnostic? Failure) where T : class;
public readonly record struct BuildOutcome(ValidatedLedger? Ledger, LedgerDiagnostic? Failure);
public readonly record struct TransitionOutcome(ValidatedLedger? Candidate, LedgerDiagnostic? Failure);

