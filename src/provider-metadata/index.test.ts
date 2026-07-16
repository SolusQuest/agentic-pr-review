import { describe, expect, it } from 'vitest';
import { canonicalJsonBytes } from '../canonical-json/index.js';
import { METADATA_MAX_BYTES } from '../state-v2/constants.js';
import { deriveAggregate, parseProviderRunMetadata } from './index.js';
import type { ValidatedAttempt } from './types.js';

const hash = 'a'.repeat(64);

function standardEmpty(): Record<string, unknown> {
  return {
    schemaVersion: 1,
    selectedProviderId: 'synthetic',
    observedProviderId: 'synthetic',
    resolvedModelId: 'model-1',
    adapterId: hash,
    logicalPrefixSha256: hash,
    prefixSha256: hash,
    capability: { mode: 'standard', aggregate: 'unknown', statelessProof: null },
    cacheStatus: 'unknown',
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
    interactionId: hash,
    consumedInputSha256: hash,
    resultSha256: hash,
    traceSha256: hash,
    predecessorLedgerSha256: 'bootstrap',
    candidateLedgerSha256: hash,
  };
}

function withUnicodeChain(count: number): Record<string, unknown> {
  const root = standardEmpty();
  let cursor: Record<string, unknown> = root;
  for (let i = 0; i < count; i += 1) {
    const child: Record<string, unknown> = {};
    cursor[`unknown-${i}`] = child;
    cursor = child;
  }
  cursor.payload = '\ud800';
  return root;
}

