using System.Collections.Immutable;
using System.Text;

namespace AgenticPrReview.Runtime.Ledger;

internal static class LedgerSemanticChecks
{
    public static LedgerDiagnostic? CheckIdentityBounds(LedgerModel model)
    {
        var h = model.Header;
        var identityStrings = new[]
        {
            h.WorkflowIdentity, h.TrustedExecutionDomain, h.SessionEpoch,
            h.ProviderId, h.ModelId,
        };
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
        return null;
    }

    public static LedgerDiagnostic? CheckSemanticInvariants(LedgerModel model)
    {
        // records length must be even and at least 2.
        var recs = model.Records;
        if (recs.Length == 0) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.RecordsEmpty);
        if (recs.Length % 2 != 0) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.RecordsLengthNotEven);

        // Pair order: each pair must be (context, outcome) with matching interactionId and ordinal.
        for (var i = 0; i < recs.Length; i += 2)
        {
            var a = recs[i];
            var b = i + 1 < recs.Length ? recs[i + 1] : null;
            if (a.Context is null || b is null || b.Outcome is null)
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.PairOrderMismatch);
            if (a.InteractionId != b.InteractionId)
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.PairOrderMismatch);
            if (a.InteractionOrdinal != b.InteractionOrdinal)
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.PairOrderMismatch);
        }

        // Ordinal continuity: pair 0 has ordinal 0, then +1 each pair.
        var expected = 0;
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < recs.Length; i += 2)
        {
            if (recs[i].InteractionOrdinal != expected)
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.OrdinalGap);
            if (!seenIds.Add(recs[i].InteractionId))
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.DuplicateInteraction);
            expected++;
        }

        // Digest recomputation on each context record.
        var cacheContractDigest = LedgerDigests.ComputeCacheContractDigestFromHeader(model.Header);
        for (var i = 0; i < recs.Length; i += 2)
        {
            var ctx = recs[i].Context!;
            var subject = LedgerDigests.ComputeSubjectDigest(
                model.Header.Repository, model.Header.HeadRepository, model.Header.PullRequest,
                ctx.ReviewedHeadSha, ctx.ReviewedBaseSha);
            if (!string.Equals(subject, ctx.SubjectDigest, StringComparison.Ordinal))
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.DigestMismatch);
            if (!string.Equals(cacheContractDigest, ctx.CacheContractDigest, StringComparison.Ordinal))
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.DigestMismatch);
        }

        // Finding location invariants.
        for (var i = 1; i < recs.Length; i += 2)
        {
            var oc = recs[i].Outcome!;
            foreach (var f in oc.Findings)
            {
                var locFailure = ValidateFindingLocation(f);
                if (locFailure is not null) return locFailure;
            }
        }

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
}
