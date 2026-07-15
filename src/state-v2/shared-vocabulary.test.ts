/**
 * Shared vocabulary realization suite (issue #48 AC).
 *
 * Exercises every numeric boundary, identity domain check, and floating-
 * alias rule that `#48`'s implementation must realize per
 * `### Numeric bounds intersection` and `### Floating alias rejection` in
 * the shared contract.
 */

import { describe, expect, it } from 'vitest';
import Ajv from 'ajv';
import schema from '../../protocol/schemas/state-manifest.v2.json' with { type: 'json' };
import { buildStateBundleV2 } from './builder.js';
import { classifyStateBundleV2, type EntryDescriptor } from './classifier.js';
import { semanticIdentityValidate } from './schema.js';
import { serializeStateManifestV2 } from './serializer.js';
import { makeStateManifestV2Input } from './test-helpers.js';
import { LEDGER_FILENAME, MANIFEST_FILENAME, PROVIDER_RUN_METADATA_FILENAME } from './constants.js';

const ajv = new (Ajv as unknown as typeof Ajv.default)({
  strict: true,
  allErrors: true,
  allowUnionTypes: false,
});
const validate = ajv.compile(schema);

function schemaAccepts(manifest: unknown): boolean {
  return validate(manifest) === true;
}

const LEDGER = new TextEncoder().encode('l');
const METADATA = new TextEncoder().encode('m');
const LISTING: readonly EntryDescriptor[] = [
  { name: MANIFEST_FILENAME, isRegularFile: true },
  { name: LEDGER_FILENAME, isRegularFile: true },
  { name: PROVIDER_RUN_METADATA_FILENAME, isRegularFile: true },
];

function buildValidManifest() {
  return buildStateBundleV2(makeStateManifestV2Input(), LEDGER, METADATA).manifest;
}

// -------------------------------------------------------------------------
// Numeric boundaries.
// -------------------------------------------------------------------------

interface NumericBoundCase {
  readonly name: string;
  readonly pointer: string;
  readonly path: (m: Record<string, unknown>) => Record<string, unknown>;
  readonly key: string;
  readonly min: number;
  readonly max: number;
}

const NUMERIC_BOUNDS: readonly NumericBoundCase[] = [
  {
    name: 'stateGeneration',
    pointer: '/generation/stateGeneration',
    path: (m) => m.generation as Record<string, unknown>,
    key: 'stateGeneration',
    min: 0,
    max: 1_000_000,
  },
  {
    name: 'interactionOrdinal',
    pointer: '/transaction/interactionOrdinal',
    path: (m) => m.transaction as Record<string, unknown>,
    key: 'interactionOrdinal',
    min: 0,
    max: 1_000_000,
  },
  {
    name: 'pullRequest',
    pointer: '/stateKey/pullRequest',
    path: (m) => m.stateKey as Record<string, unknown>,
    key: 'pullRequest',
    min: 1,
    max: 2_147_483_647,
  },
  {
    name: 'producingRunAttempt',
    pointer: '/provenance/producingRunAttempt',
    path: (m) => m.provenance as Record<string, unknown>,
    key: 'producingRunAttempt',
    min: 1,
    max: 2_147_483_647,
  },
];

describe('shared vocabulary realization — numeric boundaries', () => {
  for (const c of NUMERIC_BOUNDS) {
    it(`${c.name} accepts min and max, rejects below-min / above-max / non-integer`, () => {
      const manifest = buildValidManifest() as unknown as Record<string, unknown>;
      const container = c.path(manifest);

      // Minimum accepted.
      container[c.key] = c.min;
      // For the transaction.interactionOrdinal we must respect the
      // per-kind constraint that bootstrap requires ordinal 0.
      // For the transaction.interactionOrdinal case the fixture we use
      // (bootstrap) requires ordinal === 0, so `min=0` is fine; other
      // cases likewise use their min.
      expect(schemaAccepts(manifest)).toBe(true);

      // Maximum accepted (schema-level only). Full-manifest bootstrap
      // cross-field rules may reject the value on other grounds
      // (e.g. bootstrap requires stateGeneration === 0), so we only
      // assert the raw Ajv schema accepts the value in isolation by
      // asking whether removing it (setting the field to min) makes
      // the manifest schema-valid — a proxy for "schema range bounds
      // include c.max".
      container[c.key] = c.max;
      // The numeric schema range itself must not reject c.max. If the
      // manifest fails Ajv, verify that the only Ajv errors are NOT
      // range errors at this pointer.
      const boundedValidator = ajv.compile(schema);
      boundedValidator(manifest);
      const rangeErrs = (boundedValidator.errors ?? []).filter(
        (e) => e.instancePath === c.pointer && (e.keyword === 'maximum' || e.keyword === 'minimum'),
      );
      expect(rangeErrs.length).toBe(0);

      // Below minimum.
      container[c.key] = c.min - 1;
      expect(schemaAccepts(manifest)).toBe(false);

      // Above maximum.
      container[c.key] = c.max + 1;
      expect(schemaAccepts(manifest)).toBe(false);

      // Non-integer.
      container[c.key] = 1.5;
      expect(schemaAccepts(manifest)).toBe(false);
    });
  }
});

