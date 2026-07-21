import {
  EPOCH_ID,
  GIT_SHA,
  SHA256_HEX,
  candidateLocator,
  computeCandidateId,
  computeMarkerId,
  computeRegistrationId,
  computeSelectorId,
  computeSelectorRevision,
  isSelectorRevision,
} from './hash.js';
import { Ajv, type ErrorObject, type ValidateFunction } from 'ajv';
import registrationSchema from '../../protocol/schemas/candidate-registration.v1.json' with { type: 'json' };
import markerSchema from '../../protocol/schemas/accepted-state-marker.v1.json' with { type: 'json' };
import selectorSchema from '../../protocol/schemas/state-selector.v1.json' with { type: 'json' };
import {
  assertCanonicalRecord,
  encodeRecord,
  parseRecord,
  validateRecordUnicode,
} from './codec.js';
import type {
  AcceptedStateMarkerV1,
  CandidateId,
  CandidateRegistrationDraft,
  CandidateRegistrationV1,
  DecimalSequence,
  EpochId,
  MarkerId,
  SelectorRevision,
  StateKeyV2,
  StateSelectorV1,
} from './types.js';
import type { StateManifestV2Transition } from '../state-v2/index.js';

const STATE_KEY_KEYS = [
  'namespace',
  'repository',
  'headRepository',
  'pullRequest',
  'workflowIdentity',
  'trustedExecutionDomain',
] as const;
const TRANSITION_KEYS: Record<string, readonly string[]> = {
  bootstrap: ['kind', 'predecessorManifestSha256', 'predecessorLedgerSha256', 'reason'],
  continuation: [
    'kind',
    'predecessorManifestSha256',
    'predecessorLedgerSha256',
    'predecessorStateGeneration',
    'predecessorLedgerEpoch',
  ],
  reset: [
    'kind',
    'predecessorManifestSha256',
    'predecessorLedgerSha256',
    'predecessorStateGeneration',
    'predecessorLedgerEpoch',
    'reason',
  ],
  recovery_root: ['kind', 'predecessorManifestSha256', 'predecessorLedgerSha256', 'reason'],
};
const REGISTRATION_KEYS = [
  'schemaVersion',
  'registrationId',
  'registrationSequence',
  'candidateId',
  'candidateArtifactLocator',
  'observedSelectorRevision',
  'observedSelectorSnapshotSha256',
  'predecessorMarkerId',
  'predecessorManifestSha256',
  'predecessorLedgerSha256',
  'stateKey',
  'sessionEpoch',
  'stateGeneration',
  'ledgerEpoch',
  'transition',
  'interactionId',
  'interactionOrdinal',
  'producingRunId',
  'producingRunAttempt',
  'consumedInputSha256',
  'manifestSha256',
  'candidateLedgerSha256',
  'providerRunMetadataSha256',
  'metadataSemanticSha256',
  'resultSha256',
  'traceSha256',
  'registeredAt',
] as const;
const MARKER_KEYS = [
  'schemaVersion',
  'markerId',
  'candidateId',
  'registrationId',
  'stateKey',
  'sessionEpoch',
  'stateGeneration',
  'ledgerEpoch',
  'transition',
  'predecessorMarkerId',
  'predecessorManifestSha256',
  'predecessorLedgerSha256',
  'observedSelectorRevision',
  'manifestSha256',
  'candidateLedgerSha256',
  'providerRunMetadataSha256',
  'metadataSemanticSha256',
  'consumedInputSha256',
  'resultSha256',
  'traceSha256',
  'producingRunId',
  'producingRunAttempt',
  'acceptingRunId',
  'acceptingRunAttempt',
  'acceptedAt',
] as const;
const SELECTOR_KEYS = [
  'schemaVersion',
  'selectorId',
  'stateKey',
  'previousSelectorRevision',
  'selectorRevision',
  'acceptedMarkerId',
  'candidateId',
  'sessionEpoch',
  'stateGeneration',
  'ledgerEpoch',
  'transition',
  'manifestSha256',
  'candidateLedgerSha256',
  'providerRunMetadataSha256',
  'metadataSemanticSha256',
  'consumedInputSha256',
  'resultSha256',
  'traceSha256',
  'currentHeadSha',
  'currentBaseSha',
  'workflowIdentity',
  'trustedExecutionDomain',
  'updatedAt',
] as const;

