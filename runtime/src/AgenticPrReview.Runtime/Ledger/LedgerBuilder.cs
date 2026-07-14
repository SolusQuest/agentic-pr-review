using System.Collections.Immutable;
using System.Text;

namespace AgenticPrReview.Runtime.Ledger;

/// <summary>
/// Pure projection and candidate assembly. Consumes already-validated
/// <see cref="ValidatedContextSource"/> / <see cref="ValidatedOutcomeSource"/>
/// DTOs and produces immutable ledger records or full validated ledger
/// candidates.
/// </summary>
public static class LedgerBuilder
{
    public static ProjectionOutcome<ReviewContextRecord> BuildReviewContext(
        ValidatedContextSource source, ExpectedIdentities identities, InteractionIdentity interaction)
    {
        // Guard top-level null arguments: the public contract for #49 is that
        // every failure returns a typed outcome + non-null Failure, never a
        // NullReferenceException. Non-nullable reference parameters can still
        // be forwarded as `null!` by callers; classify as ledger_schema_violation.
        if (source is null || identities is null || interaction is null)
            return new ProjectionOutcome<ReviewContextRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));

        // Guard default(ImmutableArray<T>) inputs: a default-initialized
        // ImmutableArray throws on Length / iteration. Normalize by returning
        // ledger_schema_violation so no public path exits through an
        // unclassified exception.
        if (source.ChangedFiles.IsDefault)
            return new ProjectionOutcome<ReviewContextRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));

        // Preflight (null and lone-surrogate on every caller-supplied string)
        // runs before any pattern / length / enum helper touches the values.
        var contextUnicode = PreflightContextSourceUnicode(source, identities, interaction);
        if (contextUnicode is not null)
            return new ProjectionOutcome<ReviewContextRecord>(null, contextUnicode);

        // Identity constraints
        var idFailure = ValidateIdentities(identities);
        if (idFailure is not null) return new ProjectionOutcome<ReviewContextRecord>(null, idFailure);

        // Reviewed SHAs
        if (!IsSha1(source.ReviewedHeadSha) || !IsSha1(source.ReviewedBaseSha))
            return new ProjectionOutcome<ReviewContextRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));

        // Changed files
        if (source.ChangedFiles.Length > LedgerLimits.MaxChangedFilesPerContext)
            return new ProjectionOutcome<ReviewContextRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.ChangedFileLimitExceeded));

        var files = ImmutableArray.CreateBuilder<ChangedFileEntry>();
        foreach (var f in source.ChangedFiles)
        {
            if (HasInvalidUnicode(f.Path) || (f.PreviousPath is not null && HasInvalidUnicode(f.PreviousPath)) ||
                HasInvalidUnicode(f.Status))
                return new ProjectionOutcome<ReviewContextRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode));
            if (!IsSafeRelativePath(f.Path) || (f.PreviousPath is not null && !IsSafeRelativePath(f.PreviousPath)))
                return new ProjectionOutcome<ReviewContextRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
            if (!IsSupportedStatus(f.Status))
                return new ProjectionOutcome<ReviewContextRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.UnsupportedChangeStatus));
            if (f.Additions < 0 || f.Deletions < 0 || f.Changes < 0)
                return new ProjectionOutcome<ReviewContextRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
            ChangedFilePatch? patch = null;
            if (f.Patch is ValidatedPatchSource ps)
            {
                if (!IsHex64(ps.Sha256))
                    return new ProjectionOutcome<ReviewContextRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
                patch = new ChangedFilePatch(ps.Sha256, ps.Truncated, ps.MaxChars);
            }
            files.Add(new ChangedFileEntry(f.Path, f.PreviousPath, f.Status, f.Additions, f.Deletions, f.Changes, patch));
        }

        // Interaction identity
        if (!IsHex64(interaction.InteractionId) || interaction.InteractionOrdinal < 0)
            return new ProjectionOutcome<ReviewContextRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));

        // Digests
        var subject = LedgerDigests.ComputeSubjectDigest(
            identities.Repository, identities.HeadRepository, identities.PullRequest,
            source.ReviewedHeadSha, source.ReviewedBaseSha);
        var cacheContract = LedgerDigests.ComputeCacheContractDigest(identities);

        var record = new ReviewContextRecord(
            InteractionId: interaction.InteractionId,
            InteractionOrdinal: interaction.InteractionOrdinal,
            ReviewedHeadSha: source.ReviewedHeadSha,
            ReviewedBaseSha: source.ReviewedBaseSha,
            SubjectDigest: subject,
            CacheContractDigest: cacheContract,
            ChangedFiles: files.ToImmutable());

        return new ProjectionOutcome<ReviewContextRecord>(record, null);
    }

    public static ProjectionOutcome<ReviewOutcomeRecord> BuildReviewOutcome(
        ValidatedOutcomeSource source, InteractionIdentity interaction)
    {
        if (source is null || interaction is null)
            return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
        if (source.Findings.IsDefault || source.Limitations.IsDefault)
            return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));

        // Preflight null/lone-surrogate on every caller-supplied string first.
        var unicode = PreflightOutcomeSourceUnicode(source, interaction);
        if (unicode is not null)
            return new ProjectionOutcome<ReviewOutcomeRecord>(null, unicode);

        // Interaction identity
        if (!IsHex64(interaction.InteractionId) || interaction.InteractionOrdinal < 0)
            return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));

        // schema maxLength before \S pattern (mapper precedence 6 before 7).
        if (source.Summary.Length > LedgerLimits.MaxSummaryChars)
            return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.OverlongValue));
        if (HasInvalidUnicode(source.Summary))
            return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode));
        if (!ContainsNonWhitespace(source.Summary))
            return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));

        if (source.Findings.Length > LedgerLimits.MaxFindingsPerOutcome)
            return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.FindingLimitExceeded));
        if (source.Limitations.Length > LedgerLimits.MaxLimitationsPerOutcome)
            return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.LimitationsLimitExceeded));

        var findings = ImmutableArray.CreateBuilder<LedgerFinding>();
        foreach (var f in source.Findings)
        {
            if (f is null)
                return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
            var f2 = new LedgerFinding(
                f.Severity, f.Confidence, f.Category, f.Title, f.Body,
                f.Path, f.StartLine, f.EndLine,
                f.Evidence, f.SuggestedAction, f.InlinePreference);
            if (HasInvalidUnicode(f2.Title) || HasInvalidUnicode(f2.Body) ||
                (f2.Evidence is not null && HasInvalidUnicode(f2.Evidence)) ||
                (f2.SuggestedAction is not null && HasInvalidUnicode(f2.SuggestedAction)) ||
                (f2.Path is not null && HasInvalidUnicode(f2.Path)) ||
                HasInvalidUnicode(f2.Severity) || HasInvalidUnicode(f2.Confidence) ||
                HasInvalidUnicode(f2.Category) ||
                (f2.InlinePreference is not null && HasInvalidUnicode(f2.InlinePreference)))
                return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode));
            // schema string maxLength (mapper precedence 6) before \S pattern
            if (f2.Body.Length > LedgerLimits.MaxFindingBodyChars ||
                f2.Title.Length > LedgerLimits.MaxFindingTitleChars ||
                (f2.Evidence is not null && f2.Evidence.Length > LedgerLimits.MaxFindingEvidenceChars) ||
                (f2.SuggestedAction is not null && f2.SuggestedAction.Length > LedgerLimits.MaxFindingSuggestedActionChars) ||
                (f2.Path is not null && f2.Path.Length > LedgerLimits.MaxSafeRelativePathChars))
                return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.OverlongValue));
            // schema enums
            if (f2.Severity is not ("low" or "medium" or "high") ||
                f2.Confidence is not ("medium" or "high") ||
                f2.Category is not ("correctness" or "security" or "requirements" or "test_coverage" or "build" or "performance" or "maintainability" or "documentation") ||
                (f2.InlinePreference is not null && f2.InlinePreference is not ("allowed" or "preferred" or "avoid")))
                return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
            // schema \S pattern
            if (!ContainsNonWhitespace(f2.Title) || !ContainsNonWhitespace(f2.Body) ||
                (f2.Evidence is not null && !ContainsNonWhitespace(f2.Evidence)) ||
                (f2.SuggestedAction is not null && !ContainsNonWhitespace(f2.SuggestedAction)))
                return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
            // safe-relative-path
            if (f2.Path is not null && !IsSafeRelativePath(f2.Path))
                return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
            // Line minimum (schema)
            if ((f2.StartLine is int sl && sl < 1) || (f2.EndLine is int el && el < 1))
                return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
            var locFailure = LedgerSemanticChecks.ValidateFindingLocation(f2);
            if (locFailure is not null) return new ProjectionOutcome<ReviewOutcomeRecord>(null, locFailure);
            findings.Add(f2);
        }

        var lims = ImmutableArray.CreateBuilder<string>();
        foreach (var l in source.Limitations)
        {
            if (l is null)
                return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
            if (HasInvalidUnicode(l))
                return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode));
            // schema maxLength before \S pattern.
            if (l.Length > LedgerLimits.MaxLimitationsItemChars)
                return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.OverlongValue));
            if (!ContainsNonWhitespace(l))
                return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
            lims.Add(l);
        }

        var record = new ReviewOutcomeRecord(
            interaction.InteractionId,
            interaction.InteractionOrdinal,
            source.Summary,
            findings.ToImmutable(),
            lims.ToImmutable());
        return new ProjectionOutcome<ReviewOutcomeRecord>(record, null);
    }

    // -----------------------------------------------------------------
    // Candidate assembly

    public static BuildOutcome CreateBootstrap(BootstrapTransition expected, ReviewContextRecord context, ReviewOutcomeRecord outcome)
    {
        // Top-level null argument guard (see BuildReviewContext for rationale).
        // Null context or outcome would otherwise reach PreflightCandidate as
        // a schema-shape mismatch classified as pair_order — issue #49 pins
        // schema-shape errors to ledger_schema_violation, so we normalize here.
        if (expected is null || context is null || outcome is null)
            return new BuildOutcome(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
        // Identity unicode preflight is performed later by PreflightCandidate,
        // but ExpectedTransition string fields must be checked before the
        // schema/const enum path below can access them.
        if (expected.StateGeneration != 0)
            return new BuildOutcome(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.BootstrapShapeViolation));
        var header = BuildHeader(expected.Identities, "bootstrap",
            stateGeneration: 0, ledgerEpoch: expected.LedgerEpoch, predecessor: "bootstrap");
        return AssembleAndValidate(header, ImmutableArray.Create(
            new LedgerRecord(context, null), new LedgerRecord(null, outcome)),
            candidate => LedgerAppend.ValidateBootstrap(candidate, expected));
    }

    public static BuildOutcome CreateRecovery(RecoveryTransition expected, ReviewContextRecord context, ReviewOutcomeRecord outcome)
    {
        if (expected is null || context is null || outcome is null)
            return new BuildOutcome(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
        if (HasInvalidUnicode(expected.RecoveryReason))
            return new BuildOutcome(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode));
        if (expected.StateGeneration != 0)
            return new BuildOutcome(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.RecoveryShapeViolation));
        if (!IsValidRecoveryReason(expected.RecoveryReason))
            return new BuildOutcome(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.RecoveryReasonMissing));
        var header = BuildHeader(expected.Identities, "recovery",
            stateGeneration: 0, ledgerEpoch: expected.LedgerEpoch, predecessor: "bootstrap",
            recoveryReason: expected.RecoveryReason);
        return AssembleAndValidate(header, ImmutableArray.Create(
            new LedgerRecord(context, null), new LedgerRecord(null, outcome)),
            candidate => LedgerAppend.ValidateRecovery(candidate, expected));
    }

    public static BuildOutcome AppendContinuation(ValidatedLedger predecessor, ContinuationTransition expected, ReviewContextRecord context, ReviewOutcomeRecord outcome)
    {
        if (predecessor is null || expected is null || context is null || outcome is null)
            return new BuildOutcome(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
        if (HasInvalidUnicode(expected.PredecessorLedgerSha256))
            return new BuildOutcome(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode));
        var header = BuildHeader(expected.Identities, "continuation",
            stateGeneration: expected.StateGeneration,
            ledgerEpoch: expected.LedgerEpoch,
            predecessor: expected.PredecessorLedgerSha256,
            predecessorStateGeneration: expected.PredecessorStateGeneration);

        var records = ImmutableArray.CreateBuilder<LedgerRecord>();
        foreach (var r in predecessor.PrivateModel.Records) records.Add(r);
        records.Add(new LedgerRecord(context, null));
        records.Add(new LedgerRecord(null, outcome));
        return AssembleAndValidate(header, records.ToImmutable(),
            candidate => LedgerAppend.ValidateContinuation(predecessor, candidate, expected));
    }

    public static BuildOutcome CreateReset(ValidatedLedger predecessor, ResetTransition expected, ReviewContextRecord context, ReviewOutcomeRecord outcome)
    {
        if (predecessor is null || expected is null || context is null || outcome is null)
            return new BuildOutcome(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
        if (HasInvalidUnicode(expected.ResetReason) ||
            HasInvalidUnicode(expected.PredecessorLedgerSha256) ||
            HasInvalidUnicode(expected.PredecessorManifestSha256))
            return new BuildOutcome(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode));
        if (!IsValidResetReason(expected.ResetReason))
            return new BuildOutcome(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.ResetReasonMissing));
        var header = BuildHeader(expected.Identities, "reset",
            stateGeneration: expected.StateGeneration,
            ledgerEpoch: expected.LedgerEpoch,
            predecessor: expected.PredecessorLedgerSha256,
            predecessorStateGeneration: expected.PredecessorStateGeneration,
            predecessorManifestSha256: expected.PredecessorManifestSha256,
            resetReason: expected.ResetReason);
        return AssembleAndValidate(header, ImmutableArray.Create(
            new LedgerRecord(context, null), new LedgerRecord(null, outcome)),
            candidate => LedgerAppend.ValidateReset(predecessor, candidate, expected));
    }

    // -----------------------------------------------------------------
    // Helpers

    private static LedgerHeader BuildHeader(
        ExpectedIdentities i, string kind,
        long stateGeneration, long ledgerEpoch, string predecessor,
        long? predecessorStateGeneration = null,
        string? predecessorManifestSha256 = null,
        string? resetReason = null,
        string? recoveryReason = null)
    {
        return new LedgerHeader(
            Kind: kind,
            Repository: i.Repository,
            HeadRepository: i.HeadRepository,
            PullRequest: i.PullRequest,
            WorkflowIdentity: i.WorkflowIdentity,
            TrustedExecutionDomain: i.TrustedExecutionDomain,
            SessionEpoch: i.SessionEpoch,
            ProviderId: i.ProviderId,
            ModelId: i.ModelId,
            AdapterId: i.AdapterId,
            TemplateId: i.TemplateId,
            PolicyId: i.PolicyId,
            ToolDefinitionId: i.ToolDefinitionId,
            CacheConfigId: i.CacheConfigId,
            StateGeneration: stateGeneration,
            LedgerEpoch: ledgerEpoch,
            PredecessorLedgerSha256: predecessor,
            PredecessorStateGeneration: predecessorStateGeneration,
            PredecessorManifestSha256: predecessorManifestSha256,
            ResetReason: resetReason,
            RecoveryReason: recoveryReason);
    }

    private static LedgerDiagnostic? PreflightCandidate(LedgerHeader header, ImmutableArray<LedgerRecord> records)
    {
        // Header identity strings
        var identityStrings = new[]
        {
            header.Kind,
            header.Repository, header.HeadRepository,
            header.WorkflowIdentity, header.TrustedExecutionDomain, header.SessionEpoch,
            header.ProviderId, header.ModelId,
            header.AdapterId, header.TemplateId, header.PolicyId, header.ToolDefinitionId, header.CacheConfigId,
            header.PredecessorLedgerSha256,
        };
        foreach (var s in identityStrings)
        {
            if (HasInvalidUnicode(s)) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);
        }
        if (header.PredecessorManifestSha256 is not null && HasInvalidUnicode(header.PredecessorManifestSha256))
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);
        if (header.ResetReason is not null && HasInvalidUnicode(header.ResetReason))
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);
        if (header.RecoveryReason is not null && HasInvalidUnicode(header.RecoveryReason))
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);

        // Records: verify every user-visible string.
        foreach (var rec in records)
        {
            if (rec.Context is ReviewContextRecord ctx)
            {
                if (ctx.ChangedFiles.IsDefault)
                    return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
                if (HasInvalidUnicode(ctx.InteractionId) ||
                    HasInvalidUnicode(ctx.ReviewedHeadSha) ||
                    HasInvalidUnicode(ctx.ReviewedBaseSha) ||
                    HasInvalidUnicode(ctx.SubjectDigest) ||
                    HasInvalidUnicode(ctx.CacheContractDigest))
                    return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);
                foreach (var f in ctx.ChangedFiles)
                {
                    if (f is null)
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
                    if (HasInvalidUnicode(f.Path) || HasInvalidUnicode(f.Status) ||
                        (f.PreviousPath is not null && HasInvalidUnicode(f.PreviousPath)))
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);
                    if (f.Patch is not null && HasInvalidUnicode(f.Patch.Sha256))
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);
                }
            }
            if (rec.Outcome is ReviewOutcomeRecord oc)
            {
                if (oc.Findings.IsDefault || oc.Limitations.IsDefault)
                    return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
                if (HasInvalidUnicode(oc.InteractionId) || HasInvalidUnicode(oc.Summary))
                    return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);
                foreach (var f in oc.Findings)
                {
                    if (f is null)
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
                    if (HasInvalidUnicode(f.Severity) || HasInvalidUnicode(f.Confidence) ||
                        HasInvalidUnicode(f.Category) || HasInvalidUnicode(f.Title) ||
                        HasInvalidUnicode(f.Body) ||
                        (f.Path is not null && HasInvalidUnicode(f.Path)) ||
                        (f.Evidence is not null && HasInvalidUnicode(f.Evidence)) ||
                        (f.SuggestedAction is not null && HasInvalidUnicode(f.SuggestedAction)) ||
                        (f.InlinePreference is not null && HasInvalidUnicode(f.InlinePreference)))
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);
                }
                foreach (var l in oc.Limitations)
                {
                    if (l is null)
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
                    if (HasInvalidUnicode(l)) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);
                }
            }
        }
        return null;
    }

    private static LedgerDiagnostic? ValidateModelSchemaAndSemantics(LedgerModel model)
    {
        // Schema-first: every schema-owned constraint (patterns, enums,
        // numeric ranges, maxItems, maxLength, safe-path) fires BEFORE any
        // semantic check that could mask them (identity byte-length,
        // control-character-in-identity, digest recomputation, pair order).

        // Header shape.
        var h = model.Header;
        if (!IsRepositorySlug(h.Repository) || !IsRepositorySlug(h.HeadRepository))
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
        foreach (var s in new[] { h.AdapterId, h.TemplateId, h.PolicyId, h.ToolDefinitionId, h.CacheConfigId })
        {
            if (!IsHex64(s)) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
        }
        // Minimum-length on the free-form identity strings.
        foreach (var s in new[] { h.WorkflowIdentity, h.TrustedExecutionDomain, h.SessionEpoch, h.ProviderId, h.ModelId })
        {
            if (s.Length == 0) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
            // maxLength: 256 characters (before UTF-8 byte-length check).
            if (s.Length > 256) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.OverlongValue);
        }
        if (h.PullRequest < 1) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
        if (h.StateGeneration < 0 || h.StateGeneration > 1_000_000)
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
        if (h.LedgerEpoch < 0 || h.LedgerEpoch > 1_000_000)
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
        if (h.PredecessorStateGeneration is long psg && (psg < 0 || psg > 1_000_000))
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
        // Predecessor hashes must be 64-hex except the bootstrap sentinel.
        if (h.Kind is "continuation" or "reset")
        {
            if (!IsHex64(h.PredecessorLedgerSha256))
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
        }
        else if (h.PredecessorLedgerSha256 != "bootstrap")
        {
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
        }
        if (h.PredecessorManifestSha256 is string pms && !IsHex64(pms))
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);

        // Per-record aggregate limits (schema-level maxItems).
        foreach (var rec in model.Records)
        {
            if (rec.Context is ReviewContextRecord ctxA)
            {
                if (ctxA.ChangedFiles.IsDefault ||
                    ctxA.ChangedFiles.Length > LedgerLimits.MaxChangedFilesPerContext)
                {
                    return ctxA.ChangedFiles.IsDefault
                        ? LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation)
                        : LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.ChangedFileLimitExceeded);
                }
            }
            if (rec.Outcome is ReviewOutcomeRecord ocA)
            {
                if (ocA.Findings.IsDefault || ocA.Limitations.IsDefault)
                    return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
                if (ocA.Findings.Length > LedgerLimits.MaxFindingsPerOutcome)
                    return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.FindingLimitExceeded);
                if (ocA.Limitations.Length > LedgerLimits.MaxLimitationsPerOutcome)
                    return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.LimitationsLimitExceeded);
            }
        }

        // Per-record schema-shape validation on the manually-constructed
        // records — the same overlong / path / patch / finding checks that
        // BuildReviewContext / BuildReviewOutcome apply when a caller uses
        // the projection helpers. Runs BEFORE identity-bounds and semantic
        // invariants so schema errors are not masked by a
        // control_character_in_identity or digest_mismatch diagnostic.
        foreach (var rec in model.Records)
        {
            if (rec.Context is ReviewContextRecord ctx)
            {
                if (ctx.ChangedFiles.Length > LedgerLimits.MaxChangedFilesPerContext)
                    return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.ChangedFileLimitExceeded);
                if (!IsSha1(ctx.ReviewedHeadSha) || !IsSha1(ctx.ReviewedBaseSha) ||
                    !IsHex64(ctx.SubjectDigest) || !IsHex64(ctx.CacheContractDigest) ||
                    !IsHex64(ctx.InteractionId))
                    return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
                if (ctx.InteractionOrdinal < 0 || ctx.InteractionOrdinal > 1_000_000)
                    return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
                foreach (var f in ctx.ChangedFiles)
                {
                    if (!IsSupportedStatus(f.Status))
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.UnsupportedChangeStatus);
                    if (!IsSafeRelativePath(f.Path) ||
                        (f.PreviousPath is not null && !IsSafeRelativePath(f.PreviousPath)))
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
                    if (f.Additions < 0 || f.Additions > 1_000_000 ||
                        f.Deletions < 0 || f.Deletions > 1_000_000 ||
                        f.Changes < 0 || f.Changes > 1_000_000)
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
                    if (f.Path.Length > LedgerLimits.MaxSafeRelativePathChars)
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.OverlongValue);
                    if (f.Patch is not null)
                    {
                        if (!IsHex64(f.Patch.Sha256))
                            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
                        if (f.Patch.MaxChars < 0 || f.Patch.MaxChars > 10_000_000)
                            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
                    }
                }
            }
            if (rec.Outcome is ReviewOutcomeRecord oc)
            {
                if (!IsHex64(oc.InteractionId))
                    return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
                if (oc.InteractionOrdinal < 0 || oc.InteractionOrdinal > 1_000_000)
                    return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
                // Order matches authoritative parser: string maxLength before
                // \S pattern (schema-first, section 6 precedence).
                if (oc.Summary.Length > LedgerLimits.MaxSummaryChars)
                    return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.OverlongValue);
                if (!ContainsNonWhitespace(oc.Summary))
                    return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
                if (oc.Findings.Length > LedgerLimits.MaxFindingsPerOutcome)
                    return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.FindingLimitExceeded);
                if (oc.Limitations.Length > LedgerLimits.MaxLimitationsPerOutcome)
                    return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.LimitationsLimitExceeded);
                foreach (var f in oc.Findings)
                {
                    // Length caps (schema maxLength).
                    if (f.Body.Length > LedgerLimits.MaxFindingBodyChars ||
                        f.Title.Length > LedgerLimits.MaxFindingTitleChars ||
                        (f.Evidence is not null && f.Evidence.Length > LedgerLimits.MaxFindingEvidenceChars) ||
                        (f.SuggestedAction is not null && f.SuggestedAction.Length > LedgerLimits.MaxFindingSuggestedActionChars) ||
                        (f.Path is not null && f.Path.Length > LedgerLimits.MaxSafeRelativePathChars))
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.OverlongValue);
                    // Enum constraints (authoritative ledger schema).
                    if (f.Severity is not ("low" or "medium" or "high"))
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
                    if (f.Confidence is not ("medium" or "high"))
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
                    if (f.Category is not ("correctness" or "security" or "requirements"
                        or "test_coverage" or "build" or "performance"
                        or "maintainability" or "documentation"))
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
                    if (f.InlinePreference is not null &&
                        f.InlinePreference is not ("allowed" or "preferred" or "avoid"))
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
                    // \S pattern: non-empty and contains at least one non-whitespace.
                    if (!ContainsNonWhitespace(f.Title) || !ContainsNonWhitespace(f.Body) ||
                        (f.Evidence is not null && !ContainsNonWhitespace(f.Evidence)) ||
                        (f.SuggestedAction is not null && !ContainsNonWhitespace(f.SuggestedAction)))
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
                    // Safe-relative-path for finding.path.
                    if (f.Path is not null && !IsSafeRelativePath(f.Path))
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
                    // Line minimum.
                    if ((f.StartLine is int sl && sl < 1) || (f.EndLine is int el && el < 1))
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
                    // Finding location invariants (path / range consistency).
                    var locFailure = LedgerSemanticChecks.ValidateFindingLocation(f);
                    if (locFailure is not null) return locFailure;
                }
                foreach (var l in oc.Limitations)
                {
                    // maxLength before \S (mapper precedence 6 before 7).
                    if (l.Length > LedgerLimits.MaxLimitationsItemChars)
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.OverlongValue);
                    if (!ContainsNonWhitespace(l))
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
                }
            }
        }

        // Identity byte-length caps and control-character rejection on
        // header identities. Runs AFTER schema-owned pattern/enum/numeric
        // checks so an invalid reviewed SHA is not preceded by an unrelated
        // control_character_in_identity diagnostic when both are present.
        var bounds = LedgerSemanticChecks.CheckIdentityBounds(model);
        if (bounds is not null) return bounds;

        // Structural / cross-record semantic invariants (pair order, ordinal
        // continuity, digest recomputation).
        var semantic = LedgerSemanticChecks.CheckSemanticInvariants(model);
        if (semantic is not null) return semantic;

        return null;
    }

    private static (int TotalProperties, int MaxArrayLength) CountStructural(LedgerHeader header, ImmutableArray<LedgerRecord> records)
    {
        // These counts must equal the exact number of "key":value pairs emitted
        // by LedgerCanonicalizer.WriteLedger. Any drift breaks the builder-level
        // composite cause precedence.

        // Top-level: schemaVersion, prefixContractVersion, header, records.
        var total = 4;

        // Header canonical writer always emits 17 base properties (see LedgerCanonicalizer.WriteHeader).
        var headerProps = 17;
        if (header.PredecessorStateGeneration is not null) headerProps++;
        if (header.PredecessorManifestSha256 is not null) headerProps++;
        if (header.ResetReason is not null) headerProps++;
        if (header.RecoveryReason is not null) headerProps++;
        total += headerProps;

        // Track the maximum array length across the ledger. records itself is
        // the top-level array.
        var maxArrayLen = records.Length;

        foreach (var rec in records)
        {
            if (rec.Context is ReviewContextRecord ctx)
            {
                // Context canonical writer emits 8 base properties:
                // role, interactionId, interactionOrdinal,
                // reviewedHeadSha, reviewedBaseSha, subjectDigest,
                // cacheContractDigest, changedFiles.
                total += 8;
                maxArrayLen = Math.Max(maxArrayLen, ctx.ChangedFiles.Length);
                foreach (var f in ctx.ChangedFiles)
                {
                    // Changed-file canonical writer emits 5 base properties:
                    // path, status, additions, deletions, changes.
                    var fProps = 5;
                    if (f.PreviousPath is not null) fProps++;
                    if (f.Patch is not null)
                    {
                        fProps++;
                        // Patch envelope: sha256, truncated, maxChars.
                        total += 3;
                    }
                    total += fProps;
                }
            }
            if (rec.Outcome is ReviewOutcomeRecord oc)
            {
                // Outcome canonical writer emits 6 base properties:
                // role, interactionId, interactionOrdinal, summary,
                // findings, limitations.
                total += 6;
                maxArrayLen = Math.Max(maxArrayLen, oc.Findings.Length);
                maxArrayLen = Math.Max(maxArrayLen, oc.Limitations.Length);
                foreach (var f in oc.Findings)
                {
                    // Finding canonical writer emits 5 base properties (severity,
                    // confidence, category, title, body) plus path / startLine /
                    // endLine which are always serialized (null-explicit) and
                    // evidence / suggestedAction / inlinePreference which are
                    // omitted when null.
                    var fProps = 5 + 3;
                    if (f.Evidence is not null) fProps++;
                    if (f.SuggestedAction is not null) fProps++;
                    if (f.InlinePreference is not null) fProps++;
                    total += fProps;
                }
            }
        }
        return (total, maxArrayLen);
    }

    private static BuildOutcome AssembleAndValidate(
        LedgerHeader header,
        ImmutableArray<LedgerRecord> records,
        Func<ValidatedLedger, TransitionOutcome> transitionValidator)
    {
        // Builder pipeline (frozen precedence):
        //   1. ledger_interaction_limit_exceeded  (composite cause)
        //   2. Unicode preflight (null / NUL / lone-surrogate)
        //   3. Model-level schema-first validation:
        //        - per-record aggregate limits (changed_file / finding /
        //          limitations); these fire BEFORE the JSON property /
        //          array aggregate counts so a caller who bypasses the
        //          projection helpers still receives the specific
        //          per-record diagnostic.
        //        - schema shape (patterns, enums, hex64, safe path,
        //          numeric ranges, overlong values).
        //        - semantic invariants (pair order, ordinal continuity,
        //          identity byte-length, digest recomputation).
        //   4. ledger_json_property_count_exceeded  (composite cause;
        //      currently unreachable through legal per-record limits but
        //      retained for closed-set completeness).
        //   5. ledger_json_array_length_exceeded  (composite cause; same
        //      caveat as property count).
        //   6. ledger_canonical_byte_limit_exceeded  (composite cause).
        //   7. Authoritative parser round-trip on the canonical bytes.
        //   8. Transition cross-check via LedgerAppend.Validate*.
        //
        // Step 1: interaction limit.
        if (records.Length / 2 > LedgerLimits.MaxInteractionPairs)
        {
            return new BuildOutcome(null, LedgerDiagnosticMessages.Of(
                LedgerDiagnosticCodes.OverBoundAppend, LedgerDiagnosticCodes.InteractionLimitExceeded));
        }

        // Step 2: Unicode / null preflight.
        var preflight = PreflightCandidate(header, records);
        if (preflight is not null) return new BuildOutcome(null, preflight);

        // Step 3: model-level schema-first validation. Runs the per-record
        // aggregate limits first so a caller bypassing BuildReviewContext /
        // BuildReviewOutcome receives the correct per-record diagnostic
        // instead of the more generic property/array/byte causes.
        var model = new LedgerModel(1, 1, header, records);
        var modelValidationFailure = ValidateModelSchemaAndSemantics(model);
        if (modelValidationFailure is not null)
            return new BuildOutcome(null, modelValidationFailure);

        // Steps 4-5: aggregate property-count and array-length.
        var (totalProperties, maxArrayLength) = CountStructural(header, records);
        if (totalProperties > LedgerLimits.MaxTotalProperties)
        {
            return new BuildOutcome(null, LedgerDiagnosticMessages.Of(
                LedgerDiagnosticCodes.OverBoundAppend, LedgerDiagnosticCodes.JsonPropertyCountExceeded));
        }
        if (maxArrayLength > LedgerLimits.MaxArrayLength)
        {
            return new BuildOutcome(null, LedgerDiagnosticMessages.Of(
                LedgerDiagnosticCodes.OverBoundAppend, LedgerDiagnosticCodes.JsonArrayLengthExceeded));
        }

        // Serialize to canonical bytes so we can produce a ValidatedLedger.
        var canonical = LedgerCanonicalizer.SerializeCanonical(model);

        // Full-pipeline re-validation via ParseAndValidate to catch any
        // manually-constructed records that bypass Build{Context,Outcome}
        // invariants. This must run before the canonical byte cap so that
        // property-count / array-length failures raised deep inside the
        // ledger surface as their specific causeCode rather than being
        // masked by a byte-limit rejection.
        // Step 4 explicit: canonical byte cap (256 KiB) is checked BEFORE the
        // raw byte cap (512 KiB) that ParseAndValidate would report first.
        // Otherwise a 512+ KiB candidate would surface as raw_byte_limit_exceeded,
        // which is not one of the four allowed causes.
        if (canonical.Length > LedgerLimits.MaxCanonicalBytes)
        {
            return new BuildOutcome(null, LedgerDiagnosticMessages.Of(
                LedgerDiagnosticCodes.OverBoundAppend, LedgerDiagnosticCodes.CanonicalByteLimitExceeded));
        }

        var parseResult = LedgerParser.ParseAndValidate(canonical);
        if (parseResult.Failure is not null)
        {
            var fail = parseResult.Failure;
            // Only the four frozen causes may be wrapped as over-bound-append.
            if (fail.Code == LedgerDiagnosticCodes.InteractionLimitExceeded ||
                fail.Code == LedgerDiagnosticCodes.JsonPropertyCountExceeded ||
                fail.Code == LedgerDiagnosticCodes.JsonArrayLengthExceeded ||
                fail.Code == LedgerDiagnosticCodes.CanonicalByteLimitExceeded)
            {
                return new BuildOutcome(null, LedgerDiagnosticMessages.Of(
                    LedgerDiagnosticCodes.OverBoundAppend, fail.Code));
            }
            return new BuildOutcome(null, fail);
        }

        // Re-run the transition cross-check matrix so caller-supplied
        // ExpectedTransition disagreements surface as build failures rather
        // than being deferred to a later Validate* call.
        var transitionOutcome = transitionValidator(parseResult.Ledger!);
        if (transitionOutcome.Candidate is null)
        {
            return new BuildOutcome(null, transitionOutcome.Failure);
        }
        return new BuildOutcome(transitionOutcome.Candidate, null);
    }

    private static LedgerDiagnostic? PreflightContextSourceUnicode(
        ValidatedContextSource source, ExpectedIdentities identities, InteractionIdentity interaction)
    {
        foreach (var s in new[]
        {
            identities.Repository, identities.HeadRepository,
            identities.WorkflowIdentity, identities.TrustedExecutionDomain, identities.SessionEpoch,
            identities.ProviderId, identities.ModelId,
            identities.AdapterId, identities.TemplateId, identities.PolicyId,
            identities.ToolDefinitionId, identities.CacheConfigId,
            source.ReviewedHeadSha, source.ReviewedBaseSha,
            interaction.InteractionId,
        })
        {
            if (HasInvalidUnicode(s)) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);
        }
        foreach (var f in source.ChangedFiles)
        {
            if (f is null)
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
            if (HasInvalidUnicode(f.Path) || HasInvalidUnicode(f.Status))
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);
            if (f.PreviousPath is not null && HasInvalidUnicode(f.PreviousPath))
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);
            if (f.Patch is ValidatedPatchSource ps && HasInvalidUnicode(ps.Sha256))
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);
        }
        return null;
    }

    private static LedgerDiagnostic? PreflightOutcomeSourceUnicode(
        ValidatedOutcomeSource source, InteractionIdentity interaction)
    {
        if (HasInvalidUnicode(interaction.InteractionId))
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);
        if (HasInvalidUnicode(source.Summary))
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);
        foreach (var f in source.Findings)
        {
            if (f is null)
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
            if (HasInvalidUnicode(f.Severity) || HasInvalidUnicode(f.Confidence) ||
                HasInvalidUnicode(f.Category) || HasInvalidUnicode(f.Title) ||
                HasInvalidUnicode(f.Body))
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);
            if (f.Path is not null && HasInvalidUnicode(f.Path))
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);
            if (f.Evidence is not null && HasInvalidUnicode(f.Evidence))
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);
            if (f.SuggestedAction is not null && HasInvalidUnicode(f.SuggestedAction))
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);
            if (f.InlinePreference is not null && HasInvalidUnicode(f.InlinePreference))
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);
        }
        foreach (var l in source.Limitations)
        {
            if (l is null)
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
            if (HasInvalidUnicode(l)) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);
        }
        return null;
    }

    private static LedgerDiagnostic? ValidateIdentities(ExpectedIdentities i)
    {
        // Every caller-supplied identity string must be free of NUL and lone
        // surrogates before any downstream encoder can see it. Canonicalization
        // otherwise throws on lone surrogates.
        var allIdentityStrings = new[]
        {
            i.Repository, i.HeadRepository,
            i.WorkflowIdentity, i.TrustedExecutionDomain, i.SessionEpoch,
            i.ProviderId, i.ModelId,
            i.AdapterId, i.TemplateId, i.PolicyId, i.ToolDefinitionId, i.CacheConfigId,
        };
        foreach (var s in allIdentityStrings)
        {
            if (HasInvalidUnicode(s))
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode);
        }
        // Schema-first: character maxLength (256) is mapped to ledger_overlong_value
        // by the authoritative parser, so the projection path must do the same
        // before running semantic byte-length or control-character checks. This
        // keeps candidate/projection/parser classification identical.
        var identityStrings = new[] { i.WorkflowIdentity, i.TrustedExecutionDomain, i.SessionEpoch, i.ProviderId, i.ModelId };
        foreach (var s in identityStrings)
        {
            if (s.Length == 0)
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
            if (s.Length > 256)
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.OverlongValue);
        }
        foreach (var s in identityStrings)
        {
            if (Encoding.UTF8.GetByteCount(s) > LedgerLimits.MaxIdentityUtf8Bytes)
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.IdentityByteLengthExceeded);
            foreach (var ch in s)
            {
                if (ch < 0x20 || ch == 0x7F)
                    return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.ControlCharacterInIdentity);
            }
        }
        // Hex64 identity fields
        foreach (var s in new[] { i.AdapterId, i.TemplateId, i.PolicyId, i.ToolDefinitionId, i.CacheConfigId })
        {
            if (!IsHex64(s)) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
        }
        // Repository slug
        if (!IsRepositorySlug(i.Repository) || !IsRepositorySlug(i.HeadRepository))
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
        if (i.PullRequest < 1)
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
        return null;
    }

    private static bool IsHex64(string s) => s.Length == 64 && s.All(ch => (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'));
    private static bool IsSha1(string s) => s.Length == 40 && s.All(ch => (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'));
    private static bool IsSupportedStatus(string s) => s is "added" or "modified" or "removed" or "renamed" or "copied";
    private static bool IsValidResetReason(string s) => s is "base_changed" or "force_push" or "cache_contract_changed";
    private static bool IsValidRecoveryReason(string s) => s is
        "predecessor_unavailable" or "predecessor_integrity_failure" or
        "predecessor_unsafe_provenance" or "predecessor_expired" or
        "predecessor_over_bound" or "predecessor_incompatible_contract";

    private static bool IsRepositorySlug(string s)
    {
        if (s.Length < 3 || s.Length > 200) return false;
        var slash = s.IndexOf('/');
        if (slash <= 0 || slash == s.Length - 1) return false;
        if (s.IndexOf('/', slash + 1) >= 0) return false;
        static bool valid(char c) => char.IsAsciiLetterOrDigit(c) || c == '.' || c == '_' || c == '-' || c == '/';
        return s.All(valid);
    }

    private static bool IsSafeRelativePath(string p)
    {
        if (p.Length < 1 || p.Length > LedgerLimits.MaxSafeRelativePathChars) return false;
        // Schema \S pattern: whitespace-only paths are rejected before any
        // segment / scheme rule can pass by structural accident.
        if (!ContainsNonWhitespace(p)) return false;
        if (p[0] == '/') return false;
        if (p.Contains('\\')) return false;
        // Reject absolute URI scheme like "http:" or "file:".
        var colon = p.IndexOf(':');
        if (colon > 0)
        {
            var scheme = p.Substring(0, colon);
            if (scheme.All(c => char.IsAsciiLetterOrDigit(c) || c == '+' || c == '.' || c == '-')) return false;
        }
        // Reject . and .. segments
        foreach (var seg in p.Split('/'))
        {
            if (seg == "." || seg == "..") return false;
        }
        return true;
    }

    private static bool ContainsNonWhitespace(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (!char.IsWhiteSpace(s[i])) return true;
        }
        return false;
    }

    private static bool HasInvalidUnicode(string? s)
    {
        if (s is null) return true;
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch == '\0') return true;
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return true;
                i++;
            }
            else if (char.IsLowSurrogate(ch)) return true;
        }
        return false;
    }
}





