import { describe, expect, it } from 'vitest';
import { isRfc3339, semanticIdentityValidate } from './schema.js';
import { makeStateManifestV2Input } from './test-helpers.js';
import { buildStateBundleV2 } from './index.js';

describe('isRfc3339', () => {
  it.each([
    '2026-07-14T00:00:00Z',
    '2026-07-14t00:00:00z',
    '2026-07-14T00:00:00.123Z',
    '2026-07-14T00:00:00+00:00',
    '2026-07-14T12:34:56-07:30',
    '2024-02-29T00:00:00Z', // leap year
    '2026-06-30T23:59:60Z', // leap second is allowed
  ])('accepts valid: %s', (value) => {
    expect(isRfc3339(value)).toBe(true);
  });

  it.each([
    '2026-07-14T00:00:00', // missing timezone
    '2026-07-14 00:00:00Z', // space instead of T
    '2026-13-01T00:00:00Z', // month out of range
    '2026-02-30T00:00:00Z', // day out of range
    '2025-02-29T00:00:00Z', // non-leap Feb 29
    '2026-07-14T24:00:00Z', // hour out of range
    '2026-07-14T00:60:00Z', // minute out of range
    '2026-07-14T00:00:00+24:00', // offset out of range
    '2026-07-14T00:00:00Z trailing',
    'yesterday',
    '',
  ])('rejects invalid: %s', (value) => {
    expect(isRfc3339(value)).toBe(false);
  });
});

describe('semantic validator surfaces RFC 3339 producedAt errors', () => {
  it('flags producedAt without timezone', () => {
    const input = makeStateManifestV2Input({
      provenance: { producedAt: '2026-07-14T00:00:00' },
    });
    // Build should fail at the manifest-shape validator; force the semantic
    // validator directly so we can assert on the code.
    expect(() =>
      buildStateBundleV2(input, new TextEncoder().encode('l'), new TextEncoder().encode('m')),
    ).toThrow();
    // Also confirm the raw semantic check returns the fixed code.
    const built = { ...input } as unknown as import('./manifest.js').StateManifestV2;
    // Splice in descriptor fields so semanticIdentityValidate can run.
    (built as unknown as { ledger: unknown }).ledger = {
      path: 'ledger.json',
      sha256: 'a'.repeat(64),
      bytes: 1,
      schemaVersion: 1,
    };
    (built as unknown as { providerRunMetadata: unknown }).providerRunMetadata = {
      path: 'provider-run-metadata.json',
      sha256: 'a'.repeat(64),
      bytes: 1,
      schemaVersion: 1,
      producingGeneration: input.providerRunMetadata.producingGeneration,
    };
    (built as unknown as { transaction: unknown }).transaction = {
      ...input.transaction,
      candidateLedgerSha256: 'a'.repeat(64),
    };
    const errs = semanticIdentityValidate(built);
    expect(errs).toContain('x_producedAt_invalid_rfc3339:provenance.producedAt');
  });
});
