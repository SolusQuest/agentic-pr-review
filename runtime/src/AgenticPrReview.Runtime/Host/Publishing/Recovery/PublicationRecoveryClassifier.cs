using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;

namespace AgenticPrReview.Runtime.Host.Publishing.Recovery;

internal static class PublicationRecoveryClassifier
{
    internal static PublicationRecoveryDecision Classify(
        PublicationRecoveryObservation? observation,
        PublicationMarkerObservation marker)
    {
        if (observation is null ||
            !observation.IsLive ||
            marker is PublicationMarkerObservation.Incomplete or
                PublicationMarkerObservation.Ambiguous)
        {
            return Conflict();
        }

        if (!observation.CleanupRecords.IsEmpty)
        {
            return observation.CleanupRecords.Length == 1
                ? Decision(
                    PublicationRecoveryAction.ResumeCleanup,
                    PublicationRecoveryCodes.ResumeCleanup,
                    PublicationRecoveryLifecycleState.CompletedCleanupDebt)
                : Conflict();
        }

        if (observation.Anchors ==
            PublicationRecoveryAnchorState.RecoverableWrite)
        {
            return observation.Candidate is not null &&
                (observation.Candidate.MatchesCurrentReviewedHead ||
                    PublicationRecoveryInventoryFactory
                        .TryGetStaleAbandonmentAnchor(
                            observation,
                            out _,
                            out _))
                ? Decision(
                    PublicationRecoveryAction.ResumeAnchoredWrite,
                    PublicationRecoveryCodes.ResumeAnchoredWrite)
                : Conflict();
        }

        if (observation.Anchors is PublicationRecoveryAnchorState.Unresolved or
            PublicationRecoveryAnchorState.Ambiguous)
        {
            return Conflict();
        }

        if (observation.Candidate is null)
        {
            if (observation.CurrentAcceptedHeadMatchesReviewedHead)
            {
                return marker == PublicationMarkerObservation.Exact &&
                    observation.Inventory?
                        .CurrentAcceptancePublicationReceipt is not null
                    ? Decision(
                        PublicationRecoveryAction.ReturnCommitted,
                        PublicationRecoveryCodes.ReturnCommitted,
                        PublicationRecoveryLifecycleState
                            .CurrentTerminalRecovery)
                    : Conflict();
            }

            if (observation.HistoricalTerminalRecovery &&
                observation.HasHistoricalCleanupDebt)
            {
                return Decision(
                    PublicationRecoveryAction.CleanupSupersededRecovery,
                    PublicationRecoveryCodes.CleanupSupersededRecovery,
                    PublicationRecoveryLifecycleState
                        .SupersededTerminalRecovery);
            }

            return Decision(
                PublicationRecoveryAction.NoPendingWork,
                PublicationRecoveryCodes.NoPendingWork,
                observation.HistoricalTerminalRecovery
                    ? PublicationRecoveryLifecycleState
                        .SupersededTerminalRecovery
                    : observation.HasHistoricalCleanupDebt ||
                    observation.Anchors ==
                        PublicationRecoveryAnchorState.CleanupDebt
                    ? PublicationRecoveryLifecycleState.CompletedCleanupDebt
                    : PublicationRecoveryLifecycleState.None);
        }

        if (!observation.CandidateMatchesCurrentHead)
        {
            if (observation.Intent is null &&
                observation.RetryIntent is null &&
                observation.StickyReadback is null &&
                observation.Failure is null &&
                observation.RetryFailure is null &&
                observation.Recovery is null &&
                marker == PublicationMarkerObservation.Absent &&
                observation.Anchors == PublicationRecoveryAnchorState.None)
            {
                return observation.Abandonment is null
                    ? Decision(
                    PublicationRecoveryAction.AbandonStaleCandidate,
                    PublicationRecoveryCodes.AbandonStaleCandidate,
                    PublicationRecoveryLifecycleState
                        .PendingCurrentTransaction)
                    : Decision(
                        PublicationRecoveryAction.ResumeStaleCleanup,
                        PublicationRecoveryCodes.ResumeStaleCleanup,
                        PublicationRecoveryLifecycleState
                            .PendingCurrentTransaction);
            }

            return Conflict();
        }

        if (marker == PublicationMarkerObservation.Exact)
        {
            var initialAllowsAcceptance = observation.RetryIntent is null &&
                (observation.Failure is null ||
                    observation.Failure.Outcome ==
                        BoundedGitHubPublisherOutcome.OutcomeUnknown);
            var retryAllowsAcceptance = observation.RetryIntent is not null &&
                (observation.RetryFailure is null ||
                    observation.RetryFailure.Outcome ==
                        BoundedGitHubPublisherOutcome.OutcomeUnknown);
            return (initialAllowsAcceptance || retryAllowsAcceptance) &&
                observation.Abandonment is null
                ? Decision(
                    PublicationRecoveryAction.CompleteAcceptance,
                    PublicationRecoveryCodes.CompleteAcceptance,
                    PublicationRecoveryLifecycleState
                        .PendingCurrentTransaction)
                : Conflict();
        }

        if (marker is not PublicationMarkerObservation.Absent and
            not PublicationMarkerObservation.PreviousAcceptedTarget)
        {
            return Conflict();
        }

        if (observation.StickyReadback is not null ||
            observation.Recovery is not null ||
            observation.Abandonment is not null)
        {
            return Conflict();
        }

        if (observation.RetryFailure is { } retryFailure)
        {
            return retryFailure.Outcome switch
            {
                BoundedGitHubPublisherOutcome.KnownNotWritten =>
                    Decision(
                        PublicationRecoveryAction.KnownNotWrittenTerminal,
                        PublicationRecoveryCodes.KnownNotWrittenTerminal),
                BoundedGitHubPublisherOutcome.OutcomeUnknown =>
                    Decision(
                        PublicationRecoveryAction.StickyOutcomeUnknown,
                        PublicationRecoveryCodes.StickyOutcomeUnknown),
                BoundedGitHubPublisherOutcome.CancelledBeforeSend =>
                    Decision(
                        PublicationRecoveryAction.CancelledBeforeSend,
                        PublicationRecoveryCodes.CancelledBeforeSend),
                BoundedGitHubPublisherOutcome
                    .AuthorizationOrValidationFailure => Decision(
                        PublicationRecoveryAction
                            .AuthorizationOrValidationFailure,
                        PublicationRecoveryCodes
                            .AuthorizationOrValidationFailure),
                _ => Conflict(),
            };
        }

        if (observation.RetryIntent is not null)
        {
            return Decision(
                PublicationRecoveryAction.StickyOutcomeUnknown,
                PublicationRecoveryCodes.StickyOutcomeUnknown);
        }

        if (observation.Failure is { } failure)
        {
            return failure.Outcome switch
            {
                BoundedGitHubPublisherOutcome.KnownNotWritten =>
                    Decision(
                        PublicationRecoveryAction.ResumeKnownNotWritten,
                        PublicationRecoveryCodes.ResumeKnownNotWritten),
                BoundedGitHubPublisherOutcome.OutcomeUnknown =>
                    Decision(
                        PublicationRecoveryAction.StickyOutcomeUnknown,
                        PublicationRecoveryCodes.StickyOutcomeUnknown),
                BoundedGitHubPublisherOutcome.CancelledBeforeSend =>
                    Decision(
                        PublicationRecoveryAction.CancelledBeforeSend,
                        PublicationRecoveryCodes.CancelledBeforeSend),
                BoundedGitHubPublisherOutcome
                    .AuthorizationOrValidationFailure => Decision(
                        PublicationRecoveryAction
                            .AuthorizationOrValidationFailure,
                        PublicationRecoveryCodes
                            .AuthorizationOrValidationFailure),
                _ => Conflict(),
            };
        }

        return observation.Intent is null
            ? Decision(
                PublicationRecoveryAction.ResumeBeforeIntent,
                PublicationRecoveryCodes.ResumeBeforeIntent)
            : Decision(
                PublicationRecoveryAction.StickyOutcomeUnknown,
                PublicationRecoveryCodes.StickyOutcomeUnknown);
    }

