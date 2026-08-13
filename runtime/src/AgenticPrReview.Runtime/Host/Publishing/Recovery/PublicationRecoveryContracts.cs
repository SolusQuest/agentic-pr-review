using System.Collections.Immutable;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State.Lineage;

namespace AgenticPrReview.Runtime.Host.Publishing.Recovery;

internal sealed record PublicationRecoveryBindingV1(
    string BaseScopeDigest,
    string Epoch,
    string SessionId,
    string? PredecessorAcceptanceIdentity,
    string CandidateObjectIdentity,
    string ReviewedHeadSha,
    string ScopeSha256,
    string BodySha256);

internal sealed record PublicationIntentV1(
    PublicationRecoveryBindingV1 Binding,
    long CreatedAtUnixSeconds,
    string RecordIdentity);

internal sealed record StickyReadbackRecordV1(
    PublicationRecoveryBindingV1 Binding,
    StickyPublicationOperation Operation,
    long RepositoryId,
    long PullRequestNumber,
    long CommentId,
    string CommentUrl,
    long ObservedAtUnixSeconds,
    string RecordIdentity)
{
    internal bool TryRehydrate(
        out StickyCommentPublisher.StickyPublicationReceipt? receipt) =>
        StickyCommentPublisher.StickyPublicationReceipt.TryRehydrate(
            Operation,
            RepositoryId,
            PullRequestNumber,
            CommentId,
            CommentUrl,
            Binding.ScopeSha256,
            Binding.BodySha256,
            Binding.ReviewedHeadSha,
            out receipt);
}

internal sealed record PublicationFailureV1(
    PublicationRecoveryBindingV1 Binding,
    BoundedGitHubPublisherOutcome Outcome,
    StickyPublicationReason Reason,
    long FailedAtUnixSeconds,
    string RecordIdentity);

internal sealed record AbandonmentV1(
    PublicationRecoveryBindingV1 Binding,
    string CompleteMarkerAbsenceEvidenceIdentity,
    long AbandonedAtUnixSeconds,
    string RecordIdentity);

internal sealed record RecoveryRecordV1(
    PublicationRecoveryBindingV1 Binding,
    string StickyReadbackRecordIdentity,
    ImmutableArray<byte> AcceptanceRecoveryHandoff,
    long MinimumSemanticExpiresAtUnixSeconds,
    string RecordIdentity);

internal static class PublicationRecoveryRetention
{
    internal const long PreStickyBudgetSeconds = 900;
    internal const long MinimumPlatformRequestSeconds = 8 * 24 * 60 * 60;

    internal static bool TryCompute(
        long trustedNowUnixSeconds,
        long candidateLogicalExpiresAtUnixSeconds,
        out long semanticRequiredExpiresAtUnixSeconds,
        out long requestedPlatformExpiresAtUnixSeconds)
    {
        semanticRequiredExpiresAtUnixSeconds = 0;
        requestedPlatformExpiresAtUnixSeconds = 0;
        if (!LineageValidation.IsTime(trustedNowUnixSeconds) ||
            !LineageValidation.IsTime(candidateLogicalExpiresAtUnixSeconds) ||
            candidateLogicalExpiresAtUnixSeconds < trustedNowUnixSeconds)
        {
            return false;
        }

        try
        {
            semanticRequiredExpiresAtUnixSeconds = checked(
                candidateLogicalExpiresAtUnixSeconds +
                PreStickyBudgetSeconds);
            requestedPlatformExpiresAtUnixSeconds = Math.Max(
                checked(trustedNowUnixSeconds +
                    MinimumPlatformRequestSeconds),
                semanticRequiredExpiresAtUnixSeconds);
            return LineageValidation.IsTime(
                    semanticRequiredExpiresAtUnixSeconds) &&
                LineageValidation.IsTime(
                    requestedPlatformExpiresAtUnixSeconds);
        }
        catch (OverflowException)
        {
            semanticRequiredExpiresAtUnixSeconds = 0;
            requestedPlatformExpiresAtUnixSeconds = 0;
            return false;
        }
    }

    internal static bool CoversReturnedExpiry(
        long returnedPlatformExpiresAtUnixSeconds,
        long requestedPlatformExpiresAtUnixSeconds) =>
        LineageValidation.IsTime(returnedPlatformExpiresAtUnixSeconds) &&
        LineageValidation.IsTime(requestedPlatformExpiresAtUnixSeconds) &&
        returnedPlatformExpiresAtUnixSeconds >=
            requestedPlatformExpiresAtUnixSeconds;
}

