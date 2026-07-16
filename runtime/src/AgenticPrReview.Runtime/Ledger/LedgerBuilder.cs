using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgenticPrReview.Runtime.Ledger;

/// <summary>
/// Pure projection and candidate assembly. Consumes already-validated
/// <see cref="ValidatedContextSource"/> / <see cref="ValidatedOutcomeSource"/>
/// DTOs and produces immutable ledger records or full validated ledger
/// candidates. Every entry point emits diagnostics from exactly one stage
/// under the 5-step pipeline documented in Issue #49 section 10:
///   (1) Unicode pre-scan
///   (2) Schema replay (accumulates)
///   (3) Structural bounds replay (3a interaction, 3b canonical-byte,
///       3c identity UTF-8, 3d identity control-char)
///   (4) Semantic-invariants replay
///   (5) Full-ledger reparse (defensive)
/// Only <c>ledger_interaction_limit_exceeded</c> and
/// <c>ledger_canonical_byte_limit_exceeded</c> may surface as composite
/// <c>ledger_over_bound_append</c> causes; per-record schema limits remain
/// schema-stage codes.
/// </summary>
public static class LedgerBuilder
{
    private const int SchemaVersion = 1;
    private const int PrefixContractVersion = 1;
    private static readonly Regex Sha256HexRegex = new(@"^[a-f0-9]{64}$", RegexOptions.Compiled);
    private static readonly Regex GitShaRegex = new(@"^([a-f0-9]{40}|[a-f0-9]{64})$", RegexOptions.Compiled);
    private static readonly Regex EpochIdRegex = new(@"^[A-Za-z0-9_-]{22}$", RegexOptions.Compiled);
    private static readonly HashSet<string> AllowedFileStatus = new(StringComparer.Ordinal)
    {
        "added", "removed", "modified", "renamed", "copied", "changed", "unchanged",
    };

    // -----------------------------------------------------------------
    // Public: BuildReviewContext

    public static BuildOutcome<ReviewContextRecord> BuildReviewContext(
        ValidatedContextSource source, ExpectedIdentities identities, InteractionIdentity interaction)
    {
        if (source is null || identities is null || interaction is null)
            return NewBuildFail<ReviewContextRecord>(LedgerDiagnosticCodes.SchemaViolation);
        if (source.ChangedFiles.IsDefault)
            return NewBuildFail<ReviewContextRecord>(LedgerDiagnosticCodes.SchemaViolation);

        // Step 1: Unicode pre-scan over identity strings + interactionId + source strings.
        var unicodeFailure = UnicodeScanContextSource(source, identities, interaction);
        if (unicodeFailure is not null) return NewBuildFail<ReviewContextRecord>(unicodeFailure);

        // Step 2: Schema replay (accumulates on the provisional record).
        var schemaDiags = ValidateContextSourceSchema(source, identities, interaction);
        if (!schemaDiags.IsEmpty) return new BuildOutcome<ReviewContextRecord>(null, schemaDiags);

        // Step 4: Semantic invariants specific to the built record (finding-location subset skipped;
        //         the outcome record path exercises those).
        // Step 3: Structural bounds specific to a single record — no candidate-level cap applies here
        //         because a single record can't exceed the ledger-wide canonical-byte cap on its own.

        var cacheContractDigest = LedgerDigests.ComputeCacheContractDigest(identities);
        var record = new ReviewContextRecord
        {
            Role = "review_context",
            InteractionId = interaction.InteractionId,
            InteractionOrdinal = interaction.InteractionOrdinal,
            SubjectDigest = source.SubjectDigest,
            CacheContractDigest = cacheContractDigest,
            ReviewedHeadSha = source.ReviewedHeadSha,
            ReviewedBaseSha = source.ReviewedBaseSha,
            ChangedFiles = source.ChangedFiles,
        };
        return new BuildOutcome<ReviewContextRecord>(record, ImmutableArray<LedgerDiagnostic>.Empty);
    }

    // -----------------------------------------------------------------
    // Public: BuildReviewOutcome

