using System.Collections.Immutable;

namespace AgenticPrReview.Runtime.Ledger;

/// <summary>
/// Validates a candidate ledger against a predecessor (when applicable) and a
/// caller-supplied <see cref="ExpectedTransition"/>. The candidate is always a
/// pre-parsed <see cref="ValidatedLedger"/>, so it has already passed schema,
/// bounds, semantic, and canonical checks. This method enforces the
/// cross-check matrix between predecessor, candidate, and expected values.
/// </summary>
public static class LedgerAppend
{
    public static TransitionOutcome ValidateBootstrap(ValidatedLedger candidate, BootstrapTransition expected)
    {
        if (candidate.PrivateModel.Header.Kind != "bootstrap")
            return Fail(LedgerDiagnosticCodes.TransitionKindMismatch);

        var common = CommonCrossChecks(candidate, expected);
        if (common is not null) return new TransitionOutcome(null, common);

        // Records: exactly one pair, ordinal 0.
        if (candidate.PrivateModel.Records.Length != 2)
            return Fail(LedgerDiagnosticCodes.BootstrapShapeViolation);
        if (candidate.PrivateModel.Records[0].InteractionOrdinal != 0)
            return Fail(LedgerDiagnosticCodes.BootstrapShapeViolation);
        return new TransitionOutcome(candidate, null);
    }

    public static TransitionOutcome ValidateRecovery(ValidatedLedger candidate, RecoveryTransition expected)
    {
        if (candidate.PrivateModel.Header.Kind != "recovery")
            return Fail(LedgerDiagnosticCodes.TransitionKindMismatch);

        var common = CommonCrossChecks(candidate, expected);
        if (common is not null) return new TransitionOutcome(null, common);

        if (candidate.PrivateModel.Header.RecoveryReason != expected.RecoveryReason)
            return Fail(LedgerDiagnosticCodes.RecoveryReasonMismatch);
        if (candidate.PrivateModel.Records.Length != 2)
            return Fail(LedgerDiagnosticCodes.RecoveryShapeViolation);
        if (candidate.PrivateModel.Records[0].InteractionOrdinal != 0)
            return Fail(LedgerDiagnosticCodes.RecoveryShapeViolation);
        return new TransitionOutcome(candidate, null);
    }

    public static TransitionOutcome ValidateContinuation(ValidatedLedger predecessor, ValidatedLedger candidate, ContinuationTransition expected)
    {
        if (candidate.PrivateModel.Header.Kind != "continuation")
            return Fail(LedgerDiagnosticCodes.TransitionKindMismatch);

        // Predecessor identities match expected.
        if (!IdentitiesEqual(predecessor.PrivateModel.Header, expected.Identities))
            return Fail(LedgerDiagnosticCodes.IdentityMismatch);

        var common = CommonCrossChecks(candidate, expected);
        if (common is not null) return new TransitionOutcome(null, common);

        if (expected.PredecessorLedgerSha256 != predecessor.ContentSha256)
            return Fail(LedgerDiagnosticCodes.PredecessorHashMismatch);
        if (candidate.PrivateModel.Header.PredecessorLedgerSha256 != predecessor.ContentSha256)
            return Fail(LedgerDiagnosticCodes.PredecessorHashMismatch);
        if (expected.PredecessorStateGeneration != predecessor.PrivateModel.Header.StateGeneration)
            return Fail(LedgerDiagnosticCodes.PredecessorGenerationMismatch);
        if (candidate.PrivateModel.Header.PredecessorStateGeneration != predecessor.PrivateModel.Header.StateGeneration)
            return Fail(LedgerDiagnosticCodes.PredecessorGenerationMismatch);
        if (candidate.PrivateModel.Header.LedgerEpoch != predecessor.PrivateModel.Header.LedgerEpoch)
            return Fail(LedgerDiagnosticCodes.LedgerEpochMismatch);

        // Records prefix element-for-element (structural).
        var predRecs = predecessor.PrivateModel.Records;
        var candRecs = candidate.PrivateModel.Records;
        if (candRecs.Length != predRecs.Length + 2)
            return Fail(LedgerDiagnosticCodes.ContinuationPrefixMismatch);
        for (var i = 0; i < predRecs.Length; i++)
        {
            if (!RecordEquals(predRecs[i], candRecs[i]))
                return Fail(LedgerDiagnosticCodes.ContinuationPrefixMismatch);
        }
        // Tail pair ordinal continuity.
        var lastOrdinal = predRecs.Length == 0 ? -1 : predRecs[^1].InteractionOrdinal;
        var tailOrdinal = candRecs[^1].InteractionOrdinal;
        if (tailOrdinal != lastOrdinal + 1)
            return Fail(LedgerDiagnosticCodes.OrdinalGap);
        return new TransitionOutcome(candidate, null);
    }

