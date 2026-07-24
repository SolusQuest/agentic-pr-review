/**
 * Evaluation profile (#54 § Key decisions D2, § Versioning).
 *
 * Provider-neutral synthetic evaluation profile: weights are versioned
 * synthetic evaluation parameters (no provider price claim). The profile is
 * versioned with a canonical digest; a weight change bumps the digest, a
 * threshold change requires a prerequisite #29 shared-contract revision.
 *
 * Frozen profile digest preimage:
 *   SHA256(UTF8("agentic-pr-review/cost-eval/profile/v1") || 0x00 ||
 *          RFC8785({schemaVersion, profileVersion, uncachedWeight,
 *                   cacheWriteWeight, cacheReadWeight, outputWeight (nullable),
 *                   epsilon, thresholdContractRef}))
 * where weights are canonicalized decimal strings (trailing zeros removed) and
 * `outputWeight` is null when total cost is not evaluated.
 */
import { canonicalJsonBytes, type CanonicalJsonValue } from '../canonical-json/index.js';
import {
  MAX_WEIGHT_VALUE,
  parseWeight,
  scaledToDecimalString,
  type InputWeights,
} from './decimal.js';
import { digestId } from './hash.js';

export const PROFILE_SCHEMA_VERSION = 1 as const;
export const PROFILE_DIGEST_TAG = 'agentic-pr-review/cost-eval/profile/v1';
/** Frozen threshold-contract reference (changes only on #29 shared-contract revision). */
export const THRESHOLD_CONTRACT_REF = 'm4-capability-usage-cost-thresholds-v1' as const;
/** Frozen epsilon (#29-owned). #54 only accepts this value. */
export const FROZEN_EPSILON = '0.01' as const;

export interface EvaluationProfileV1 {
  readonly schemaVersion: 1;
  readonly profileVersion: string;
  readonly uncachedWeight: string;
  readonly cacheWriteWeight: string;
  readonly cacheReadWeight: string;
  readonly outputWeight: string | null;
  readonly epsilon: string;
  readonly thresholdContractRef: string;
}

export interface ResolvedProfileWeights {
  readonly uncached: bigint;
  readonly cacheWrite: bigint;
  readonly cacheRead: bigint;
  readonly output: bigint | null;
}

export interface ResolvedProfile {
  readonly profile: EvaluationProfileV1;
  readonly weights: ResolvedProfileWeights;
  readonly digest: string;
}

export type ProfileParseReason =
  | 'schema'
  | 'grammar'
  | 'value_cap'
  | 'no_positive_input_weight'
  | 'epsilon_invalid'
  | 'threshold_ref_invalid';
export type ProfileParseResult =
  | { readonly ok: true; readonly resolved: ResolvedProfile }
  | { readonly ok: false; readonly reason: ProfileParseReason; readonly errors?: string[] };

const PROFILE_VERSION_RE = /^[A-Za-z0-9._-]{1,64}$/;
const THRESHOLD_REF_RE = /^[A-Za-z0-9._-]{1,128}$/;
const PROFILE_FIELDS = [
  'schemaVersion',
  'profileVersion',
  'uncachedWeight',
  'cacheWriteWeight',
  'cacheReadWeight',
  'outputWeight',
  'epsilon',
  'thresholdContractRef',
] as const;

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function parseBoundedString(value: unknown, re: RegExp): string | null {
  if (typeof value !== 'string' || !re.test(value)) return null;
  return value;
}

/**
 * Resolve raw profile fields into a canonical, validated `ResolvedProfile`
 * with its digest. Used by both the default-profile builder and the parser of
 * serialized profile JSON.
 */
