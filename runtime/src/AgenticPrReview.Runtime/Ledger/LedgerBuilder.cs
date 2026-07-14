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
        // Interaction identity
        if (!IsHex64(interaction.InteractionId) || interaction.InteractionOrdinal < 0)
            return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));

        if (source.Summary.Length > LedgerLimits.MaxSummaryChars)
            return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.OverlongValue));
        if (HasInvalidUnicode(source.Summary))
            return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode));

        if (source.Findings.Length > LedgerLimits.MaxFindingsPerOutcome)
            return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.FindingLimitExceeded));
        if (source.Limitations.Length > LedgerLimits.MaxLimitationsPerOutcome)
            return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.LimitationsLimitExceeded));

        var findings = ImmutableArray.CreateBuilder<LedgerFinding>();
        foreach (var f in source.Findings)
        {
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
            if (f2.Body.Length > LedgerLimits.MaxFindingBodyChars ||
                f2.Title.Length > LedgerLimits.MaxFindingTitleChars ||
                (f2.Evidence is not null && f2.Evidence.Length > LedgerLimits.MaxFindingEvidenceChars) ||
                (f2.SuggestedAction is not null && f2.SuggestedAction.Length > LedgerLimits.MaxFindingSuggestedActionChars) ||
                (f2.Path is not null && f2.Path.Length > LedgerLimits.MaxSafeRelativePathChars))
                return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.OverlongValue));
            var locFailure = LedgerSemanticChecks.ValidateFindingLocation(f2);
            if (locFailure is not null) return new ProjectionOutcome<ReviewOutcomeRecord>(null, locFailure);
            findings.Add(f2);
        }

        var lims = ImmutableArray.CreateBuilder<string>();
        foreach (var l in source.Limitations)
        {
            if (HasInvalidUnicode(l))
                return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InvalidUnicode));
            if (l.Length > LedgerLimits.MaxLimitationsItemChars)
                return new ProjectionOutcome<ReviewOutcomeRecord>(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.OverlongValue));
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
        // Basic expected/predecessor checks
        var predFailure = ContinuationSanityCheck(predecessor, expected);
        if (predFailure is not null) return new BuildOutcome(null, predFailure);

        var header = BuildHeader(expected.Identities, "continuation",
            stateGeneration: expected.StateGeneration,
            ledgerEpoch: expected.LedgerEpoch,
            predecessor: expected.PredecessorLedgerSha256,
            predecessorStateGeneration: expected.PredecessorStateGeneration);

        var records = ImmutableArray.CreateBuilder<LedgerRecord>();
        foreach (var r in predecessor.Model.Records) records.Add(r);
        records.Add(new LedgerRecord(context, null));
        records.Add(new LedgerRecord(null, outcome));
        return AssembleAndValidate(header, records.ToImmutable(),
            candidate => LedgerAppend.ValidateContinuation(predecessor, candidate, expected));
    }

    public static BuildOutcome CreateReset(ValidatedLedger predecessor, ResetTransition expected, ReviewContextRecord context, ReviewOutcomeRecord outcome)
    {
        if (!IsValidResetReason(expected.ResetReason))
            return new BuildOutcome(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.ResetReasonMissing));
        if (predecessor.ContentSha256 != expected.PredecessorLedgerSha256)
            return new BuildOutcome(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.PredecessorHashMismatch));
        if (predecessor.Model.Header.StateGeneration != expected.PredecessorStateGeneration)
            return new BuildOutcome(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.PredecessorGenerationMismatch));
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

    private static LedgerDiagnostic? ContinuationSanityCheck(ValidatedLedger predecessor, ContinuationTransition expected)
    {
        if (predecessor.ContentSha256 != expected.PredecessorLedgerSha256)
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.PredecessorHashMismatch);
        if (predecessor.Model.Header.StateGeneration != expected.PredecessorStateGeneration)
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.PredecessorGenerationMismatch);
        if (predecessor.Model.Header.LedgerEpoch != expected.LedgerEpoch)
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.LedgerEpochMismatch);
        return null;
    }

    private static LedgerHeader BuildHeader(
        ExpectedIdentities i, string kind,
        int stateGeneration, int ledgerEpoch, string predecessor,
        int? predecessorStateGeneration = null,
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

    private static BuildOutcome AssembleAndValidate(
        LedgerHeader header,
        ImmutableArray<LedgerRecord> records,
        Func<ValidatedLedger, TransitionOutcome> transitionValidator)
    {
        // Builder-level cause precedence (frozen order):
        //   1. interaction limit
        //   2. structural array/property caps (raised by canonical re-parse)
        //   3. canonical byte limit
        // Interaction limit is checked structurally before serialization.
        if (records.Length / 2 > LedgerLimits.MaxInteractionPairs)
        {
            return new BuildOutcome(null, LedgerDiagnosticMessages.Of(
                LedgerDiagnosticCodes.OverBoundAppend, LedgerDiagnosticCodes.InteractionLimitExceeded));
        }

        var model = new LedgerModel(1, 1, header, records);

        // Serialize to canonical bytes so we can produce a ValidatedLedger.
        var canonical = LedgerCanonicalizer.SerializeCanonical(model);

        // Full-pipeline re-validation via ParseAndValidate to catch any
        // manually-constructed records that bypass Build{Context,Outcome}
        // invariants. This must run before the canonical byte cap so that
        // property-count / array-length failures raised deep inside the
        // ledger surface as their specific causeCode rather than being
        // masked by a byte-limit rejection.
        var parseResult = LedgerParser.ParseAndValidate(canonical);
        if (parseResult.Failure is not null)
        {
            var fail = parseResult.Failure;
            if (fail.Code == LedgerDiagnosticCodes.JsonPropertyCountExceeded ||
                fail.Code == LedgerDiagnosticCodes.JsonArrayLengthExceeded ||
                fail.Code == LedgerDiagnosticCodes.InteractionLimitExceeded ||
                fail.Code == LedgerDiagnosticCodes.ChangedFileLimitExceeded ||
                fail.Code == LedgerDiagnosticCodes.FindingLimitExceeded ||
                fail.Code == LedgerDiagnosticCodes.LimitationsLimitExceeded ||
                fail.Code == LedgerDiagnosticCodes.CanonicalByteLimitExceeded ||
                fail.Code == LedgerDiagnosticCodes.RawByteLimitExceeded)
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
        // UTF-8 byte length and control-character checks (schema handles char maxLength and patterns).
        var identityStrings = new[] { i.WorkflowIdentity, i.TrustedExecutionDomain, i.SessionEpoch, i.ProviderId, i.ModelId };
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

    private static bool HasInvalidUnicode(string s)
    {
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





