import { canonicalJsonBytes } from '../canonical-json/index.js';
import { classifyStateBundleV2 } from '../state-v2/index.js';
import type { StateManifestV2 } from '../state-v2/index.js';
import { bytesEqual } from './codec.js';
import {
  candidateBundleSha256,
  compareDecimalIds,
  computeCandidateId,
  computeSelectionSnapshotId,
  observedSelectorSnapshotSha256,
  sha256Hex,
} from './hash.js';
import {
  decodeValidatedSelector,
  encodeValidatedRecord,
  materializeMarker,
  materializeSelector,
} from './validation.js';
import {
  SelectionSnapshotLimitError,
  SelectorRevisionMismatchError,
  StoreTransactionError,
  type StateAcceptanceStore,
} from './store.js';
import type {
  AcceptanceOptions,
  AcceptanceResult,
  AcceptedStateMarkerV1,
  CandidateBundleBytes,
  CandidateId,
  CandidateRegistrationDraft,
  CandidateRegistrationV1,
  CleanupWarningCode,
  CompetingScope,
  NotAcceptedReason,
  PublicationOutcome,
  StateSelectorV1,
  StateSelectionSnapshot,
} from './types.js';

export class StickyCallbackOutcomeUnknownError extends Error {
  constructor() {
    super('sticky callback outcome unknown');
    this.name = 'StickyCallbackOutcomeUnknownError';
  }
}

export interface CandidateIdentity {
  readonly candidateId: CandidateId;
  readonly bundle: CandidateBundleBytes;
  readonly manifestSha256: string;
  readonly candidateLedgerSha256: string;
  readonly providerRunMetadataSha256: string;
  readonly resultSha256: string;
  readonly traceSha256: string;
}

export function candidateIdentity(lease: AcceptanceOptions['candidate']): CandidateIdentity {
  const bundle: CandidateBundleBytes = {
    manifestBytes: new Uint8Array(lease.manifestBytes),
    ledgerBytes: new Uint8Array(lease.ledgerBytes),
    providerRunMetadataBytes: new Uint8Array(lease.providerRunMetadataBytes),
  };
  const bundleHashes = candidateBundleSha256(bundle);
  const resultSha256 = sha256Hex(lease.resultBytes);
  const traceSha256 = sha256Hex(lease.traceBytes);
  return {
    candidateId: computeCandidateId({
      ...bundleHashes,
      metadataSemanticSha256: lease.metadataSemanticSha256,
      consumedInputSha256: lease.inputSha256,
      resultSha256,
      traceSha256,
    }) as CandidateId,
    bundle,
    manifestSha256: bundleHashes.manifestSha256,
    candidateLedgerSha256: bundleHashes.candidateLedgerSha256,
    providerRunMetadataSha256: bundleHashes.providerRunMetadataSha256,
    resultSha256,
    traceSha256,
  };
}

export async function acceptLocalCandidate(
  store: StateAcceptanceStore,
  options: AcceptanceOptions,
): Promise<AcceptanceResult> {
  const warnings: CleanupWarningCode[] = [];
  let result: AcceptanceResult;
  try {
    result = await acceptWithoutCleanup(store, options);
  } catch {
    result = {
      acceptance: 'unknown',
      reason: 'acceptance_outcome_unknown',
      publication: { status: 'not_attempted' },
      cleanupWarnings: [],
    };
  }
  try {
    await options.candidate.release();
  } catch {
    warnings.push('lease_release_failed');
  }
  return addCleanupWarnings(result, warnings);
}

