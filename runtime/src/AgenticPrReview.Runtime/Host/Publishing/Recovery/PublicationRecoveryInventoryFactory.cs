using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;

namespace AgenticPrReview.Runtime.Host.Publishing.Recovery;

internal static class PublicationRecoveryInventoryFactory
{
    internal static bool TryCreate(
        AuthorizedAcceptedStateRestoreContext context,
        RetainedStateObservedCandidate candidate,
        ImmutableArray<RetainedStateOpaqueRecord> records,
        MatchedRetainedStateRecoveryAcceptance? matchedAcceptance,
        PublicationRecoveryAnchorState anchors,
        out PublicationRecoveryInventory? inventory)
    {
        inventory = null;
        if (context is null ||
            candidate is null ||
            records.IsDefault ||
            matchedAcceptance is not null &&
                !StringComparer.Ordinal.Equals(
                    matchedAcceptance.CandidateObjectIdentity,
                    candidate.Header.ObjectIdentity) ||
            !TryBinding(candidate, out var binding) ||
            binding is null)
        {
            return false;
        }

        var intents = 0;
        var readbacks = 0;
        var failures = 0;
        var abandonments = 0;
        var recoveries = 0;
        var knownNotWritten = false;
        var outcomeUnknown = false;
        foreach (var record in records)
        {
            if (record is null ||
                !StringComparer.Ordinal.Equals(
                    record.InventoryDigest,
                    candidate.InventoryDigest) ||
                !StringComparer.Ordinal.Equals(
                    record.Header.PredecessorIdentity,
                    candidate.Header.ObjectIdentity) ||
                !RestrictedStateService.TryCopyRetainedStateOpaquePayload(
                    context,
                    record,
                    out var payload))
            {
                return false;
            }

            try
            {
                switch (record.ObjectClass)
                {
                    case StateObjectClass.PublicationIntent
                        when PublicationIntentV1Codec.TryDecode(
                            payload.AsSpan(),
                            out var intent) &&
                            intent is not null &&
                            intent.Binding == binding:
                        intents++;
                        break;
                    case StateObjectClass.PublicationIntent
                        when StickyReadbackRecordV1Codec.TryDecode(
                            payload.AsSpan(),
                            out var readback) &&
                            readback is not null &&
                            readback.Binding == binding:
                        readbacks++;
                        break;
                    case StateObjectClass.PublicationIntent
                        when RecoveryRecordV1Codec.TryDecode(
                            payload.AsSpan(),
                            out var recovery,
                            out _,
                            out _) &&
                            recovery is not null &&
                            recovery.Binding == binding:
                        recoveries++;
                        break;
                    case StateObjectClass.PublicationFailure
                        when PublicationFailureV1Codec.TryDecode(
                            payload.AsSpan(),
                            out var failure) &&
                            failure is not null &&
                            failure.Binding == binding:
                        failures++;
                        knownNotWritten |= failure.Outcome ==
                            BoundedGitHubPublisherOutcome.KnownNotWritten;
                        outcomeUnknown |= failure.Outcome ==
                            BoundedGitHubPublisherOutcome.OutcomeUnknown;
                        break;
                    case StateObjectClass.Abandonment
                        when AbandonmentV1Codec.TryDecode(
                            payload.AsSpan(),
                            out var abandonment) &&
                            abandonment is not null &&
                            abandonment.Binding == binding:
                        abandonments++;
                        break;
                    default:
                        return false;
                }
            }
            finally
            {
                var array = ImmutableCollectionsMarshal.AsArray(payload);
                if (array is not null)
                {
                    CryptographicOperations.ZeroMemory(array);
                }
            }
        }

        inventory = new PublicationRecoveryInventory(
            EnumerationComplete: true,
            OwnershipRetained: true,
            CandidateCount: 1,
            CandidateMatchesCurrentHead:
                candidate.MatchesCurrentReviewedHead,
            HasStoredValidatedPublication: true,
            IntentCount: intents,
            StickyReadbackCount: readbacks,
            FailureCount: failures,
            AbandonmentCount: abandonments,
            AcceptanceCount: matchedAcceptance is null ? 0 : 1,
            RecoveryCount: recoveries,
            RecordsMatchCandidate: true,
            AcceptanceMatchesRecovery: matchedAcceptance is not null,
            HasExactKnownNotWrittenFailure: knownNotWritten,
            HasOutcomeUnknownFailure: outcomeUnknown,
            Marker: PublicationMarkerObservation.Incomplete,
            Anchors: anchors);
        return true;
    }

    private static bool TryBinding(
        RetainedStateObservedCandidate candidate,
        out PublicationRecoveryBindingV1? binding)
    {
        binding = null;
        if (candidate.Header.ObjectClass != StateObjectClass.Candidate ||
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
}