type RecordKind = 'registration' | 'marker' | 'selector';
const closedSchemaAjv = new Ajv({ strict: true, allErrors: true });
const CLOSED_SCHEMA_VALIDATORS: Record<RecordKind, ValidateFunction<unknown>> = {
  registration: closedSchemaAjv.compile(registrationSchema),
  marker: closedSchemaAjv.compile(markerSchema),
  selector: closedSchemaAjv.compile(selectorSchema),
};

export const CONTRACT_VALIDATION_CODES = [
  'candidate_id_mismatch',
  'epoch_invalid',
  'git_sha_invalid',
  'integer_out_of_range',
  'locator_invalid',
  'marker_id_mismatch',
  'object_required',
  'predecessor_binding_invalid',
  'predecessor_invalid',
  'registration_id_mismatch',
  'run_id_invalid',
  'schema_version_invalid',
  'schema_version_unsupported',
  'selector_id_mismatch',
  'selector_revision_invalid',
  'selector_revision_mismatch',
  'sequence_invalid',
  'sequence_out_of_range',
  'sha256_invalid',
  'state_key_invalid',
  'string_invalid',
  'timestamp_invalid',
  'transition_invalid',
  'unknown_or_missing_field',
] as const;
export type ContractValidationCode = (typeof CONTRACT_VALIDATION_CODES)[number];

export class ContractValidationError extends Error {
  readonly code: ContractValidationCode;
  readonly path: string;

  constructor(code: ContractValidationCode, path = '') {
    super(`state acceptance contract invalid: ${code}${path ? ` at ${path}` : ''}`);
    this.name = 'ContractValidationError';
    this.code = code;
    this.path = path;
  }
}

export function validateCandidateRegistration(
  value: unknown,
): asserts value is CandidateRegistrationV1 {
  validateRecordUnicode(value);
  const record = object(value);
  exactKeys(record, REGISTRATION_KEYS);
  schemaVersion(record.schemaVersion, '/schemaVersion');
  sha(record.registrationId, '/registrationId');
  sequence(record.registrationSequence, '/registrationSequence');
  sha(record.candidateId, '/candidateId');
  validateLocator(record.candidateArtifactLocator, record.candidateId as CandidateId);
  selectorRevision(record.observedSelectorRevision, '/observedSelectorRevision');
  sha(record.observedSelectorSnapshotSha256, '/observedSelectorSnapshotSha256');
  predecessor(record.predecessorMarkerId, '/predecessorMarkerId');
  predecessor(record.predecessorManifestSha256, '/predecessorManifestSha256');
  predecessor(record.predecessorLedgerSha256, '/predecessorLedgerSha256');
  validateStateKey(record.stateKey);
  epoch(record.sessionEpoch, '/sessionEpoch');
  boundedInt(record.stateGeneration, 0, 1_000_000, '/stateGeneration');
  epoch(record.ledgerEpoch, '/ledgerEpoch');
  validateTransition(record.transition);
  validateRecordPredecessorBinding(record);
  sha(record.interactionId, '/interactionId');
  boundedInt(record.interactionOrdinal, 0, 1_000_000, '/interactionOrdinal');
  runId(record.producingRunId, '/producingRunId');
  boundedInt(record.producingRunAttempt, 1, 2_147_483_647, '/producingRunAttempt');
  for (const key of [
    'consumedInputSha256',
    'manifestSha256',
    'candidateLedgerSha256',
    'providerRunMetadataSha256',
    'metadataSemanticSha256',
    'resultSha256',
    'traceSha256',
  ])
    sha(record[key], `/${key}`);
  timestamp(record.registeredAt, '/registeredAt');
  if (
    computeRegistrationId(record as unknown as CandidateRegistrationV1) !== record.registrationId
  ) {
    throw new ContractValidationError('registration_id_mismatch', '/registrationId');
  }
  const expectedCandidateId = computeCandidateId(record as unknown as CandidateRegistrationV1);
  if (expectedCandidateId !== record.candidateId) {
    throw new ContractValidationError('candidate_id_mismatch', '/candidateId');
  }
}

