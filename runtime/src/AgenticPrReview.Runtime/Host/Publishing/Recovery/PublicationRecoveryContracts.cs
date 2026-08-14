using System.Collections.Immutable;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;

namespace AgenticPrReview.Runtime.Host.Publishing.Recovery;

internal sealed record PublicationRecoveryPublicationV1(
    string ReviewedHeadSha,
    string ScopeSha256,
    string BodySha256);

internal sealed record PublicationIntentV1(
    PublicationRecoveryPublicationV1 Publication,
    long CreatedAtUnixSeconds,
    string RecordIdentity);

internal sealed record StickyReadbackRecordV1(
    PublicationRecoveryPublicationV1 Publication,
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
            Publication.ScopeSha256,
            Publication.BodySha256,
            Publication.ReviewedHeadSha,
            out receipt);
}

internal sealed record PublicationFailureV1(
    PublicationRecoveryPublicationV1 Publication,
    BoundedGitHubPublisherOutcome Outcome,
    StickyPublicationReason Reason,
    long FailedAtUnixSeconds,
    string RecordIdentity);

internal sealed record AbandonmentV1(
    PublicationRecoveryPublicationV1 Publication,
    string CompleteMarkerAbsenceEvidenceIdentity,
    long AbandonedAtUnixSeconds,
    string RecordIdentity);

internal sealed record RecoveryRecordV1(
    PublicationRecoveryPublicationV1 Publication,
    StickyReadbackRecordV1 StickyReadback,
    ImmutableArray<byte> AcceptanceRecoveryHandoff,
    long MinimumSemanticExpiresAtUnixSeconds,
    string RecordIdentity)
{
    internal string StickyReadbackRecordIdentity =>
        StickyReadback.RecordIdentity;
}

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
    RecoverableWrite,
    Unresolved,
    Ambiguous,
}

internal sealed class PublicationRecoveryObservation : IDisposable
{
    private RetainedStatePublicationRecoveryInventory? inventory;

    internal PublicationRecoveryObservation(
        object issuer,
        RetainedStatePublicationRecoveryInventory inventory,
        string? candidateObjectIdentity,
        ValidatedPublicationPayloadV1? storedPublication,
        PublicationIntentV1? intent,
        StickyReadbackRecordV1? stickyReadback,
        PublicationFailureV1? failure,
        AbandonmentV1? abandonment,
        RecoveryRecordV1? recovery,
        MatchedRetainedStateRecoveryAcceptance? matchedAcceptance,
        ImmutableArray<RetainedStateOpaqueRecord> historicalRecords,
        ImmutableArray<RetainedStatePublicationRecoveryAnchorEvidence>
            completedAnchors,
        ImmutableArray<RetainedStatePublicationRecoveryCleanupEvidence>
            cleanupRecords,
        bool candidateMatchesCurrentHead,
        bool currentAcceptedHeadMatchesReviewedHead,
        bool historicalTerminalRecovery,
        bool hasHistoricalCleanupDebt,
        PublicationRecoveryAnchorState anchors)
    {
        PublicationRecoveryInventoryFactory.RequireIssuer(issuer);
        this.inventory = inventory;
        CandidateObjectIdentity = candidateObjectIdentity;
        StoredPublication = storedPublication;
        Intent = intent;
        StickyReadback = stickyReadback;
        Failure = failure;
        Abandonment = abandonment;
        Recovery = recovery;
        MatchedAcceptance = matchedAcceptance;
        HistoricalRecords = historicalRecords;
        CompletedAnchors = completedAnchors;
        CleanupRecords = cleanupRecords;
        CandidateMatchesCurrentHead = candidateMatchesCurrentHead;
        CurrentAcceptedHeadMatchesReviewedHead =
            currentAcceptedHeadMatchesReviewedHead;
        HistoricalTerminalRecovery = historicalTerminalRecovery;
        HasHistoricalCleanupDebt = hasHistoricalCleanupDebt;
        Anchors = anchors;
    }

