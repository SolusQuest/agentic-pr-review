using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;

namespace AgenticPrReview.Runtime.Host.State;

internal enum StateConflictStage
{
    Locator,
    LineageInventory,
    LineageRecovery,
    LineageReset,
    LineageInitialize,
    StateSelection,
    RecoveryInventory,
    RecoveryObservation,
    StickyReadback,
    RecoveryDecision,
    RecoveryConvergence,
    RecoveryCleanup,
    RecoveryCommit,
    RecoveryIntent,
    RecoveryRetry,
    RecoveryAcceptance,
    ReviewSelection,
    RestoredSelection,
    RestoredClaim,
    ContextImport,
    ContextCapture,
    CandidatePreparation,
    CandidatePersistence,
    PublicationOwnership,
    PublicationResult,
    AcceptanceRecovery,
    AcceptancePredecessor,
    AcceptanceEvidence,
    AcceptancePersistence,
    IntentAdmission,
    IntentCandidate,
    IntentOwnership,
    IntentDocument,
    IntentAnchor,
    IntentPersistence,
    IntentInventory,
    IntentReadback,
    IntentAuthorization,
    AnchoredAdmission,
    AnchoredCandidate,
    AnchoredSelection,
    AnchoredPersistence,
    IntentCleanupAuthority,
    IntentCleanupCoverage,
    IntentCleanupInventory,
    IntentCleanupDocument,
    IntentCleanupPersistence,
    IntentCleanupReadback,
    IntentCleanupTargetDelete,
    IntentCleanupTargetUnconfirmed,
    IntentCleanupFinalReadback,
    IntentCleanupRecordDelete,
    IntentCleanupRecordUnconfirmed,
    Unclassified,
}

internal enum StateConflictReason
{
    Conflict,
    Incomplete,
    AuthenticationFailed,
    KeyUnavailable,
    RetentionFailed,
    CleanupFailed,
    ScopeMismatch,
    AncestryFailed,
    Incompatible,
    NonCurrent,
    Overflow,
    Stale,
    Invalid,
    Absent,
    Unavailable,
    Denied,
    OutcomeUnknown,
    Cancelled,
    CensusIncomplete,
    StillListed,
    Unknown,
}

// Optional preview-only observer. No inputs, payloads, keys or authority are retained.
internal sealed class StateConflictTrace
{
    private readonly object gate = new();
    private (StateConflictStage Stage, StateConflictReason Reason)? first;

    internal void Record(StateConflictStage stage, StateConflictReason reason)
    {
        lock (gate) first ??= (stage, reason);
    }

    internal (StateConflictStage Stage, StateConflictReason Reason) Read()
    {
        lock (gate) return first ?? (StateConflictStage.Unclassified, StateConflictReason.Unknown);
    }

    internal void RecordCode(StateConflictStage stage, string code) => Record(stage, code switch
    {
        LocatorCodes.Conflict or LineageCodes.Conflict or AcceptedStateCodes.Conflict or
            RetainedStateTransactionCodes.Conflict => StateConflictReason.Conflict,
        LocatorCodes.AuthenticationFailed or LineageCodes.AuthenticationFailed or
            AcceptedStateCodes.AuthenticationFailed => StateConflictReason.AuthenticationFailed,
        LocatorCodes.KeyUnavailable or LineageCodes.KeyUnavailable or AcceptedStateCodes.KeyUnavailable or
            RetainedStateTransactionCodes.KeyUnavailable => StateConflictReason.KeyUnavailable,
        LocatorCodes.CleanupFailed or LineageCodes.CleanupFailed or
            RetainedStateTransactionCodes.CleanupDebt => StateConflictReason.CleanupFailed,
        LineageCodes.RetentionFailed or RetainedStateTransactionCodes.RetentionFailed => StateConflictReason.RetentionFailed,
        AcceptedStateCodes.ScopeMismatch => StateConflictReason.ScopeMismatch,
        AcceptedStateCodes.AncestryFailed => StateConflictReason.AncestryFailed,
        AcceptedStateCodes.IncompatibleCurrent => StateConflictReason.Incompatible,
        AcceptedStateCodes.NonCurrent => StateConflictReason.NonCurrent,
        AcceptedStateCodes.Overflow => StateConflictReason.Overflow,
        RetainedStateTransactionCodes.OutcomeUnknown => StateConflictReason.OutcomeUnknown,
        RetainedStateTransactionCodes.Cancelled => StateConflictReason.Cancelled,
        RetainedStateTransactionCodes.Stale => StateConflictReason.Stale,
        LocatorCodes.Invalid or LineageCodes.Invalid or RetainedStateTransactionCodes.Invalid => StateConflictReason.Invalid,
        AcceptedStateCodes.Absent => StateConflictReason.Absent,
        LocatorCodes.Unavailable or LineageCodes.Unavailable => StateConflictReason.Unavailable,
        LocatorCodes.AccessDenied or LineageCodes.AccessDenied or AcceptedStateCodes.AccessDenied or
            RetainedStateTransactionCodes.AccessDenied => StateConflictReason.Denied,
        _ => StateConflictReason.Unknown,
    });

    internal void RecordLineage(StateConflictStage stage, LineageReadOnlyObservationResult result) =>
        RecordReadback(stage, result.Code, result.DiagnosticTerminal);

    internal void RecordReadback(StateConflictStage stage, string code, StateReconciliationTerminal? terminal)
    {
        if (terminal == StateReconciliationTerminal.Incomplete)
            Record(stage, StateConflictReason.Incomplete);
        else
            RecordCode(stage, code);
    }
}