export function validateAcceptedStateMarker(
  value: unknown,
): asserts value is AcceptedStateMarkerV1 {
  validateRecordUnicode(value);
  const record = object(value);
  exactKeys(record, MARKER_KEYS);
  schemaVersion(record.schemaVersion, '/schemaVersion');
  sha(record.markerId, '/markerId');
  sha(record.candidateId, '/candidateId');
  sha(record.registrationId, '/registrationId');
  validateStateKey(record.stateKey);
  epoch(record.sessionEpoch, '/sessionEpoch');
  boundedInt(record.stateGeneration, 0, 1_000_000, '/stateGeneration');
  epoch(record.ledgerEpoch, '/ledgerEpoch');
  validateTransition(record.transition);
  validateRecordPredecessorBinding(record);
  predecessor(record.predecessorMarkerId, '/predecessorMarkerId');
  predecessor(record.predecessorManifestSha256, '/predecessorManifestSha256');
  predecessor(record.predecessorLedgerSha256, '/predecessorLedgerSha256');
  selectorRevision(record.observedSelectorRevision, '/observedSelectorRevision');
  for (const key of [
    'manifestSha256',
    'candidateLedgerSha256',
    'providerRunMetadataSha256',
    'metadataSemanticSha256',
    'consumedInputSha256',
    'resultSha256',
    'traceSha256',
  ])
    sha(record[key], `/${key}`);
  runId(record.producingRunId, '/producingRunId');
  boundedInt(record.producingRunAttempt, 1, 2_147_483_647, '/producingRunAttempt');
  runId(record.acceptingRunId, '/acceptingRunId');
  boundedInt(record.acceptingRunAttempt, 1, 2_147_483_647, '/acceptingRunAttempt');
  timestamp(record.acceptedAt, '/acceptedAt');
  if (computeMarkerId(record as unknown as AcceptedStateMarkerV1) !== record.markerId) {
    throw new ContractValidationError('marker_id_mismatch', '/markerId');
  }
}

export function validateStateSelector(value: unknown): asserts value is StateSelectorV1 {
  validateRecordUnicode(value);
  const record = object(value);
  exactKeys(record, SELECTOR_KEYS);
  schemaVersion(record.schemaVersion, '/schemaVersion');
  sha(record.selectorId, '/selectorId');
  validateStateKey(record.stateKey);
  selectorRevision(record.previousSelectorRevision, '/previousSelectorRevision');
  if (
    typeof record.selectorRevision !== 'string' ||
    !/^sha256:[a-f0-9]{64}$/.test(record.selectorRevision)
  ) {
    throw new ContractValidationError('selector_revision_invalid', '/selectorRevision');
  }
  sha(record.acceptedMarkerId, '/acceptedMarkerId');
  sha(record.candidateId, '/candidateId');
  epoch(record.sessionEpoch, '/sessionEpoch');
  boundedInt(record.stateGeneration, 0, 1_000_000, '/stateGeneration');
  epoch(record.ledgerEpoch, '/ledgerEpoch');
  validateTransition(record.transition);
  for (const key of [
    'manifestSha256',
    'candidateLedgerSha256',
    'providerRunMetadataSha256',
    'metadataSemanticSha256',
    'consumedInputSha256',
    'resultSha256',
    'traceSha256',
  ])
    sha(record[key], `/${key}`);
  gitSha(record.currentHeadSha, '/currentHeadSha');
  gitSha(record.currentBaseSha, '/currentBaseSha');
  nonEmptyString(record.workflowIdentity, '/workflowIdentity');
  nonEmptyString(record.trustedExecutionDomain, '/trustedExecutionDomain');
  timestamp(record.updatedAt, '/updatedAt');
  if (computeSelectorRevision(record as unknown as StateSelectorV1) !== record.selectorRevision) {
    throw new ContractValidationError('selector_revision_mismatch', '/selectorRevision');
  }
  if (computeSelectorId(record as unknown as StateSelectorV1) !== record.selectorId) {
    throw new ContractValidationError('selector_id_mismatch', '/selectorId');
  }
}

export function encodeValidatedRecord(
  value: CandidateRegistrationV1 | AcceptedStateMarkerV1 | StateSelectorV1,
): Uint8Array {
  if ('registrationSequence' in value) validateCandidateRegistration(value);
  else if ('markerId' in value && 'acceptingRunId' in value) validateAcceptedStateMarker(value);
  else validateStateSelector(value);
  return encodeRecord(value);
}

function decodeVersionedRecord(
  bytes: Uint8Array,
  keys: readonly string[],
  kind: RecordKind,
): unknown {
  const parsed = parseRecord(bytes);
  if (parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed)) {
    const schemaVersion = (parsed as Record<string, unknown>).schemaVersion;
    if (schemaVersion !== undefined) {
      if (typeof schemaVersion !== 'number' || !Number.isInteger(schemaVersion)) {
        throw new ContractValidationError('schema_version_invalid', '/schemaVersion');
      }
      if (schemaVersion !== 1) {
        throw new ContractValidationError('schema_version_unsupported', '/schemaVersion');
      }
    }
  }
  validateRecordUnicode(parsed);
  const record = object(parsed);
  exactKeys(record, keys);
  validateClosedSchema(record, kind);
  assertCanonicalRecord(bytes, parsed);
  return parsed;
}