async function acceptWithoutCleanup(
  store: StateAcceptanceStore,
  options: AcceptanceOptions,
): Promise<AcceptanceResult> {
  if (options.signal?.aborted) return notAccepted('cancelled_before_acceptance');
  if (options.selectionSnapshot.kind === 'explicit_restore_invalid')
    return notAccepted('selector_invalid');

  if (!leaseIdentityIsConsistent(options.candidate) || !selectionFactsAreConsistent(options))
    return notAccepted('candidate_invalid');
  const identity = candidateIdentity(options.candidate);
  const upload = await store.uploadCandidate(identity.candidateId, identity.bundle);
  if (upload.kind === 'existing_content_conflict')
    return notAccepted('candidate_readback_mismatch');
  if (upload.kind === 'outcome_unknown') {
    const readBack = await store.readCandidate(identity.candidateId);
    if (readBack.status !== 'present') {
      if (readBack.status === 'unsafe') return notAccepted('candidate_readback_mismatch');
      if (readBack.status === 'missing' && allCandidateEntriesMissing(readBack.evidence)) {
        return notAccepted('candidate_upload_failed');
      }
      return unknownAcceptance();
    }
    if (!bundlesEqual(readBack.bundle, identity.bundle))
      return notAccepted('candidate_readback_mismatch');
  }

  const draft = registrationDraft(options, identity);
  let registrationResult: Awaited<ReturnType<StateAcceptanceStore['registerCandidate']>>;
  try {
    registrationResult = await store.registerCandidate(draft);
  } catch (error) {
    if (error instanceof StoreTransactionError) return notAccepted(error.reason);
    throw error;
  }
  if (registrationResult.kind === 'registration_write_conflict')
    return notAccepted('registration_write_conflict');
  if (registrationResult.kind === 'registration_write_failed')
    return notAccepted('registration_write_failed');
  if (registrationResult.kind === 'registration_sequence_overflow')
    return notAccepted('registration_sequence_overflow');
  if (registrationResult.kind === 'outcome_unknown') {
    return unknownAcceptance();
  }
  const registration = registrationResult.registration;
  if (!registration) return unknownAcceptance();

  const scope = competingScope(registration);
  let snapshot;
  try {
    snapshot = await store.createAcceptanceSnapshot(
      options.selectionSnapshot.observedSelectorRevision,
      scope,
      options.selectionSnapshot.selectionSnapshotId,
    );
  } catch (error) {
    if (error instanceof SelectionSnapshotLimitError)
      return notAccepted('candidate_snapshot_limit_exceeded');
    if (error instanceof SelectorRevisionMismatchError) return notAccepted('stale_candidate');
    if (error instanceof StoreTransactionError) return notAccepted(error.reason);
    return unknownAcceptance();
  }

  const classification = classifyCandidateRegistrations(
    registration,
    snapshot.registrations.map((entry) => entry.registration),
  );
  if (classification === 'stale') return notAccepted('stale_candidate');
  if (classification === 'conflict') return notAccepted('semantic_conflict');
  const winner = classification;
  const winnerCandidate = await store.readCandidate(winner.candidateId);
  if (winnerCandidate.status !== 'present') return notAccepted('candidate_invalid');
  const winnerClassification = classifyStateBundleV2({
    entryListing: [
      { name: 'manifest.json', isRegularFile: true },
      { name: 'ledger.json', isRegularFile: true },
      { name: 'provider-run-metadata.json', isRegularFile: true },
    ],
    manifestBytes: winnerCandidate.bundle.manifestBytes,
    ledgerBytes: winnerCandidate.bundle.ledgerBytes,
    providerRunMetadataBytes: winnerCandidate.bundle.providerRunMetadataBytes,
  });
  if (
    winnerClassification.kind !== 'valid' ||
    !winnerManifestMatchesRegistration(
      winnerClassification.manifest,
      winner,
      candidateBundleSha256(winnerCandidate.bundle),
    )
  )
    return notAccepted('candidate_invalid');
  const winnerManifest = winnerClassification.manifest;

  if (options.signal?.aborted) return notAccepted('cancelled_before_acceptance');
  const marker = materializeMarker(markerInput(winner, options));
  const markerBytes = encodeValidatedRecord(marker);
  let markerWrite;
  try {
    markerWrite = await store.writeMarker(marker);
  } catch (error) {
    if (error instanceof StoreTransactionError) return notAccepted(error.reason);
    return notAccepted('marker_write_failed');
  }
  let acceptedMarker: AcceptedStateMarkerV1;
  if (markerWrite.kind === 'created' || markerWrite.kind === 'already_exists_same') {
    acceptedMarker = markerWrite.value;
  } else if (markerWrite.kind === 'outcome_unknown') {
    let readBack: Awaited<ReturnType<StateAcceptanceStore['readMarker']>>;
    try {
      readBack = await store.readMarker(options.selectionSnapshot.stateKey, marker.markerId);
    } catch (error) {
      if (error instanceof StoreTransactionError) return notAccepted(error.reason);
      return unknownAcceptance();
    }
    if (
      readBack.marker === null ||
      readBack.bytes === null ||
      !bytesEqual(readBack.bytes, markerBytes)
    )
      return unknownAcceptance();
    acceptedMarker = readBack.marker;
  } else {
    return notAccepted('marker_write_conflict');
  }

  if (options.signal?.aborted) return notAccepted('cancelled_before_acceptance');
  const selector = materializeSelector(selectorInput(acceptedMarker, options, winnerManifest));
  const selectorBytes = encodeValidatedRecord(selector);
  let cas;
  try {
    cas = await store.casSelector(options.selectionSnapshot.observedSelectorRevision, selector);
  } catch (error) {
    if (error instanceof StoreTransactionError) return notAccepted(error.reason);
    return notAccepted('store_transaction_failed');
  }
  let selected: 'accepted' | 'already_accepted';
  if (cas.kind === 'applied') selected = 'accepted';
  else if (cas.kind === 'already_applied_same_target') selected = 'already_accepted';
  else if (cas.kind === 'outcome_unknown') {
    let readBack: Awaited<ReturnType<StateAcceptanceStore['readSelector']>>;
    try {
      readBack = await store.readSelector(options.selectionSnapshot.stateKey);
    } catch (error) {
      if (error instanceof StoreTransactionError) return notAccepted(error.reason);
      return unknownAcceptance();
    }
    if (
      readBack.selector?.selectorId !== selector.selectorId ||
      readBack.bytes === null ||
      !bytesEqual(readBack.bytes, selectorBytes)
    )
      return unknownAcceptance();
    selected = 'accepted';
  } else return notAccepted('selector_cas_rejected');

  let publication: PublicationOutcome = { status: 'not_attempted' };
  if (options.signal?.aborted) {
    publication = { status: 'pending', code: 'cancelled_after_acceptance' };
  } else if (options.publishSticky) {
    try {
      await options.publishSticky(acceptedMarker.markerId);
      publication = { status: 'succeeded' };
    } catch (error) {
      publication =
        error instanceof StickyCallbackOutcomeUnknownError
          ? { status: 'unknown', code: 'sticky_callback_outcome_unknown' }
          : { status: 'failed', code: 'sticky_callback_failed' };
    }
  }
  return {
    acceptance: selected,
    markerId: acceptedMarker.markerId,
    selectorRevision: selector.selectorRevision,
    publication,
    cleanupWarnings: [],
  };
}