    public static BuildOutcome<ReviewOutcomeRecord> BuildReviewOutcome(
        ValidatedOutcomeSource source, InteractionIdentity interaction)
    {
        if (source is null || interaction is null)
            return NewBuildFail<ReviewOutcomeRecord>(LedgerDiagnosticCodes.SchemaViolation);
        if (source.Findings.IsDefault || source.Limitations.IsDefault)
            return NewBuildFail<ReviewOutcomeRecord>(LedgerDiagnosticCodes.SchemaViolation);

        // Step 1: Unicode pre-scan.
        var unicodeFailure = UnicodeScanOutcomeSource(source, interaction);
        if (unicodeFailure is not null) return NewBuildFail<ReviewOutcomeRecord>(unicodeFailure);

        // Step 2: Schema replay.
        var schemaDiags = ValidateOutcomeSourceSchema(source, interaction);
        if (!schemaDiags.IsEmpty) return new BuildOutcome<ReviewOutcomeRecord>(null, schemaDiags);

        // Step 4: Semantic finding-location invariants.
        foreach (var f in source.Findings)
        {
            var loc = LedgerSemanticChecks.ValidateFindingLocation(f);
            if (loc is not null) return NewBuildFail<ReviewOutcomeRecord>(loc);
        }

        var record = new ReviewOutcomeRecord
        {
            Role = "review_outcome",
            InteractionId = interaction.InteractionId,
            InteractionOrdinal = interaction.InteractionOrdinal,
            Summary = source.Summary,
            Findings = source.Findings,
            Limitations = source.Limitations,
        };
        return new BuildOutcome<ReviewOutcomeRecord>(record, ImmutableArray<LedgerDiagnostic>.Empty);
    }

    // -----------------------------------------------------------------
    // Public: Create* / AppendContinuation

    public static CandidateOutcome CreateBootstrap(
        BootstrapTransition expected,
        ValidatedContextSource contextSource,
        InteractionIdentity contextInteraction,
        ValidatedOutcomeSource outcomeSource,
        InteractionIdentity outcomeInteraction)
    {
        return AssembleRoot(expected, "bootstrap", predecessorLedgerSha256: "bootstrap",
            contextSource, contextInteraction, outcomeSource, outcomeInteraction,
            recoveryReason: null);
    }

    public static CandidateOutcome CreateRecoveryRoot(
        RecoveryRootTransition expected,
        ValidatedContextSource contextSource,
        InteractionIdentity contextInteraction,
        ValidatedOutcomeSource outcomeSource,
        InteractionIdentity outcomeInteraction)
    {
        return AssembleRoot(expected, "recovery_root", predecessorLedgerSha256: "bootstrap",
            contextSource, contextInteraction, outcomeSource, outcomeInteraction,
            recoveryReason: expected.RecoveryReason);
    }

    public static CandidateOutcome CreateReset(
        ResetTransition expected,
        ValidatedLedger predecessor,
        ValidatedContextSource contextSource,
        InteractionIdentity contextInteraction,
        ValidatedOutcomeSource outcomeSource,
        InteractionIdentity outcomeInteraction)
    {
        if (expected is null || predecessor is null || contextSource is null || outcomeSource is null
            || contextInteraction is null || outcomeInteraction is null)
            return NewCandidateFail(LedgerDiagnosticCodes.SchemaViolation);

        var header = BuildHeader(expected, "reset",
            predecessorLedgerSha256: predecessor.ContentSha256,
            predecessorLedgerEpoch: predecessor.PrivateModel.Header.LedgerEpoch,
            predecessorStateGeneration: predecessor.PrivateModel.Header.StateGeneration,
            predecessorManifestSha256: expected.PredecessorManifestSha256,
            resetReason: expected.ResetReason,
            recoveryReason: null);
        return AssembleCandidate(header, ImmutableArray<LedgerRecord>.Empty,
            contextSource, contextInteraction, outcomeSource, outcomeInteraction);
    }

    public static CandidateOutcome AppendContinuation(
        ContinuationTransition expected,
        ValidatedLedger predecessor,
        ValidatedContextSource contextSource,
        InteractionIdentity contextInteraction,
        ValidatedOutcomeSource outcomeSource,
        InteractionIdentity outcomeInteraction)
    {
        if (expected is null || predecessor is null || contextSource is null || outcomeSource is null
            || contextInteraction is null || outcomeInteraction is null)
            return NewCandidateFail(LedgerDiagnosticCodes.SchemaViolation);

        var header = BuildHeader(expected, "continuation",
            predecessorLedgerSha256: predecessor.ContentSha256,
            predecessorLedgerEpoch: predecessor.PrivateModel.Header.LedgerEpoch,
            predecessorStateGeneration: predecessor.PrivateModel.Header.StateGeneration,
            predecessorManifestSha256: null,
            resetReason: null,
            recoveryReason: null);
        return AssembleCandidate(header, predecessor.PrivateModel.Records,
            contextSource, contextInteraction, outcomeSource, outcomeInteraction);
    }

