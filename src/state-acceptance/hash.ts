import { createHash } from 'node:crypto';
import { canonicalJsonBytes } from '../canonical-json/index.js';
import type {
  AcceptedStateMarkerV1,
  CandidateBundleBytes,
  CandidateId,
  CandidateRegistrationDraft,
  CandidateRegistrationV1,
  CompetingScope,
  SelectorRevision,
  StateSelectorV1,
  StateSelectionSnapshot,
  Sha256Hex,
} from './types.js';

export const SHA256_HEX = /^[a-f0-9]{64}$/;
export const EPOCH_ID = /^[A-Za-z0-9_-]{22}$/;
export const GIT_SHA = /^(?:[a-f0-9]{40}|[a-f0-9]{64})$/;

const encoder = new TextEncoder();

export function sha256Hex(bytes: Uint8Array): Sha256Hex {
  return createHash('sha256').update(bytes).digest('hex') as Sha256Hex;
}

export function digestId(tag: string, value: unknown): Sha256Hex {
  const tagBytes = encoder.encode(tag);
  const valueBytes = canonicalJsonBytes(value);
  const preimage = new Uint8Array(tagBytes.byteLength + 1 + valueBytes.byteLength);
  preimage.set(tagBytes, 0);
  preimage[tagBytes.byteLength] = 0;
  preimage.set(valueBytes, tagBytes.byteLength + 1);
  return sha256Hex(preimage);
}

export function digestBytesId(tag: string, bytes: Uint8Array): Sha256Hex {
  const tagBytes = encoder.encode(tag);
  const preimage = new Uint8Array(tagBytes.byteLength + 1 + bytes.byteLength);
  preimage.set(tagBytes, 0);
  preimage[tagBytes.byteLength] = 0;
  preimage.set(bytes, tagBytes.byteLength + 1);
  return sha256Hex(preimage);
}

export function computeCandidateId(input: {
  readonly manifestSha256: string;
  readonly candidateLedgerSha256: string;
  readonly providerRunMetadataSha256: string;
  readonly metadataSemanticSha256: string;
  readonly consumedInputSha256: string;
  readonly resultSha256: string;
  readonly traceSha256: string;
}): CandidateId {
  return digestId('agentic-pr-review/m4/candidate/v1', {
    candidateLedgerSha256: input.candidateLedgerSha256,
    consumedInputSha256: input.consumedInputSha256,
    manifestSha256: input.manifestSha256,
    metadataSemanticSha256: input.metadataSemanticSha256,
    providerRunMetadataSha256: input.providerRunMetadataSha256,
    resultSha256: input.resultSha256,
    traceSha256: input.traceSha256,
  }) as CandidateId;
}

export function candidateLocator(candidateId: CandidateId) {
  return {
    kind: 'store-object' as const,
    namespace: 'm4-state-v1' as const,
    objectId: `candidate-${candidateId}` as `candidate-${CandidateId}`,
  };
}

export function candidateBundleSha256(bundle: CandidateBundleBytes): {
  readonly manifestSha256: Sha256Hex;
  readonly candidateLedgerSha256: Sha256Hex;
  readonly providerRunMetadataSha256: Sha256Hex;
} {
  return {
    manifestSha256: sha256Hex(bundle.manifestBytes),
    candidateLedgerSha256: sha256Hex(bundle.ledgerBytes),
    providerRunMetadataSha256: sha256Hex(bundle.providerRunMetadataBytes),
  };
}

function registrationEnvelope(value: CandidateRegistrationDraft | CandidateRegistrationV1) {
  const {
    registrationId: _registrationId,
    registrationSequence: _registrationSequence,
    candidateArtifactLocator: _locator,
    registeredAt: _registeredAt,
    ...semantic
  } = value as CandidateRegistrationV1;
  return semantic;
}

export function computeRegistrationId(value: CandidateRegistrationDraft | CandidateRegistrationV1) {
  return digestId('agentic-pr-review/m4/candidate-registration/v1', registrationEnvelope(value));
}

export function markerEnvelope(value: AcceptedStateMarkerV1) {
  const { markerId: _markerId, acceptedAt: _acceptedAt, ...semantic } = value;
  return semantic;
}

