/**
 * Two-stage deterministic derivation for ProviderRunMetadataV1.
 *
 * Given the authoritative per-attempt array plus run-level capability.mode and
 * capability.statelessProof (which drive telemetryCompleteness.statelessProof),
 * this helper reproduces every derived field the semantic validator checks:
 *
 *   - normalizedUsage.requests (per-request reduction, retries summed by attempt)
 *   - normalizedUsage.aggregate (run-level field-wise sums)
 *   - capability.aggregate      (run-level capability precedence)
 *   - cacheStatus               (run-level cache-status precedence)
 *   - telemetryCompleteness     (usage / cache / statelessProof / aggregate)
 *   - retryObservations.requests + retryObservations.aggregate
 *   - errorCodes                (sorted, deduplicated union of attemptErrorCodes)
 *
 * It is called by the semantic validator after schema and syntactic checks have
 * passed. Consumers should not call it directly on unvalidated JSON.
 */

import type { MetadataError } from './types.js';
import type {
  AttemptObservation,
  CacheStatusEnum,
  CapabilityBlock,
  CapabilityEnum,
  CapabilityMode,
  ErrorCode,
  NormalizedUsage,
  RequestObservation,
  RetryObservations,
  RetryRequestEntry,
  StatelessProof,
  StatelessProofCompleteness,
  TelemetryCompleteness,
  UsageAggregate,
  UsageCompletenessAggregate,
  UsageCompletenessAttempt,
} from './types.js';
import { ALLOWED_ERROR_CODES } from './types.js';

export type DeriveAggregateInput =
  | {
      attempts: readonly AttemptObservation[];
      capabilityMode: 'standard';
      statelessProof: null;
    }
  | {
      attempts: readonly AttemptObservation[];
      capabilityMode: 'stateless';
      statelessProof: StatelessProof;
    };

// Backwards-friendly alias used by internal callers that still hold a plain
// object shape (semantic validator constructs one from a validated metadata
// value). Public callers must use `DeriveAggregateInput`.
export interface DeriveAggregateInputLoose {
  attempts: readonly AttemptObservation[];
  capabilityMode: CapabilityMode;
  statelessProof: StatelessProof | null;
}

export interface DerivedProviderRunMetadataAggregate {
  normalizedUsage: NormalizedUsage;
  capability: Pick<CapabilityBlock, 'aggregate'>;
  cacheStatus: CacheStatusEnum;
  retryObservations: RetryObservations;
  errorCodes: ErrorCode[];
  telemetryCompleteness: TelemetryCompleteness;
}

export type DeriveAggregateResult =
  | { valid: true; aggregate: DerivedProviderRunMetadataAggregate }
  | { valid: false; errors: readonly MetadataError[] };

function isAttemptArray(value: unknown): value is AttemptObservation[] {
  return Array.isArray(value);
}

/**
 * Defensive runtime guard. Semantic validator calls this after schema + ordering
 * + uniqueness + contiguity checks; a malformed input here indicates a caller
 * bypassed the validator surface.
 */
function assertValidInput(input: DeriveAggregateInputLoose): void {
  if (input === null || typeof input !== 'object') {
    throw new TypeError('deriveAggregate: input must be an object');
  }
  if (!isAttemptArray(input.attempts)) {
    throw new TypeError('deriveAggregate: input.attempts must be an array');
  }
  if (input.capabilityMode !== 'standard' && input.capabilityMode !== 'stateless') {
    throw new TypeError('deriveAggregate: input.capabilityMode must be standard or stateless');
  }
  if (input.statelessProof !== null && typeof input.statelessProof !== 'object') {
    throw new TypeError('deriveAggregate: input.statelessProof must be an object or null');
  }
}

function sumNullable(
  values: (number | null)[],
  path: string,
  overflow: MetadataError[],
): number | null {
  let acc = 0;
  for (const v of values) {
    if (v === null) return null;
    // Pre-addition safe-integer check per `### Aggregate token overflow`.
    if (v > Number.MAX_SAFE_INTEGER - acc) {
      overflow.push({ code: 'invalid-metadata-token-out-of-range', path });
      return acc;
    }
    acc += v;
  }
  return acc;
}

