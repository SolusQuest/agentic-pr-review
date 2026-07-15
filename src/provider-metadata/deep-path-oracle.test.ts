import { describe, it, expect } from 'vitest';
import { finalizePath, utf8ByteLength } from './safe-path-helpers.js';
import { parseProviderRunMetadata } from './parse.js';
import { MAX_METADATA_PATH_CHARS, MAX_METADATA_PATH_UTF8_BYTES } from './types.js';

/**
 * Deep-path oracle -- byte-exact truncation and no-truncation cases against
 * the frozen `MetadataError.path` caps (`MAX_METADATA_PATH_CHARS = 256`,
 * `MAX_METADATA_PATH_UTF8_BYTES = 1024`). Values are asserted through the
 * public parser wherever meaningful, and through `finalizePath` directly for
 * cases the parser cannot reach (e.g. artificially long path segments the
 * schema wouldn't tolerate).
 */

describe('deep-path safe-path oracle -- no truncation for short paths', () => {
  it('composes a 5-segment schema-known path unchanged (under both caps)', () => {
    const segments = ['normalizedUsage', 'attempts', '0', 'attemptErrorCodes', '2'];
    const out = finalizePath(segments);
    expect(out).toBe('/normalizedUsage/attempts/0/attemptErrorCodes/2');
    expect(out.length).toBeLessThanOrEqual(MAX_METADATA_PATH_CHARS);
    expect(utf8ByteLength(out)).toBeLessThanOrEqual(MAX_METADATA_PATH_UTF8_BYTES);
  });
});

describe('deep-path safe-path oracle -- truncation preserves the final segment and inserts <path-truncated>', () => {
  it('40 x 8-char leading segments + 1 final segment triggers truncation and preserves the final segment', () => {
    const long: string[] = [];
    for (let i = 0; i < 40; i += 1) long.push(`seg${String(i).padStart(4, '0')}`);
    long.push('leaf-final-segment');
    const out = finalizePath(long);
    expect(out.length).toBeLessThanOrEqual(MAX_METADATA_PATH_CHARS);
    expect(utf8ByteLength(out)).toBeLessThanOrEqual(MAX_METADATA_PATH_UTF8_BYTES);
    expect(out.endsWith('/leaf-final-segment')).toBe(true);
    expect(out).toMatch(/<path-truncated>\/leaf-final-segment$/);
  });

  it('single multi-byte final segment above both caps still preserves the final segment (final-segment rule normative)', () => {
    const seg = '\u4e00'.repeat(400); // 400 chars, 1200 UTF-8 bytes
    const out = finalizePath([seg]);
    // The frozen algorithm preserves the final segment verbatim even when it
    // alone exceeds the caps.
    expect(out.endsWith('/' + seg)).toBe(true);
  });
});

describe('deep-path safe-path oracle -- exercised through the parser', () => {
  it('a stage-8 aggregate-mismatch on a deeply-nested schema-known field emits the byte-exact schema-known path', () => {
    // Construct a valid document with normalizedUsage.aggregate that drifts
    // from deriveAggregate; the aggregate-mismatch code should carry a byte-
    // exact schema-known path (e.g. /normalizedUsage/aggregate/attemptCount).
    const doc = {
      schemaVersion: 1,
      selectedProviderId: 'a',
      observedProviderId: 'a',
      resolvedModelId: 'm',
      adapterId: 'a'.repeat(64),
      logicalPrefixSha256: 'a'.repeat(64),
      prefixSha256: 'a'.repeat(64),
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
          attemptCount: 3, // drift: derivation would produce 0 (no attempts)
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
    const r = parseProviderRunMetadata(new TextEncoder().encode(JSON.stringify(doc)));
    expect(r.valid).toBe(false);
    if (r.valid) return;
    const attemptErr = r.errors.find(
      (e) =>
        e.code === 'invalid-metadata-aggregate-mismatch' &&
        e.path === '/normalizedUsage/aggregate/attemptCount',
    );
    expect(attemptErr).toBeDefined();
    // Every returned path is within caps.
    for (const err of r.errors) {
      expect(err.path.length).toBeLessThanOrEqual(MAX_METADATA_PATH_CHARS);
      expect(utf8ByteLength(err.path)).toBeLessThanOrEqual(MAX_METADATA_PATH_UTF8_BYTES);
    }
  });
});
