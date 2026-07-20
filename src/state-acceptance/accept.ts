import { canonicalJsonBytes } from '../canonical-json/index.js';
import { bytesEqual } from './codec.js';
import { candidateBundleSha256, compareDecimalIds, computeCandidateId, sha256Hex } from './hash.js';
import { encodeValidatedRecord, materializeMarker, materializeSelector } from './validation.js';
import {
  ReferenceStateStore,
  SelectionSnapshotLimitError,
  SelectorRevisionMismatchError,
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
  store: ReferenceStateStore,
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
  store: ReferenceStateStore,
  options: AcceptanceOptions,
): Promise<AcceptanceResult> {
  if (options.signal?.aborted) return notAccepted('cancelled_before_acceptance');
  if (options.selectionSnapshot.kind === 'explicit_restore_invalid')
    return notAccepted('selector_invalid');

  if (!leaseIdentityIsConsistent(options.candidate)) return notAccepted('candidate_invalid');
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
  const registrationResult = await store.registerCandidate(draft);
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
    if (error instanceof SelectorRevisionMismatchError) return notAccepted('selector_cas_rejected');
    return unknownAcceptance();
  }

  const classification = classify(
    registration,
    snapshot.registrations.map((entry) => entry.registration),
  );
  if (classification === 'stale') return notAccepted('stale_candidate');
  if (classification === 'conflict') return notAccepted('semantic_conflict');
  const winner = classification;

  if (options.signal?.aborted) return notAccepted('cancelled_before_acceptance');
  const marker = materializeMarker(markerInput(winner, options));
  const markerBytes = encodeValidatedRecord(marker);
  let markerWrite;
  try {
    markerWrite = await store.writeMarker(marker);
  } catch {
    return notAccepted('marker_write_failed');
  }
  let acceptedMarker: AcceptedStateMarkerV1;
  if (markerWrite.kind === 'created' || markerWrite.kind === 'already_exists_same') {
    acceptedMarker = markerWrite.value;
  } else if (markerWrite.kind === 'outcome_unknown') {
    const readBack = await store.readMarker(options.selectionSnapshot.stateKey, marker.markerId);
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
  const selector = materializeSelector(selectorInput(acceptedMarker, options));
  const selectorBytes = encodeValidatedRecord(selector);
  let cas;
  try {
    cas = await store.casSelector(options.selectionSnapshot.observedSelectorRevision, selector);
  } catch {
    return notAccepted('store_transaction_failed');
  }
  let selected: 'accepted' | 'already_accepted';
  if (cas.kind === 'applied') selected = 'accepted';
  else if (cas.kind === 'already_applied_same_target') selected = 'already_accepted';
  else if (cas.kind === 'outcome_unknown') {
    const readBack = await store.readSelector(options.selectionSnapshot.stateKey);
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
    transition: options.transition,
    interactionId: options.interactionId,
    interactionOrdinal: options.interactionOrdinal,
    producingRunId: options.producingRunId,
    producingRunAttempt: options.producingRunAttempt,
    consumedInputSha256: options.consumedInputSha256,
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

function classify(
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

function selectorInput(marker: AcceptedStateMarkerV1, options: AcceptanceOptions) {
  const provenance = options.candidate.manifest.provenance;
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