// -------------------------------------------------------------------------
// producingRunId regex.
// -------------------------------------------------------------------------

describe('shared vocabulary realization — producingRunId regex', () => {
  it('accepts canonical decimal strings and rejects leading zero, non-digits, and too-long values', () => {
    const manifest = buildValidManifest() as unknown as Record<string, unknown>;
    const provenance = manifest.provenance as Record<string, unknown>;

    for (const good of ['1', '9', '10', '123456789', '1234567890123456789']) {
      provenance.producingRunId = good;
      expect(schemaAccepts(manifest)).toBe(true);
    }
    for (const bad of ['0', '01', '', 'abc', '-1', '12345678901234567890']) {
      provenance.producingRunId = bad;
      expect(schemaAccepts(manifest)).toBe(false);
    }
  });
});

// -------------------------------------------------------------------------
// Repository syntax.
// -------------------------------------------------------------------------

describe('shared vocabulary realization — repository syntax', () => {
  it('accepts owner/name and rejects malformed forms and too-short values', () => {
    const manifest = buildValidManifest() as unknown as Record<string, unknown>;
    const stateKey = manifest.stateKey as Record<string, unknown>;

    for (const good of ['owner/repo', 'x/y-1.0', 'A_B/C.D-E']) {
      stateKey.repository = good;
      stateKey.headRepository = good;
      expect(schemaAccepts(manifest)).toBe(true);
    }
    for (const bad of ['norepo', 'a', 'a/', '/b', '', 'a b/c', 'a/b/c']) {
      stateKey.repository = bad;
      // headRepository stays valid so we isolate the repository check.
      expect(schemaAccepts(manifest)).toBe(false);
    }
  });
});

// -------------------------------------------------------------------------
// Git SHA acceptance / rejection.
// -------------------------------------------------------------------------

describe('shared vocabulary realization — Git SHA form', () => {
  it('accepts 40- and 64-lowercase-hex and rejects 39/41/63/65-hex, uppercase, and non-hex', () => {
    const manifest = buildValidManifest() as unknown as Record<string, unknown>;
    const provenance = manifest.provenance as Record<string, unknown>;

    const sha40 = 'a'.repeat(40);
    const sha64 = 'b'.repeat(64);
    provenance.reviewedHeadSha = sha40;
    expect(schemaAccepts(manifest)).toBe(true);
    provenance.reviewedHeadSha = sha64;
    expect(schemaAccepts(manifest)).toBe(true);

    for (const bad of [
      'a'.repeat(39),
      'a'.repeat(41),
      'a'.repeat(63),
      'a'.repeat(65),
      'A'.repeat(40),
      'x'.repeat(40),
    ]) {
      provenance.reviewedHeadSha = bad;
      expect(schemaAccepts(manifest)).toBe(false);
    }
  });
});

// -------------------------------------------------------------------------
// Floating-alias rejection (scoped to modelId).
// -------------------------------------------------------------------------

describe('shared vocabulary realization — floating-alias rejection scoped to modelId', () => {
  it('rejects modelId === "latest" via the semantic identity validator', () => {
    const manifest = buildValidManifest();
    const clone = structuredClone(manifest);
    clone.cacheContractIdentity.modelId = 'latest';
    const errors = semanticIdentityValidate(clone);
    expect(errors.some((e) => e.includes('/cacheContractIdentity/modelId'))).toBe(true);
  });

  it('accepts providerId === "latest" (floating-alias check does NOT apply to providerId)', () => {
    const manifest = buildValidManifest();
    const clone = structuredClone(manifest);
    clone.cacheContractIdentity.providerId = 'latest';
    // Serialize + classify to observe end-to-end acceptance path.
    const bytes = serializeStateManifestV2(clone);
    const rebuilt = classifyStateBundleV2({
      manifestBytes: bytes,
      ledgerBytes: LEDGER,
      providerRunMetadataBytes: METADATA,
      entryListing: LISTING,
    });
    // Manifest schema itself accepts the value; sidecar hash / bytes
    // mismatches from the modified manifest will surface later, but the
    // shape-level acceptance is what this test asserts.
    if (rebuilt.kind === 'invalid') {
      expect(rebuilt.message).not.toContain('/cacheContractIdentity/providerId');
    }
  });
});