function registrationDraft(
  options: AcceptanceOptions,
  identity: CandidateIdentity,
): CandidateRegistrationDraft {
  const predecessor = predecessorFor(options.selectionSnapshot);
  return {
    schemaVersion: 1,
    candidateId: identity.candidateId,
    observedSelectorRevision: options.selectionSnapshot.observedSelectorRevision,
    observedSelectorSnapshotSha256: options.selectionSnapshot
      .observedSelectorSnapshotSha256 as CandidateRegistrationDraft['observedSelectorSnapshotSha256'],
    predecessorMarkerId: predecessor.markerId,
    predecessorManifestSha256: predecessor.manifestSha256,
    predecessorLedgerSha256: predecessor.ledgerSha256,
    stateKey: options.selectionSnapshot.stateKey,
    sessionEpoch: options.candidate.manifest.sessionEpoch,
    stateGeneration: options.candidate.manifest.generation.stateGeneration,
    ledgerEpoch: options.candidate.manifest.generation.ledgerEpoch,
    transition: options.candidate.manifest.transition,
    interactionId: options.candidate.manifest.transaction.interactionId,
    interactionOrdinal: options.candidate.manifest.transaction.interactionOrdinal,
    producingRunId: options.candidate.manifest.provenance.producingRunId,
    producingRunAttempt: options.candidate.manifest.provenance.producingRunAttempt,
    consumedInputSha256: options.candidate.manifest.transaction.consumedInputSha256,
    manifestSha256: identity.manifestSha256 as CandidateRegistrationDraft['manifestSha256'],
    candidateLedgerSha256:
      identity.candidateLedgerSha256 as CandidateRegistrationDraft['candidateLedgerSha256'],
    providerRunMetadataSha256:
      identity.providerRunMetadataSha256 as CandidateRegistrationDraft['providerRunMetadataSha256'],
    metadataSemanticSha256: options.candidate
      .metadataSemanticSha256 as CandidateRegistrationDraft['metadataSemanticSha256'],
    resultSha256: identity.resultSha256 as CandidateRegistrationDraft['resultSha256'],
    traceSha256: identity.traceSha256 as CandidateRegistrationDraft['traceSha256'],
  };
}

