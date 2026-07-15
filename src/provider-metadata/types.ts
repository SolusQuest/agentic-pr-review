/**
 * ProviderRunMetadataV1 - provider-neutral run metadata sidecar types.
 *
 * Authoritative sources:
 * - `protocol/schemas/provider-run-metadata.v1.json` (JSON Schema draft-07,
 *   closed shape and structural bounds).
 * - Semantic validator in this module (cross-field, derivation, identity).
 * - `docs/20_architecture/session-ledger-and-prefix-contract.md` (shared M4
 *   Batch #1 vocabulary and safe-diagnostic-path contract).
 * - Issue #51 body (workstream-specific stage table and public API surface).
 *
 * TypeScript interfaces here are developer ergonomics only; deserialization is
 * governed by the JSON Schema and the parser pipeline.
 */

export type CapabilityEnum =
  | 'eligible'
  | 'ineligible'
  | 'unsupported'
  | 'telemetryUnavailable'
  | 'unknown';

export type CacheStatusEnum = 'hit' | 'partial' | 'miss' | 'unsupported' | 'unknown';

export type OutcomeEnum = 'succeeded' | 'failed' | 'cancelled';

export type UsageCompletenessAttempt = 'complete' | 'partial' | 'missing';

export type UsageCompletenessAggregate = 'complete' | 'partial' | 'missing' | 'unknown';

export type StatelessProofCompleteness = 'notApplicable' | 'complete' | 'missing';

export type ErrorCode =
  | 'provider_timeout'
  | 'provider_4xx'
  | 'provider_5xx'
  | 'provider_rate_limited'
  | 'provider_cancelled'
  | 'capability_unsupported'
  | 'cache_marker_mismatch'
  | 'stateless_proof_missing';

/**
 * Frozen allowlist order for `errorCodes` and per-attempt `attemptErrorCodes`.
 * NOT lexicographic; NOT `Array.prototype.sort()`. Semantic validator rejects
 * any other ordering with `invalid-metadata-error-code-order`.
 */
export const ALLOWED_ERROR_CODES: readonly ErrorCode[] = [
  'provider_timeout',
  'provider_4xx',
  'provider_5xx',
  'provider_rate_limited',
  'provider_cancelled',
  'capability_unsupported',
  'cache_marker_mismatch',
  'stateless_proof_missing',
] as const;

export type CapabilityMode = 'standard' | 'stateless';

export interface StatelessProof {
  kind: 'providerAdvertised' | 'synthetic';
  verified: boolean;
}

export interface CapabilityBlock {
  mode: CapabilityMode;
  aggregate: CapabilityEnum;
  statelessProof: StatelessProof | null;
}

export interface AttemptObservation {
  requestOrdinal: number;
  attemptOrdinal: number;
  outcome: OutcomeEnum;
  capability: CapabilityEnum;
  cacheStatus: CacheStatusEnum;
  usageCompleteness: UsageCompletenessAttempt;
  totalInputTokens: number | null;
  uncachedInputTokens: number | null;
  cacheWriteInputTokens: number | null;
  cacheReadInputTokens: number | null;
  outputTokens: number | null;
  attemptErrorCodes: ErrorCode[];
}

export interface RequestObservation {
  requestOrdinal: number;
  capability: CapabilityEnum;
  cacheStatus: CacheStatusEnum;
  usageCompleteness: UsageCompletenessAttempt;
  totalInputTokens: number | null;
  uncachedInputTokens: number | null;
  cacheWriteInputTokens: number | null;
  cacheReadInputTokens: number | null;
  outputTokens: number | null;
}

export interface UsageAggregate {
  totalInputTokens: number | null;
  uncachedInputTokens: number | null;
  cacheWriteInputTokens: number | null;
  cacheReadInputTokens: number | null;
  outputTokens: number | null;
  requestCount: number;
  attemptCount: number;
}

export interface NormalizedUsage {
  attempts: AttemptObservation[];
  requests: RequestObservation[];
  aggregate: UsageAggregate;
}

export interface RetryRequestEntry {
  requestOrdinal: number;
  attemptCount: number;
  succeededCount: number;
  failedCount: number;
  cancelledCount: number;
}

export interface RetryAggregate {
  requestCount: number;
  attemptCount: number;
  succeededCount: number;
  failedCount: number;
  cancelledCount: number;
}

export interface RetryObservations {
  requests: RetryRequestEntry[];
  aggregate: RetryAggregate;
}

export interface TelemetryCompleteness {
  usage: UsageCompletenessAggregate;
  cache: UsageCompletenessAggregate;
  statelessProof: StatelessProofCompleteness;
  aggregate: UsageCompletenessAggregate;
}

