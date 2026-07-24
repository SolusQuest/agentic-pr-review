/**
 * Outcome taxonomy, per-leg ratio-eligibility, and suite reducer (#54 §
 * Outcome taxonomy, § Suite & graduation).
 *
 * Outcomes align to the frozen #29 graduation vocabulary {pass, inconclusive,
 * regression} plus the non-counting {contract_regression, invalid} and the
 * conformance-only {not_applicable}. A ratio that cannot be computed (any
 * reason) is `inconclusive` (frozen DeepSeek-live = inconclusive); the reason
 * distinguishes why. `arithmetic_overflow` is absent - it is a decimal-helper
 * internal guard, not a run-evidence outcome.
 */
import type { TelemetryCompleteness } from '../provider-metadata/types.js';
import { classifyRatio } from './decimal.js';

export type ScenarioOutcome =
  | 'pass'
  | 'inconclusive'
  | 'regression'
  | 'contract_regression'
  | 'invalid';
/** Conformance cases may also produce `not_applicable` (legal invalidation). */
export type ConformanceOutcome = ScenarioOutcome | 'not_applicable';

export type ScenarioReason =
  // invalid-class
  | 'malformed_metadata'
  | 'profile_schema_mismatch'
  | 'profile_invalid'
  | 'equivalence_mismatch'
  | 'intra_leg_chain_mismatch'
  | 'attempt_topology_mismatch'
  | 'strategy_identity_mismatch'
  | 'zero_denominator'
  | 'retry_policy_drift'
  | 'conflicting_evidence'
  | 'window_partition_mismatch'
  | 'oracle_unavailable'
  | 'review_input_hash_mismatch'
  | 'review_target_mismatch'
  | 'token_bound_violation'
  | 'weight_bound_violation'
  | 'run_count_bound_violation'
  // contract_regression
  | 'prefix_drift'
  // inconclusive-class (ratio uncomputable or gray band)
  | 'telemetry_incomplete'
  | 'cache_completeness_unknown'
  | 'stateless_proof_unverified'
  | 'capability_stateless_unsupported'
  | 'partition_null'
  | 'no_evaluable_requests'
  | 'ratio_gray_band'
  // regression / pass
  | 'ratio_regression'
  | 'ratio_pass'
  // conformance-only
  | 'legal_invalidation';

const REASON_OUTCOME: Record<ScenarioReason, ScenarioOutcome | 'not_applicable'> = {
  malformed_metadata: 'invalid',
  profile_schema_mismatch: 'invalid',
  profile_invalid: 'invalid',
  equivalence_mismatch: 'invalid',
  intra_leg_chain_mismatch: 'invalid',
  attempt_topology_mismatch: 'invalid',
  strategy_identity_mismatch: 'invalid',
  zero_denominator: 'invalid',
  retry_policy_drift: 'invalid',
  conflicting_evidence: 'invalid',
  window_partition_mismatch: 'invalid',
  oracle_unavailable: 'invalid',
  review_input_hash_mismatch: 'invalid',
  review_target_mismatch: 'invalid',
  token_bound_violation: 'invalid',
  weight_bound_violation: 'invalid',
  run_count_bound_violation: 'invalid',
  prefix_drift: 'contract_regression',
  telemetry_incomplete: 'inconclusive',
  cache_completeness_unknown: 'inconclusive',
  stateless_proof_unverified: 'inconclusive',
  capability_stateless_unsupported: 'inconclusive',
  partition_null: 'inconclusive',
  no_evaluable_requests: 'inconclusive',
  ratio_gray_band: 'inconclusive',
  ratio_regression: 'regression',
  ratio_pass: 'pass',
  legal_invalidation: 'not_applicable',
};

/** Closed reason->outcome map (each reason -> exactly one outcome). */
export function reasonToOutcome(reason: ScenarioReason): ScenarioOutcome | 'not_applicable' {
  return REASON_OUTCOME[reason];
}

/** Deterministic precedence (highest first). */
const OUTCOME_PRECEDENCE: Record<ScenarioOutcome, number> = {
  invalid: 4,
  contract_regression: 3,
  regression: 2,
  inconclusive: 1,
  pass: 0,
};

export type Leg = 'resumed' | 'stateless';

/**
 * Structural view of the metadata fields needed for ratio-eligibility.
 * Compatible with #51 `ValidatedProviderRunMetadataV1` (the run-evidence layer
 * passes the validated metadata directly).
 */
export interface LegMetadataView {
  readonly capability: {
    readonly mode: 'standard' | 'stateless';
    readonly statelessProof: {
      readonly kind: 'providerAdvertised' | 'synthetic';
      readonly verified: boolean;
    } | null;
  };
  readonly telemetryCompleteness: TelemetryCompleteness;
  readonly normalizedUsage: {
    readonly aggregate: {
      readonly totalInputTokens: number | null;
      readonly uncachedInputTokens: number | null;
      readonly cacheWriteInputTokens: number | null;
      readonly cacheReadInputTokens: number | null;
    };
  };
}

export interface LegEligibility {
  readonly eligible: boolean;
  readonly reason: ScenarioReason | null;
}

/**
 * Per-leg ratio-eligibility predicate (#54 § Outcome taxonomy). A conclusive
 * ratio requires, per leg: usage/aggregate completeness = complete, all input
 * partitions non-null, cache completeness = complete (unknown -> distinct
 * reason), per-leg capability/proof expectations, and non-zero total input.
 */
