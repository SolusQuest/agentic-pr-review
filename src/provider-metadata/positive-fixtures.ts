import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { canonicalJsonBytes } from '../canonical-json/index.js';
import { deriveAggregate, parseProviderRunMetadata } from './index.js';
import type { ValidatedAttempt } from './types.js';

const basePath = resolve('protocol/fixtures/provider-run-metadata/v1/valid-standard-resumed.json');

function base(): any {
  return JSON.parse(readFileSync(basePath, 'utf8'));
}

function attempt(overrides: Partial<ValidatedAttempt> = {}): ValidatedAttempt {
  return {
    requestOrdinal: 0,
    attemptOrdinal: 0,
    outcome: 'succeeded',
    capability: 'eligible',
    cacheStatus: 'unknown',
    usageCompleteness: 'missing',
    totalInputTokens: null,
    uncachedInputTokens: null,
    cacheWriteInputTokens: null,
    cacheReadInputTokens: null,
    outputTokens: null,
    attemptErrorCodes: [],
    ...overrides,
  } as ValidatedAttempt;
}

function parseDerived(
  attempts: readonly ValidatedAttempt[],
  capabilityMode: 'standard' | 'stateless' = 'standard',
  statelessProof: { kind: 'providerAdvertised' | 'synthetic'; verified: boolean } | null = null,
): boolean {
  const value = base();
  const derived =
    capabilityMode === 'standard'
      ? deriveAggregate({ attempts, capabilityMode: 'standard', statelessProof: null })
      : deriveAggregate({ attempts, capabilityMode, statelessProof: statelessProof! });
  if (!derived.valid) throw new Error(JSON.stringify(derived.errors));
  value.capability = {
    mode: capabilityMode,
    aggregate: derived.aggregate.capability.aggregate,
    statelessProof,
  };
  value.normalizedUsage = {
    attempts,
    requests: derived.aggregate.normalizedUsage.requests,
    aggregate: derived.aggregate.normalizedUsage.aggregate,
  };
  value.cacheStatus = derived.aggregate.cacheStatus;
  value.retryObservations = derived.aggregate.retryObservations;
  value.errorCodes = derived.aggregate.errorCodes;
  value.telemetryCompleteness = derived.aggregate.telemetryCompleteness;
  return parseProviderRunMetadata(canonicalJsonBytes(value)).valid;
}

export const POSITIVE_FIXTURE_CASES = [
  'cache-hit',
  'cache-miss',
  'cache-partial',
  'cache-unsupported',
  'cache-unknown',
  'capability-eligible',
  'capability-ineligible',
  'capability-unsupported',
  'capability-telemetry-unavailable',
  'capability-unknown',
  'usage-complete',
  'usage-missing',
  'usage-partial-output-only',
  'multi-request-retry',
  'stateless-verified',
  'stateless-unverified',
] as const;

export function runPositiveFixture(name: (typeof POSITIVE_FIXTURE_CASES)[number]): boolean {
  switch (name) {
    case 'cache-hit':
    case 'cache-miss':
    case 'cache-partial':
    case 'cache-unsupported':
    case 'cache-unknown':
      return parseDerived([
        attempt({ cacheStatus: name.slice('cache-'.length) as ValidatedAttempt['cacheStatus'] }),
      ]);
    case 'capability-eligible':
    case 'capability-ineligible':
    case 'capability-telemetry-unavailable':
    case 'capability-unknown':
      return parseDerived([
        attempt({
          capability:
            name === 'capability-telemetry-unavailable'
              ? 'telemetryUnavailable'
              : (name.slice('capability-'.length) as ValidatedAttempt['capability']),
        }),
      ]);
    case 'capability-unsupported':
      return parseDerived([
        attempt({ capability: 'unsupported', attemptErrorCodes: ['capability_unsupported'] }),
      ]);
    case 'usage-complete':
      return parseDerived([
        attempt({
          cacheStatus: 'hit',
          usageCompleteness: 'complete',
          totalInputTokens: 4,
          uncachedInputTokens: 4,
          cacheWriteInputTokens: 0,
          cacheReadInputTokens: 0,
          outputTokens: 2,
        }),
      ]);
    case 'usage-missing':
      return parseDerived([attempt()]);
    case 'usage-partial-output-only':
      return parseDerived([attempt({ usageCompleteness: 'partial', outputTokens: 2 })]);
    case 'multi-request-retry':
      return parseDerived([
        attempt({
          requestOrdinal: 0,
          attemptOrdinal: 0,
          outcome: 'failed',
          attemptErrorCodes: ['provider_timeout'],
        }),
        attempt({ requestOrdinal: 0, attemptOrdinal: 1 }),
        attempt({ requestOrdinal: 1, attemptOrdinal: 0 }),
      ]);
    case 'stateless-verified':
      return parseDerived([attempt({ cacheStatus: 'hit' })], 'stateless', {
        kind: 'synthetic',
        verified: true,
      });
    case 'stateless-unverified':
      return parseDerived(
        [attempt({ attemptErrorCodes: ['stateless_proof_missing'] })],
        'stateless',
        { kind: 'synthetic', verified: false },
      );
  }
}
