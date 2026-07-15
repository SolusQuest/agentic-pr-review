import { describe, expect, it } from 'vitest';
import { parseProviderRunMetadata } from './parse.js';
import { MAX_METADATA_ERRORS } from './types.js';
import { finalizeErrors } from './error-list.js';
import type { MetadataError } from './types.js';

const encoder = new TextEncoder();
function bytes(s: string): Uint8Array {
  return encoder.encode(s);
}

/**
 * Conformance vectors from issue #51 covering the ordered exception mapping at
 * stage 7, the stage-8 non-suppression policy, and the deterministic sorted
 * output. These exist as inline vectors rather than fixture files so they can
 * change alongside the source without a byte-level fixture regeneration step.
 */
describe('stage 7 ordered-exception mapping produces per-Ajv-error codes', () => {
  it('an enum violation at path A and an unrelated pattern violation at path B return both mapped codes in sorted order', () => {
    // Build a minimal shape violating two schema keywords at different paths:
    //  - `cacheStatus`: unknown enum value.
    //  - `producingRunId`: fails the ^[1-9][0-9]{0,18}$ pattern.
    // The maxLength failure is neither additionalProperties, enum, nor a
    // token-field maximum, so it maps to `invalid-metadata-schema`. The enum
    // failure maps to `invalid-metadata-unknown-enum`. Both must be present in
    // the returned MetadataError[] after deterministic sorting.
    const shape = {
      schemaVersion: 1,
      selectedProviderId: 'a',
      observedProviderId: 'a',
      resolvedModelId: 'x'.repeat(300),
      adapterId: 'a'.repeat(64),
      logicalPrefixSha256: 'a'.repeat(64),
      prefixSha256: 'a'.repeat(64),
      capability: { mode: 'standard', aggregate: 'unknown', statelessProof: null },
      cacheStatus: 'BOGUS',
      normalizedUsage: {
        attempts: [],
        requests: [],
        aggregate: {
          totalInputTokens: null,
          uncachedInputTokens: null,
          cacheWriteInputTokens: null,
          cacheReadInputTokens: null,
          outputTokens: null,
          requestCount: 0,
          attemptCount: 0,
        },
      },
      retryObservations: {
        requests: [],
        aggregate: {
          requestCount: 0,
          attemptCount: 0,
          succeededCount: 0,
          failedCount: 0,
          cancelledCount: 0,
        },
      },
      errorCodes: [],
      telemetryCompleteness: {
        usage: 'missing',
        cache: 'missing',
        statelessProof: 'notApplicable',
        aggregate: 'missing',
      },
      producingRunId: '1',
      runAttempt: 1,
      interactionId: 'a'.repeat(64),
      consumedInputSha256: 'a'.repeat(64),
      resultSha256: 'a'.repeat(64),
      traceSha256: 'a'.repeat(64),
      predecessorLedgerSha256: 'bootstrap',
      candidateLedgerSha256: 'a'.repeat(64),
    };
    const r = parseProviderRunMetadata(bytes(JSON.stringify(shape)));
    expect(r.valid).toBe(false);
    if (r.valid) return;
    const codes = r.errors.map((e) => e.code);
    expect(codes).toContain('invalid-metadata-unknown-enum');
    expect(codes).toContain('invalid-metadata-schema');
    // Deterministic sorted output invariant: earlier path first.
    const enumErr = r.errors.find((e) => e.code === 'invalid-metadata-unknown-enum')!;
    const schemaErr = r.errors.find((e) => e.code === 'invalid-metadata-schema')!;
    expect(enumErr.path).toBe('/cacheStatus');
    expect(schemaErr.path).toBe('/resolvedModelId');
    // /cacheStatus < /resolvedModelId in byte order.
    expect(r.errors.findIndex((e) => e === enumErr)).toBeLessThan(
      r.errors.findIndex((e) => e === schemaErr),
    );
  });
});

describe('post-processing truncation sentinel', () => {
  it('sentinel is the terminal entry and total length equals MAX_METADATA_ERRORS', () => {
    // Directly exercise finalizeErrors with 33 distinct (code, path) tuples.
    const input: MetadataError[] = Array.from({ length: MAX_METADATA_ERRORS + 1 }, (_, i) => ({
      code: 'invalid-metadata-schema' as const,
      path: '/leaf-' + String(i).padStart(3, '0'),
    }));
    const out = finalizeErrors(input);
    expect(out.length).toBe(MAX_METADATA_ERRORS);
    expect(out[out.length - 1]!).toEqual({
      code: 'invalid-metadata-error-list-truncated',
      path: '',
    });
    // The 31 preceding entries are real errors from the sorted list.
    for (let i = 0; i < MAX_METADATA_ERRORS - 1; i += 1) {
      expect(out[i]!.code).toBe('invalid-metadata-schema');
    }
  });
});
