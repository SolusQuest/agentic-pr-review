import type {
  CacheContractIdentityV2,
  EpochId,
  GitSha,
  Sha256Hex,
  StateKeyV2,
  StateManifestV2,
  StateManifestV2Transition,
} from '../state-v2/index.js';
import type { InvalidDiagnosticCode } from '../state-v2/diagnostics.js';

export type { EpochId, GitSha, Sha256Hex, StateKeyV2, StateManifestV2Transition };

export type CandidateId = Sha256Hex;
export type RegistrationId = Sha256Hex;
export type MarkerId = Sha256Hex;
export type SelectorId = Sha256Hex;
export type DecimalSequence = string;
export type SelectorRevision = 'bootstrap' | `sha256:${Sha256Hex}` | `invalid:${Sha256Hex}`;
export type PredecessorDigest = Sha256Hex | 'bootstrap';

export interface CandidateArtifactLocator {
  readonly kind: 'store-object';
  readonly namespace: 'm4-state-v1';
  readonly objectId: `candidate-${Sha256Hex}`;
}

export interface CandidateBundleBytes {
  readonly manifestBytes: Uint8Array;
  readonly ledgerBytes: Uint8Array;
  readonly providerRunMetadataBytes: Uint8Array;
}

export interface CandidateLeaseLike extends CandidateBundleBytes {
  readonly manifest: StateManifestV2;
  readonly resultBytes: Uint8Array;
  readonly traceBytes: Uint8Array;
  readonly inputSha256: string;
  readonly resultSha256: string;
  readonly traceSha256: string;
  readonly candidateLedgerSha256: string;
  readonly metadataSemanticSha256: string;
  release(): Promise<void>;
}

export interface CandidateRegistrationV1 {
  readonly schemaVersion: 1;
  readonly registrationId: RegistrationId;
  readonly registrationSequence: DecimalSequence;
  readonly candidateId: CandidateId;
  readonly candidateArtifactLocator: CandidateArtifactLocator;
  readonly observedSelectorRevision: SelectorRevision;
  readonly observedSelectorSnapshotSha256: Sha256Hex;
  readonly predecessorMarkerId: PredecessorDigest;
  readonly predecessorManifestSha256: PredecessorDigest;
  readonly predecessorLedgerSha256: PredecessorDigest;
  readonly stateKey: StateKeyV2;
  readonly sessionEpoch: EpochId;
  readonly stateGeneration: number;
  readonly ledgerEpoch: EpochId;
  readonly transition: StateManifestV2Transition;
  readonly interactionId: Sha256Hex;
  readonly interactionOrdinal: number;
  readonly producingRunId: string;
  readonly producingRunAttempt: number;
  readonly consumedInputSha256: Sha256Hex;
  readonly manifestSha256: Sha256Hex;
  readonly candidateLedgerSha256: Sha256Hex;
  readonly providerRunMetadataSha256: Sha256Hex;
  readonly metadataSemanticSha256: Sha256Hex;
  readonly resultSha256: Sha256Hex;
  readonly traceSha256: Sha256Hex;
  readonly registeredAt: string;
}

export type CandidateRegistrationDraft = Omit<
  CandidateRegistrationV1,
  'registrationId' | 'registrationSequence' | 'candidateArtifactLocator' | 'registeredAt'
> & {
  readonly registeredAt?: string;
};

export interface AcceptedStateMarkerV1 {
  readonly schemaVersion: 1;
  readonly markerId: MarkerId;
  readonly candidateId: CandidateId;
  readonly registrationId: RegistrationId;
  readonly stateKey: StateKeyV2;
  readonly sessionEpoch: EpochId;
  readonly stateGeneration: number;
  readonly ledgerEpoch: EpochId;
  readonly transition: StateManifestV2Transition;
  readonly predecessorMarkerId: PredecessorDigest;
  readonly predecessorManifestSha256: PredecessorDigest;
  readonly predecessorLedgerSha256: PredecessorDigest;
  readonly observedSelectorRevision: SelectorRevision;
  readonly manifestSha256: Sha256Hex;
  readonly candidateLedgerSha256: Sha256Hex;
  readonly providerRunMetadataSha256: Sha256Hex;
  readonly metadataSemanticSha256: Sha256Hex;
  readonly consumedInputSha256: Sha256Hex;
  readonly resultSha256: Sha256Hex;
  readonly traceSha256: Sha256Hex;
  readonly producingRunId: string;
  readonly producingRunAttempt: number;
  readonly acceptingRunId: string;
  readonly acceptingRunAttempt: number;
  readonly acceptedAt: string;
}