function predecessorFor(snapshot: StateSelectionSnapshot) {
  if (snapshot.kind !== 'continuation_selected' && snapshot.kind !== 'reset_selected') {
    return {
      markerId: 'bootstrap' as const,
      manifestSha256: 'bootstrap' as const,
      ledgerSha256: 'bootstrap' as const,
    };
  }
  return {
    markerId: snapshot.markerId,
    manifestSha256: sha256Hex(
      snapshot.predecessorBytes.manifestBytes,
    ) as CandidateRegistrationDraft['predecessorManifestSha256'],
    ledgerSha256: sha256Hex(
      snapshot.predecessorBytes.ledgerBytes,
    ) as CandidateRegistrationDraft['predecessorLedgerSha256'],
  };
}

function competingScope(registration: CandidateRegistrationV1): CompetingScope {
  return {
    stateKey: registration.stateKey,
    sessionEpoch: registration.sessionEpoch,
    observedSelectorRevision: registration.observedSelectorRevision,
    predecessorMarkerId: registration.predecessorMarkerId,
    predecessorManifestSha256: registration.predecessorManifestSha256,
    predecessorLedgerSha256: registration.predecessorLedgerSha256,
    targetStateGeneration: registration.stateGeneration,
    interactionId: registration.interactionId,
  };
}

export function classifyCandidateRegistrations(
  current: CandidateRegistrationV1,
  registrations: readonly CandidateRegistrationV1[],
): CandidateRegistrationV1 | 'stale' | 'conflict' {
  const currentScope = competingScope(current);
  const sameScope = registrations.filter((entry) =>
    bytesEqual(canonicalJsonBytes(competingScope(entry)), canonicalJsonBytes(currentScope)),
  );
  const duplicates = sameScope.filter((entry) => duplicateKey(entry) === duplicateKey(current));
  if (sameScope.some((entry) => duplicateKey(entry) !== duplicateKey(current))) return 'conflict';
  const winner = [...duplicates].sort((left, right) => {
    const runOrder = compareDecimalIds(left.producingRunId, right.producingRunId);
    return runOrder === 0 ? left.producingRunAttempt - right.producingRunAttempt : runOrder;
  })[0];
  return winner?.registrationId === current.registrationId ? current : 'stale';
}

function duplicateKey(registration: CandidateRegistrationV1): string {
  return JSON.stringify([
    registration.sessionEpoch,
    registration.observedSelectorRevision,
    registration.interactionId,
    registration.predecessorLedgerSha256,
    registration.candidateLedgerSha256,
    registration.resultSha256,
    registration.traceSha256,
    registration.metadataSemanticSha256,
  ]);
}