    // -----------------------------------------------------------------
    // Root assembly (bootstrap / recovery_root)

    private static CandidateOutcome AssembleRoot(
        ExpectedTransition expected, string kind, string predecessorLedgerSha256,
        ValidatedContextSource contextSource, InteractionIdentity contextInteraction,
        ValidatedOutcomeSource outcomeSource, InteractionIdentity outcomeInteraction,
        string? recoveryReason)
    {
        if (expected is null || contextSource is null || outcomeSource is null
            || contextInteraction is null || outcomeInteraction is null)
            return NewCandidateFail(LedgerDiagnosticCodes.SchemaViolation);

        var header = BuildHeader(expected, kind,
            predecessorLedgerSha256: predecessorLedgerSha256,
            predecessorLedgerEpoch: null,
            predecessorStateGeneration: null,
            predecessorManifestSha256: null,
            resetReason: null,
            recoveryReason: recoveryReason);
        return AssembleCandidate(header, ImmutableArray<LedgerRecord>.Empty,
            contextSource, contextInteraction, outcomeSource, outcomeInteraction);
    }

    private static LedgerHeader BuildHeader(ExpectedTransition expected, string kind,
        string predecessorLedgerSha256,
        string? predecessorLedgerEpoch,
        long? predecessorStateGeneration,
        string? predecessorManifestSha256,
        string? resetReason,
        string? recoveryReason)
    {
        return new LedgerHeader
        {
            Kind = kind,
            SessionEpoch = expected.SessionEpoch,
            LedgerEpoch = expected.GetLedgerEpoch(),
            StateGeneration = expected.GetStateGeneration(),
            PredecessorLedgerSha256 = predecessorLedgerSha256,
            PredecessorLedgerEpoch = predecessorLedgerEpoch,
            PredecessorStateGeneration = predecessorStateGeneration,
            PredecessorManifestSha256 = predecessorManifestSha256,
            ResetReason = resetReason,
            RecoveryReason = recoveryReason,
            Repository = expected.Identities.Repository,
            HeadRepository = expected.Identities.HeadRepository,
            PullRequest = expected.Identities.PullRequest,
            WorkflowIdentity = expected.Identities.WorkflowIdentity,
            TrustedExecutionDomain = expected.Identities.TrustedExecutionDomain,
            ProviderId = expected.Identities.ProviderId,
            ModelId = expected.Identities.ModelId,
            AdapterId = expected.Identities.AdapterId,
            TemplateId = expected.Identities.TemplateId,
            PolicyId = expected.Identities.PolicyId,
            ToolDefinitionId = expected.Identities.ToolDefinitionId,
            CacheConfigId = expected.Identities.CacheConfigId,
        };
    }