    internal RetainedStatePublicationRecoveryInventory? Inventory =>
        Volatile.Read(ref inventory);
    internal RetainedStateObservedCandidate? Candidate => Inventory?.Candidate;
    internal ImmutableArray<RetainedStateOpaqueRecord> Records =>
        Inventory?.Records ?? default;
    internal string InventoryDigest => Inventory?.InventoryDigest ??
        string.Empty;
    internal long ObservedAtUnixSeconds =>
        Inventory?.ObservedAtUnixSeconds ?? 0;
    internal string? CandidateObjectIdentity { get; }
    internal ValidatedPublicationPayloadV1? StoredPublication { get; }
    internal PublicationIntentV1? Intent { get; }
    internal StickyReadbackRecordV1? StickyReadback { get; }
    internal PublicationFailureV1? Failure { get; }
    internal AbandonmentV1? Abandonment { get; }
    internal RecoveryRecordV1? Recovery { get; }
    internal MatchedRetainedStateRecoveryAcceptance? MatchedAcceptance
    {
        get;
    }
    internal ImmutableArray<RetainedStateOpaqueRecord> HistoricalRecords
    {
        get;
    }
    internal ImmutableArray<RetainedStatePublicationRecoveryAnchorEvidence>
        CompletedAnchors { get; }
    internal ImmutableArray<RetainedStatePublicationRecoveryCleanupEvidence>
        CleanupRecords { get; }
    internal bool CandidateMatchesCurrentHead { get; }
    internal bool CurrentAcceptedHeadMatchesReviewedHead { get; }
    internal bool HistoricalTerminalRecovery { get; }
    internal bool HasHistoricalCleanupDebt { get; }
    internal PublicationRecoveryAnchorState Anchors { get; }
    internal bool IsLive => Inventory is not null;

    public void Dispose() =>
        Interlocked.Exchange(ref inventory, null)?.Dispose();

    public override string ToString() => "[PRIVATE]";
}

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
    CleanupSupersededRecovery,
    ReturnCommitted,
    ResumeBeforeIntent,
    ResumeKnownNotWritten,
    CompleteAcceptance,
    StickyOutcomeUnknown,
    AbandonStaleCandidate,
    ResumeStaleCleanup,
    ResumeAnchoredWrite,
    ResumeCleanup,
    CancelledBeforeSend,
    AuthorizationOrValidationFailure,
}

internal sealed record PublicationRecoveryDecision(
    PublicationRecoveryAction Action,
    string Code,
    PublicationRecoveryLifecycleState Lifecycle)
{
    internal bool AllowsProvider => Action ==
        PublicationRecoveryAction.NoPendingWork;
    internal bool AllowsStickyWrite => false;
    internal bool AllowsAcceptance => Action ==
        PublicationRecoveryAction.CompleteAcceptance;
    internal bool AllowsStaleCleanup => Action ==
            PublicationRecoveryAction.AbandonStaleCandidate ||
        Action == PublicationRecoveryAction.ResumeStaleCleanup;
    internal bool AllowsSupersededCleanup =>
        Action == PublicationRecoveryAction.CleanupSupersededRecovery;
}

internal sealed record PublicationRecoveryEvaluation(
    PublicationRecoveryDecision Decision,
    StickyCommentPublisher.StickyPublicationReceipt? ExactReadbackReceipt,
    StickyDiscoveryKind DiscoveryKind,
    StickyPublicationReason DiscoveryReason,
    PublicationRecoveryObservation? Observation,
    PublicationStickyWriteAuthorization? StickyWriteAuthorization = null,
    PublicationMarkerAbsenceEvidence? MarkerAbsenceEvidence = null) :
    IDisposable
{
    public void Dispose()
    {
        StickyWriteAuthorization?.Dispose();
        MarkerAbsenceEvidence?.Dispose();
        Observation?.Dispose();
    }
}

internal sealed class PublicationMarkerAbsenceEvidence : IDisposable
{
    private int usable = 1;
    private readonly object issuer;