    private static PublicationRecoveryDecision Conflict() =>
        Decision(
            PublicationRecoveryAction.Conflict,
            PublicationRecoveryCodes.Conflict,
            PublicationRecoveryLifecycleState.AmbiguousConflict);

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
                    reason is StickyPublicationReason.RequestInvalid or
                        StickyPublicationReason.Deadline => new(
                    PublicationTransportTransition
                        .PersistKnownNotWrittenAndRetry,
                    null,
                    reason),
            BoundedGitHubPublisherOutcome.OutcomeUnknown
                when receipt is null &&
                    reason is
                        StickyPublicationReason.ReconciliationIncomplete or
                        StickyPublicationReason.Deadline => new(
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
            BoundedGitHubPublisherOutcome.AuthorizationOrValidationFailure
                when receipt is null &&
                    reason is StickyPublicationReason.AdmissionInvalid or
                        StickyPublicationReason.DiscoveryIncomplete or
                        StickyPublicationReason.TargetConflict or
                        StickyPublicationReason.AuthorizationDenied => new(
                    PublicationTransportTransition
                        .AuthorizationOrValidationFailure,
                    null,
                    reason),
            _ => new(
                PublicationTransportTransition
                    .AuthorizationOrValidationFailure,
                null,
                reason),
        };
}