function markerInput(
  registration: CandidateRegistrationV1,
  options: AcceptanceOptions,
): Omit<AcceptedStateMarkerV1, 'markerId' | 'acceptedAt'> {
  return {
    schemaVersion: 1,
    candidateId: registration.candidateId,
    registrationId: registration.registrationId,
    stateKey: registration.stateKey,
    sessionEpoch: registration.sessionEpoch,
    stateGeneration: registration.stateGeneration,
    ledgerEpoch: registration.ledgerEpoch,
    transition: registration.transition,
    predecessorMarkerId: registration.predecessorMarkerId,
    predecessorManifestSha256: registration.predecessorManifestSha256,
    predecessorLedgerSha256: registration.predecessorLedgerSha256,
    observedSelectorRevision: registration.observedSelectorRevision,
    manifestSha256: registration.manifestSha256,
    candidateLedgerSha256: registration.candidateLedgerSha256,
    providerRunMetadataSha256: registration.providerRunMetadataSha256,
    metadataSemanticSha256: registration.metadataSemanticSha256,
    consumedInputSha256: registration.consumedInputSha256,
    resultSha256: registration.resultSha256,
    traceSha256: registration.traceSha256,
    producingRunId: registration.producingRunId,
    producingRunAttempt: registration.producingRunAttempt,
    acceptingRunId: options.acceptingRunId,
    acceptingRunAttempt: options.acceptingRunAttempt,
  };
}

function selectorInput(
  marker: AcceptedStateMarkerV1,
  options: AcceptanceOptions,
  manifest: AcceptanceOptions['candidate']['manifest'],
) {
  const provenance = manifest.provenance;
  return {
    schemaVersion: 1 as const,
    stateKey: marker.stateKey,
    previousSelectorRevision: options.selectionSnapshot.observedSelectorRevision,
    acceptedMarkerId: marker.markerId,
    candidateId: marker.candidateId,
    sessionEpoch: marker.sessionEpoch,
    stateGeneration: marker.stateGeneration,
    ledgerEpoch: marker.ledgerEpoch,
    transition: marker.transition,
    manifestSha256: marker.manifestSha256,
    candidateLedgerSha256: marker.candidateLedgerSha256,
    providerRunMetadataSha256: marker.providerRunMetadataSha256,
    metadataSemanticSha256: marker.metadataSemanticSha256,
    consumedInputSha256: marker.consumedInputSha256,
    resultSha256: marker.resultSha256,
    traceSha256: marker.traceSha256,
    currentHeadSha: provenance.currentHeadSha,
    currentBaseSha: provenance.currentBaseSha,
    workflowIdentity: marker.stateKey.workflowIdentity,
    trustedExecutionDomain: marker.stateKey.trustedExecutionDomain,
  };
}

function notAccepted(reason: NotAcceptedReason): AcceptanceResult {
  return {
    acceptance: 'not_accepted',
    reason,
    publication: { status: 'not_attempted' },
    cleanupWarnings: [],
  };
}

function unknownAcceptance(): AcceptanceResult {
  return {
    acceptance: 'unknown',
    reason: 'acceptance_outcome_unknown',
    publication: { status: 'not_attempted' },
    cleanupWarnings: [],
  };
}

function addCleanupWarnings(
  result: AcceptanceResult,
  warnings: readonly CleanupWarningCode[],
): AcceptanceResult {
  if (warnings.length === 0) return result;
  return { ...result, cleanupWarnings: [...result.cleanupWarnings, ...warnings] };
}

function bundlesEqual(left: CandidateBundleBytes, right: CandidateBundleBytes): boolean {
  return (
    left.manifestBytes.length === right.manifestBytes.length &&
    left.manifestBytes.every((value, index) => value === right.manifestBytes[index]) &&
    left.ledgerBytes.length === right.ledgerBytes.length &&
    left.ledgerBytes.every((value, index) => value === right.ledgerBytes[index]) &&
    left.providerRunMetadataBytes.length === right.providerRunMetadataBytes.length &&
    left.providerRunMetadataBytes.every(
      (value, index) => value === right.providerRunMetadataBytes[index],
    )
  );
}

function allCandidateEntriesMissing(evidence: {
  readonly manifest: { readonly status: string };
  readonly ledger: { readonly status: string };
  readonly providerRunMetadata: { readonly status: string };
}): boolean {
  return (
    evidence.manifest.status === 'missing' &&
    evidence.ledger.status === 'missing' &&
    evidence.providerRunMetadata.status === 'missing'
  );
}