export interface ProviderRunMetadataV1 {
  schemaVersion: 1;
  selectedProviderId: string;
  observedProviderId: string;
  resolvedModelId: string;
  adapterId: string;
  logicalPrefixSha256: string;
  prefixSha256: string;
  capability: CapabilityBlock;
  cacheStatus: CacheStatusEnum;
  normalizedUsage: NormalizedUsage;
  retryObservations: RetryObservations;
  errorCodes: ErrorCode[];
  telemetryCompleteness: TelemetryCompleteness;
  producingRunId: string;
  runAttempt: number;
  interactionId: string;
  consumedInputSha256: string;
  resultSha256: string;
  traceSha256: string;
  predecessorLedgerSha256: string;
  candidateLedgerSha256: string;
}

/**
 * Host-supplied identity for `identityAgrees(metadata, expected)`. Cross-sidecar
 * identity mapping to a diagnostic code is a #53/#55 host concern; #51 returns
 * a plain boolean.
 */
export interface HostMetadataIdentity {
  providerId: string;
  resolvedModelId: string;
  adapterId: string;
}

/**
 * Closed workstream-specific error taxonomy. Each code has at least one named
 * fixture per issue #51 `### Coded validator errors and stage table`.
 */
export type MetadataErrorCode =
  // Stage 1 raw-transport bounds
  | 'invalid-metadata-bounds'
  // Stage 2 UTF-8 BOM
  | 'invalid-metadata-bom'
  // Stage 3 illegal UTF-8
  | 'invalid-metadata-utf8'
  // Stage 4 JSON syntax
  | 'invalid-metadata-json'
  // Stage 5 duplicate JSON property (pre-JSON.parse)
  | 'invalid-metadata-duplicate-json-property'
  // Stage 6 string-safety (NUL / unpaired UTF-16 surrogate)
  | 'invalid-metadata-unicode'
  // Stage 7 schema
  | 'invalid-metadata-schema'
  | 'invalid-metadata-additional-property'
  | 'invalid-metadata-unknown-enum'
  | 'invalid-metadata-token-out-of-range'
  // Stage 8 semantic
  | 'invalid-metadata-identity-syntax'
  | 'invalid-metadata-model-alias-literal'
  | 'invalid-metadata-provider-identity-cross-mismatch'
  | 'invalid-metadata-attempt-uniqueness'
  | 'invalid-metadata-attempt-ordering'
  | 'invalid-metadata-attempt-contiguity'
  | 'invalid-metadata-request-ordering'
  | 'invalid-metadata-multiple-succeeded-attempts'
  | 'invalid-metadata-attempt-usage-inconsistent'
  | 'invalid-metadata-attempt-outcome-error-consistency'
  | 'invalid-metadata-stateless-proof'
  | 'invalid-metadata-error-code-order'
  | 'invalid-metadata-aggregate-mismatch'
  // Post-processing (any stage): error-list truncated
  | 'invalid-metadata-error-list-truncated';

/**
 * Structured diagnostic. `path` is a shared-sanitizer / shared-resolver /
 * shared-truncator safe path bound by `MAX_METADATA_PATH_CHARS` (UTF-16 code
 * units) and `MAX_METADATA_PATH_UTF8_BYTES`. NO caller-controlled property
 * name appears verbatim; markers `<empty-name>`, `<invalid-utf16>`,
 * `<invalid-nul>`, `<invalid-control>`, `<untrusted-property>`,
 * `<path-truncated>` may appear per the shared subsection.
 */
export interface MetadataError {
  code: MetadataErrorCode;
  path: string;
}

export type ValidationResult<T> =
  | { valid: true; metadata: T }
  | { valid: false; errors: MetadataError[] };

// --- shared and workstream-local constants ---------------------------------

export { METADATA_MAX_BYTES, PROVIDER_RUN_METADATA_SCHEMA_VERSION } from '../state-v2/constants.js';

export const METADATA_SEMANTIC_HASH_DOMAIN_TAG =
  'agentic-pr-review/provider-run-metadata-semantic/v1' as const;

/** Safe-diagnostic-path cap in UTF-16 code units (issue #51 workstream-local). */
export const MAX_METADATA_PATH_CHARS = 256 as const;
/** Safe-diagnostic-path cap in UTF-8 bytes (issue #51 workstream-local). */
export const MAX_METADATA_PATH_UTF8_BYTES = 1024 as const;
/** Total length of the returned error array including sentinel. */
export const MAX_METADATA_ERRORS = 32 as const;

/** Semantic byte cap for identity strings (UTF-8 bytes). */
export const IDENTITY_STRING_MAX_UTF8_BYTES = 256 as const;

/** Semantic control-character set rejected inside identity strings. */
export const IDENTITY_CONTROL_CHARS_REGEX = /[\u0000-\u001F\u007F]/;

/**
 * Branded validated metadata. Produced only by `parseProviderRunMetadata` after
 * all eight stages pass. `buildSemanticEnvelope` accepts only this brand.
 */
declare const __providerRunMetadataValidated: unique symbol;
export type ValidatedProviderRunMetadataV1 = ProviderRunMetadataV1 & {
  readonly [__providerRunMetadataValidated]: true;
};

/**
 * Per-attempt entry of a validated value. Exposed so `deriveAggregate` and
 * downstream cost harness can consume individual attempts without weakening
 * the top-level brand.
 */
export type ValidatedAttempt = AttemptObservation;