export function perLegRatioEligibility(metadata: LegMetadataView, leg: Leg): LegEligibility {
  const tc = metadata.telemetryCompleteness;
  if (tc.usage !== 'complete') return { eligible: false, reason: 'telemetry_incomplete' };
  if (tc.aggregate !== 'complete') return { eligible: false, reason: 'telemetry_incomplete' };
  const agg = metadata.normalizedUsage.aggregate;
  if (
    agg.uncachedInputTokens === null ||
    agg.cacheWriteInputTokens === null ||
    agg.cacheReadInputTokens === null
  ) {
    return { eligible: false, reason: 'partition_null' };
  }
  if (tc.cache === 'unknown') return { eligible: false, reason: 'cache_completeness_unknown' };
  if (tc.cache !== 'complete') return { eligible: false, reason: 'telemetry_incomplete' };
  if (leg === 'resumed') {
    if (metadata.capability.mode !== 'standard') {
      return { eligible: false, reason: 'capability_stateless_unsupported' };
    }
    if (metadata.capability.statelessProof !== null) {
      return { eligible: false, reason: 'capability_stateless_unsupported' };
    }
    if (tc.statelessProof !== 'notApplicable') {
      return { eligible: false, reason: 'telemetry_incomplete' };
    }
  } else {
    if (metadata.capability.mode !== 'stateless') {
      return { eligible: false, reason: 'capability_stateless_unsupported' };
    }
    const proof = metadata.capability.statelessProof;
    if (proof === null || !proof.verified) {
      return { eligible: false, reason: 'stateless_proof_unverified' };
    }
    if (tc.statelessProof !== 'complete') {
      return { eligible: false, reason: 'stateless_proof_unverified' };
    }
  }
  if (agg.totalInputTokens === 0) return { eligible: false, reason: 'no_evaluable_requests' };
  return { eligible: true, reason: null };
}

/**
 * Scenario evaluation input, produced by the run-evidence/equivalence layer.
 * `validityReason` carries the first invalid-class reason found (or null);
 * `prefixDrift` is determined against the leg's own trusted oracle; the
 * eligibilities and costs come from per-leg metadata.
 */
export interface ScenarioEvaluationInput {
  readonly validityReason: ScenarioReason | null;
  readonly prefixDrift: boolean;
  readonly resumedEligibility: LegEligibility;
  readonly statelessEligibility: LegEligibility;
  readonly num: bigint | null;
  readonly den: bigint | null;
}

export interface ScenarioEvaluationResult {
  readonly outcome: ScenarioOutcome;
  readonly reason: ScenarioReason;
  readonly num: bigint | null;
  readonly den: bigint | null;
  readonly ratioClass?: 'pass' | 'inconclusive' | 'regression';
}

/**
 * Evaluate a single scenario under deterministic precedence
 * invalid > contract_regression > (ratio-eligibility -> inconclusive) > ratio.
 */
export function evaluateScenario(input: ScenarioEvaluationInput): ScenarioEvaluationResult {
  if (input.validityReason !== null) {
    return { outcome: 'invalid', reason: input.validityReason, num: input.num, den: input.den };
  }
  if (input.prefixDrift) {
    return {
      outcome: 'contract_regression',
      reason: 'prefix_drift',
      num: input.num,
      den: input.den,
    };
  }
  if (!input.resumedEligibility.eligible) {
    return {
      outcome: 'inconclusive',
      reason: input.resumedEligibility.reason ?? 'telemetry_incomplete',
      num: input.num,
      den: input.den,
    };
  }
  if (!input.statelessEligibility.eligible) {
    return {
      outcome: 'inconclusive',
      reason: input.statelessEligibility.reason ?? 'telemetry_incomplete',
      num: input.num,
      den: input.den,
    };
  }
  if (input.num === null || input.den === null) {
    // Eligible legs have non-null partitions, so costs are computable; reaching
    // here with a null cost is an internal invariant violation.
    return { outcome: 'inconclusive', reason: 'partition_null', num: input.num, den: input.den };
  }
  const ratio = classifyRatio(input.num, input.den);
  if (!ratio.ok) {
    return { outcome: 'invalid', reason: 'zero_denominator', num: input.num, den: input.den };
  }
  if (ratio.class === 'pass') {
    return {
      outcome: 'pass',
      reason: 'ratio_pass',
      num: input.num,
      den: input.den,
      ratioClass: 'pass',
    };
  }
  if (ratio.class === 'regression') {
    return {
      outcome: 'regression',
      reason: 'ratio_regression',
      num: input.num,
      den: input.den,
      ratioClass: 'regression',
    };
  }
  return {
    outcome: 'inconclusive',
    reason: 'ratio_gray_band',
    num: input.num,
    den: input.den,
    ratioClass: 'inconclusive',
  };
}

/**
 * Total suite reducer (#54 § Suite reducer). Consumes evaluationScenario
 * outcomes only (conformanceCases are verified separately). Every combination
 * resolves to exactly one suite outcome.
 */
export function suiteReducer(scenarioOutcomes: readonly ScenarioOutcome[]): ScenarioOutcome {
  if (scenarioOutcomes.some((o) => o === 'invalid')) return 'invalid';
  if (scenarioOutcomes.some((o) => o === 'contract_regression')) return 'contract_regression';
  if (scenarioOutcomes.some((o) => o === 'regression')) return 'regression';
  if (scenarioOutcomes.some((o) => o === 'inconclusive')) return 'inconclusive';
  return 'pass';
}

export { OUTCOME_PRECEDENCE };