function validateClosedSchema(record: Record<string, unknown>, kind: RecordKind): void {
  const validate = CLOSED_SCHEMA_VALIDATORS[kind];
  if (validate(record)) return;
  const error = validate.errors?.find(
    (candidate) =>
      candidate.keyword === 'additionalProperties' ||
      candidate.keyword === 'required' ||
      candidate.keyword === 'type' ||
      candidate.keyword === 'oneOf' ||
      candidate.keyword === 'anyOf' ||
      candidate.keyword === 'const' ||
      candidate.keyword === 'enum',
  );
  if (!error) return;
  const path = schemaErrorPath(error);
  throw new ContractValidationError(schemaErrorCode(error, path), path);
}

function schemaErrorPath(error: ErrorObject | undefined): string {
  if (!error) return '';
  const base = error.instancePath ?? '';
  const params = error.params as Record<string, unknown>;
  const property = params?.missingProperty ?? params?.additionalProperty;
  return typeof property === 'string'
    ? `${base}/${property.replaceAll('~', '~0').replaceAll('/', '~1')}`
    : base;
}

function schemaErrorCode(error: ErrorObject | undefined, path: string): ContractValidationCode {
  if (error?.keyword === 'additionalProperties' || error?.keyword === 'required') {
    return 'unknown_or_missing_field';
  }
  if (path === '/candidateArtifactLocator' || path.startsWith('/candidateArtifactLocator/')) {
    return 'locator_invalid';
  }
  if (path === '/stateKey' || path.startsWith('/stateKey/')) return 'state_key_invalid';
  if (path === '/transition' || path.startsWith('/transition/')) return 'transition_invalid';
  if (path === '/schemaVersion') return 'schema_version_invalid';
  if (path === '/registrationSequence') return 'sequence_invalid';
  if (path === '/previousSelectorRevision' || path === '/observedSelectorRevision') {
    return 'selector_revision_invalid';
  }
  if (path.endsWith('At')) return 'timestamp_invalid';
  if (path.endsWith('RunId')) return 'run_id_invalid';
  if (path.endsWith('Sha256') || path.endsWith('Id')) return 'sha256_invalid';
  if (path === '/stateKey/repository' || path === '/stateKey/headRepository') {
    return 'state_key_invalid';
  }
  if (path.startsWith('/stateKey/')) return 'string_invalid';
  if (path.endsWith('/schemaVersion')) return 'schema_version_invalid';
  if (error?.keyword === 'type' || error?.keyword === 'pattern' || error?.keyword === 'const') {
    return 'string_invalid';
  }
  return 'unknown_or_missing_field';
}

export function decodeValidatedRegistration(bytes: Uint8Array): CandidateRegistrationV1 {
  const value = decodeVersionedRecord(bytes, REGISTRATION_KEYS, 'registration');
  validateCandidateRegistration(value);
  return value;
}

export function decodeValidatedMarker(bytes: Uint8Array): AcceptedStateMarkerV1 {
  const value = decodeVersionedRecord(bytes, MARKER_KEYS, 'marker');
  validateAcceptedStateMarker(value);
  return value;
}

export function decodeValidatedSelector(bytes: Uint8Array): StateSelectorV1 {
  const value = decodeVersionedRecord(bytes, SELECTOR_KEYS, 'selector');
  validateStateSelector(value);
  return value;
}

export function materializeRegistration(
  draft: CandidateRegistrationDraft,
  registrationSequence: DecimalSequence,
  now = new Date().toISOString(),
): CandidateRegistrationV1 {
  validateRecordUnicode(draft);
  const value = {
    ...draft,
    registrationId: computeRegistrationId(draft) as CandidateRegistrationV1['registrationId'],
    registrationSequence,
    candidateArtifactLocator: candidateLocator(draft.candidateId),
    registeredAt: now,
  } as CandidateRegistrationV1;
  validateCandidateRegistration(value);
  return value;
}

