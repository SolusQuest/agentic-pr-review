import { describe, expect, it } from 'vitest';
import { canonicalJsonBytes } from '../canonical-json/index.js';
import { deriveAggregate, parseProviderRunMetadata } from '../provider-metadata/index.js';
import {
  DEEPSEEK_CACHE_CONTRACT_IDENTITY,
  deepSeekContractForMaxFindings,
} from './deepseek-contract.js';

const hash = 'a'.repeat(64);

function liveMetadata(hit: number, miss: number, cacheStatus: 'hit' | 'miss' | 'partial') {
  const attempt = {
    requestOrdinal: 0,
    attemptOrdinal: 0,
    outcome: 'succeeded' as const,
    capability: 'eligible' as const,
    cacheStatus,
    usageCompleteness: 'partial' as const,
    totalInputTokens: 2,
    uncachedInputTokens: miss,
    cacheWriteInputTokens: null,
    cacheReadInputTokens: hit,
    outputTokens: 1,
    attemptErrorCodes: [],
  };
  return {
    schemaVersion: 1 as const,
    selectedProviderId: DEEPSEEK_CACHE_CONTRACT_IDENTITY.providerId,
    observedProviderId: DEEPSEEK_CACHE_CONTRACT_IDENTITY.providerId,
    resolvedModelId: DEEPSEEK_CACHE_CONTRACT_IDENTITY.modelId,
    adapterId: DEEPSEEK_CACHE_CONTRACT_IDENTITY.adapterId,
    logicalPrefixSha256: hash,
    prefixSha256: hash,
    capability: { mode: 'standard' as const, aggregate: 'eligible' as const, statelessProof: null },
    cacheStatus,
    normalizedUsage: {
      attempts: [attempt],
      requests: [
        {
          requestOrdinal: 0,
          capability: 'eligible' as const,
          cacheStatus,
          usageCompleteness: 'partial' as const,
          totalInputTokens: 2,
          uncachedInputTokens: miss,
          cacheWriteInputTokens: null,
          cacheReadInputTokens: hit,
          outputTokens: 1,
        },
      ],
      aggregate: {
        totalInputTokens: 2,
        uncachedInputTokens: miss,
        cacheWriteInputTokens: null,
        cacheReadInputTokens: hit,
        outputTokens: 1,
        requestCount: 1,
        attemptCount: 1,
      },
    },
    retryObservations: {
      requests: [
        {
          requestOrdinal: 0,
          attemptCount: 1,
          succeededCount: 1,
          failedCount: 0,
          cancelledCount: 0,
        },
      ],
      aggregate: {
        requestCount: 1,
        attemptCount: 1,
        succeededCount: 1,
        failedCount: 0,
        cancelledCount: 0,
      },
    },
    errorCodes: [],
    telemetryCompleteness: {
      usage: 'partial' as const,
      cache: 'complete' as const,
      statelessProof: 'notApplicable' as const,
      aggregate: 'partial' as const,
    },
    producingRunId: '1',
    runAttempt: 1,
    interactionId: hash,
    consumedInputSha256: hash,
    resultSha256: hash,
    traceSha256: hash,
    predecessorLedgerSha256: 'bootstrap',
    candidateLedgerSha256: hash,
  };
}

describe('DeepSeek live transaction metadata oracle', () => {
  it.each([
    [2, 0, 'hit'],
    [0, 2, 'miss'],
    [1, 1, 'partial'],
  ] as const)(
    'parses %s/%s usage and matches deriveAggregate for %s cache',
    (hit, miss, status) => {
      const parsed = parseProviderRunMetadata(canonicalJsonBytes(liveMetadata(hit, miss, status)));
      expect(parsed.valid).toBe(true);
      if (!parsed.valid) return;

      const derived = deriveAggregate({
        attempts: parsed.metadata.normalizedUsage.attempts,
        capabilityMode: 'standard',
        statelessProof: null,
      });
      expect(derived.valid).toBe(true);
      if (!derived.valid) return;
      expect(derived.aggregate.normalizedUsage.aggregate).toEqual(
        parsed.metadata.normalizedUsage.aggregate,
      );
      expect(derived.aggregate.cacheStatus).toBe(status);
    },
  );

  it('binds the lower policy cap to a distinct DeepSeek identity used by live transactions', () => {
    const lower = deepSeekContractForMaxFindings(7);
    expect(lower.identity.policyId).not.toBe(DEEPSEEK_CACHE_CONTRACT_IDENTITY.policyId);
    expect(lower.identity.adapterId).toBe(DEEPSEEK_CACHE_CONTRACT_IDENTITY.adapterId);
  });
});