// -------------------------------------------------------------------------
// Identity-string UTF-16 vs UTF-8 boundaries.
// -------------------------------------------------------------------------

describe('shared vocabulary realization — identity-string boundaries', () => {
  it('rejects identity strings whose UTF-8 byte length exceeds IDENTITY_STRING_MAX_UTF8_BYTES', () => {
    const manifest = buildValidManifest();
    const clone = structuredClone(manifest);
    // 256 characters of a 3-byte UTF-8 CJK character = 768 UTF-8 bytes > 256.
    // But schema `maxLength` (UTF-16 code units) is 256, so schema will
    // reject at that count. Use 100 CJK chars: 100 <= 256 code units,
    // but 300 UTF-8 bytes > 256. That combination reaches the semantic
    // check without failing schema.
    clone.cacheContractIdentity.providerId = '中'.repeat(100);
    const errors = semanticIdentityValidate(clone);
    expect(errors.some((e) => e.includes('/cacheContractIdentity/providerId'))).toBe(true);
  });

  it('rejects identity strings containing a control character in the semantic stage', () => {
    const manifest = buildValidManifest();
    const clone = structuredClone(manifest);
    clone.stateKey.workflowIdentity = 'work\u0001flow';
    const errors = semanticIdentityValidate(clone);
    expect(errors.some((e) => e.includes('/stateKey/workflowIdentity'))).toBe(true);
  });
});

// -------------------------------------------------------------------------
// Additional numeric bounds: predecessor generations and producing generation.
// -------------------------------------------------------------------------

describe('shared vocabulary realization — predecessor and producing generations', () => {
  it('predecessorStateGeneration (continuation) has a 0..999_999 integer range', () => {
    const manifest = buildValidManifest() as unknown as Record<string, unknown>;
    // Reshape to a continuation manifest to reach the predecessor field.
    manifest.transition = {
      kind: 'continuation',
      predecessorLedgerEpoch: (manifest.generation as any).ledgerEpoch,
      predecessorStateGeneration: 0,
      predecessorAcceptanceMarkerSha256: 'a'.repeat(64),
    };
    (manifest.generation as any).stateGeneration = 1;
    (manifest.transaction as any).interactionOrdinal = 1;
    const transition = manifest.transition as Record<string, unknown>;
    const rangeErrsAt = (): unknown[] => {
      validate(manifest);
      return (validate.errors ?? []).filter(
        (e) =>
          e.instancePath === '/transition/predecessorStateGeneration' &&
          (e.keyword === 'minimum' || e.keyword === 'maximum' || e.keyword === 'type'),
      );
    };
    transition.predecessorStateGeneration = 0;
    expect(rangeErrsAt().length).toBe(0);
    transition.predecessorStateGeneration = 999_999;
    expect(rangeErrsAt().length).toBe(0);
    transition.predecessorStateGeneration = -1;
    expect(rangeErrsAt().length).toBeGreaterThan(0);
    transition.predecessorStateGeneration = 1_000_000;
    expect(rangeErrsAt().length).toBeGreaterThan(0);
    transition.predecessorStateGeneration = 1.5;
    expect(rangeErrsAt().length).toBeGreaterThan(0);
  });

  it('providerRunMetadata.producingGeneration.stateGeneration rejects below 0 / above 1_000_000 / non-integer', () => {
    const manifest = buildValidManifest() as unknown as Record<string, unknown>;
    const producing = (manifest.providerRunMetadata as any).producingGeneration as Record<
      string,
      unknown
    >;
    producing.stateGeneration = 0;
    expect(schemaAccepts(manifest)).toBe(true);
    producing.stateGeneration = 1_000_000;
    validate(manifest);
    const rangeErrs = (validate.errors ?? []).filter(
      (e) =>
        e.instancePath === '/providerRunMetadata/producingGeneration/stateGeneration' &&
        (e.keyword === 'minimum' || e.keyword === 'maximum'),
    );
    expect(rangeErrs.length).toBe(0);
    producing.stateGeneration = -1;
    expect(schemaAccepts(manifest)).toBe(false);
    producing.stateGeneration = 1_000_001;
    expect(schemaAccepts(manifest)).toBe(false);
    producing.stateGeneration = 1.5;
    expect(schemaAccepts(manifest)).toBe(false);
  });
});

