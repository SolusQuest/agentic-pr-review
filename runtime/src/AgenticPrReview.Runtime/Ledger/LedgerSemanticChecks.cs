using System.Text;

namespace AgenticPrReview.Runtime.Ledger;

/// <summary>
/// Structural-bounds (semantic) checks post-schema and semantic-invariant
/// checks. Both are fail-fast under the fixed order documented in Issue #49
/// section 9.
/// </summary>
internal static class LedgerSemanticChecks
{
    /// <summary>
    /// Structural-bounds subset that runs after the canonical serializer:
    /// (2) identity UTF-8 byte-length cap, (3) control character in identity.
    /// The canonical-byte cap (1) is enforced by the parser directly.
    /// </summary>
    public static LedgerDiagnostic? CheckIdentityBounds(LedgerModel model)
    {
        // Order: identityByteLength before controlCharacter, per section 9.
        foreach (var (name, value) in EnumerateIdentityStrings(model.Header))
        {
            if (Encoding.UTF8.GetByteCount(value) > LedgerLimits.MaxIdentityUtf8Bytes)
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.IdentityByteLengthExceeded);
        }
        foreach (var (name, value) in EnumerateIdentityStrings(model.Header))
        {
            foreach (var ch in value)
            {
                if (ch < 0x20 || ch == 0x7F)
                    return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.ControlCharacterInIdentity);
            }
        }
        return null;
    }

    /// <summary>
    /// Semantic invariants under the numbered order:
    ///   (1) ledger_records_length_not_even
    ///   (2) ledger_pair_order_mismatch
    ///   (3) ledger_pair_interaction_id_mismatch
    ///   (4) ledger_ordinal_gap
    ///   (5) ledger_duplicate_interaction
    ///   (6) ledger_finding_location_mismatch
    ///   (7) ledger_finding_location_missing_path
    ///   (8) ledger_finding_line_range_invalid
    ///   (9) ledger_digest_mismatch (cacheContractDigest only; subjectDigest is host-supplied)
    ///  (10) ledger_model_alias_literal
    /// </summary>
    public static LedgerDiagnostic? CheckSemanticInvariants(LedgerModel model)
    {
        var recs = model.Records;
        if (recs.Length == 0) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.RecordsEmpty);

        // (1) length parity.
        if (recs.Length % 2 != 0) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.RecordsLengthNotEven);

        // (2) pair order: each even index must be a context and the following odd index must be an outcome.
        for (var i = 0; i < recs.Length; i += 2)
        {
            if (recs[i] is not ReviewContextRecord || recs[i + 1] is not ReviewOutcomeRecord)
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.PairOrderMismatch);
        }

        // (3) pair interaction id: within each pair, both records must share interactionId.
        for (var i = 0; i < recs.Length; i += 2)
        {
            if (recs[i].InteractionId != recs[i + 1].InteractionId)
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.PairInteractionIdMismatch);
        }

        // (4) ordinal continuity: expected == 0, +1 per pair; also both records in a pair share ordinal.
        long expected = 0;
        for (var i = 0; i < recs.Length; i += 2)
        {
            if (recs[i].InteractionOrdinal != expected || recs[i + 1].InteractionOrdinal != expected)
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.OrdinalGap);
            expected++;
        }

        // (5) duplicate interaction: each interactionId may appear at most once across pairs.
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < recs.Length; i += 2)
        {
            if (!seenIds.Add(recs[i].InteractionId))
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.DuplicateInteraction);
        }

        // (6)-(8) finding location invariants.
        for (var i = 1; i < recs.Length; i += 2)
        {
            var oc = (ReviewOutcomeRecord)recs[i];
            foreach (var f in oc.Findings)
            {
                var locFailure = ValidateFindingLocation(f);
                if (locFailure is not null) return locFailure;
            }
        }

        // (9) digest recomputation on each context record.
        //     subjectDigest is host-supplied pass-through per the M4 Batch #1 shared contract;
        //     only cacheContractDigest is ledger-computed and verified.
        var cacheContractDigest = LedgerDigests.ComputeCacheContractDigestFromHeader(model.Header);
        for (var i = 0; i < recs.Length; i += 2)
        {
            var ctx = (ReviewContextRecord)recs[i];
            if (!string.Equals(cacheContractDigest, ctx.CacheContractDigest, StringComparison.Ordinal))
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.DigestMismatch);
        }

        // (10) model-alias literal rejection: `header.modelId == "latest"` is a floating alias.
        if (model.Header.ModelId == "latest")
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.ModelAliasLiteral);

        return null;
    }

    public static LedgerDiagnostic? ValidateFindingLocation(LedgerFinding f)
    {
        var hasStart = f.StartLine.HasValue;
        var hasEnd = f.EndLine.HasValue;
        if (hasStart != hasEnd)
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.FindingLocationMismatch);
        if (hasStart)
        {
            if (f.Path is null)
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.FindingLocationMissingPath);
            if (f.StartLine!.Value > f.EndLine!.Value)
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.FindingLineRangeInvalid);
        }
        return null;
    }

    private static IEnumerable<(string Name, string Value)> EnumerateIdentityStrings(LedgerHeader h)
    {
        yield return ("workflowIdentity", h.WorkflowIdentity);
        yield return ("trustedExecutionDomain", h.TrustedExecutionDomain);
        yield return ("sessionEpoch", h.SessionEpoch);
        yield return ("ledgerEpoch", h.LedgerEpoch);
        yield return ("providerId", h.ProviderId);
        yield return ("modelId", h.ModelId);
        yield return ("adapterId", h.AdapterId);
        yield return ("templateId", h.TemplateId);
        yield return ("policyId", h.PolicyId);
        yield return ("toolDefinitionId", h.ToolDefinitionId);
        yield return ("cacheConfigId", h.CacheConfigId);
    }
}
