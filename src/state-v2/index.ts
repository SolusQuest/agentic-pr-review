/**
 * Public entry point for the M4 v2 state-bundle contract library (issue #48).
 * Pure library: no filesystem I/O; no imports from src/main.ts, src/runtime.ts,
 * src/runtime-invocation/*, src/runtime-integration/*, or src/state.ts.
 */

export {
  CANONICAL_JSON_VERSION,
  CanonicalJsonInputError,
  canonicalJsonBytes,
  type CanonicalJsonValue,
} from '../canonical-json/index.js';

export {
  EPOCH_ID_REGEX,
  EXPECTED_BUNDLE_FILENAMES,
  GIT_SHA_REGEX,
  LEDGER_FILENAME,
  LEDGER_MAX_BYTES,
  LEDGER_SCHEMA_VERSION,
  MANIFEST_FILENAME,
  MANIFEST_MAX_BYTES,
  MAX_DIAGNOSTIC_ERRORS,
  MAX_DIAGNOSTIC_MESSAGE_CHARS,
  MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES,
  METADATA_MAX_BYTES,
  PREFIX_CONTRACT_VERSION,
  PROVIDER_RUN_METADATA_FILENAME,
  PROVIDER_RUN_METADATA_SCHEMA_VERSION,
  SHA256_HEX_REGEX,
  STATE_NAMESPACE,
} from './constants.js';

export type { CrossFieldMessageCode, DiagnosticCode } from './diagnostics.js';

export type {
  CacheContractIdentityV2,
  EpochId,
  GenerationV2,
  GitSha,
  LedgerDescriptorV2,
  ProducingGenerationV2,
  ProvenanceV2,
  ProviderRunMetadataDescriptorV2,
  Sha256Hex,
  StateKeyV2,
  StateManifestV2,
  StateManifestV2Input,
  StateManifestV2Transition,
  TransactionV2,
} from './manifest.js';

export {
  crossFieldValidate,
  semanticIdentityValidate,
  validateStateManifestV2,
  type ValidationResult,
} from './schema.js';

export { StateManifestSerializationError, serializeStateManifestV2 } from './serializer.js';

export {
  BuilderValidationError,
  LedgerOverBoundError,
  MetadataOverBoundError,
  buildStateBundleV2,
  type BuildStateBundleV2Result,
} from './builder.js';

export {
  classifyStateBundleV2,
  type BundleClassification,
  type ClassifyStateBundleV2Input,
  type EntryDescriptor,
} from './classifier.js';

export {
  checkStateManifestV2Compatibility,
  type CompatibilityOutcome,
  type ExpectedInvalidationCode,
  type ExpectedStateManifestV2Context,
  type HeadRelationship,
  type IncompatibilityCode,
} from './compatibility.js';
