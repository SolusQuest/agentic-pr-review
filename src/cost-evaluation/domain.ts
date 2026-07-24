/**
 * Shared domain types and digests for the cost-evaluation harness (#54).
 *
 * Kept dependency-light (canonical-json + hash only) so report, graduation, and
 * run-evidence can share these without circular imports.
 */
import { canonicalJsonBytes, type CanonicalJsonValue } from '../canonical-json/index.js';
import { digestId, sha256Hex } from './hash.js';

export const STRATEGY_IDENTITY_SCHEMA_VERSION = 1 as const;
export const STRATEGY_IDENTITY_DIGEST_TAG = 'agentic-pr-review/cost-eval/strategy-identity/v1';

/**
 * Per-leg strategy identity (#54 § Domain model). Resumed leg:
 * `capabilityMode = "standard"`, `statelessProofKind = null`. Stateless leg:
 * `capabilityMode = "stateless"`, `statelessProofKind = "synthetic"`. All runs
 * of a leg must produce the same identity.
 */
export interface CostEvaluationStrategyIdentityV1 {
  readonly schemaVersion: 1;
  readonly adapterId: string;
  readonly cacheConfigId: string;
  readonly capabilityMode: 'standard' | 'stateless';
  readonly statelessProofKind: null | 'synthetic';
}

/** Frozen strategy-identity digest preimage. */
export function strategyIdentityDigest(identity: CostEvaluationStrategyIdentityV1): string {
  return digestId(
    STRATEGY_IDENTITY_DIGEST_TAG,
    canonicalJsonBytes({
      schemaVersion: identity.schemaVersion,
      adapterId: identity.adapterId,
      cacheConfigId: identity.cacheConfigId,
      capabilityMode: identity.capabilityMode,
      statelessProofKind: identity.statelessProofKind,
    }),
  );
}

export const WINDOW_PARTITION_DIGEST_TAG = 'agentic-pr-review/cost-eval/window-partition/v1';

/** Inputs to the window partition key (re-derivable from validated report fields). */
export interface WindowPartitionInputs {
  readonly profileDigest: string;
  readonly providerId: string;
  readonly modelId: string;
  readonly resumedStrategyIdentity: CostEvaluationStrategyIdentityV1;
  readonly statelessStrategyIdentity: CostEvaluationStrategyIdentityV1;
  readonly fixtureSuiteDigest: string;
  readonly prefixContractVersion: string;
  readonly harnessVersion: string;
  readonly mode: string;
}

/** Frozen window-partition-key digest preimage (suite reports only). */
export function windowPartitionKeyDigest(inputs: WindowPartitionInputs): string {
  return digestId(
    WINDOW_PARTITION_DIGEST_TAG,
    canonicalJsonBytes({
      profileDigest: inputs.profileDigest,
      providerId: inputs.providerId,
      modelId: inputs.modelId,
      resumedStrategyIdentityDigest: strategyIdentityDigest(inputs.resumedStrategyIdentity),
      statelessStrategyIdentityDigest: strategyIdentityDigest(inputs.statelessStrategyIdentity),
      fixtureSuiteDigest: inputs.fixtureSuiteDigest,
      prefixContractVersion: inputs.prefixContractVersion,
      harnessVersion: inputs.harnessVersion,
      mode: inputs.mode,
    }),
  );
}

/**
 * `reportSha256 = SHA256(RFC8785(semanticEnvelope))` (no domain tag). Used by
 * the report builder to compute and by graduation to recompute+verify.
 */
export function reportSha256Of(semanticEnvelope: CanonicalJsonValue): string {
  return sha256Hex(canonicalJsonBytes(semanticEnvelope));
}

/**
 * Validated suite-report view consumed by graduation. Graduation recomputes
 * `reportSha256` from `semanticEnvelope`, recomputes the window partition key
 * from `windowPartitionInputs`, and dedups by `evidenceOccurrenceId`.
 */
export interface SuiteReportView {
  readonly reportSha256: string;
  readonly semanticEnvelope: CanonicalJsonValue;
  readonly evidenceOccurrenceId: string;
  readonly suiteOutcome: import('./outcome.js').ScenarioOutcome;
  readonly windowPartitionInputs: WindowPartitionInputs;
}