    public static TransitionOutcome ValidateReset(ValidatedLedger predecessor, ValidatedLedger candidate, ResetTransition expected)
    {
        if (candidate.PrivateModel.Header.Kind != "reset")
            return Fail(LedgerDiagnosticCodes.TransitionKindMismatch);

        // Predecessor session-scope must match expected/current.
        if (!SessionScopeEqual(predecessor.PrivateModel.Header, expected.Identities))
            return Fail(LedgerDiagnosticCodes.IdentityMismatch);
        // Candidate identities must equal expected.
        if (!IdentitiesEqual(candidate.PrivateModel.Header, expected.Identities))
            return Fail(LedgerDiagnosticCodes.IdentityMismatch);
        if (candidate.PrivateModel.Header.StateGeneration != expected.StateGeneration)
            return Fail(LedgerDiagnosticCodes.StateGenerationMismatch);
        if (candidate.PrivateModel.Header.LedgerEpoch != expected.LedgerEpoch)
            return Fail(LedgerDiagnosticCodes.LedgerEpochMismatch);

        if (expected.PredecessorLedgerSha256 != predecessor.ContentSha256 ||
            candidate.PrivateModel.Header.PredecessorLedgerSha256 != predecessor.ContentSha256)
            return Fail(LedgerDiagnosticCodes.PredecessorHashMismatch);
        if (expected.PredecessorStateGeneration != predecessor.PrivateModel.Header.StateGeneration ||
            candidate.PrivateModel.Header.PredecessorStateGeneration != predecessor.PrivateModel.Header.StateGeneration)
            return Fail(LedgerDiagnosticCodes.PredecessorGenerationMismatch);
        if (candidate.PrivateModel.Header.PredecessorManifestSha256 != expected.PredecessorManifestSha256)
            return Fail(LedgerDiagnosticCodes.PredecessorManifestHashMismatch);
        if (candidate.PrivateModel.Header.LedgerEpoch == predecessor.PrivateModel.Header.LedgerEpoch)
            return Fail(LedgerDiagnosticCodes.ResetEpochNotFresh);
        if (candidate.PrivateModel.Header.ResetReason != expected.ResetReason)
            return Fail(LedgerDiagnosticCodes.ResetReasonMismatch);
        if (candidate.PrivateModel.Records.Length != 2)
            return Fail(LedgerDiagnosticCodes.ResetRecordsShapeMismatch);
        if (candidate.PrivateModel.Records[0].InteractionOrdinal != 0)
            return Fail(LedgerDiagnosticCodes.ResetRecordsShapeMismatch);
        return new TransitionOutcome(candidate, null);
    }

    // -----------------------------------------------------------------
    // Common cross-check for candidate.header vs expected

    private static LedgerDiagnostic? CommonCrossChecks(ValidatedLedger candidate, ExpectedTransition expected)
    {
        if (!IdentitiesEqual(candidate.PrivateModel.Header, expected.Identities))
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.IdentityMismatch);
        if (candidate.PrivateModel.Header.StateGeneration != expected.GetStateGeneration())
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.StateGenerationMismatch);
        if (candidate.PrivateModel.Header.LedgerEpoch != expected.GetLedgerEpoch())
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.LedgerEpochMismatch);
        return null;
    }

    private static bool IdentitiesEqual(LedgerHeader h, ExpectedIdentities e)
    {
        return h.Repository == e.Repository &&
               h.HeadRepository == e.HeadRepository &&
               h.PullRequest == e.PullRequest &&
               h.WorkflowIdentity == e.WorkflowIdentity &&
               h.TrustedExecutionDomain == e.TrustedExecutionDomain &&
               h.SessionEpoch == e.SessionEpoch &&
               h.ProviderId == e.ProviderId &&
               h.ModelId == e.ModelId &&
               h.AdapterId == e.AdapterId &&
               h.TemplateId == e.TemplateId &&
               h.PolicyId == e.PolicyId &&
               h.ToolDefinitionId == e.ToolDefinitionId &&
               h.CacheConfigId == e.CacheConfigId;
    }

    private static bool SessionScopeEqual(LedgerHeader h, ExpectedIdentities e)
    {
        return h.Repository == e.Repository &&
               h.HeadRepository == e.HeadRepository &&
               h.PullRequest == e.PullRequest &&
               h.WorkflowIdentity == e.WorkflowIdentity &&
               h.TrustedExecutionDomain == e.TrustedExecutionDomain &&
               h.SessionEpoch == e.SessionEpoch;
    }

    private static bool RecordEquals(LedgerRecord a, LedgerRecord b)
    {
        if (a.Role != b.Role) return false;
        if (a.InteractionId != b.InteractionId || a.InteractionOrdinal != b.InteractionOrdinal) return false;
        if (a.Context is not null) return ContextEquals(a.Context, b.Context!);
        return OutcomeEquals(a.Outcome!, b.Outcome!);
    }

    private static bool ContextEquals(ReviewContextRecord a, ReviewContextRecord b)
    {
        if (a.ReviewedHeadSha != b.ReviewedHeadSha) return false;
        if (a.ReviewedBaseSha != b.ReviewedBaseSha) return false;
        if (a.SubjectDigest != b.SubjectDigest) return false;
        if (a.CacheContractDigest != b.CacheContractDigest) return false;
        if (a.ChangedFiles.Length != b.ChangedFiles.Length) return false;
        for (var i = 0; i < a.ChangedFiles.Length; i++)
        {
            var fa = a.ChangedFiles[i];
            var fb = b.ChangedFiles[i];
            if (fa != fb) return false; // record structural equality (ChangedFileEntry has no collections)
        }
        return true;
    }

    private static bool OutcomeEquals(ReviewOutcomeRecord a, ReviewOutcomeRecord b)
    {
        if (a.Summary != b.Summary) return false;
        if (a.Findings.Length != b.Findings.Length) return false;
        for (var i = 0; i < a.Findings.Length; i++)
        {
            if (a.Findings[i] != b.Findings[i]) return false;
        }
        if (a.Limitations.Length != b.Limitations.Length) return false;
        for (var i = 0; i < a.Limitations.Length; i++)
        {
            if (a.Limitations[i] != b.Limitations[i]) return false;
        }
        return true;
    }

    private static TransitionOutcome Fail(string code)
        => new TransitionOutcome(null, LedgerDiagnosticMessages.Of(code));
}