describe('ProviderRunMetadataV1 parser', () => {
  it('accepts the legal standard zero-attempt shape', () => {
    const result = parseProviderRunMetadata(canonicalJsonBytes(standardEmpty()));
    expect(result.valid).toBe(true);
  });

  it('enforces the raw byte cap before JSON parsing', () => {
    const exactLimit = new Uint8Array(METADATA_MAX_BYTES);
    const body = canonicalJsonBytes(standardEmpty());
    exactLimit.set(body);
    exactLimit.fill(0x20, body.length);
    const exactResult = parseProviderRunMetadata(exactLimit);
    expect(exactResult.valid).toBe(true);
    const result = parseProviderRunMetadata(new Uint8Array(METADATA_MAX_BYTES + 1));
    expect(result).toEqual({
      valid: false,
      errors: [{ code: 'invalid-metadata-bounds', path: '' }],
    });
  });

  it('returns structured diagnostics for deep arrays and objects below the raw cap', () => {
    const deepArray = new TextEncoder().encode(`${'['.repeat(8000)}${']'.repeat(8000)}`);
    const deepObject = new TextEncoder().encode(`${'{"x":'.repeat(4000)}null${'}'.repeat(4000)}`);
    expect(() => parseProviderRunMetadata(deepArray)).not.toThrow();
    expect(() => parseProviderRunMetadata(deepObject)).not.toThrow();
    expect(parseProviderRunMetadata(deepArray).valid).toBe(false);
    expect(parseProviderRunMetadata(deepObject).valid).toBe(false);
  });

  it('keeps depth-first ordering when a later sibling property name is unsafe', () => {
    const value = standardEmpty();
    (value as Record<string, unknown>).normalizedUsage = {
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
    };
    const nested: Record<string, unknown> = { b: '\ud800' };
    (value as Record<string, unknown>).a = nested;
    (value as Record<string, unknown>)['\ud800'] = 0;
    const result = parseProviderRunMetadata(new TextEncoder().encode(JSON.stringify(value)));
    expect(result.valid).toBe(false);
    expect(result.valid ? [] : result.errors[0]).toEqual({
      code: 'invalid-metadata-unicode',
      path: '/<untrusted-property>/<untrusted-property>',
    });
  });

  it('rejects a UTF-8 BOM', () => {
    const body = canonicalJsonBytes(standardEmpty());
    const bytes = new Uint8Array(body.length + 3);
    bytes.set([0xef, 0xbb, 0xbf]);
    bytes.set(body, 3);
    const result = parseProviderRunMetadata(bytes);
    expect(result).toEqual({ valid: false, errors: [{ code: 'invalid-metadata-bom', path: '' }] });
  });

  it('rejects duplicate properties after valid JSON parsing', () => {
    const json = JSON.stringify(standardEmpty()).replace(
      '"schemaVersion":1',
      '"schemaVersion":1,"schemaVersion":1',
    );
    const result = parseProviderRunMetadata(new TextEncoder().encode(json));
    expect(result).toEqual({
      valid: false,
      errors: [{ code: 'invalid-metadata-duplicate-json-property', path: '' }],
    });
  });

  it('keeps JSON syntax and UTF-8 failures ahead of later stages', () => {
    expect(parseProviderRunMetadata(new TextEncoder().encode('{'))).toEqual({
      valid: false,
      errors: [{ code: 'invalid-metadata-json', path: '' }],
    });
    expect(parseProviderRunMetadata(new Uint8Array([0xc3, 0x28]))).toEqual({
      valid: false,
      errors: [{ code: 'invalid-metadata-utf8', path: '' }],
    });
  });

  it('detects duplicate properties in nested objects', () => {
    const json = '{"normalizedUsage":{"attempts":[],"attempts":[]}}';
    expect(parseProviderRunMetadata(new TextEncoder().encode(json))).toEqual({
      valid: false,
      errors: [{ code: 'invalid-metadata-duplicate-json-property', path: '' }],
    });
  });

  it('reports root string-safety violations at the empty pointer', () => {
    for (const value of ['\ud800', '\u0000']) {
      const result = parseProviderRunMetadata(new TextEncoder().encode(JSON.stringify(value)));
      expect(result).toEqual({
        valid: false,
        errors: [{ code: 'invalid-metadata-unicode', path: '' }],
      });
    }
  });

  it.each([
    [
      'G1',
      '{"secretToken":{"nestedProp":"\\ud800"}}',
      '/<untrusted-property>/<untrusted-property>',
    ],
    [
      'G2',
      '{"attacker\\ncontrolled":{"nestedProp":"\\ud800"}}',
      '/<invalid-control>/<untrusted-property>',
    ],
    ['G3', '{"\\ud800":1}', '/<invalid-utf16>'],
    ['G4', '{"contains\\u0000nul":1}', '/<invalid-nul>'],
  ] as const)('%s produces the shared parser-facing Unicode path', (_id, json, path) => {
    expect(parseProviderRunMetadata(new TextEncoder().encode(json))).toEqual({
      valid: false,
      errors: [{ code: 'invalid-metadata-unicode', path }],
    });
  });

  it('G5 uses the empty-name path in the parser schema stage', () => {
    const value = standardEmpty();
    value[''] = 1;
    expect(parseProviderRunMetadata(new TextEncoder().encode(JSON.stringify(value)))).toEqual({
      valid: false,
      errors: [{ code: 'invalid-metadata-additional-property', path: '/<empty-name>' }],
    });
  });

  it('G6 keeps a schema-known ancestor chain in the parser path', () => {
    const value = standardEmpty();
    value.resolvedModelId = '\ud800';
    expect(parseProviderRunMetadata(new TextEncoder().encode(JSON.stringify(value)))).toEqual({
      valid: false,
      errors: [{ code: 'invalid-metadata-unicode', path: '/resolvedModelId' }],
    });
  });

  it('G7 accepts a well-formed surrogate pair through the parser', () => {
    const value = standardEmpty();
    value.selectedProviderId = 'agentic\ud83d\ude00review';
    value.observedProviderId = value.selectedProviderId;
    expect(parseProviderRunMetadata(canonicalJsonBytes(value)).valid).toBe(true);
  });

  it('bounds and terminates a large same-stage error list', () => {
    const value = standardEmpty();
    const attempt = {
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
      outputTokens: Number.MAX_SAFE_INTEGER,
      attemptErrorCodes: [],
    };
    (value.normalizedUsage as { attempts: unknown[] }).attempts = Array.from(
      { length: 32 },
      () => ({ ...attempt }),
    );
    const result = parseProviderRunMetadata(canonicalJsonBytes(value));
    expect(result.valid).toBe(false);
    if (!result.valid) {
      expect(result.errors).toHaveLength(32);
      expect(result.errors.at(-1)).toEqual({
        code: 'invalid-metadata-error-list-truncated',
        path: '',
      });
    }
  });

  it('rejects a stateless aggregate with no attempts', () => {
    const result = deriveAggregate({
      attempts: [],
      capabilityMode: 'stateless',
      statelessProof: { kind: 'synthetic', verified: false },
    });
    expect(result).toEqual({
      valid: false,
      errors: [{ code: 'invalid-metadata-stateless-proof', path: '/capability' }],
    });
  });

  it('retains the structured token overflow diagnostic', () => {
    const attempt = {
      requestOrdinal: 0,
      attemptOrdinal: 0,
      outcome: 'succeeded',
      capability: 'eligible',
      cacheStatus: 'miss',
      usageCompleteness: 'complete',
      totalInputTokens: 0,
      uncachedInputTokens: 0,
      cacheWriteInputTokens: 0,
      cacheReadInputTokens: 0,
      outputTokens: Number.MAX_SAFE_INTEGER,
      attemptErrorCodes: [],
    } as const;
    const result = deriveAggregate({
      attempts: [
        attempt as unknown as ValidatedAttempt,
        { ...attempt, attemptOrdinal: 1, outputTokens: 1 } as unknown as ValidatedAttempt,
      ],
      capabilityMode: 'standard',
      statelessProof: null,
    });
    expect(result.valid).toBe(false);
    if (!result.valid)
      expect(result.errors).toEqual([
        {
          code: 'invalid-metadata-token-out-of-range',
          path: '/normalizedUsage/attempts/1/outputTokens',
        },
      ]);
  });

  it('uses a bounded, final-segment-preserving Unicode path', () => {
    const noTruncation = parseProviderRunMetadata(
      new TextEncoder().encode(JSON.stringify(withUnicodeChain(9))),
    );
    expect(noTruncation.valid).toBe(false);
    if (!noTruncation.valid) {
      expect(noTruncation.errors[0]?.code).toBe('invalid-metadata-unicode');
      expect(noTruncation.errors[0]?.path).toBe(
        '/' + '<untrusted-property>/'.repeat(9) + '<untrusted-property>',
      );
    }

    const truncation = parseProviderRunMetadata(
      new TextEncoder().encode(JSON.stringify(withUnicodeChain(13))),
    );
    expect(truncation.valid).toBe(false);
    if (!truncation.valid) {
      const path = truncation.errors[0]?.path ?? '';
      expect(path).toBe(
        '/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<path-truncated>/<untrusted-property>',
      );
      expect(path.length).toBe(248);
    }
  });

  it('sanitizes an unknown schema-stage property', () => {
    const value = standardEmpty();
    value.secretToken = 1;
    const result = parseProviderRunMetadata(canonicalJsonBytes(value));
    expect(result).toEqual({
      valid: false,
      errors: [{ code: 'invalid-metadata-additional-property', path: '/<untrusted-property>' }],
    });
  });

  it('uses the request ordinal for request-level usage diagnostics', () => {
    const attempt = {
      requestOrdinal: 1,
      attemptOrdinal: 0,
      outcome: 'succeeded',
      capability: 'eligible',
      cacheStatus: 'miss',
      usageCompleteness: 'complete',
      totalInputTokens: 10,
      uncachedInputTokens: 1,
      cacheWriteInputTokens: 1,
      cacheReadInputTokens: 1,
      outputTokens: 1,
      attemptErrorCodes: [],
    } as unknown as ValidatedAttempt;
    const result = deriveAggregate({
      attempts: [attempt],
      capabilityMode: 'standard',
      statelessProof: null,
    });
    expect(result).toEqual({
      valid: false,
      errors: [
        {
          code: 'invalid-metadata-attempt-usage-inconsistent',
          path: '/normalizedUsage/requests/1',
        },
      ],
    });
  });

  it('bounds ordinary schema-error paths through the same renderer', () => {
    const value = standardEmpty();
    (value.capability as { aggregate: unknown }).aggregate = 'not-a-capability';
    const result = parseProviderRunMetadata(canonicalJsonBytes(value));
    expect(result).toEqual({
      valid: false,
      errors: [{ code: 'invalid-metadata-unknown-enum', path: '/capability/aggregate' }],
    });
  });

  it('keeps overflow and independent retry drift diagnostics together', () => {
    const value = standardEmpty() as any;
    const attempt = {
      requestOrdinal: 0,
      attemptOrdinal: 0,
      outcome: 'failed',
      capability: 'eligible',
      cacheStatus: 'miss',
      usageCompleteness: 'complete',
      totalInputTokens: 0,
      uncachedInputTokens: 0,
      cacheWriteInputTokens: 0,
      cacheReadInputTokens: 0,
      outputTokens: Number.MAX_SAFE_INTEGER,
      attemptErrorCodes: ['provider_timeout'],
    };
    value.normalizedUsage.attempts = [
      attempt,
      {
        ...attempt,
        attemptOrdinal: 1,
        outcome: 'succeeded',
        attemptErrorCodes: [],
        outputTokens: 1,
      },
    ];
    value.normalizedUsage.requests = [
      {
        requestOrdinal: 0,
        capability: 'eligible',
        cacheStatus: 'miss',
        usageCompleteness: 'complete',
        totalInputTokens: 0,
        uncachedInputTokens: 0,
        cacheWriteInputTokens: 0,
        cacheReadInputTokens: 0,
        outputTokens: Number.MAX_SAFE_INTEGER,
      },
    ];
    value.normalizedUsage.aggregate = {
      totalInputTokens: 0,
      uncachedInputTokens: 0,
      cacheWriteInputTokens: 0,
      cacheReadInputTokens: 0,
      outputTokens: Number.MAX_SAFE_INTEGER,
      requestCount: 1,
      attemptCount: 2,
    };
    value.retryObservations = {
      requests: [
        {
          requestOrdinal: 0,
          attemptCount: 2,
          succeededCount: 0,
          failedCount: 1,
          cancelledCount: 0,
        },
      ],
      aggregate: {
        requestCount: 1,
        attemptCount: 2,
        succeededCount: 0,
        failedCount: 1,
        cancelledCount: 0,
      },
    };
    value.errorCodes = ['provider_timeout'];
    value.capability.aggregate = 'eligible';
    value.cacheStatus = 'miss';
    value.telemetryCompleteness = {
      usage: 'complete',
      cache: 'complete',
      statelessProof: 'notApplicable',
      aggregate: 'complete',
    };
    const result = parseProviderRunMetadata(canonicalJsonBytes(value));
    expect(result.valid).toBe(false);
    if (!result.valid) {
      expect(result.errors).toEqual(
        expect.arrayContaining([
          {
            code: 'invalid-metadata-token-out-of-range',
            path: '/normalizedUsage/attempts/1/outputTokens',
          },
          { code: 'invalid-metadata-aggregate-mismatch', path: '' },
        ]),
      );
    }
  });

  it('keeps input-partition diagnostics when a different token field overflows', () => {
    const value = standardEmpty() as any;
    value.normalizedUsage.attempts = [
      {
        requestOrdinal: 0,
        attemptOrdinal: 0,
        outcome: 'failed',
        capability: 'eligible',
        cacheStatus: 'miss',
        usageCompleteness: 'complete',
        totalInputTokens: 10,
        uncachedInputTokens: 9,
        cacheWriteInputTokens: 0,
        cacheReadInputTokens: 0,
        outputTokens: Number.MAX_SAFE_INTEGER,
        attemptErrorCodes: ['provider_timeout'],
      },
      {
        requestOrdinal: 0,
        attemptOrdinal: 1,
        outcome: 'succeeded',
        capability: 'eligible',
        cacheStatus: 'miss',
        usageCompleteness: 'complete',
        totalInputTokens: 0,
        uncachedInputTokens: 0,
        cacheWriteInputTokens: 0,
        cacheReadInputTokens: 0,
        outputTokens: 1,
        attemptErrorCodes: [],
      },
    ];
    value.normalizedUsage.requests = [
      {
        requestOrdinal: 0,
        capability: 'eligible',
        cacheStatus: 'miss',
        usageCompleteness: 'partial',
        totalInputTokens: 10,
        uncachedInputTokens: 9,
        cacheWriteInputTokens: 0,
        cacheReadInputTokens: 0,
        outputTokens: null,
      },
    ];
    value.normalizedUsage.aggregate = {
      totalInputTokens: 10,
      uncachedInputTokens: 9,
      cacheWriteInputTokens: 0,
      cacheReadInputTokens: 0,
      outputTokens: null,
      requestCount: 1,
      attemptCount: 2,
    };
    value.retryObservations = {
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
    };
    value.capability.aggregate = 'eligible';
    value.cacheStatus = 'miss';
    value.telemetryCompleteness = {
      usage: 'partial',
      cache: 'complete',
      statelessProof: 'notApplicable',
      aggregate: 'partial',
    };
    const result = parseProviderRunMetadata(canonicalJsonBytes(value));
    expect(result.valid).toBe(false);
    if (!result.valid) {
      expect(result.errors).toEqual(
        expect.arrayContaining([
          {
            code: 'invalid-metadata-token-out-of-range',
            path: '/normalizedUsage/attempts/1/outputTokens',
          },
          {
            code: 'invalid-metadata-attempt-usage-inconsistent',
            path: '/normalizedUsage/requests/0',
          },
        ]),
      );
    }
  });
});