    internal PublicationMarkerAbsenceEvidence(
        object issuer,
        string candidateObjectIdentity,
        string inventoryDigest,
        string evidenceIdentity)
    {
        PublicationRecoveryInventoryFactory.RequireIssuer(issuer);
        this.issuer = issuer;
        CandidateObjectIdentity = candidateObjectIdentity;
        InventoryDigest = inventoryDigest;
        EvidenceIdentity = evidenceIdentity;
    }

    internal string CandidateObjectIdentity { get; }
    internal string InventoryDigest { get; }
    internal string EvidenceIdentity { get; }

    internal bool TryConsume(
        object expectedIssuer,
        string candidateObjectIdentity,
        string inventoryDigest) =>
        PublicationRecoveryInventoryFactory.IsIssuer(expectedIssuer) &&
        ReferenceEquals(issuer, expectedIssuer) &&
        StringComparer.Ordinal.Equals(
            CandidateObjectIdentity,
            candidateObjectIdentity) &&
        StringComparer.Ordinal.Equals(InventoryDigest, inventoryDigest) &&
        Interlocked.CompareExchange(ref usable, 0, 1) == 1;

    public void Dispose() => Interlocked.Exchange(ref usable, 0);

    public override string ToString() => "[PRIVATE]";
}

internal enum PublicationStickyWriteTransition
{
    InitialIntent = 1,
    KnownNotWrittenRetry,
}

internal sealed class PublicationStickyWriteAuthorization : IDisposable
{
    private int usable = 1;
    private readonly object issuer;

    internal PublicationStickyWriteAuthorization(
        object issuer,
        string candidateObjectIdentity,
        string inventoryDigest,
        string evidenceRecordIdentity,
        PublicationStickyWriteTransition transition)
    {
        PublicationRecoveryInventoryFactory.RequireIssuer(issuer);
        this.issuer = issuer;
        CandidateObjectIdentity = candidateObjectIdentity;
        InventoryDigest = inventoryDigest;
        EvidenceRecordIdentity = evidenceRecordIdentity;
        Transition = transition;
    }

    internal string CandidateObjectIdentity { get; }
    internal string InventoryDigest { get; }
    internal string EvidenceRecordIdentity { get; }
    internal PublicationStickyWriteTransition Transition { get; }

    internal bool TryConsume(object expectedIssuer) =>
        PublicationRecoveryInventoryFactory.IsIssuer(expectedIssuer) &&
        ReferenceEquals(issuer, expectedIssuer) &&
        Interlocked.CompareExchange(ref usable, 0, 1) == 1;

    public void Dispose() => Interlocked.Exchange(ref usable, 0);

    public override string ToString() => "[PRIVATE]";
}

internal sealed class PublicationStaleAbandonmentAuthorization : IDisposable
{
    private int ownershipUsable = 1;
    private int writeUsable = 1;
    private readonly object issuer;

    internal PublicationStaleAbandonmentAuthorization(
        object issuer,
        string candidateObjectIdentity,
        string inventoryDigest,
        string evidenceIdentity)
    {
        PublicationRecoveryInventoryFactory.RequireIssuer(issuer);
        this.issuer = issuer;
        CandidateObjectIdentity = candidateObjectIdentity;
        InventoryDigest = inventoryDigest;
        EvidenceIdentity = evidenceIdentity;
    }

    internal string CandidateObjectIdentity { get; }
    internal string InventoryDigest { get; }
    internal string EvidenceIdentity { get; }

    internal bool TryAuthorizeOwnership(
        object expectedIssuer,
        string candidateObjectIdentity,
        string inventoryDigest) =>
        PublicationRecoveryInventoryFactory.IsIssuer(expectedIssuer) &&
        ReferenceEquals(issuer, expectedIssuer) &&
        StringComparer.Ordinal.Equals(
            CandidateObjectIdentity,
            candidateObjectIdentity) &&
        StringComparer.Ordinal.Equals(InventoryDigest, inventoryDigest) &&
        Interlocked.CompareExchange(ref ownershipUsable, 0, 1) == 1;