export interface StateSelectorV1 {
  readonly schemaVersion: 1;
  readonly selectorId: SelectorId;
  readonly stateKey: StateKeyV2;
  readonly previousSelectorRevision: SelectorRevision;
  readonly selectorRevision: `sha256:${Sha256Hex}`;
  readonly acceptedMarkerId: MarkerId;
  readonly candidateId: CandidateId;
  readonly sessionEpoch: EpochId;
  readonly stateGeneration: number;
  readonly ledgerEpoch: EpochId;
  readonly transition: StateManifestV2Transition;
  readonly manifestSha256: Sha256Hex;
  readonly candidateLedgerSha256: Sha256Hex;
  readonly providerRunMetadataSha256: Sha256Hex;
  readonly metadataSemanticSha256: Sha256Hex;
  readonly consumedInputSha256: Sha256Hex;
  readonly resultSha256: Sha256Hex;
  readonly traceSha256: Sha256Hex;
  readonly currentHeadSha: GitSha;
  readonly currentBaseSha: GitSha;
  readonly workflowIdentity: string;
  readonly trustedExecutionDomain: string;
  readonly updatedAt: string;
}

export type RecoveryReason =
  | 'unavailable_accepted_artifact'
  | 'contract_version_incompatible'
  | 'corrupt_accepted_artifact'
  | 'integrity_mismatch'
  | 'unsafe_provenance'
  | 'state_key_mismatch'
  | 'over_bound_ledger';

export type ExplicitRestoreInvalidReason =
  | 'explicit_state_invalid'
  | 'selector_invalid'
  | 'marker_invalid'
  | 'candidate_invalid'
  | 'provenance_invalid'
  | 'state_key_mismatch'
  | 'contract_version_incompatible'
  | 'over_bound_ledger';

export type ObservedCandidateEntry =
  | { readonly status: 'present'; readonly sha256: Sha256Hex }
  | { readonly status: 'missing' }
  | { readonly status: 'unsafe' };

export type RecoveryEvidence =
  | { readonly kind: 'selector_bytes'; readonly sha256: Sha256Hex }
  | { readonly kind: 'marker_reference'; readonly markerId: MarkerId }
  | { readonly kind: 'marker_bytes'; readonly markerId: MarkerId; readonly sha256: Sha256Hex }
  | { readonly kind: 'candidate_reference'; readonly candidateId: CandidateId }
  | {
      readonly kind: 'candidate_bundle';
      readonly candidateId: CandidateId;
      readonly manifest: ObservedCandidateEntry;
      readonly ledger: ObservedCandidateEntry;
      readonly providerRunMetadata: ObservedCandidateEntry;
      readonly bundleDiagnostic: InvalidDiagnosticCode;
    };

export interface SelectionSnapshotCommon {
  readonly schemaVersion: 1;
  readonly stateKey: StateKeyV2;
  readonly currentHeadSha: GitSha;
  readonly currentBaseSha: GitSha;
  readonly currentBaseRef: string;
  readonly observedSelectorBytes: Uint8Array | null;
  readonly observedSelectorRevision: SelectorRevision;
  readonly observedSelectorSnapshotSha256: Sha256Hex;
  readonly selectionSnapshotId: Sha256Hex;
}

