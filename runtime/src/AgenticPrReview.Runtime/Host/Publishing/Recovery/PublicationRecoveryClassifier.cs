using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;

namespace AgenticPrReview.Runtime.Host.Publishing.Recovery;

internal static class PublicationRecoveryClassifier
{
    internal static PublicationRecoveryDecision Classify(
        PublicationRecoveryInventory? inventory)
    {
        if (!StructurallyComplete(inventory))
        {
            return Decision(
                PublicationRecoveryAction.Conflict,
                PublicationRecoveryCodes.Conflict);
        }

        var value = inventory!;
        if (value.IsSupersededTerminalRecovery)
        {
            return value.HasDurableSuccessorRecovery &&
                value.Anchors is PublicationRecoveryAnchorState.None or
                    PublicationRecoveryAnchorState.CleanupDebt
                ? Decision(
                    PublicationRecoveryAction.NoPendingWork,
                    PublicationRecoveryCodes.NoPendingWork,
                    PublicationRecoveryLifecycleState
                        .SupersededTerminalRecovery)
                : Decision(
                    PublicationRecoveryAction.Conflict,
                    PublicationRecoveryCodes.Conflict);
        }

        if (value.CandidateCount == 0)
        {
            return value.IntentCount == 0 &&
                value.StickyReadbackCount == 0 &&
                value.FailureCount == 0 &&
                value.AbandonmentCount == 0 &&
                value.AcceptanceCount == 0 &&
                value.RecoveryCount == 0 &&
                value.Anchors is PublicationRecoveryAnchorState.None or
                    PublicationRecoveryAnchorState.CleanupDebt
                ? Decision(
                    PublicationRecoveryAction.NoPendingWork,
                    PublicationRecoveryCodes.NoPendingWork,
                    value.Anchors ==
                        PublicationRecoveryAnchorState.CleanupDebt
                        ? PublicationRecoveryLifecycleState
                            .CompletedCleanupDebt
                        : PublicationRecoveryLifecycleState.None)
                : Decision(
                    PublicationRecoveryAction.Conflict,
                    PublicationRecoveryCodes.Conflict);
        }

        if (!value.CandidateMatchesCurrentHead)
        {
            return value.IntentCount == 0 &&
                value.StickyReadbackCount == 0 &&
                value.FailureCount == 0 &&
                value.AbandmentFree() &&
                value.AcceptanceCount == 0 &&
                value.RecoveryCount == 0 &&
                value.Marker == PublicationMarkerObservation.Absent &&
                value.Anchors == PublicationRecoveryAnchorState.None
                ? Decision(
                    PublicationRecoveryAction.AbandonStaleCandidate,
                    PublicationRecoveryCodes.AbandonStaleCandidate,
                    PublicationRecoveryLifecycleState
                        .PendingCurrentTransaction)
                : Decision(
                    PublicationRecoveryAction.Conflict,
                    PublicationRecoveryCodes.Conflict);
        }

        if (!value.HasStoredValidatedPublication ||
            !value.RecordsMatchCandidate)
        {
            return Decision(
                PublicationRecoveryAction.Conflict,
                PublicationRecoveryCodes.Conflict);
        }

        if (value.AcceptanceCount == 1)
        {
            return value.Marker == PublicationMarkerObservation.Exact &&
                value.StickyReadbackCount == 1 &&
                value.RecoveryCount == 1 &&
                value.AcceptanceMatchesRecovery
                ? Decision(
                    PublicationRecoveryAction.ReturnCommitted,
                    PublicationRecoveryCodes.ReturnCommitted,
                    PublicationRecoveryLifecycleState
                        .CurrentTerminalRecovery)
                : Decision(
                    PublicationRecoveryAction.Conflict,
                    PublicationRecoveryCodes.Conflict);
        }

        if (value.Marker == PublicationMarkerObservation.Exact)
        {
            return value.IntentCount <= 1 &&
                value.StickyReadbackCount <= 1 &&
                value.FailureCount <= 1
                ? Decision(
                    PublicationRecoveryAction.CompleteAcceptance,
                    PublicationRecoveryCodes.CompleteAcceptance,
                    PublicationRecoveryLifecycleState
                        .PendingCurrentTransaction)
                : Decision(
                    PublicationRecoveryAction.Conflict,
                    PublicationRecoveryCodes.Conflict);
        }

        if (value.Marker != PublicationMarkerObservation.Absent)
        {
            return Decision(
                PublicationRecoveryAction.Conflict,
                PublicationRecoveryCodes.Conflict);
        }

        if (value.HasExactKnownNotWrittenFailure &&
            value.FailureCount == 1 &&
            value.StickyReadbackCount == 0 &&
            value.RecoveryCount == 0)
        {
            return Decision(
                PublicationRecoveryAction.ResumeKnownNotWritten,
                PublicationRecoveryCodes.ResumeKnownNotWritten,
                PublicationRecoveryLifecycleState
                    .PendingCurrentTransaction);
        }

        if (value.IntentCount == 1)
        {
            return Decision(
                PublicationRecoveryAction.StickyOutcomeUnknown,
                PublicationRecoveryCodes.StickyOutcomeUnknown,
                PublicationRecoveryLifecycleState
                    .PendingCurrentTransaction);
        }

        if (value.HasOutcomeUnknownFailure)
        {
            return Decision(
                PublicationRecoveryAction.StickyOutcomeUnknown,
                PublicationRecoveryCodes.StickyOutcomeUnknown,
                PublicationRecoveryLifecycleState
                    .PendingCurrentTransaction);
        }

        return value.IntentCount == 0 &&
            value.StickyReadbackCount == 0 &&
            value.FailureCount == 0 &&
            value.AbandonmentCount == 0 &&
            value.RecoveryCount == 0
            ? Decision(
                PublicationRecoveryAction.ResumeBeforeIntent,
                PublicationRecoveryCodes.ResumeBeforeIntent,
                PublicationRecoveryLifecycleState.PendingCurrentTransaction)
            : Decision(
                PublicationRecoveryAction.Conflict,
                PublicationRecoveryCodes.Conflict);
    }

