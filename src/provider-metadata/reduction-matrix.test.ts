import { describe, expect, it } from 'vitest';
import { deriveAggregate } from './index.js';
import type { CacheStatus, ObservedCapability, ValidatedAttempt } from './types.js';

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

function aggregate(attempts: readonly ValidatedAttempt[]) {
  const result = deriveAggregate({ attempts, capabilityMode: 'standard', statelessProof: null });
  if (!result.valid) throw new Error(JSON.stringify(result.errors));
  return result.aggregate;
}

function twoRequestAggregate(
  request0: Partial<ValidatedAttempt>,
  request1: Partial<ValidatedAttempt>,
) {
  return aggregate([
    attempt({ requestOrdinal: 0, attemptOrdinal: 0, ...request0 }),
    attempt({ requestOrdinal: 1, attemptOrdinal: 0, ...request1 }),
  ]);
}

describe('ProviderRunMetadataV1 reduction truth tables', () => {
  const runCacheCases: readonly [string, CacheStatus, CacheStatus, CacheStatus][] = [
    ['all hit', 'hit', 'hit', 'hit'],
    ['all miss', 'miss', 'miss', 'miss'],
    ['mixed hit miss', 'hit', 'miss', 'partial'],
    ['partial present', 'partial', 'hit', 'partial'],
    ['unsupported present', 'unsupported', 'hit', 'unsupported'],
    ['unknown present', 'unknown', 'hit', 'unknown'],
  ];
  for (const [name, first, second, expected] of runCacheCases) {
    it(`reduces request-to-run cache: ${name}`, () => {
      expect(
        twoRequestAggregate(
          { cacheStatus: first, capability: 'eligible' },
          { cacheStatus: second, capability: 'eligible' },
        ).cacheStatus,
      ).toBe(expected);
    });
  }

  const runCapabilityCases: readonly [string, ObservedCapability, ObservedCapability, string][] = [
    ['eligible', 'eligible', 'eligible', 'eligible'],
    ['ineligible collapses to unsupported', 'ineligible', 'eligible', 'unsupported'],
    ['unsupported wins', 'unsupported', 'eligible', 'unsupported'],
    ['telemetryUnavailable collapses to unknown', 'telemetryUnavailable', 'eligible', 'unknown'],
    ['unknown wins over eligible', 'unknown', 'eligible', 'unknown'],
  ];
  for (const [name, first, second, expected] of runCapabilityCases) {
    it(`reduces request-to-run capability: ${name}`, () => {
      expect(
        twoRequestAggregate({ capability: first }, { capability: second }).capability.aggregate,
      ).toBe(expected);
    });
  }

  it('uses missing as the zero-request telemetry and aggregate baseline', () => {
    const result = aggregate([]);
    expect(result.capability.aggregate).toBe('unknown');
    expect(result.cacheStatus).toBe('unknown');
    expect(result.telemetryCompleteness).toEqual({
      usage: 'missing',
      cache: 'missing',
      statelessProof: 'notApplicable',
      aggregate: 'missing',
    });
  });

  it('locks partial cache telemetry for mixed supported and unsupported requests', () => {
    const result = twoRequestAggregate(
      { cacheStatus: 'unsupported', usageCompleteness: 'missing' },
      {
        cacheStatus: 'hit',
        usageCompleteness: 'complete',
        totalInputTokens: 1,
        uncachedInputTokens: 1,
        cacheWriteInputTokens: 0,
        cacheReadInputTokens: 0,
        outputTokens: 1,
      },
    );
    expect(result.telemetryCompleteness.cache).toBe('partial');
    expect(result.telemetryCompleteness.aggregate).toBe('partial');
  });
  const cacheCases: readonly [string, readonly CacheStatus[], CacheStatus][] = [
    ['hit + miss', ['hit', 'miss'], 'partial'],
    ['partial + hit', ['partial', 'hit'], 'partial'],
    ['unknown + hit', ['unknown', 'hit'], 'unknown'],
    ['unsupported + hit', ['unsupported', 'hit'], 'unsupported'],
    ['unsupported + unknown', ['unsupported', 'unknown'], 'unsupported'],
  ];

  for (const [name, statuses, expected] of cacheCases) {
    it(`reduces request cache status: ${name}`, () => {
      const result = aggregate(
        statuses.map((cacheStatus, attemptOrdinal) =>
          attempt({ attemptOrdinal, cacheStatus, capability: 'eligible' }),
        ),
      );
      expect(result.normalizedUsage.requests[0]?.cacheStatus).toBe(expected);
    });
  }

  const capabilityCases: readonly [ObservedCapability[], string][] = [
    [['unsupported', 'eligible'], 'unsupported'],
    [['ineligible', 'eligible'], 'ineligible'],
    [['telemetryUnavailable', 'eligible'], 'telemetryUnavailable'],
    [['unknown', 'eligible'], 'unknown'],
    [['eligible', 'eligible'], 'eligible'],
  ];
  for (const [capabilities, expected] of capabilityCases) {
    it(`reduces request capability: ${capabilities.join(' + ')}`, () => {
      const result = aggregate(
        capabilities.map((capability, attemptOrdinal) => attempt({ attemptOrdinal, capability })),
      );
      expect(result.normalizedUsage.requests[0]?.capability).toBe(expected);
    });
  }

  it('covers complete, missing, and output-only partial usage', () => {
    const complete = aggregate([
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
    expect(complete.normalizedUsage.requests[0]?.usageCompleteness).toBe('complete');
    expect(aggregate([attempt()]).normalizedUsage.requests[0]?.usageCompleteness).toBe('missing');
    expect(
      aggregate([attempt({ usageCompleteness: 'partial', outputTokens: 2 })]).normalizedUsage
        .requests[0]?.usageCompleteness,
    ).toBe('partial');
  });

  it('sums retry observations and derives telemetry completeness', () => {
    const result = aggregate([
      attempt({ attemptOrdinal: 0, outcome: 'failed', attemptErrorCodes: ['provider_timeout'] }),
      attempt({ attemptOrdinal: 1, outcome: 'succeeded' }),
    ]);
    expect(result.retryObservations).toEqual({
      requests: [
        {
          requestOrdinal: 0,
          attemptCount: 2,
          succeededCount: 1,
          failedCount: 1,
          cancelledCount: 0,
        },
      ],
      aggregate: {
        requestCount: 1,
        attemptCount: 2,
        succeededCount: 1,
        failedCount: 1,
        cancelledCount: 0,
      },
    });
    expect(result.telemetryCompleteness).toEqual({
      usage: 'missing',
      cache: 'unknown',
      statelessProof: 'notApplicable',
      aggregate: 'unknown',
    });
  });

  it('covers verified and unverified stateless proof telemetry', () => {
    const verified = deriveAggregate({
      attempts: [attempt({ cacheStatus: 'hit' })],
      capabilityMode: 'stateless',
      statelessProof: { kind: 'synthetic', verified: true },
    });
    expect(verified.valid && verified.aggregate.telemetryCompleteness.statelessProof).toBe(
      'complete',
    );
    const unverified = deriveAggregate({
      attempts: [attempt({ attemptErrorCodes: ['stateless_proof_missing'] })],
      capabilityMode: 'stateless',
      statelessProof: { kind: 'synthetic', verified: false },
    });
    expect(unverified.valid && unverified.aggregate.telemetryCompleteness.statelessProof).toBe(
      'missing',
    );
  });
});
