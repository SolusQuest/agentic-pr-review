using System.Collections.Immutable;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;

namespace AgenticPrReview.Runtime.Host.Publishing.Recovery;

internal static class PublicationRecoveryPersistence
{
    internal static bool TryCreateIntentWrite(
        RetainedStatePersistedCandidate candidate,
        long trustedNowUnixSeconds,
        long candidateLogicalExpiresAtUnixSeconds,
        out PublicationIntentV1? intent,
        out RetainedStateOpaqueWriteRequest? request)
    {
        intent = null;
        request = null;
        if (!TryBinding(candidate, out var binding) ||
            !PublicationRecoveryRetention.TryCompute(
                trustedNowUnixSeconds,
                candidateLogicalExpiresAtUnixSeconds,
                out var semanticExpiry,
                out _) ||
            !PublicationIntentV1Codec.TryCreate(
                binding!,
                trustedNowUnixSeconds,
                out intent) ||
            !PublicationIntentV1Codec.TryEncode(intent, out var bytes))
        {
            return false;
        }

        request = Request(
            StateObjectClass.PublicationIntent,
            bytes,
            binding!.CandidateObjectIdentity,
            semanticExpiry);
        CryptographicOperations.ZeroMemory(bytes);
        return true;
    }

    internal static bool TryCreateStickyReadbackWrite(
        RetainedStatePersistedCandidate candidate,
        StickyCommentPublisher.StickyPublicationReceipt receipt,
        long observedAtUnixSeconds,
        long candidateLogicalExpiresAtUnixSeconds,
        out StickyReadbackRecordV1? readback,
        out RetainedStateOpaqueWriteRequest? request)
    {
        readback = null;
        request = null;
        if (!TryBinding(candidate, out var binding) ||
            !PublicationRecoveryRetention.TryCompute(
                observedAtUnixSeconds,
                candidateLogicalExpiresAtUnixSeconds,
                out var semanticExpiry,
                out _) ||
            !StickyReadbackRecordV1Codec.TryCreate(
                binding!,
                receipt,
                observedAtUnixSeconds,
                out readback) ||
            !StickyReadbackRecordV1Codec.TryEncode(
                readback,
                out var bytes))
        {
            return false;
        }

        request = Request(
            StateObjectClass.PublicationIntent,
            bytes,
            binding!.CandidateObjectIdentity,
            semanticExpiry);
        CryptographicOperations.ZeroMemory(bytes);
        return true;
    }

    internal static bool TryCreateFailureWrite(
        RetainedStatePersistedCandidate candidate,
        BoundedGitHubPublisherOutcome outcome,
        StickyPublicationReason reason,
        long failedAtUnixSeconds,
        long candidateLogicalExpiresAtUnixSeconds,
        out PublicationFailureV1? failure,
        out RetainedStateOpaqueWriteRequest? request)
    {
        failure = null;
        request = null;
        if (!TryBinding(candidate, out var binding) ||
            !PublicationRecoveryRetention.TryCompute(
                failedAtUnixSeconds,
                candidateLogicalExpiresAtUnixSeconds,
                out var semanticExpiry,
                out _) ||
            !PublicationFailureV1Codec.TryCreate(
                binding!,
                outcome,
                reason,
                failedAtUnixSeconds,
                out failure) ||
            !PublicationFailureV1Codec.TryEncode(failure, out var bytes))
        {
            return false;
        }

        request = Request(
            StateObjectClass.PublicationFailure,
            bytes,
            binding!.CandidateObjectIdentity,
            semanticExpiry);
        CryptographicOperations.ZeroMemory(bytes);
        return true;
    }

    internal static bool TryCreateAbandonmentWrite(
        RetainedStatePersistedCandidate candidate,
        string completeMarkerAbsenceEvidenceIdentity,
        long abandonedAtUnixSeconds,
        long candidateLogicalExpiresAtUnixSeconds,
        out AbandonmentV1? abandonment,
        out RetainedStateOpaqueWriteRequest? request)
    {
        abandonment = null;
        request = null;
        if (!TryBinding(candidate, out var binding) ||
            !PublicationRecoveryRetention.TryCompute(
                abandonedAtUnixSeconds,
                candidateLogicalExpiresAtUnixSeconds,
                out var semanticExpiry,
                out _) ||
            !AbandonmentV1Codec.TryCreate(
                binding!,
                completeMarkerAbsenceEvidenceIdentity,
                abandonedAtUnixSeconds,
                out abandonment) ||
            !AbandonmentV1Codec.TryEncode(
                abandonment,
                out var bytes))
        {
            return false;
        }

        request = Request(
            StateObjectClass.Abandonment,
            bytes,
            binding!.CandidateObjectIdentity,
            semanticExpiry);
        CryptographicOperations.ZeroMemory(bytes);
        return true;
    }