    private static bool StructurallyComplete(
        PublicationRecoveryInventory? value) =>
        value is not null &&
        value.EnumerationComplete &&
        value.OwnershipRetained &&
        value.CandidateCount is 0 or 1 &&
        value.IntentCount is >= 0 and <= 1 &&
        value.StickyReadbackCount is >= 0 and <= 1 &&
        value.FailureCount is >= 0 and <= 1 &&
        value.AbandonmentCount is >= 0 and <= 1 &&
        value.AcceptanceCount is >= 0 and <= 1 &&
        value.RecoveryCount is >= 0 and <= 1 &&
        value.Marker is not PublicationMarkerObservation.Incomplete &&
        value.Anchors is not (
            PublicationRecoveryAnchorState.Unresolved or
            PublicationRecoveryAnchorState.Ambiguous);

    private static bool AbandmentFree(
        this PublicationRecoveryInventory value) =>
        value.AbandonmentCount == 0;

    private static PublicationRecoveryDecision Decision(
        PublicationRecoveryAction action,
        string code,
        PublicationRecoveryLifecycleState? lifecycle = null) => new(
            action,
            code,
            lifecycle ?? (action == PublicationRecoveryAction.Conflict
                ? PublicationRecoveryLifecycleState.AmbiguousConflict
                : PublicationRecoveryLifecycleState
                    .PendingCurrentTransaction));
}

internal static class PublicationTransportOutcomeMapper
{
    internal static PublicationTransportDecision Map(
        StickyCommentPublisher.StickyPublicationResult? result)
    {
        if (result is null)
        {
            return new(
                PublicationTransportTransition
                    .AuthorizationOrValidationFailure,
                null,
                StickyPublicationReason.AdmissionInvalid);
        }

        return Map(result.Outcome, result.Reason, result.Receipt);
    }

    internal static PublicationTransportDecision Map(
        BoundedGitHubPublisherOutcome outcome,
        StickyPublicationReason reason,
        StickyCommentPublisher.StickyPublicationReceipt? receipt) =>
        outcome switch
        {
            BoundedGitHubPublisherOutcome.WrittenAndReadBack
                when receipt is not null &&
                    reason == StickyPublicationReason.None => new(
                    PublicationTransportTransition.PersistStickyReadback,
                    receipt,
                    StickyPublicationReason.None),
            BoundedGitHubPublisherOutcome.KnownNotWritten
                when receipt is null &&
                    reason != StickyPublicationReason.None => new(
                    PublicationTransportTransition
                        .PersistKnownNotWrittenAndRetry,
                    null,
                    reason),
            BoundedGitHubPublisherOutcome.OutcomeUnknown
                when receipt is null &&
                    reason != StickyPublicationReason.None => new(
                    PublicationTransportTransition
                        .PersistOutcomeUnknownAndStop,
                    null,
                    reason),
            BoundedGitHubPublisherOutcome.CancelledBeforeSend
                when receipt is null &&
                    reason == StickyPublicationReason.Cancelled => new(
                    PublicationTransportTransition.CancelledBeforeSend,
                    null,
                    reason),
            _ => new(
                PublicationTransportTransition
                    .AuthorizationOrValidationFailure,
                null,
                reason),
        };
}