function leaseIdentityIsConsistent(lease: AcceptanceOptions['candidate']): boolean {
  const manifestBytes = new Uint8Array(lease.manifestBytes);
  const expectedManifestBytes = canonicalJsonBytes(lease.manifest);
  if (!bytesEqual(manifestBytes, expectedManifestBytes)) return false;
  const classification = classifyStateBundleV2({
    entryListing: [
      { name: 'manifest.json', isRegularFile: true },
      { name: 'ledger.json', isRegularFile: true },
      { name: 'provider-run-metadata.json', isRegularFile: true },
    ],
    manifestBytes: lease.manifestBytes,
    ledgerBytes: lease.ledgerBytes,
    providerRunMetadataBytes: lease.providerRunMetadataBytes,
  });
  if (classification.kind !== 'valid') return false;
  const bundleHashes = candidateBundleSha256(lease);
  const transaction = lease.manifest.transaction;
  return (
    bundleHashes.candidateLedgerSha256 === transaction.candidateLedgerSha256 &&
    bundleHashes.candidateLedgerSha256 === lease.candidateLedgerSha256 &&
    bundleHashes.providerRunMetadataSha256 === lease.manifest.providerRunMetadata.sha256 &&
    lease.metadataSemanticSha256 === transaction.metadataSemanticSha256 &&
    lease.inputSha256 === transaction.consumedInputSha256 &&
    sha256Hex(lease.resultBytes) === transaction.resultSha256 &&
    lease.resultSha256 === transaction.resultSha256 &&
    sha256Hex(lease.traceBytes) === transaction.traceSha256 &&
    lease.traceSha256 === transaction.traceSha256
  );
}

function selectionFactsAreConsistent(options: AcceptanceOptions): boolean {
  const snapshot = options.selectionSnapshot;
  if (computeSelectionSnapshotId(snapshot) !== snapshot.selectionSnapshotId) return false;
  if (
    observedSelectorSnapshotSha256(snapshot.observedSelectorBytes) !==
    snapshot.observedSelectorSnapshotSha256
  )
    return false;
  if (
    !bytesEqual(
      canonicalJsonBytes(snapshot.stateKey),
      canonicalJsonBytes(options.candidate.manifest.stateKey),
    )
  )
    return false;
  if (
    !bytesEqual(
      canonicalJsonBytes(options.transition),
      canonicalJsonBytes(options.candidate.manifest.transition),
    ) ||
    options.interactionId !== options.candidate.manifest.transaction.interactionId ||
    options.interactionOrdinal !== options.candidate.manifest.transaction.interactionOrdinal ||
    options.producingRunId !== options.candidate.manifest.provenance.producingRunId ||
    options.producingRunAttempt !== options.candidate.manifest.provenance.producingRunAttempt ||
    options.consumedInputSha256 !== options.candidate.manifest.transaction.consumedInputSha256
  )
    return false;
  if (!observedRevisionMatchesBytes(snapshot)) return false;
  const observedSelector = observedSelectorForSnapshot(snapshot);
  if (snapshot.observedSelectorBytes !== null && observedSelector === undefined) {
    if (snapshot.kind !== 'recovery_root_selected') return false;
  }
  if (
    observedSelector !== null &&
    observedSelector !== undefined &&
    !bytesEqual(
      canonicalJsonBytes(observedSelector.stateKey),
      canonicalJsonBytes(snapshot.stateKey),
    )
  )
    return false;
  const predecessor =
    snapshot.kind === 'continuation_selected' || snapshot.kind === 'reset_selected'
      ? predecessorFacts(snapshot.predecessorBytes)
      : null;
  if (
    (snapshot.kind === 'continuation_selected' || snapshot.kind === 'reset_selected') &&
    (predecessor === null ||
      observedSelector === null ||
      observedSelector === undefined ||
      !selectorPredecessorMatchesSnapshot(observedSelector, snapshot, predecessor) ||
      options.candidate.manifest.sessionEpoch !== predecessor.manifest.sessionEpoch)
  )
    return false;
  if (
    observedSelector !== null &&
    observedSelector !== undefined &&
    snapshot.kind === 'recovery_root_selected' &&
    (options.candidate.manifest.sessionEpoch === observedSelector.sessionEpoch ||
      options.candidate.manifest.generation.ledgerEpoch === observedSelector.ledgerEpoch)
  )
    return false;
  return selectionPlanMatchesManifest(snapshot, options.candidate.manifest);
}