export function materializeMarker(
  input: Omit<AcceptedStateMarkerV1, 'markerId' | 'acceptedAt'>,
  now = new Date().toISOString(),
): AcceptedStateMarkerV1 {
  validateRecordUnicode(input);
  const semantic = {
    ...input,
    acceptedAt: now,
  };
  const value = {
    ...semantic,
    markerId: computeMarkerId(semantic as AcceptedStateMarkerV1) as MarkerId,
  } as AcceptedStateMarkerV1;
  validateAcceptedStateMarker(value);
  return value;
}

export function materializeSelector(
  input: Omit<StateSelectorV1, 'selectorId' | 'selectorRevision' | 'updatedAt'>,
  now = new Date().toISOString(),
): StateSelectorV1 {
  validateRecordUnicode(input);
  const semantic = {
    ...input,
    updatedAt: now,
  } as StateSelectorV1;
  const selectorRevision = computeSelectorRevision(semantic);
  const value = {
    ...semantic,
    selectorRevision,
    selectorId: computeSelectorId({ ...semantic, selectorRevision } as StateSelectorV1),
  } as StateSelectorV1;
  validateStateSelector(value);
  return value;
}

function object(value: unknown): Record<string, unknown> {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    throw new ContractValidationError('object_required');
  }
  return value as Record<string, unknown>;
}

function exactKeys(record: Record<string, unknown>, keys: readonly string[]): void {
  const actual = Object.keys(record).sort();
  const expected = [...keys].sort();
  if (actual.length !== expected.length || actual.some((key, index) => key !== expected[index])) {
    throw new ContractValidationError('unknown_or_missing_field');
  }
}

function schemaVersion(value: unknown, path: string): void {
  if (typeof value !== 'number' || !Number.isInteger(value)) {
    throw new ContractValidationError('schema_version_invalid', path);
  }
  if (value !== 1) throw new ContractValidationError('schema_version_unsupported', path);
}

function boundedInt(value: unknown, min: number, max: number, path: string): void {
  if (typeof value !== 'number' || !Number.isInteger(value) || value < min || value > max) {
    throw new ContractValidationError('integer_out_of_range', path);
  }
}

function sha(value: unknown, path: string): void {
  if (typeof value !== 'string' || !SHA256_HEX.test(value)) {
    throw new ContractValidationError('sha256_invalid', path);
  }
}

function epoch(value: unknown, path: string): asserts value is EpochId {
  if (typeof value !== 'string' || !EPOCH_ID.test(value)) {
    throw new ContractValidationError('epoch_invalid', path);
  }
}

function gitSha(value: unknown, path: string): void {
  if (typeof value !== 'string' || !GIT_SHA.test(value)) {
    throw new ContractValidationError('git_sha_invalid', path);
  }
}

function runId(value: unknown, path: string): void {
  if (typeof value !== 'string' || !/^[1-9][0-9]{0,18}$/.test(value)) {
    throw new ContractValidationError('run_id_invalid', path);
  }
}

function sequence(value: unknown, path: string): asserts value is DecimalSequence {
  if (typeof value !== 'string' || !/^(?:0|[1-9][0-9]*)$/.test(value)) {
    throw new ContractValidationError('sequence_invalid', path);
  }
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed) || parsed < 1 || parsed > 1_000_000) {
    throw new ContractValidationError('sequence_out_of_range', path);
  }
}

function predecessor(value: unknown, path: string): void {
  if (value !== 'bootstrap' && (typeof value !== 'string' || !SHA256_HEX.test(value))) {
    throw new ContractValidationError('predecessor_invalid', path);
  }
}

function selectorRevision(value: unknown, path: string): asserts value is SelectorRevision {
  if (!isSelectorRevision(value))
    throw new ContractValidationError('selector_revision_invalid', path);
}

function timestamp(value: unknown, path: string): void {
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/.test(value)) {
    throw new ContractValidationError('timestamp_invalid', path);
  }
  const parsed = Date.parse(value);
  if (!Number.isFinite(parsed) || new Date(parsed).toISOString() !== value) {
    throw new ContractValidationError('timestamp_invalid', path);
  }
}

function nonEmptyString(value: unknown, path: string): void {
  if (typeof value !== 'string' || value.length === 0 || Array.from(value).length > 256) {
    throw new ContractValidationError('string_invalid', path);
  }
}

function repositoryName(value: unknown, path: string): void {
  if (
    typeof value !== 'string' ||
    value.length < 3 ||
    value.length > 200 ||
    !/^[A-Za-z0-9._-]+\/[A-Za-z0-9._-]+$/.test(value)
  ) {
    throw new ContractValidationError('state_key_invalid', path);
  }
}