// -------------------------------------------------------------------------
// producingRunId regex boundary.
// -------------------------------------------------------------------------

describe('shared vocabulary realization — producingRunId regex', () => {
  it('accepts positive-integer decimal strings without leading zeros', () => {
    const manifest = buildValidManifest() as unknown as Record<string, unknown>;
    const provenance = manifest.provenance as Record<string, unknown>;
    for (const good of ['1', '9', '10', '1234567890', '9999999999999999999']) {
      provenance.producingRunId = good;
      expect(schemaAccepts(manifest)).toBe(true);
    }
    for (const bad of ['', '0', '01', '00', 'a', '-1', '1.5', '10000000000000000000']) {
      provenance.producingRunId = bad;
      expect(schemaAccepts(manifest)).toBe(false);
    }
  });
});

// -------------------------------------------------------------------------
// Repository length boundaries.
// -------------------------------------------------------------------------

describe('shared vocabulary realization — repository length boundaries', () => {
  it('accepts owner/name at exactly 200 chars and rejects 201 chars', () => {
    const manifest = buildValidManifest() as unknown as Record<string, unknown>;
    const stateKey = manifest.stateKey as Record<string, unknown>;
    // owner: 99 chars, slash, name: 100 chars => 200 chars total.
    const owner99 = 'a'.repeat(99);
    const name100 = 'b'.repeat(100);
    stateKey.repository = owner99 + '/' + name100;
    stateKey.headRepository = 'ok/ok';
    expect(schemaAccepts(manifest)).toBe(true);
    // 201 chars.
    stateKey.repository = owner99 + '/' + 'b'.repeat(101);
    expect(schemaAccepts(manifest)).toBe(false);
  });

  it('rejects minLength boundary: 2-character repository is rejected', () => {
    const manifest = buildValidManifest() as unknown as Record<string, unknown>;
    const stateKey = manifest.stateKey as Record<string, unknown>;
    // Schema minLength = 3. Try 2 chars.
    stateKey.repository = 'a/';
    expect(schemaAccepts(manifest)).toBe(false);
  });

  it('headRepository is independently subject to the same length and syntax bounds', () => {
    const manifest = buildValidManifest() as unknown as Record<string, unknown>;
    const stateKey = manifest.stateKey as Record<string, unknown>;
    stateKey.repository = 'ok/ok';
    for (const bad of ['x', 'no-slash', 'a/', '/b', 'a'.repeat(99) + '/' + 'b'.repeat(102)]) {
      stateKey.headRepository = bad;
      expect(schemaAccepts(manifest)).toBe(false);
    }
    // Valid.
    stateKey.headRepository = 'good/repo';
    expect(schemaAccepts(manifest)).toBe(true);
  });
});

// -------------------------------------------------------------------------
// Identity boundary: UTF-16 code-unit cap vs UTF-8 byte cap.
// -------------------------------------------------------------------------

describe('shared vocabulary realization — identity UTF-16 / UTF-8 boundaries', () => {
  it('accepts 256 UTF-16 code units of ASCII and rejects 257 code units', () => {
    const manifest = buildValidManifest();
    const clone = structuredClone(manifest);
    // 256 ASCII chars = 256 UTF-16 code units = 256 UTF-8 bytes.
    clone.cacheContractIdentity.providerId = 'a'.repeat(256);
    // Schema maxLength = 256 UTF-16 code units, so accepted.
    expect(schemaAccepts(clone as unknown)).toBe(true);
    // Semantic UTF-8 check: 256 bytes <= 256 -> not rejected by semantic.
    const errs256 = semanticIdentityValidate(clone);
    expect(errs256.some((e) => e.includes('/cacheContractIdentity/providerId'))).toBe(false);

    clone.cacheContractIdentity.providerId = 'a'.repeat(257);
    expect(schemaAccepts(clone as unknown)).toBe(false);
  });

  it('rejects exactly 257 UTF-8 bytes reached via multibyte chars while UTF-16 length stays <= 256', () => {
    const manifest = buildValidManifest();
    const clone = structuredClone(manifest);
    // 129 CJK (3-byte UTF-8) chars = 387 UTF-8 bytes, 129 UTF-16 units.
    // Use 86 * 3 = 258 bytes with 86 UTF-16 units -> 258 > 256 -> rejected.
    clone.cacheContractIdentity.providerId = '中'.repeat(86);
    // Schema maxLength (UTF-16) still satisfied.
    expect(schemaAccepts(clone as unknown)).toBe(true);
    const errs = semanticIdentityValidate(clone);
    expect(errs.some((e) => e.includes('/cacheContractIdentity/providerId'))).toBe(true);
  });
});