    /// <summary>
    /// Common assembly path shared by root, reset, and continuation. Runs the
    /// 5-step pipeline in order and mints a <see cref="ValidatedLedger"/> only
    /// after step 5 succeeds.
    /// </summary>
    private static CandidateOutcome AssembleCandidate(
        LedgerHeader header,
        ImmutableArray<LedgerRecord> predecessorRecords,
        ValidatedContextSource contextSource, InteractionIdentity contextInteraction,
        ValidatedOutcomeSource outcomeSource, InteractionIdentity outcomeInteraction)
    {
        // Step 1: Unicode pre-scan over the full source graph (identities via header, records via sources).
        var identitiesFromHeader = new ExpectedIdentities(
            Repository: header.Repository,
            HeadRepository: header.HeadRepository,
            PullRequest: header.PullRequest,
            WorkflowIdentity: header.WorkflowIdentity,
            TrustedExecutionDomain: header.TrustedExecutionDomain,
            ProviderId: header.ProviderId,
            ModelId: header.ModelId,
            AdapterId: header.AdapterId,
            TemplateId: header.TemplateId,
            PolicyId: header.PolicyId,
            ToolDefinitionId: header.ToolDefinitionId,
            CacheConfigId: header.CacheConfigId);

        var ctxUnicode = UnicodeScanContextSource(contextSource, identitiesFromHeader, contextInteraction);
        if (ctxUnicode is not null) return NewCandidateFail(ctxUnicode);
        var ocUnicode = UnicodeScanOutcomeSource(outcomeSource, outcomeInteraction);
        if (ocUnicode is not null) return NewCandidateFail(ocUnicode);

        // Step 2: Schema replay on header + each record.
        var schemaDiags = ImmutableArray.CreateBuilder<LedgerDiagnostic>();
        // Header validation is delegated to BuildHeader-shape guarantees, plus a light identity check.
        var idDiag = ValidateIdentitiesShape(identitiesFromHeader, header);
        if (idDiag is not null) schemaDiags.Add(idDiag);
        schemaDiags.AddRange(ValidateContextSourceSchema(contextSource, identitiesFromHeader, contextInteraction));
        schemaDiags.AddRange(ValidateOutcomeSourceSchema(outcomeSource, outcomeInteraction));
        if (schemaDiags.Count > 0) return new CandidateOutcome(null, schemaDiags.ToImmutable());

        // Build the new pair of records.
        var cacheContractDigest = LedgerDigests.ComputeCacheContractDigest(identitiesFromHeader);
        var newContext = new ReviewContextRecord
        {
            Role = "review_context",
            InteractionId = contextInteraction.InteractionId,
            InteractionOrdinal = contextInteraction.InteractionOrdinal,
            SubjectDigest = contextSource.SubjectDigest,
            CacheContractDigest = cacheContractDigest,
            ReviewedHeadSha = contextSource.ReviewedHeadSha,
            ReviewedBaseSha = contextSource.ReviewedBaseSha,
            ChangedFiles = contextSource.ChangedFiles,
        };
        var newOutcome = new ReviewOutcomeRecord
        {
            Role = "review_outcome",
            InteractionId = outcomeInteraction.InteractionId,
            InteractionOrdinal = outcomeInteraction.InteractionOrdinal,
            Summary = outcomeSource.Summary,
            Findings = outcomeSource.Findings,
            Limitations = outcomeSource.Limitations,
        };

        var recordsBuilder = ImmutableArray.CreateBuilder<LedgerRecord>(predecessorRecords.Length + 2);
        recordsBuilder.AddRange(predecessorRecords);
        recordsBuilder.Add(newContext);
        recordsBuilder.Add(newOutcome);
        var records = recordsBuilder.ToImmutable();

        var model = new LedgerModel
        {
            SchemaVersion = SchemaVersion,
            PrefixContractVersion = PrefixContractVersion,
            Header = header,
            Records = records,
        };

        // Step 3: Structural-bounds replay (candidate-level).
        //   3a interaction limit  -> composite ledger_over_bound_append (ledger_interaction_limit_exceeded)
        //   3b canonical-byte cap -> composite ledger_over_bound_append (ledger_canonical_byte_limit_exceeded)
        //   3c identity byte cap  -> direct code ledger_identity_byte_length_exceeded
        //   3d identity control   -> direct code ledger_control_character_in_identity
        if (records.Length / 2 > LedgerLimits.MaxInteractionPairs)
        {
            return new CandidateOutcome(null, ImmutableArray.Create(
                new LedgerDiagnostic
                {
                    Code = LedgerDiagnosticCodes.OverBoundAppend,
                    Message = LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.OverBoundAppend).Message,
                    CauseCode = LedgerDiagnosticCodes.InteractionLimitExceeded,
                }));
        }

