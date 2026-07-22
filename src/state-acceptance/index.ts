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
  CONTRACT_VALIDATION_CODES,
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
  type ContractValidationCode,
} from './validation.js';
export {
  assertCanonicalRecord,
  bytesEqual,
  decodeRecord,
  encodeRecord,
  RECORD_CODEC_CODES,
  RECORD_CODEC_DIAGNOSTIC_VECTORS,
  RecordCodecError,
  RECORD_MAX_BYTES,
  validateRecordUnicode,
  type RecordCodecCode,
} from './codec.js';
export {
  MAX_ACCEPTANCE_SNAPSHOT_REGISTRATION_BYTES,
  MAX_ACCEPTANCE_SNAPSHOT_REGISTRATIONS,
  acceptanceSnapshotLimitExceeded,
  ReferenceStateStore,
  type StateAcceptanceStore,
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
  StickyCallbackKnownFailureError,
  type CandidateIdentity,
} from './accept.js';
export {
  GitDataStateTransport,
  GitStateTransportError,
  type GitDataClient,
  type GitStateRef,
} from './github-git-data.js';
export { GitHubGitStateAcceptanceStore } from './github-state-store.js';
export { OctokitGitDataClient } from './github-octokit-client.js';