    internal bool TryCreateWrite(
        object expectedIssuer,
        string candidateObjectIdentity,
        string inventoryDigest) =>
        PublicationRecoveryInventoryFactory.IsIssuer(expectedIssuer) &&
        ReferenceEquals(issuer, expectedIssuer) &&
        Volatile.Read(ref ownershipUsable) == 0 &&
        StringComparer.Ordinal.Equals(
            CandidateObjectIdentity,
            candidateObjectIdentity) &&
        StringComparer.Ordinal.Equals(InventoryDigest, inventoryDigest) &&
        Interlocked.CompareExchange(ref writeUsable, 0, 1) == 1;

    public void Dispose()
    {
        Interlocked.Exchange(ref ownershipUsable, 0);
        Interlocked.Exchange(ref writeUsable, 0);
    }

    public override string ToString() => "[PRIVATE]";
}

internal sealed class PublicationStaleCleanupAuthorization : IDisposable
{
    private int usable = 1;
    private readonly object issuer;

    internal PublicationStaleCleanupAuthorization(
        object issuer,
        string candidateObjectIdentity,
        string inventoryDigest,
        string abandonmentRecordIdentity,
        string classificationIdentity,
        string markerEvidenceIdentity)
    {
        PublicationRecoveryInventoryFactory.RequireIssuer(issuer);
        this.issuer = issuer;
        CandidateObjectIdentity = candidateObjectIdentity;
        InventoryDigest = inventoryDigest;
        AbandonmentRecordIdentity = abandonmentRecordIdentity;
        ClassificationIdentity = classificationIdentity;
        MarkerEvidenceIdentity = markerEvidenceIdentity;
    }

    internal string CandidateObjectIdentity { get; }
    internal string InventoryDigest { get; }
    internal string AbandonmentRecordIdentity { get; }
    internal string ClassificationIdentity { get; }
    internal string MarkerEvidenceIdentity { get; }

    internal bool TryConsume(
        object expectedIssuer,
        string candidateObjectIdentity,
        string inventoryDigest,
        string abandonmentRecordIdentity) =>
        PublicationRecoveryInventoryFactory.IsIssuer(expectedIssuer) &&
        ReferenceEquals(issuer, expectedIssuer) &&
        StringComparer.Ordinal.Equals(
            CandidateObjectIdentity,
            candidateObjectIdentity) &&
        StringComparer.Ordinal.Equals(InventoryDigest, inventoryDigest) &&
        StringComparer.Ordinal.Equals(
            AbandonmentRecordIdentity,
            abandonmentRecordIdentity) &&
        Interlocked.CompareExchange(ref usable, 0, 1) == 1;

    public void Dispose() => Interlocked.Exchange(ref usable, 0);

    public override string ToString() => "[PRIVATE]";
}

internal sealed record PublicationIntentPersistenceResult(
    PublicationIntentV1 Intent,
    string InventoryDigest,
    PublicationRecoveryObservation Observation,
    PublicationStickyWriteAuthorization StickyWriteAuthorization) :
    IDisposable
{
    public void Dispose()
    {
        StickyWriteAuthorization.Dispose();
        Observation.Dispose();
    }
}

internal static class PublicationRecoveryCodes
{
    internal const string NoPendingWork = "publication_recovery_no_pending";
    internal const string CleanupSupersededRecovery =
        "publication_recovery_cleanup_superseded";
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
    internal const string ResumeStaleCleanup =
        "publication_recovery_resume_stale_cleanup";
    internal const string ResumeAnchoredWrite =
        "publication_recovery_resume_anchored_write";
    internal const string ResumeCleanup =
        "publication_recovery_resume_cleanup";
    internal const string CancelledBeforeSend =
        "publication_recovery_cancelled_before_send";
    internal const string AuthorizationOrValidationFailure =
        "publication_recovery_authorization_or_validation_failure";
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