internal enum PublicationMarkerObservation
{
    Incomplete = 0,
    Absent,
    Exact,
    Ambiguous,
}

internal enum PublicationRecoveryAnchorState
{
    None = 0,
    CleanupDebt,
    Unresolved,
    Ambiguous,
}

internal sealed record PublicationRecoveryInventory(
    bool EnumerationComplete,
    bool OwnershipRetained,
    int CandidateCount,
    bool CandidateMatchesCurrentHead,
    bool HasStoredValidatedPublication,
    int IntentCount,
    int StickyReadbackCount,
    int FailureCount,
    int AbandonmentCount,
    int AcceptanceCount,
    int RecoveryCount,
    bool RecordsMatchCandidate,
    bool AcceptanceMatchesRecovery,
    bool HasExactKnownNotWrittenFailure,
    bool HasOutcomeUnknownFailure,
    PublicationMarkerObservation Marker,
    PublicationRecoveryAnchorState Anchors,
    bool IsSupersededTerminalRecovery = false,
    bool HasDurableSuccessorRecovery = false);

internal enum PublicationRecoveryLifecycleState
{
    None = 0,
    PendingCurrentTransaction,
    CurrentTerminalRecovery,
    SupersededTerminalRecovery,
    CompletedCleanupDebt,
    AmbiguousConflict,
}

internal enum PublicationRecoveryAction
{
    Conflict = 0,
    NoPendingWork,
    ReturnCommitted,
    ResumeBeforeIntent,
    ResumeKnownNotWritten,
    CompleteAcceptance,
    StickyOutcomeUnknown,
    AbandonStaleCandidate,
}

internal sealed record PublicationRecoveryDecision(
    PublicationRecoveryAction Action,
    string Code,
    PublicationRecoveryLifecycleState Lifecycle)
{
    internal bool AllowsProvider => Action ==
        PublicationRecoveryAction.NoPendingWork;
    internal bool AllowsStickyWrite => Action is
        PublicationRecoveryAction.ResumeBeforeIntent or
        PublicationRecoveryAction.ResumeKnownNotWritten;
    internal bool AllowsAcceptance => Action ==
        PublicationRecoveryAction.CompleteAcceptance;
    internal bool AllowsStaleCleanup => Action ==
        PublicationRecoveryAction.AbandonStaleCandidate;
    internal bool AllowsSupersededCleanup =>
        Lifecycle ==
            PublicationRecoveryLifecycleState.SupersededTerminalRecovery;
}

internal sealed record PublicationRecoveryEvaluation(
    PublicationRecoveryDecision Decision,
    StickyCommentPublisher.StickyPublicationReceipt? ExactReadbackReceipt,
    StickyDiscoveryKind DiscoveryKind,
    StickyPublicationReason DiscoveryReason);

internal static class PublicationRecoveryCodes
{
    internal const string NoPendingWork = "publication_recovery_no_pending";
    internal const string ReturnCommitted = "publication_recovery_committed";
    internal const string ResumeBeforeIntent =
        "publication_recovery_resume_before_intent";
    internal const string ResumeKnownNotWritten =
        "publication_recovery_resume_known_not_written";
    internal const string CompleteAcceptance =
        "publication_recovery_complete_acceptance";
    internal const string StickyOutcomeUnknown =
        "publication_recovery_sticky_outcome_unknown";
    internal const string AbandonStaleCandidate =
        "publication_recovery_abandon_stale_candidate";
    internal const string Conflict = "publication_recovery_conflict";
}

internal enum PublicationTransportTransition
{
    PersistStickyReadback = 1,
    PersistKnownNotWrittenAndRetry,
    PersistOutcomeUnknownAndStop,
    CancelledBeforeSend,
    AuthorizationOrValidationFailure,
}

internal sealed record PublicationTransportDecision(
    PublicationTransportTransition Transition,
    StickyCommentPublisher.StickyPublicationReceipt? Receipt,
    StickyPublicationReason Reason)
{
    internal bool AllowsRetry => Transition ==
        PublicationTransportTransition.PersistKnownNotWrittenAndRetry;
}
