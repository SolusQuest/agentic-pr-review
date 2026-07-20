/**
 * M4 v2 state acceptance contract and deterministic reference oracle.
 *
 * This module is deliberately not imported by the default action path. It is
 * the contract seam consumed by the future production integration in issue
 * #53 and by synthetic cross-workflow tests.
 */

export * from './types.js';
export {
  EPOCH_ID,
  GIT_SHA,
  SHA256_HEX,
  candidateLocator,
  computeCandidateId,
  computeCandidateSetDigest,
  computeMarkerId,
  computeRegistrationId,
  computeSelectorId,
  computeSelectorRevision,
  computeSelectionSnapshotId,
  digestBytesId,
  digestId,
  observedSelectorSnapshotSha256,
  sha256Hex,
} from './hash.js';
export {
  ContractValidationError,
  decodeValidatedMarker,
  decodeValidatedRegistration,
  decodeValidatedSelector,
  encodeValidatedRecord,
  materializeMarker,
  materializeRegistration,
  materializeSelector,
  validateAcceptedStateMarker,
  validateCandidateRegistration,
  validateStateKey,
  validateStateSelector,
  validateTransition,
} from './validation.js';
export {
  bytesEqual,
  decodeRecord,
  encodeRecord,
  RecordCodecError,
  RECORD_MAX_BYTES,
} from './codec.js';
export {
  MAX_ACCEPTANCE_SNAPSHOT_REGISTRATION_BYTES,
  MAX_ACCEPTANCE_SNAPSHOT_REGISTRATIONS,
  ReferenceStateStore,
  SelectionSnapshotLimitError,
  SelectorRevisionMismatchError,
  StoreTransactionError,
  type CandidateEvidence,
  type CandidateReadResult,
  type ReferenceStoreHooks,
  type RegistrationWriteResult,
} from './store.js';
export {
  acceptLocalCandidate,
  candidateIdentity,
  StickyCallbackOutcomeUnknownError,
  type CandidateIdentity,
} from './accept.js';