export type StateSelectionSnapshot =
  | (SelectionSnapshotCommon & {
      readonly kind: 'bootstrap_selected';
      readonly transitionPlan: 'bootstrap';
    })
  | (SelectionSnapshotCommon & {
      readonly kind: 'recovery_root_selected';
      readonly transitionPlan: 'recovery_root';
      readonly recoveryReason: RecoveryReason;
      readonly recoveryEvidence: readonly RecoveryEvidence[];
    })
  | (SelectionSnapshotCommon & {
      readonly kind: 'continuation_selected';
      readonly transitionPlan: 'continuation';
      readonly markerId: MarkerId;
      readonly predecessorBytes: CandidateBundleBytes;
    })
  | (SelectionSnapshotCommon & {
      readonly kind: 'reset_selected';
      readonly transitionPlan: 'reset';
      readonly markerId: MarkerId;
      readonly predecessorBytes: CandidateBundleBytes;
      readonly resetReason: 'base_change' | 'cache_contract_change' | 'head_history_discontinuity';
    })
  | (SelectionSnapshotCommon & {
      readonly kind: 'explicit_restore_invalid';
      readonly failure: ExplicitRestoreInvalidReason;
    });

export interface FrozenRegistration {
  readonly registrationSequence: DecimalSequence;
  readonly registrationId: RegistrationId;
  readonly registrationRecordSha256: Sha256Hex;
  readonly registrationBytes: Uint8Array;
  readonly registration: CandidateRegistrationV1;
}

export interface CompetingScope {
  readonly stateKey: StateKeyV2;
  readonly sessionEpoch: EpochId;
  readonly observedSelectorRevision: SelectorRevision;
  readonly predecessorMarkerId: PredecessorDigest;
  readonly predecessorManifestSha256: PredecessorDigest;
  readonly predecessorLedgerSha256: PredecessorDigest;
  readonly ledgerEpoch: EpochId;
  readonly targetStateGeneration: number;
  readonly interactionId: Sha256Hex;
}

export interface AcceptanceEnumerationReceipt {
  readonly kind: 'complete';
  readonly matchingRegistrationCount: number;
  readonly matchingRegistrationBytes: number;
}

export interface AcceptanceSnapshot {
  readonly schemaVersion: 1;
  readonly selectionSnapshotId: Sha256Hex;
  readonly expectedObservedSelectorRevision: SelectorRevision;
  readonly currentSelectorRevision: SelectorRevision;
  readonly competingScope: CompetingScope;
  readonly cutoff: DecimalSequence;
  readonly registrations: readonly FrozenRegistration[];
  /**
   * Completeness assertion from the trusted store transaction. Acceptance
   * checks it against the returned projection, but it is not an independent
   * proof against a faulty backend that omits records coherently.
   */
  readonly enumeration: AcceptanceEnumerationReceipt;
  readonly candidateSetDigest: Sha256Hex;
}

export type CandidateUploadOutcome =
  | { readonly kind: 'created'; readonly locator: CandidateArtifactLocator }
  | { readonly kind: 'already_exists_same'; readonly locator: CandidateArtifactLocator }
  | { readonly kind: 'existing_content_conflict' }
  | { readonly kind: 'outcome_unknown'; readonly locator: CandidateArtifactLocator };

export type WriteOutcome<T> =
  | { readonly kind: 'created'; readonly value: T }
  | { readonly kind: 'already_exists_same'; readonly value: T }
  | { readonly kind: 'existing_content_conflict' }
  | { readonly kind: 'outcome_unknown' };

export type SelectorCasOutcome =
  | { readonly kind: 'applied'; readonly selector: StateSelectorV1 }
  | { readonly kind: 'already_applied_same_target'; readonly selector: StateSelectorV1 }
  | { readonly kind: 'rejected_with_current_revision'; readonly currentRevision: SelectorRevision }
  | { readonly kind: 'outcome_unknown' };

export type SelectionFailureReason =
  | 'store_capability_unsupported'
  | 'store_transaction_failed'
  | 'selector_read_failed'
  | 'state_key_mismatch'
  | 'marker_read_failed'
  | 'candidate_read_failed'
  | 'selection_snapshot_limit_exceeded';