export function resolveProfile(input: {
  readonly profileVersion: unknown;
  readonly uncachedWeight: unknown;
  readonly cacheWriteWeight: unknown;
  readonly cacheReadWeight: unknown;
  readonly outputWeight: unknown;
  readonly epsilon: unknown;
  readonly thresholdContractRef: unknown;
}): ProfileParseResult {
  const profileVersion = parseBoundedString(input.profileVersion, PROFILE_VERSION_RE);
  if (profileVersion === null) return { ok: false, reason: 'schema', errors: ['profileVersion'] };

  const thresholdRef = parseBoundedString(input.thresholdContractRef, THRESHOLD_REF_RE);
  if (thresholdRef === null) return { ok: false, reason: 'threshold_ref_invalid' };

  // Weights: parse (grammar + value cap) then canonicalize.
  const weightsRaw = {
    uncached: input.uncachedWeight,
    cacheWrite: input.cacheWriteWeight,
    cacheRead: input.cacheReadWeight,
  };
  const scaled: Partial<Record<keyof typeof weightsRaw, bigint>> = {};
  const canonical: Partial<Record<keyof typeof weightsRaw, string>> = {};
  for (const key of Object.keys(weightsRaw) as (keyof typeof weightsRaw)[]) {
    if (typeof weightsRaw[key] !== 'string') {
      return { ok: false, reason: 'grammar', errors: [key] };
    }
    const parsed = parseWeight(weightsRaw[key]);
    if (!parsed.ok) return { ok: false, reason: parsed.reason, errors: [key] };
    scaled[key] = parsed.scaled;
    canonical[key] = scaledToDecimalString(parsed.scaled);
  }

  // At least one input weight must be positive.
  if (scaled.uncached === 0n && scaled.cacheWrite === 0n && scaled.cacheRead === 0n) {
    return { ok: false, reason: 'no_positive_input_weight' };
  }

  // outputWeight: null/absent (string|null allowed; may be zero).
  let outputScaled: bigint | null = null;
  let outputCanonical: string | null = null;
  if (input.outputWeight !== null && input.outputWeight !== undefined) {
    if (typeof input.outputWeight !== 'string') {
      return { ok: false, reason: 'grammar', errors: ['outputWeight'] };
    }
    const parsed = parseWeight(input.outputWeight);
    if (!parsed.ok) return { ok: false, reason: parsed.reason, errors: ['outputWeight'] };
    outputScaled = parsed.scaled;
    outputCanonical = scaledToDecimalString(parsed.scaled);
  }

  // epsilon: must canonicalize to the frozen "0.01".
  if (typeof input.epsilon !== 'string') {
    return { ok: false, reason: 'epsilon_invalid' };
  }
  const epsilonParsed = parseWeight(input.epsilon);
  if (!epsilonParsed.ok) return { ok: false, reason: 'epsilon_invalid' };
  const epsilonCanonical = scaledToDecimalString(epsilonParsed.scaled);
  if (epsilonCanonical !== FROZEN_EPSILON) {
    return { ok: false, reason: 'epsilon_invalid' };
  }

  const profile: EvaluationProfileV1 = {
    schemaVersion: PROFILE_SCHEMA_VERSION,
    profileVersion,
    uncachedWeight: canonical.uncached!,
    cacheWriteWeight: canonical.cacheWrite!,
    cacheReadWeight: canonical.cacheRead!,
    outputWeight: outputCanonical,
    epsilon: epsilonCanonical,
    thresholdContractRef: thresholdRef,
  };

  const envelope: CanonicalJsonValue = {
    schemaVersion: PROFILE_SCHEMA_VERSION,
    profileVersion,
    uncachedWeight: canonical.uncached!,
    cacheWriteWeight: canonical.cacheWrite!,
    cacheReadWeight: canonical.cacheRead!,
    outputWeight: outputCanonical,
    epsilon: epsilonCanonical,
    thresholdContractRef: thresholdRef,
  };
  const digest = digestId(PROFILE_DIGEST_TAG, canonicalJsonBytes(envelope));

  return {
    ok: true,
    resolved: {
      profile,
      weights: {
        uncached: scaled.uncached!,
        cacheWrite: scaled.cacheWrite!,
        cacheRead: scaled.cacheRead!,
        output: outputScaled,
      },
      digest,
    },
  };
}

/** Parse a serialized profile JSON value. */
export function parseProfile(value: unknown): ProfileParseResult {
  if (!isPlainObject(value)) return { ok: false, reason: 'schema' };
  for (const field of PROFILE_FIELDS) {
    if (!Object.prototype.hasOwnProperty.call(value, field)) {
      return { ok: false, reason: 'schema', errors: [field] };
    }
  }
  if (value.schemaVersion !== PROFILE_SCHEMA_VERSION) {
    return { ok: false, reason: 'schema', errors: ['schemaVersion'] };
  }
  return resolveProfile({
    profileVersion: value.profileVersion,
    uncachedWeight: value.uncachedWeight,
    cacheWriteWeight: value.cacheWriteWeight,
    cacheReadWeight: value.cacheReadWeight,
    outputWeight: value.outputWeight,
    epsilon: value.epsilon,
    thresholdContractRef: value.thresholdContractRef,
  });
}

/**
 * Default provider-neutral synthetic evaluation profile (issue #54 D2).
 * Weights informed by DeepSeek v4 pro cache-pricing shape; `cacheWriteWeight`
 * is a declared policy assumption for the synthetic provider's explicit
 * cache-write tier (not derived from any provider price table).
 */
export const DEFAULT_PROFILE_INPUT = {
  profileVersion: 'm4-cost-evaluation-default-v1',
  uncachedWeight: '1.0',
  cacheWriteWeight: '1.25',
  cacheReadWeight: '0.01',
  outputWeight: '2.0',
  epsilon: '0.01',
  thresholdContractRef: THRESHOLD_CONTRACT_REF,
} as const;

export const DEFAULT_PROFILE: ResolvedProfile = (() => {
  const result = resolveProfile(DEFAULT_PROFILE_INPUT);
  if (!result.ok) {
    throw new Error(`default profile failed to resolve: ${result.reason}`);
  }
  return result.resolved;
})();

/** Convert an `InputWeights`-compatible view from a resolved profile. */
export function profileInputWeights(resolved: ResolvedProfile): InputWeights {
  return {
    uncached: resolved.weights.uncached,
    cacheWrite: resolved.weights.cacheWrite,
    cacheRead: resolved.weights.cacheRead,
  };
}

// Weight value cap is enforced by parseWeight; MAX_WEIGHT_VALUE re-exported for
// callers that need the constant directly.
export { MAX_WEIGHT_VALUE };