    internal static bool TryCreateStaleAbandonmentWrite(
        RetainedStateObservedCandidate candidate,
        string completeMarkerAbsenceEvidenceIdentity,
        long abandonedAtUnixSeconds,
        out AbandonmentV1? abandonment,
        out RetainedStateOpaqueWriteRequest? request)
    {
        abandonment = null;
        request = null;
        if (candidate is null ||
            candidate.MatchesCurrentReviewedHead ||
            !TryBinding(candidate, out var binding) ||
            !PublicationRecoveryRetention.TryCompute(
                abandonedAtUnixSeconds,
                candidate.Header.LogicalExpiresAtUnixSeconds,
                out var semanticExpiry,
                out _) ||
            !AbandonmentV1Codec.TryCreate(
                binding!,
                completeMarkerAbsenceEvidenceIdentity,
                abandonedAtUnixSeconds,
                out abandonment) ||
            !AbandonmentV1Codec.TryEncode(
                abandonment,
                out var bytes))
        {
            return false;
        }

        request = Request(
            StateObjectClass.Abandonment,
            bytes,
            binding!.CandidateObjectIdentity,
            semanticExpiry);
        CryptographicOperations.ZeroMemory(bytes);
        return true;
    }

    internal static bool TryCreateAcceptanceRecoveryWrite(
        RetainedStateAcceptancePreparation preparation,
        StickyReadbackRecordV1 readback,
        out RecoveryRecordV1? recovery,
        out RetainedStateOpaqueWriteRequest? request)
    {
        recovery = null;
        request = null;
        if (preparation is null ||
            readback is null ||
            !preparation.TryCreateRecoveryHandoff(out var handoff) ||
            handoff is null ||
            !TryBinding(preparation.Candidate, out var binding) ||
            binding != readback.Binding ||
            !RecoveryRecordV1Codec.TryCreate(
                binding!,
                readback.RecordIdentity,
                handoff.OpaqueInnerPayload,
                handoff.MinimumSemanticExpiresAtUnixSeconds,
                out recovery) ||
            !RecoveryRecordV1Codec.TryEncode(
                recovery,
                out var bytes,
                out _,
                out _))
        {
            return false;
        }

        request = Request(
            StateObjectClass.PublicationIntent,
            bytes,
            handoff.CandidateObjectIdentity,
            handoff.MinimumSemanticExpiresAtUnixSeconds);
        CryptographicOperations.ZeroMemory(bytes);
        return true;
    }

    internal static RetainedStateTransactionResult<
        RetainedStateOpaquePayloadExtraction>
        CreateAcceptanceRecoveryExtraction(
            AuthorizedAcceptedStateRestoreContext context,
            RetainedStateOpaqueRecord record)
    {
        if (context is null ||
            record is null ||
            !RestrictedStateService.TryCopyRetainedStateOpaquePayload(
                context,
                record,
                out var payload))
        {
            return RetainedStateTransactionResult<
                RetainedStateOpaquePayloadExtraction>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

        try
        {
            if (!RecoveryRecordV1Codec.TryDecode(
                    payload.AsSpan(),
                    out _,
                    out var offset,
                    out var length))
            {
                return RetainedStateTransactionResult<
                    RetainedStateOpaquePayloadExtraction>.Fail(
                        RetainedStateTransactionCodes.Conflict);
            }

            return RestrictedStateService
                .CreateRetainedStateOpaquePayloadExtraction(
                    context,
                    record,
                    offset,
                    length);
        }
        finally
        {
            var array = System.Runtime.InteropServices
                .ImmutableCollectionsMarshal.AsArray(payload);
            if (array is not null)
            {
                CryptographicOperations.ZeroMemory(array);
            }
        }
    }

    internal static bool TryBinding(
        RetainedStatePersistedCandidate candidate,
        out PublicationRecoveryBindingV1? binding)
    {
        binding = null;
        var prepared = candidate?.Prepared;
        var header = prepared?.Header;
        var publication = prepared?.Publication;
        if (prepared is null || header is null || publication is null ||
            header.ObjectClass != StateObjectClass.Candidate ||
            !StringComparer.Ordinal.Equals(
                header.ObjectIdentity,
                prepared.Header.ObjectIdentity) ||
            !StringComparer.Ordinal.Equals(
                publication.ReviewedHeadSha,
                prepared.Generation.ProducerHeadSha))
        {
            return false;
        }

        binding = new PublicationRecoveryBindingV1(
            header.BaseScopeDigest,
            header.Epoch,
            header.SessionId,
            header.PredecessorIdentity,
            header.ObjectIdentity,
            publication.ReviewedHeadSha,
            publication.ScopeSha256,
            publication.BodySha256);
        return true;
    }

    internal static bool TryBinding(
        RetainedStateObservedCandidate candidate,
        out PublicationRecoveryBindingV1? binding)
    {
        binding = null;
        if (candidate is null ||
            candidate.Header.ObjectClass != StateObjectClass.Candidate ||
            !StringComparer.Ordinal.Equals(
                candidate.Publication.ReviewedHeadSha,
                candidate.Generation.ProducerHeadSha))
        {
            return false;
        }

        binding = new PublicationRecoveryBindingV1(
            candidate.Header.BaseScopeDigest,
            candidate.Header.Epoch,
            candidate.Header.SessionId,
            candidate.Header.PredecessorIdentity,
            candidate.Header.ObjectIdentity,
            candidate.Publication.ReviewedHeadSha,
            candidate.Publication.ScopeSha256,
            candidate.Publication.BodySha256);
        return true;
    }

    private static RetainedStateOpaqueWriteRequest Request(
        StateObjectClass objectClass,
        byte[] bytes,
        string candidateIdentity,
        long semanticExpiry) => new(
            objectClass,
            ImmutableArray.CreateRange(bytes),
            candidateIdentity,
            SuccessorIdentity: null,
            semanticExpiry);
}