function observedSelectorForSnapshot(
  snapshot: StateSelectionSnapshot,
): StateSelectorV1 | null | undefined {
  if (snapshot.observedSelectorBytes === null) return null;
  try {
    return decodeValidatedSelector(snapshot.observedSelectorBytes);
  } catch {
    return undefined;
  }
}

function selectorPredecessorMatchesSnapshot(
  selector: StateSelectorV1,
  snapshot: Extract<StateSelectionSnapshot, { kind: 'continuation_selected' | 'reset_selected' }>,
  predecessor: PredecessorFacts,
): boolean {
  return (
    selector.acceptedMarkerId === snapshot.markerId &&
    bytesEqual(
      canonicalJsonBytes(selector.stateKey),
      canonicalJsonBytes(predecessor.manifest.stateKey),
    ) &&
    selector.sessionEpoch === predecessor.manifest.sessionEpoch &&
    selector.stateGeneration === predecessor.manifest.generation.stateGeneration &&
    selector.ledgerEpoch === predecessor.manifest.generation.ledgerEpoch &&
    bytesEqual(
      canonicalJsonBytes(selector.transition),
      canonicalJsonBytes(predecessor.manifest.transition),
    ) &&
    selector.manifestSha256 === sha256Hex(snapshot.predecessorBytes.manifestBytes) &&
    selector.candidateLedgerSha256 === sha256Hex(snapshot.predecessorBytes.ledgerBytes) &&
    selector.providerRunMetadataSha256 ===
      sha256Hex(snapshot.predecessorBytes.providerRunMetadataBytes) &&
    selector.candidateLedgerSha256 === predecessor.manifest.transaction.candidateLedgerSha256 &&
    selector.providerRunMetadataSha256 === predecessor.manifest.providerRunMetadata.sha256 &&
    selector.metadataSemanticSha256 === predecessor.manifest.transaction.metadataSemanticSha256 &&
    selector.consumedInputSha256 === predecessor.manifest.transaction.consumedInputSha256 &&
    selector.resultSha256 === predecessor.manifest.transaction.resultSha256 &&
    selector.traceSha256 === predecessor.manifest.transaction.traceSha256 &&
    selector.currentHeadSha === predecessor.manifest.provenance.currentHeadSha &&
    selector.currentBaseSha === predecessor.manifest.provenance.currentBaseSha &&
    selector.workflowIdentity === predecessor.manifest.stateKey.workflowIdentity &&
    selector.trustedExecutionDomain === predecessor.manifest.stateKey.trustedExecutionDomain
  );
}

function observedRevisionMatchesBytes(snapshot: StateSelectionSnapshot): boolean {
  if (snapshot.observedSelectorBytes === null)
    return snapshot.observedSelectorRevision === 'bootstrap';
  try {
    return (
      decodeValidatedSelector(snapshot.observedSelectorBytes).selectorRevision ===
      snapshot.observedSelectorRevision
    );
  } catch {
    return (
      snapshot.observedSelectorRevision === `invalid:${sha256Hex(snapshot.observedSelectorBytes)}`
    );
  }
}