function groupByRequest(attempts: AttemptObservation[]): Map<number, AttemptObservation[]> {
  const map = new Map<number, AttemptObservation[]>();
  for (const a of attempts) {
    const existing = map.get(a.requestOrdinal);
    if (existing) {
      existing.push(a);
    } else {
      map.set(a.requestOrdinal, [a]);
    }
  }
  return map;
}

function reduceRequestCapability(group: AttemptObservation[]): CapabilityEnum {
  const set = new Set(group.map((a) => a.capability));
  if (set.has('unsupported')) return 'unsupported';
  if (set.has('ineligible')) return 'ineligible';
  if (set.has('telemetryUnavailable')) return 'telemetryUnavailable';
  if (set.has('unknown')) return 'unknown';
  return 'eligible';
}

function reduceRequestCacheStatus(group: AttemptObservation[]): CacheStatusEnum {
  const set = new Set(group.map((a) => a.cacheStatus));
  if (set.has('unsupported')) return 'unsupported';
  if (set.has('unknown')) return 'unknown';
  if (set.has('partial') || (set.has('hit') && set.has('miss'))) return 'partial';
  if (set.size === 1 && set.has('hit')) return 'hit';
  if (set.size === 1 && set.has('miss')) return 'miss';
  // Mixed subsets already covered; remaining case is defensive (should not occur on validated input).
  return 'partial';
}

function reduceRequestUsageCompleteness(group: AttemptObservation[]): UsageCompletenessAttempt {
  const set = new Set(group.map((a) => a.usageCompleteness));
  if (set.size === 1 && set.has('complete')) return 'complete';
  if (set.size === 1 && set.has('missing')) return 'missing';
  return 'partial';
}

function reduceRequest(
  requestOrdinal: number,
  group: AttemptObservation[],
  overflow: MetadataError[],
): RequestObservation {
  const basePath = `/normalizedUsage/requests/${requestOrdinal}`;
  return {
    requestOrdinal,
    capability: reduceRequestCapability(group),
    cacheStatus: reduceRequestCacheStatus(group),
    usageCompleteness: reduceRequestUsageCompleteness(group),
    totalInputTokens: sumNullable(
      group.map((a) => a.totalInputTokens),
      `${basePath}/totalInputTokens`,
      overflow,
    ),
    uncachedInputTokens: sumNullable(
      group.map((a) => a.uncachedInputTokens),
      `${basePath}/uncachedInputTokens`,
      overflow,
    ),
    cacheWriteInputTokens: sumNullable(
      group.map((a) => a.cacheWriteInputTokens),
      `${basePath}/cacheWriteInputTokens`,
      overflow,
    ),
    cacheReadInputTokens: sumNullable(
      group.map((a) => a.cacheReadInputTokens),
      `${basePath}/cacheReadInputTokens`,
      overflow,
    ),
    outputTokens: sumNullable(
      group.map((a) => a.outputTokens),
      `${basePath}/outputTokens`,
      overflow,
    ),
  };
}

function reduceRunCapability(requests: RequestObservation[]): {
  capability: CapabilityEnum;
  cacheStatus: CacheStatusEnum;
} {
  if (requests.length === 0) {
    return { capability: 'unknown', cacheStatus: 'unknown' };
  }
  const capSet = new Set(requests.map((r) => r.capability));
  if (capSet.has('unsupported') || capSet.has('ineligible')) {
    return { capability: 'unsupported', cacheStatus: 'unsupported' };
  }
  const cacheSet = new Set(requests.map((r) => r.cacheStatus));
  if (capSet.has('telemetryUnavailable') || capSet.has('unknown') || cacheSet.has('unknown')) {
    return { capability: 'unknown', cacheStatus: 'unknown' };
  }
  if (cacheSet.has('partial') || (cacheSet.has('hit') && cacheSet.has('miss'))) {
    return { capability: 'eligible', cacheStatus: 'partial' };
  }
  if (cacheSet.size === 1 && cacheSet.has('hit')) {
    return { capability: 'eligible', cacheStatus: 'hit' };
  }
  if (cacheSet.size === 1 && cacheSet.has('miss')) {
    return { capability: 'eligible', cacheStatus: 'miss' };
  }
  // Defensive: should not occur on validated input; keep total.
  return { capability: 'eligible', cacheStatus: 'partial' };
}