export function computeMarkerId(value: AcceptedStateMarkerV1) {
  return digestId('agentic-pr-review/m4/accepted-state-marker/v1', markerEnvelope(value));
}

export function selectorEnvelope(value: StateSelectorV1) {
  const {
    selectorId: _selectorId,
    selectorRevision: _selectorRevision,
    updatedAt: _updatedAt,
    ...semantic
  } = value;
  return semantic;
}

export function computeSelectorRevision(value: StateSelectorV1): `sha256:${string}` {
  return `sha256:${digestId('agentic-pr-review/m4/state-selector-revision/v1', selectorEnvelope(value))}` as `sha256:${Sha256Hex}`;
}

export function computeSelectorId(value: StateSelectorV1) {
  return digestId('agentic-pr-review/m4/state-selector/v1', selectorEnvelope(value));
}

export function observedSelectorSnapshotSha256(bytes: Uint8Array | null): Sha256Hex {
  return digestBytesId(
    'agentic-pr-review/m4/selector-snapshot/v1',
    bytes === null ? new Uint8Array() : bytes,
  );
}

export function computeCandidateSetDigest(
  scope: CompetingScope,
  cutoff: string,
  registrations: readonly {
    registrationSequence: string;
    registrationId: string;
    registrationRecordSha256: string;
  }[],
): Sha256Hex {
  return digestId('agentic-pr-review/m4/candidate-set/v1', {
    competingScope: scope,
    cutoff,
    registrations,
  });
}

export function computeSelectionSnapshotId(snapshot: StateSelectionSnapshot): Sha256Hex {
  const common = {
    schemaVersion: snapshot.schemaVersion,
    kind: snapshot.kind,
    stateKey: snapshot.stateKey,
    observedSelectorRevision: snapshot.observedSelectorRevision,
    observedSelectorSnapshotSha256: snapshot.observedSelectorSnapshotSha256,
  };
  let branch: Record<string, unknown>;
  switch (snapshot.kind) {
    case 'bootstrap_selected':
      branch = { ...common, transitionPlan: snapshot.transitionPlan };
      break;
    case 'recovery_root_selected':
      branch = {
        ...common,
        transitionPlan: snapshot.transitionPlan,
        recoveryReason: snapshot.recoveryReason,
        recoveryEvidence: snapshot.recoveryEvidence,
      };
      break;
    case 'continuation_selected':
      branch = {
        ...common,
        transitionPlan: snapshot.transitionPlan,
        markerId: snapshot.markerId,
        predecessorManifestSha256: sha256Hex(snapshot.predecessorBytes.manifestBytes),
        predecessorLedgerSha256: sha256Hex(snapshot.predecessorBytes.ledgerBytes),
        predecessorProviderRunMetadataSha256: sha256Hex(
          snapshot.predecessorBytes.providerRunMetadataBytes,
        ),
      };
      break;
    case 'reset_selected':
      branch = {
        ...common,
        transitionPlan: snapshot.transitionPlan,
        markerId: snapshot.markerId,
        predecessorManifestSha256: sha256Hex(snapshot.predecessorBytes.manifestBytes),
        predecessorLedgerSha256: sha256Hex(snapshot.predecessorBytes.ledgerBytes),
        predecessorProviderRunMetadataSha256: sha256Hex(
          snapshot.predecessorBytes.providerRunMetadataBytes,
        ),
        resetReason: snapshot.resetReason,
      };
      break;
    case 'explicit_restore_invalid':
      branch = { ...common, failure: snapshot.failure };
      break;
  }
  return digestId('agentic-pr-review/m4/selection-snapshot/v1', branch);
}

export function isSelectorRevision(value: unknown): value is SelectorRevision {
  return (
    value === 'bootstrap' ||
    (typeof value === 'string' &&
      (/^sha256:[a-f0-9]{64}$/.test(value) || /^invalid:[a-f0-9]{64}$/.test(value)))
  );
}

export function compareDecimalIds(left: string, right: string): number {
  const a = left.replace(/^0+(?=\d)/, '');
  const b = right.replace(/^0+(?=\d)/, '');
  return a.length === b.length ? (a < b ? -1 : a > b ? 1 : 0) : a.length - b.length;
}
