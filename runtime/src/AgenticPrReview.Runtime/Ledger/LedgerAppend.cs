using System.Collections.Immutable;

namespace AgenticPrReview.Runtime.Ledger;

/// <summary>
/// Transition validator. Every entry point starts with a kind guard: if the
/// candidate header's <c>kind</c> does not match the wire value expected by the
/// entry point, the validator emits a single <c>ledger_transition_kind_mismatch</c>
/// and skips every other check.
/// After the guard, the validator accumulates diagnostics one per category
/// under the fixed category order documented in Issue #49 section 10 (kind,
/// identities, session epoch, ledger epoch, state generation, predecessor
/// fields, kind-specific expected fields, transition structure). Diagnostics
/// are appended in that deterministic order.
/// </summary>
public static class LedgerAppend
{
    public static TransitionOutcome ValidateBootstrap(BootstrapTransition expected, ValidatedLedger candidate)
    {
        if (expected is null) throw new ArgumentNullException(nameof(expected));
        if (candidate is null) throw new ArgumentNullException(nameof(candidate));

        var diags = ImmutableArray.CreateBuilder<LedgerDiagnostic>();

        // (1) Kind guard.
        if (candidate.PrivateModel.Header.Kind != "bootstrap")
        {
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.TransitionKindMismatch));
            return new TransitionOutcome(diags.ToImmutable());
        }

        var h = candidate.PrivateModel.Header;

        // (2) Identities (common two-way; no three-way replacement for bootstrap).
        if (!IdentitiesEqual(h, expected.Identities))
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.IdentityMismatch));

        // (3) Session epoch.
        if (h.SessionEpoch != expected.SessionEpoch)
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SessionEpochMismatch));

        // (4) Ledger epoch.
        if (h.LedgerEpoch != expected.LedgerEpoch)
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.LedgerEpochMismatch));

        // (5) State generation.
        if (h.StateGeneration != expected.StateGeneration)
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.StateGenerationMismatch));

        // (6) Predecessor fields: bootstrap has none; schema pins predecessorLedgerSha256 == "bootstrap".

        // (7) Kind-specific expected fields: none for bootstrap.

        // (8) Transition structure: records shape.
        if (!RootRecordsShape(candidate.PrivateModel.Records))
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.RootRecordsShapeMismatch));

        return new TransitionOutcome(diags.ToImmutable());
    }

    public static TransitionOutcome ValidateRecoveryRoot(RecoveryRootTransition expected, ValidatedLedger candidate)
    {
        if (expected is null) throw new ArgumentNullException(nameof(expected));
        if (candidate is null) throw new ArgumentNullException(nameof(candidate));

        var diags = ImmutableArray.CreateBuilder<LedgerDiagnostic>();

        if (candidate.PrivateModel.Header.Kind != "recovery_root")
        {
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.TransitionKindMismatch));
            return new TransitionOutcome(diags.ToImmutable());
        }

        var h = candidate.PrivateModel.Header;

        // (2) Identities.
        if (!IdentitiesEqual(h, expected.Identities))
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.IdentityMismatch));

        // (3) Session epoch.
        if (h.SessionEpoch != expected.SessionEpoch)
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SessionEpochMismatch));

        // (4) Ledger epoch.
        if (h.LedgerEpoch != expected.LedgerEpoch)
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.LedgerEpochMismatch));

        // (5) State generation: recovery-root pins stateGeneration == 0 at schema; still cross-check with expected.
        if (h.StateGeneration != 0L)
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.StateGenerationMismatch));

        // (6) Predecessor fields: none.

        // (7) Kind-specific: recoveryReason.
        if (h.RecoveryReason != expected.RecoveryReason)
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.RecoveryRootReasonMismatch));

        // (8) Structure.
        if (!RootRecordsShape(candidate.PrivateModel.Records))
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.RootRecordsShapeMismatch));

        return new TransitionOutcome(diags.ToImmutable());
    }

    public static TransitionOutcome ValidateContinuation(ContinuationTransition expected, ValidatedLedger predecessor, ValidatedLedger candidate)
    {
        if (expected is null) throw new ArgumentNullException(nameof(expected));
        if (predecessor is null) throw new ArgumentNullException(nameof(predecessor));
        if (candidate is null) throw new ArgumentNullException(nameof(candidate));

        var diags = ImmutableArray.CreateBuilder<LedgerDiagnostic>();

        if (candidate.PrivateModel.Header.Kind != "continuation")
        {
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.TransitionKindMismatch));
            return new TransitionOutcome(diags.ToImmutable());
        }

        var ch = candidate.PrivateModel.Header;
        var ph = predecessor.PrivateModel.Header;
        var predSha = predecessor.ContentSha256;

        // (2) Identities: three-way agreement across expected, candidate, predecessor.
        if (!IdentitiesEqual(ch, expected.Identities) || !IdentitiesEqual(ph, expected.Identities))
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.IdentityMismatch));

        // (3) Session epoch three-way.
        if (!(expected.SessionEpoch == ch.SessionEpoch && ch.SessionEpoch == ph.SessionEpoch))
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SessionEpochMismatch));

        // (4) Ledger epoch reuse three-way.
        if (!(expected.LedgerEpoch == ch.LedgerEpoch && ch.LedgerEpoch == ph.LedgerEpoch))
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.LedgerEpochMismatch));

        // (5) Successor state generation.
        if (ch.StateGeneration != ph.StateGeneration + 1 || ch.StateGeneration != expected.StateGeneration)
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.StateGenerationMismatch));

        // (6) Predecessor fields: fail-fast within the category.
        //     6a: predecessor ledger sha256.
        //     6c: predecessor ledger epoch.
        //     6d: predecessor state generation.
        if (!(expected.PredecessorLedgerSha256 == ch.PredecessorLedgerSha256 && ch.PredecessorLedgerSha256 == predSha))
        {
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.PredecessorHashMismatch));
        }
        else if (!(expected.PredecessorLedgerEpoch == ch.PredecessorLedgerEpoch && ch.PredecessorLedgerEpoch == ph.LedgerEpoch))
        {
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.PredecessorLedgerEpochMismatch));
        }
        else if (!(expected.PredecessorStateGeneration == ch.PredecessorStateGeneration && ch.PredecessorStateGeneration == ph.StateGeneration))
        {
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.PredecessorGenerationMismatch));
        }

        // (7) Kind-specific expected fields: none for continuation.

        // (8) Transition structure: prefix invariant + length + tail pair ordinal.
        if (!ContinuationPrefixOk(predecessor.PrivateModel.Records, candidate.PrivateModel.Records))
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.ContinuationPrefixMismatch));

        return new TransitionOutcome(diags.ToImmutable());
    }

    public static TransitionOutcome ValidateReset(ResetTransition expected, ValidatedLedger predecessor, ValidatedLedger candidate)
    {
        if (expected is null) throw new ArgumentNullException(nameof(expected));
        if (predecessor is null) throw new ArgumentNullException(nameof(predecessor));
        if (candidate is null) throw new ArgumentNullException(nameof(candidate));

        var diags = ImmutableArray.CreateBuilder<LedgerDiagnostic>();

        if (candidate.PrivateModel.Header.Kind != "reset")
        {
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.TransitionKindMismatch));
            return new TransitionOutcome(diags.ToImmutable());
        }

        var ch = candidate.PrivateModel.Header;
        var ph = predecessor.PrivateModel.Header;
        var predSha = predecessor.ContentSha256;

        // (2) Identities: split into session-scope (three-way) and cache-contract (two-way OR three-way).
        var sessionOk = SessionScopeEqual(ch, expected.Identities) && SessionScopeEqual(ph, expected.Identities);
        var cacheOk = CacheContractIdentityMatchesForReset(expected, ch, ph);
        if (!sessionOk || !cacheOk)
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.IdentityMismatch));

        // (3) Session epoch three-way.
        if (!(expected.SessionEpoch == ch.SessionEpoch && ch.SessionEpoch == ph.SessionEpoch))
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SessionEpochMismatch));

        // (4) Ledger epoch: two-leg precedence.
        if (ch.LedgerEpoch != expected.LedgerEpoch)
        {
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.LedgerEpochMismatch));
        }
        else if (ch.LedgerEpoch == ph.LedgerEpoch)
        {
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.ResetEpochNotFresh));
        }

        // (5) Successor state generation.
        if (ch.StateGeneration != ph.StateGeneration + 1 || ch.StateGeneration != expected.StateGeneration)
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.StateGenerationMismatch));

        // (6) Predecessor fields: fail-fast within category.
        //     6a: predecessor ledger sha256.
        //     6b: predecessor manifest sha256.
        //     6c: predecessor ledger epoch.
        //     6d: predecessor state generation.
        if (!(expected.PredecessorLedgerSha256 == ch.PredecessorLedgerSha256 && ch.PredecessorLedgerSha256 == predSha))
        {
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.PredecessorHashMismatch));
        }
        else if (ch.PredecessorManifestSha256 != expected.PredecessorManifestSha256)
        {
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.PredecessorManifestHashMismatch));
        }
        else if (!(expected.PredecessorLedgerEpoch == ch.PredecessorLedgerEpoch && ch.PredecessorLedgerEpoch == ph.LedgerEpoch))
        {
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.PredecessorLedgerEpochMismatch));
        }
        else if (!(expected.PredecessorStateGeneration == ch.PredecessorStateGeneration && ch.PredecessorStateGeneration == ph.StateGeneration))
        {
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.PredecessorGenerationMismatch));
        }

        // (7) Kind-specific: resetReason.
        if (ch.ResetReason != expected.ResetReason)
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.ResetReasonMismatch));

        // (8) Structure: reset records shape.
        if (!ResetRecordsShape(candidate.PrivateModel.Records))
            diags.Add(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.ResetRecordsShapeMismatch));

        return new TransitionOutcome(diags.ToImmutable());
    }

    // -----------------------------------------------------------------
    // Helpers

    private static bool IdentitiesEqual(LedgerHeader h, ExpectedIdentities e)
    {
        return h.Repository == e.Repository &&
               h.HeadRepository == e.HeadRepository &&
               h.PullRequest == e.PullRequest &&
               h.WorkflowIdentity == e.WorkflowIdentity &&
               h.TrustedExecutionDomain == e.TrustedExecutionDomain &&
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
               h.TrustedExecutionDomain == e.TrustedExecutionDomain;
    }

    private static bool CacheContractIdentityMatchesForReset(ResetTransition expected, LedgerHeader ch, LedgerHeader ph)
    {
        // Every field of ExpectedIdentities cache-contract portion must match candidate.
        var expectedMatchesCandidate =
            ch.ProviderId == expected.Identities.ProviderId &&
            ch.ModelId == expected.Identities.ModelId &&
            ch.AdapterId == expected.Identities.AdapterId &&
            ch.TemplateId == expected.Identities.TemplateId &&
            ch.PolicyId == expected.Identities.PolicyId &&
            ch.ToolDefinitionId == expected.Identities.ToolDefinitionId &&
            ch.CacheConfigId == expected.Identities.CacheConfigId;
        if (!expectedMatchesCandidate) return false;

        // When resetReason != "cache_contract_change", cache-contract identity must
        // additionally agree with predecessor's cache-contract identity.
        if (expected.ResetReason != "cache_contract_change")
        {
            if (!(ch.ProviderId == ph.ProviderId &&
                  ch.ModelId == ph.ModelId &&
                  ch.AdapterId == ph.AdapterId &&
                  ch.TemplateId == ph.TemplateId &&
                  ch.PolicyId == ph.PolicyId &&
                  ch.ToolDefinitionId == ph.ToolDefinitionId &&
                  ch.CacheConfigId == ph.CacheConfigId))
                return false;
        }
        return true;
    }

    private static bool RootRecordsShape(ImmutableArray<LedgerRecord> records)
    {
        if (records.Length != 2) return false;
        if (records[0].InteractionOrdinal != 0L) return false;
        if (records[1].InteractionOrdinal != 0L) return false;
        if (records[0].Role != "review_context") return false;
        if (records[1].Role != "review_outcome") return false;
        return true;
    }

    private static bool ResetRecordsShape(ImmutableArray<LedgerRecord> records) => RootRecordsShape(records);

    private static bool ContinuationPrefixOk(ImmutableArray<LedgerRecord> pred, ImmutableArray<LedgerRecord> cand)
    {
        if (cand.Length != pred.Length + 2) return false;
        for (var i = 0; i < pred.Length; i++)
        {
            var predBytes = LedgerCanonicalizer.SerializeRecord(pred[i]);
            var candBytes = LedgerCanonicalizer.SerializeRecord(cand[i]);
            if (!predBytes.AsSpan().SequenceEqual(candBytes.AsSpan())) return false;
        }
        // Tail pair ordinal continuity: new ordinal == pred.length / 2.
        var lastOrdinal = pred.Length == 0 ? -1L : pred[^1].InteractionOrdinal;
        var expectedOrdinal = lastOrdinal + 1;
        if (cand[pred.Length].InteractionOrdinal != expectedOrdinal) return false;
        if (cand[pred.Length + 1].InteractionOrdinal != expectedOrdinal) return false;
        if (cand[pred.Length].Role != "review_context") return false;
        if (cand[pred.Length + 1].Role != "review_outcome") return false;
        return true;
    }
}