function validateLocator(value: unknown, candidateId: CandidateId): void {
  const locator = object(value);
  exactKeys(locator, ['kind', 'namespace', 'objectId']);
  if (locator.kind !== 'store-object' || locator.namespace !== 'm4-state-v1') {
    throw new ContractValidationError('locator_invalid', '/candidateArtifactLocator');
  }
  if (
    locator.objectId !== `candidate-${candidateId}` ||
    typeof locator.objectId !== 'string' ||
    locator.objectId.length !== 74
  ) {
    throw new ContractValidationError('locator_invalid', '/candidateArtifactLocator/objectId');
  }
}

function validateRecordPredecessorBinding(record: Record<string, unknown>): void {
  const transition = record.transition as Record<string, unknown>;
  const predecessorFields = [
    'predecessorMarkerId',
    'predecessorManifestSha256',
    'predecessorLedgerSha256',
  ];
  if (transition.kind === 'bootstrap' || transition.kind === 'recovery_root') {
    for (const field of predecessorFields) {
      if (record[field] !== 'bootstrap') {
        throw new ContractValidationError('predecessor_binding_invalid', `/${field}`);
      }
    }
  } else {
    if (
      record.predecessorManifestSha256 !== transition.predecessorManifestSha256 ||
      record.predecessorLedgerSha256 !== transition.predecessorLedgerSha256 ||
      record.predecessorMarkerId === 'bootstrap'
    ) {
      throw new ContractValidationError('predecessor_binding_invalid', '/predecessor');
    }
  }
}

export function validateStateKey(value: unknown): asserts value is StateKeyV2 {
  const key = object(value);
  exactKeys(key, STATE_KEY_KEYS);
  if (key.namespace !== 'm4-ledger-v2')
    throw new ContractValidationError('state_key_invalid', '/stateKey/namespace');
  repositoryName(key.repository, '/stateKey/repository');
  repositoryName(key.headRepository, '/stateKey/headRepository');
  nonEmptyString(key.workflowIdentity, '/stateKey/workflowIdentity');
  nonEmptyString(key.trustedExecutionDomain, '/stateKey/trustedExecutionDomain');
  boundedInt(key.pullRequest, 1, 2_147_483_647, '/stateKey/pullRequest');
}

export function validateTransition(value: unknown): asserts value is StateManifestV2Transition {
  const transition = object(value);
  if (typeof transition.kind !== 'string' || !(transition.kind in TRANSITION_KEYS)) {
    throw new ContractValidationError('transition_invalid', '/transition/kind');
  }
  exactKeys(transition, TRANSITION_KEYS[transition.kind]);
  if (transition.kind === 'bootstrap') {
    if (
      transition.predecessorManifestSha256 !== 'bootstrap' ||
      transition.predecessorLedgerSha256 !== 'bootstrap' ||
      transition.reason !== 'new_session'
    ) {
      throw new ContractValidationError('transition_invalid', '/transition');
    }
  } else if (transition.kind === 'recovery_root') {
    if (
      transition.predecessorManifestSha256 !== 'bootstrap' ||
      transition.predecessorLedgerSha256 !== 'bootstrap'
    ) {
      throw new ContractValidationError('transition_invalid', '/transition');
    }
    if (
      ![
        'corrupt_accepted_artifact',
        'integrity_mismatch',
        'unsafe_provenance',
        'state_key_mismatch',
        'contract_version_incompatible',
        'over_bound_ledger',
        'unavailable_accepted_artifact',
      ].includes(String(transition.reason))
    ) {
      throw new ContractValidationError('transition_invalid', '/transition/reason');
    }
  } else {
    sha(transition.predecessorManifestSha256, '/transition/predecessorManifestSha256');
    sha(transition.predecessorLedgerSha256, '/transition/predecessorLedgerSha256');
    epoch(transition.predecessorLedgerEpoch, '/transition/predecessorLedgerEpoch');
    boundedInt(
      transition.predecessorStateGeneration,
      0,
      999_999,
      '/transition/predecessorStateGeneration',
    );
    if (
      transition.kind === 'reset' &&
      !['base_change', 'head_history_discontinuity', 'cache_contract_change'].includes(
        String(transition.reason),
      )
    ) {
      throw new ContractValidationError('transition_invalid', '/transition/reason');
    }
  }
}