function selectionPlanMatchesManifest(
  snapshot: StateSelectionSnapshot,
  manifest: AcceptanceOptions['candidate']['manifest'],
): boolean {
  switch (snapshot.kind) {
    case 'bootstrap_selected':
      return manifest.transition.kind === 'bootstrap';
    case 'recovery_root_selected':
      return (
        manifest.transition.kind === 'recovery_root' &&
        manifest.transition.reason === snapshot.recoveryReason
      );
    case 'continuation_selected': {
      const predecessor = predecessorFacts(snapshot.predecessorBytes);
      return (
        predecessor !== null &&
        manifest.transition.kind === 'continuation' &&
        manifest.sessionEpoch === predecessor.manifest.sessionEpoch &&
        manifest.transition.predecessorManifestSha256 ===
          sha256Hex(snapshot.predecessorBytes.manifestBytes) &&
        manifest.transition.predecessorLedgerSha256 ===
          sha256Hex(snapshot.predecessorBytes.ledgerBytes) &&
        manifest.transition.predecessorStateGeneration === predecessor.stateGeneration &&
        manifest.transition.predecessorLedgerEpoch === predecessor.ledgerEpoch
      );
    }
    case 'reset_selected': {
      const predecessor = predecessorFacts(snapshot.predecessorBytes);
      return (
        predecessor !== null &&
        manifest.transition.kind === 'reset' &&
        manifest.sessionEpoch === predecessor.manifest.sessionEpoch &&
        manifest.transition.reason === snapshot.resetReason &&
        manifest.transition.predecessorManifestSha256 ===
          sha256Hex(snapshot.predecessorBytes.manifestBytes) &&
        manifest.transition.predecessorLedgerSha256 ===
          sha256Hex(snapshot.predecessorBytes.ledgerBytes) &&
        manifest.transition.predecessorStateGeneration === predecessor.stateGeneration &&
        manifest.transition.predecessorLedgerEpoch === predecessor.ledgerEpoch
      );
    }
    case 'explicit_restore_invalid':
      return false;
  }
}

type PredecessorFacts = {
  readonly manifest: StateManifestV2;
  readonly stateGeneration: number;
  readonly ledgerEpoch: string;
};

function predecessorFacts(bytes: CandidateBundleBytes): PredecessorFacts | null {
  const classification = classifyStateBundleV2({
    entryListing: [
      { name: 'manifest.json', isRegularFile: true },
      { name: 'ledger.json', isRegularFile: true },
      { name: 'provider-run-metadata.json', isRegularFile: true },
    ],
    manifestBytes: bytes.manifestBytes,
    ledgerBytes: bytes.ledgerBytes,
    providerRunMetadataBytes: bytes.providerRunMetadataBytes,
  });
  return classification.kind === 'valid'
    ? {
        manifest: classification.manifest,
        stateGeneration: classification.manifest.generation.stateGeneration,
        ledgerEpoch: classification.manifest.generation.ledgerEpoch,
      }
    : null;
}

function winnerManifestMatchesRegistration(
  manifest: AcceptanceOptions['candidate']['manifest'],
  registration: CandidateRegistrationV1,
  hashes: ReturnType<typeof candidateBundleSha256>,
): boolean {
  return (
    bytesEqual(canonicalJsonBytes(manifest.stateKey), canonicalJsonBytes(registration.stateKey)) &&
    manifest.sessionEpoch === registration.sessionEpoch &&
    manifest.generation.stateGeneration === registration.stateGeneration &&
    manifest.generation.ledgerEpoch === registration.ledgerEpoch &&
    bytesEqual(
      canonicalJsonBytes(manifest.transition),
      canonicalJsonBytes(registration.transition),
    ) &&
    manifest.transaction.interactionId === registration.interactionId &&
    manifest.transaction.interactionOrdinal === registration.interactionOrdinal &&
    manifest.provenance.producingRunId === registration.producingRunId &&
    manifest.provenance.producingRunAttempt === registration.producingRunAttempt &&
    manifest.transaction.consumedInputSha256 === registration.consumedInputSha256 &&
    hashes.manifestSha256 === registration.manifestSha256 &&
    hashes.candidateLedgerSha256 === registration.candidateLedgerSha256 &&
    hashes.providerRunMetadataSha256 === registration.providerRunMetadataSha256 &&
    manifest.ledger.sha256 === registration.candidateLedgerSha256 &&
    manifest.providerRunMetadata.sha256 === registration.providerRunMetadataSha256 &&
    manifest.transaction.candidateLedgerSha256 === registration.candidateLedgerSha256 &&
    manifest.transaction.metadataSemanticSha256 === registration.metadataSemanticSha256 &&
    manifest.transaction.resultSha256 === registration.resultSha256 &&
    manifest.transaction.traceSha256 === registration.traceSha256
  );
}