function deriveUsageAggregate(
  requests: RequestObservation[],
  attemptCount: number,
  overflow: MetadataError[],
): UsageAggregate {
  const base = '/normalizedUsage/aggregate';
  return {
    totalInputTokens: sumNullable(
      requests.map((r) => r.totalInputTokens),
      `${base}/totalInputTokens`,
      overflow,
    ),
    uncachedInputTokens: sumNullable(
      requests.map((r) => r.uncachedInputTokens),
      `${base}/uncachedInputTokens`,
      overflow,
    ),
    cacheWriteInputTokens: sumNullable(
      requests.map((r) => r.cacheWriteInputTokens),
      `${base}/cacheWriteInputTokens`,
      overflow,
    ),
    cacheReadInputTokens: sumNullable(
      requests.map((r) => r.cacheReadInputTokens),
      `${base}/cacheReadInputTokens`,
      overflow,
    ),
    outputTokens: sumNullable(
      requests.map((r) => r.outputTokens),
      `${base}/outputTokens`,
      overflow,
    ),
    requestCount: requests.length,
    attemptCount,
  };
}

function deriveRetryEntry(requestOrdinal: number, group: AttemptObservation[]): RetryRequestEntry {
  let succeededCount = 0;
  let failedCount = 0;
  let cancelledCount = 0;
  for (const a of group) {
    if (a.outcome === 'succeeded') succeededCount += 1;
    else if (a.outcome === 'failed') failedCount += 1;
    else cancelledCount += 1;
  }
  return {
    requestOrdinal,
    attemptCount: group.length,
    succeededCount,
    failedCount,
    cancelledCount,
  };
}

function deriveRetryObservations(
  requestOrdinals: number[],
  grouped: Map<number, AttemptObservation[]>,
  attemptCount: number,
): RetryObservations {
  const requests = requestOrdinals.map((ord) => deriveRetryEntry(ord, grouped.get(ord) ?? []));
  const aggregate = {
    requestCount: requests.length,
    attemptCount,
    succeededCount: requests.reduce((acc, r) => acc + r.succeededCount, 0),
    failedCount: requests.reduce((acc, r) => acc + r.failedCount, 0),
    cancelledCount: requests.reduce((acc, r) => acc + r.cancelledCount, 0),
  };
  return { requests, aggregate };
}

function deriveErrorCodes(attempts: AttemptObservation[]): ErrorCode[] {
  const seen = new Set<ErrorCode>();
  for (const a of attempts) {
    for (const code of a.attemptErrorCodes) {
      seen.add(code);
    }
  }
  return ALLOWED_ERROR_CODES.filter((code) => seen.has(code));
}

function deriveUsageCompletenessAggregate(
  requests: RequestObservation[],
): UsageCompletenessAggregate {
  if (requests.length === 0) return 'missing';
  const set = new Set(requests.map((r) => r.usageCompleteness));
  if (set.size === 1 && set.has('complete')) return 'complete';
  if (set.size === 1 && set.has('missing')) return 'missing';
  if (set.size === 1 && set.has('partial')) return 'partial';
  return 'partial';
}