        var canonicalIm = LedgerCanonicalizer.SerializeCanonical(model);
        var canonicalBytes = canonicalIm.ToArray();
        if (canonicalBytes.Length > LedgerLimits.MaxCanonicalBytes)
        {
            return new CandidateOutcome(null, ImmutableArray.Create(
                new LedgerDiagnostic
                {
                    Code = LedgerDiagnosticCodes.OverBoundAppend,
                    Message = LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.OverBoundAppend).Message,
                    CauseCode = LedgerDiagnosticCodes.CanonicalByteLimitExceeded,
                }));
        }

        var identityBoundsFailure = LedgerSemanticChecks.CheckIdentityBounds(model);
        if (identityBoundsFailure is not null) return new CandidateOutcome(null, ImmutableArray.Create(identityBoundsFailure));

        // Step 4: Semantic-invariants replay.
        var semanticFailure = LedgerSemanticChecks.CheckSemanticInvariants(model);
        if (semanticFailure is not null) return new CandidateOutcome(null, ImmutableArray.Create(semanticFailure));

        // Step 5: Defensive full-ledger reparse through the same LedgerParser pipeline.
        var reparseOutcome = LedgerParser.ParseAndValidate(canonicalBytes);
        if (reparseOutcome.Ledger is null)
            return new CandidateOutcome(null, reparseOutcome.Diagnostics);

        return new CandidateOutcome(reparseOutcome.Ledger, ImmutableArray<LedgerDiagnostic>.Empty);
    }

    // -----------------------------------------------------------------
    // Step 1 helpers: Unicode pre-scan

    private static LedgerDiagnostic? UnicodeScanContextSource(
        ValidatedContextSource source, ExpectedIdentities identities, InteractionIdentity interaction)
    {
        var identityStrings = new (string Name, string Value)[]
        {
            ("workflowIdentity", identities.WorkflowIdentity),
            ("trustedExecutionDomain", identities.TrustedExecutionDomain),
            ("providerId", identities.ProviderId),
            ("modelId", identities.ModelId),
            ("adapterId", identities.AdapterId),
            ("templateId", identities.TemplateId),
            ("policyId", identities.PolicyId),
            ("toolDefinitionId", identities.ToolDefinitionId),
            ("cacheConfigId", identities.CacheConfigId),
            ("repository", identities.Repository),
            ("headRepository", identities.HeadRepository),
        };
        foreach (var (name, value) in identityStrings)
        {
            if (value is null || HasInvalidUnicode(value))
                return LedgerDiagnosticMessages.Of(
                    LedgerDiagnosticCodes.InvalidUnicode,
                    LedgerSafePath.Encode(new[] { "header", name }, "ledger_invalid_unicode:"));
        }
        if (interaction.InteractionId is null || HasInvalidUnicode(interaction.InteractionId))
            return LedgerDiagnosticMessages.Of(
                LedgerDiagnosticCodes.InvalidUnicode,
                LedgerSafePath.Encode(new[] { "records", (2 * interaction.InteractionOrdinal).ToString(System.Globalization.CultureInfo.InvariantCulture), "interactionId" }, "ledger_invalid_unicode:"));

        // Context-source strings
        if (source.SubjectDigest is null || HasInvalidUnicode(source.SubjectDigest))
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode, "/records/subjectDigest");
        if (source.ReviewedHeadSha is null || HasInvalidUnicode(source.ReviewedHeadSha))
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode, "/records/reviewedHeadSha");
        if (source.ReviewedBaseSha is null || HasInvalidUnicode(source.ReviewedBaseSha))
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode, "/records/reviewedBaseSha");
        foreach (var cf in source.ChangedFiles)
        {
            if (cf.Path is null || HasInvalidUnicode(cf.Path))
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode, "/records/changedFiles/path");
            if (cf.PreviousPath is not null && HasInvalidUnicode(cf.PreviousPath))
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode, "/records/changedFiles/previousPath");
            if (cf.Status is null || HasInvalidUnicode(cf.Status))
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode, "/records/changedFiles/status");
            if (cf.Patch is not null && (cf.Patch.Sha256 is null || HasInvalidUnicode(cf.Patch.Sha256)))
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode, "/records/changedFiles/patch/sha256");
        }
        return null;
    }

    private static LedgerDiagnostic? UnicodeScanOutcomeSource(
        ValidatedOutcomeSource source, InteractionIdentity interaction)
    {
        if (interaction.InteractionId is null || HasInvalidUnicode(interaction.InteractionId))
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode, "/records/interactionId");
        if (source.Summary is null || HasInvalidUnicode(source.Summary))
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode, "/records/summary");
        foreach (var lim in source.Limitations)
        {
            if (lim is null || HasInvalidUnicode(lim))
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode, "/records/limitations");
        }
        foreach (var f in source.Findings)
        {
            if (f.Severity is null || HasInvalidUnicode(f.Severity)) return U("severity");
            if (f.Confidence is null || HasInvalidUnicode(f.Confidence)) return U("confidence");
            if (f.Category is null || HasInvalidUnicode(f.Category)) return U("category");
            if (f.Title is null || HasInvalidUnicode(f.Title)) return U("title");
            if (f.Body is null || HasInvalidUnicode(f.Body)) return U("body");
            if (f.Path is not null && HasInvalidUnicode(f.Path)) return U("path");
            if (f.Evidence is not null && HasInvalidUnicode(f.Evidence)) return U("evidence");
            if (f.SuggestedAction is not null && HasInvalidUnicode(f.SuggestedAction)) return U("suggestedAction");
            if (f.InlinePreference is not null && HasInvalidUnicode(f.InlinePreference)) return U("inlinePreference");
        }
        return null;

        static LedgerDiagnostic U(string field)
            => LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode, "/records/findings/" + field);
    }

    private static bool HasInvalidUnicode(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch == '\u0000') return true;
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return true;
                i++;
                continue;
            }
            if (char.IsLowSurrogate(ch)) return true;
        }
        return false;
    }

    // -----------------------------------------------------------------
    // Step 2 helpers: schema-shape validation (accumulates)

    private static ImmutableArray<LedgerDiagnostic> ValidateContextSourceSchema(
        ValidatedContextSource source, ExpectedIdentities identities, InteractionIdentity interaction)
    {
        var diags = ImmutableArray.CreateBuilder<LedgerDiagnostic>();

        if (source.SubjectDigest is null || !Sha256HexRegex.IsMatch(source.SubjectDigest))
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
        if (source.ReviewedHeadSha is null || !GitShaRegex.IsMatch(source.ReviewedHeadSha))
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
        if (source.ReviewedBaseSha is null || !GitShaRegex.IsMatch(source.ReviewedBaseSha))
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
        if (interaction.InteractionId is null || !Sha256HexRegex.IsMatch(interaction.InteractionId))
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
        if (interaction.InteractionOrdinal < 0)
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
        if (source.ChangedFiles.Length > LedgerLimits.MaxChangedFilesPerContext)
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.ChangedFileLimitExceeded));

        foreach (var cf in source.ChangedFiles)
        {
            if (!AllowedFileStatus.Contains(cf.Status))
                diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.UnsupportedChangeStatus));
            if (cf.Additions < 0 || cf.Additions > LedgerLimits.MaxIntegerValue)
                diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
            if (cf.Deletions < 0 || cf.Deletions > LedgerLimits.MaxIntegerValue)
                diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
            if (cf.Changes < 0 || cf.Changes > LedgerLimits.MaxIntegerValue)
                diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
            if (cf.Patch is not null)
            {
                if (cf.Patch.Sha256 is null || !Sha256HexRegex.IsMatch(cf.Patch.Sha256))
                    diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
                if (cf.Patch.MaxChars < 0 || cf.Patch.MaxChars > LedgerLimits.MaxIntegerValue)
                    diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
            }
        }
        return diags.ToImmutable();
    }

    private static ImmutableArray<LedgerDiagnostic> ValidateOutcomeSourceSchema(
        ValidatedOutcomeSource source, InteractionIdentity interaction)
    {
        var diags = ImmutableArray.CreateBuilder<LedgerDiagnostic>();
        if (interaction.InteractionId is null || !Sha256HexRegex.IsMatch(interaction.InteractionId))
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
        if (interaction.InteractionOrdinal < 0)
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
        if (source.Summary is null)
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
        else if (LedgerLimits.SchemaStringLength(source.Summary) > LedgerLimits.MaxSummaryChars)
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.OverlongValue));
        if (source.Findings.Length > LedgerLimits.MaxFindingsPerOutcome)
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.FindingLimitExceeded));
        if (source.Limitations.Length > LedgerLimits.MaxLimitationsPerOutcome)
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.LimitationsLimitExceeded));
        return diags.ToImmutable();
    }

    private static LedgerDiagnostic? ValidateIdentitiesShape(ExpectedIdentities identities, LedgerHeader header)
    {
        // sessionEpoch / ledgerEpoch validated as EpochId strings.
        if (header.SessionEpoch is null || !EpochIdRegex.IsMatch(header.SessionEpoch))
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
        if (header.LedgerEpoch is null || !EpochIdRegex.IsMatch(header.LedgerEpoch))
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
        // Cache-contract IDs are Sha256Hex.
        if (!Sha256HexRegex.IsMatch(identities.AdapterId)
            || !Sha256HexRegex.IsMatch(identities.TemplateId)
            || !Sha256HexRegex.IsMatch(identities.PolicyId)
            || !Sha256HexRegex.IsMatch(identities.ToolDefinitionId)
            || !Sha256HexRegex.IsMatch(identities.CacheConfigId))
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
        return null;
    }

    // -----------------------------------------------------------------
    // Failure helpers

    private static BuildOutcome<T> NewBuildFail<T>(LedgerDiagnostic d) where T : class
        => new(null, ImmutableArray.Create(d));

    private static BuildOutcome<T> NewBuildFail<T>(string code) where T : class
        => new(null, ImmutableArray.Create(LedgerDiagnosticMessages.Of(code)));

    private static CandidateOutcome NewCandidateFail(LedgerDiagnostic d)
        => new(null, ImmutableArray.Create(d));

    private static CandidateOutcome NewCandidateFail(string code)
        => new(null, ImmutableArray.Create(LedgerDiagnosticMessages.Of(code)));
}