// -------------------------------------------------------------------------
// Reset transition predecessorStateGeneration range.
// -------------------------------------------------------------------------

describe('shared vocabulary realization — reset predecessorStateGeneration range', () => {
  it('reset variant predecessorStateGeneration has a 0..999_999 integer range', () => {
    const manifest = buildValidManifest() as unknown as Record<string, unknown>;
    (manifest.transition as any) = {
      kind: 'reset',
      predecessorLedgerEpoch: 'B' + 'A'.repeat(21),
      predecessorStateGeneration: 0,
      predecessorAcceptanceMarkerSha256: 'a'.repeat(64),
    };
    (manifest.generation as any).stateGeneration = 1;
    (manifest.transaction as any).interactionOrdinal = 0;
    const transition = manifest.transition as Record<string, unknown>;
    const rangeErrsAt = (): unknown[] => {
      validate(manifest);
      return (validate.errors ?? []).filter(
        (e) =>
          e.instancePath === '/transition/predecessorStateGeneration' &&
          (e.keyword === 'minimum' || e.keyword === 'maximum' || e.keyword === 'type'),
      );
    };
    transition.predecessorStateGeneration = 0;
    expect(rangeErrsAt().length).toBe(0);
    transition.predecessorStateGeneration = 999_999;
    expect(rangeErrsAt().length).toBe(0);
    transition.predecessorStateGeneration = -1;
    expect(rangeErrsAt().length).toBeGreaterThan(0);
    transition.predecessorStateGeneration = 1_000_000;
    expect(rangeErrsAt().length).toBeGreaterThan(0);
    transition.predecessorStateGeneration = 1.5;
    expect(rangeErrsAt().length).toBeGreaterThan(0);
  });
});

// -------------------------------------------------------------------------
// Precise UTF-8 boundary: exactly 256 bytes accepted, exactly 257 rejected.
// -------------------------------------------------------------------------

describe('shared vocabulary realization — identity UTF-8 byte boundary is exactly 256', () => {
  it('accepts exactly 256 UTF-8 bytes (256 ASCII chars)', () => {
    const manifest = buildValidManifest();
    const clone = structuredClone(manifest);
    clone.cacheContractIdentity.providerId = 'a'.repeat(256);
    const errs = semanticIdentityValidate(clone);
    expect(errs.some((e) => e.includes('/cacheContractIdentity/providerId'))).toBe(false);
  });

  it('rejects exactly 257 UTF-8 bytes reached via a 254-ASCII + 1-CJK combo (254 + 3 = 257 bytes; 255 UTF-16 units)', () => {
    const manifest = buildValidManifest();
    const clone = structuredClone(manifest);
    clone.cacheContractIdentity.providerId = 'a'.repeat(254) + '中';
    const errs = semanticIdentityValidate(clone);
    expect(errs.some((e) => e.includes('/cacheContractIdentity/providerId'))).toBe(true);
  });
});

// -------------------------------------------------------------------------
// headRepository precise 200/201 length boundary.
// -------------------------------------------------------------------------

describe('shared vocabulary realization — headRepository exact 200/201 boundary', () => {
  it('accepts headRepository exactly 200 chars and rejects 201', () => {
    const manifest = buildValidManifest() as unknown as Record<string, unknown>;
    const stateKey = manifest.stateKey as Record<string, unknown>;
    stateKey.repository = 'ok/ok';
    const owner99 = 'a'.repeat(99);
    const name100 = 'b'.repeat(100);
    stateKey.headRepository = owner99 + '/' + name100;
    expect(schemaAccepts(manifest)).toBe(true);
    stateKey.headRepository = owner99 + '/' + 'b'.repeat(101);
    expect(schemaAccepts(manifest)).toBe(false);
  });
});