function deriveCacheCompletenessAggregate(
  requests: RequestObservation[],
): UsageCompletenessAggregate {
  if (requests.length === 0) return 'missing';
  const set = new Set(requests.map((r) => r.cacheStatus));
  if (set.has('unknown')) return 'unknown';
  const observed: Array<'observed' | 'unsupported'> = requests.map((r) =>
    r.cacheStatus === 'unsupported' ? 'unsupported' : 'observed',
  );
  const observedSet = new Set(observed);
  if (observedSet.size === 1 && observedSet.has('unsupported')) return 'missing';
  if (observedSet.size === 1 && observedSet.has('observed')) return 'complete';
  return 'partial';
}

function deriveStatelessProofCompleteness(
  mode: CapabilityMode,
  proof: StatelessProof | null,
): StatelessProofCompleteness {
  if (mode === 'standard') return 'notApplicable';
  if (proof && proof.verified) return 'complete';
  return 'missing';
}

function deriveAggregateCompleteness(
  usage: UsageCompletenessAggregate,
  cache: UsageCompletenessAggregate,
  stateless: StatelessProofCompleteness,
): UsageCompletenessAggregate {
  const statelessOk = stateless === 'complete' || stateless === 'notApplicable';
  const statelessMissing = stateless === 'missing' || stateless === 'notApplicable';
  if (usage === 'complete' && cache === 'complete' && statelessOk) return 'complete';
  if (usage === 'missing' && cache === 'missing' && statelessMissing) return 'missing';
  if (usage === 'unknown' || cache === 'unknown') return 'unknown';
  return 'partial';
}

/**
 * Pure total function returning a discriminated result. Success carries the
 * computed derived aggregate; failure represents aggregate token overflow
 * (the pre-addition safe-integer check). Constructing the discriminated
 * `DeriveAggregateInput` guarantees that capabilityMode/statelessProof
 * combinations are structurally valid.
 */
export function deriveAggregate(input: DeriveAggregateInput): DeriveAggregateResult {
  return deriveAggregateInternal(input);
}

/**
 * Internal loose entry point used by the semantic validator after it has
 * cross-checked capability.mode vs. capability.statelessProof and can safely
 * pass the raw pair. Public callers use the discriminated `DeriveAggregateInput`.
 */
export function deriveAggregateInternal(input: DeriveAggregateInputLoose): DeriveAggregateResult {
  assertValidInput(input);
  const attempts: AttemptObservation[] = [...input.attempts];
  const { capabilityMode, statelessProof } = input;

  const grouped = groupByRequest(attempts);
  const ordinals = Array.from(grouped.keys()).sort((a, b) => a - b);

  const overflowErrors: MetadataError[] = [];

  const requests: RequestObservation[] = ordinals.map((ord) =>
    reduceRequest(ord, grouped.get(ord) ?? [], overflowErrors),
  );
  if (overflowErrors.length > 0) return { valid: false, errors: overflowErrors };

  const { capability, cacheStatus } = reduceRunCapability(requests);
  const usageAggregate = deriveUsageAggregate(requests, attempts.length, overflowErrors);
  if (overflowErrors.length > 0) return { valid: false, errors: overflowErrors };

  const retryObservations = deriveRetryObservations(ordinals, grouped, attempts.length);
  const errorCodes = deriveErrorCodes(attempts);

  const usageCompleteness = deriveUsageCompletenessAggregate(requests);
  const cacheCompleteness = deriveCacheCompletenessAggregate(requests);
  const statelessCompleteness = deriveStatelessProofCompleteness(capabilityMode, statelessProof);
  const aggregateCompleteness = deriveAggregateCompleteness(
    usageCompleteness,
    cacheCompleteness,
    statelessCompleteness,
  );

  return {
    valid: true,
    aggregate: {
      normalizedUsage: {
        attempts,
        requests,
        aggregate: usageAggregate,
      },
      capability: { aggregate: capability },
      cacheStatus,
      retryObservations,
      errorCodes,
      telemetryCompleteness: {
        usage: usageCompleteness,
        cache: cacheCompleteness,
        statelessProof: statelessCompleteness,
        aggregate: aggregateCompleteness,
      },
    },
  };
}