export type SelectionOutcome =
  | { readonly selection: 'selected'; readonly snapshot: StateSelectionSnapshot }
  | { readonly selection: 'failed'; readonly reason: SelectionFailureReason }
  | { readonly selection: 'unknown'; readonly reason: 'selection_outcome_unknown' };

export type PublicationOutcome =
  | { readonly status: 'not_attempted' }
  | { readonly status: 'succeeded' }
  | { readonly status: 'failed'; readonly code: 'sticky_callback_failed' }
  | { readonly status: 'unknown'; readonly code: 'sticky_callback_outcome_unknown' }
  | { readonly status: 'pending'; readonly code: 'cancelled_after_acceptance' };

export type NotAcceptedReason =
  | 'stale_candidate'
  | 'semantic_conflict'
  | 'selector_cas_rejected'
  | 'cancelled_before_acceptance'
  | 'store_capability_unsupported'
  | 'selector_invalid'
  | 'marker_invalid'
  | 'registration_invalid'
  | 'candidate_invalid'
  | 'candidate_upload_failed'
  | 'candidate_readback_mismatch'
  | 'registration_write_conflict'
  | 'registration_write_failed'
  | 'marker_write_conflict'
  | 'marker_write_failed'
  | 'candidate_snapshot_invalid'
  | 'candidate_snapshot_limit_exceeded'
  | 'registration_sequence_overflow'
  | 'candidate_set_digest_mismatch'
  | 'store_transaction_failed'
  | 'provenance_invalid'
  | 'unsafe_provenance'
  | 'state_key_mismatch'
  | 'contract_version_incompatible'
  | 'over_bound_ledger';

export type CleanupWarningCode = 'lease_release_failed' | 'lease_release_outcome_unknown';

export type AcceptanceResult =
  | {
      readonly acceptance: 'accepted' | 'already_accepted';
      readonly markerId: MarkerId;
      readonly selectorRevision: `sha256:${Sha256Hex}`;
      readonly publication: PublicationOutcome;
      readonly cleanupWarnings: readonly CleanupWarningCode[];
    }
  | {
      readonly acceptance: 'not_accepted';
      readonly reason: NotAcceptedReason;
      readonly publication: { readonly status: 'not_attempted' };
      readonly cleanupWarnings: readonly CleanupWarningCode[];
    }
  | {
      readonly acceptance: 'unknown';
      readonly reason: 'acceptance_outcome_unknown';
      readonly publication: { readonly status: 'not_attempted' };
      readonly cleanupWarnings: readonly CleanupWarningCode[];
    };

export interface SelectionOptions {
  readonly stateKey: StateKeyV2;
  readonly expectedLedgerSchemaVersion: number;
  readonly expectedPrefixContractVersion: number;
  readonly cacheContractIdentity: Omit<
    CacheContractIdentityV2,
    'ledgerSchemaVersion' | 'prefixContractVersion'
  >;
  readonly currentHeadSha: GitSha;
  readonly currentBaseSha: GitSha;
  readonly currentBaseRef: string;
  readonly provenanceTrusted: boolean;
  readonly workflowIdentity: string;
  readonly trustedExecutionDomain: string;
  readonly headRelationship?: 'same' | 'descendant' | 'non_descendant' | 'unknown';
  readonly explicitRestore?: boolean;
}

export interface AcceptanceOptions {
  readonly selectionSnapshot: StateSelectionSnapshot;
  readonly candidate: CandidateLeaseLike;
  readonly interactionId: Sha256Hex;
  readonly interactionOrdinal: number;
  readonly producingRunId: string;
  readonly producingRunAttempt: number;
  readonly acceptingRunId: string;
  readonly acceptingRunAttempt: number;
  readonly consumedInputSha256: Sha256Hex;
  readonly transition: StateManifestV2Transition;
  readonly signal?: AbortSignal;
  readonly publishSticky?: (markerId: MarkerId) => Promise<void>;
  readonly now?: () => string;
}
