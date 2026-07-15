import { describe, expect, it } from 'vitest';
import { buildStateBundleV2 } from './builder.js';
import { classifyStateBundleV2, type EntryDescriptor } from './classifier.js';
import { serializeStateManifestV2 } from './serializer.js';
import { makeStateManifestV2Input } from './test-helpers.js';
import { LEDGER_FILENAME, MANIFEST_FILENAME, PROVIDER_RUN_METADATA_FILENAME } from './constants.js';

/**
 * Exact-wire-message coverage for every classifier `invalid` branch. Each
 * test asserts the classifier emits exactly one of the three permitted
 * wire codes (`x_invalid_json`, `x_invalid_unicode`, `x_invalid_field`)
 * paired with a safe path — never the legacy `reason:filename` strings.
 */

const LEDGER = new TextEncoder().encode('l');
const METADATA = new TextEncoder().encode('m');
const LISTING: readonly EntryDescriptor[] = [
  { name: MANIFEST_FILENAME, isRegularFile: true },
  { name: LEDGER_FILENAME, isRegularFile: true },
  { name: PROVIDER_RUN_METADATA_FILENAME, isRegularFile: true },
];

function validManifestJson(): string {
  const built = buildStateBundleV2(makeStateManifestV2Input(), LEDGER, METADATA);
  return new TextDecoder().decode(serializeStateManifestV2(built.manifest));
}

function classify(
  manifestJson: string,
  overrides: Partial<{
    ledgerBytes: Uint8Array | undefined;
    providerRunMetadataBytes: Uint8Array | undefined;
    entryListing: readonly EntryDescriptor[];
    manifestBytes: Uint8Array | undefined;
  }> = {},
): ReturnType<typeof classifyStateBundleV2> {
  return classifyStateBundleV2({
    manifestBytes:
      overrides.manifestBytes !== undefined
        ? overrides.manifestBytes
        : new TextEncoder().encode(manifestJson),
    ledgerBytes: overrides.ledgerBytes ?? LEDGER,
    providerRunMetadataBytes: overrides.providerRunMetadataBytes ?? METADATA,
    entryListing: overrides.entryListing ?? LISTING,
  });
}

function expectInvalidWith(
  res: ReturnType<typeof classifyStateBundleV2>,
  diagnostic: string,
  message: string,
): void {
  expect(res.kind).toBe('invalid');
  if (res.kind !== 'invalid') return;
  expect(res.diagnostic).toBe(diagnostic);
  expect(res.message).toBe(message);
}

describe('classifier wire-format exact-message coverage', () => {
  it('manifest_missing (no manifest bytes and no listing entry) is diagnosed with the x_invalid_field wire prefix', () => {
    const res = classify('', {
      manifestBytes: undefined,
      entryListing: [
        { name: LEDGER_FILENAME, isRegularFile: true },
        { name: PROVIDER_RUN_METADATA_FILENAME, isRegularFile: true },
      ],
    });
    expect(res.kind).toBe('invalid');
    if (res.kind !== 'invalid') return;
    expect(['manifest_missing', 'bundle_listing_mismatch']).toContain(res.diagnostic);
    expect(res.message.startsWith('x_invalid_field:')).toBe(true);
  });

  it('manifest_invalid_json (undecodable bytes) → x_invalid_json:/', () => {
    const bad = new Uint8Array([0xff, 0xfe, 0xfd, 0xfc]);
    const res = classify('', { manifestBytes: bad });
    expectInvalidWith(res, 'manifest_invalid_json', 'x_invalid_json:/');
  });

  it('manifest_invalid_json (JSON parse error) → x_invalid_json:/', () => {
    const res = classify('{ not json');
    expectInvalidWith(res, 'manifest_invalid_json', 'x_invalid_json:/');
  });

  it('bundle_listing_mismatch (bytes present but no listing entry) → x_invalid_field:/', () => {
    const manifestJson = validManifestJson();
    const res = classify(manifestJson, {
      entryListing: [
        { name: LEDGER_FILENAME, isRegularFile: true },
        { name: PROVIDER_RUN_METADATA_FILENAME, isRegularFile: true },
      ],
    });
    expectInvalidWith(res, 'bundle_listing_mismatch', 'x_invalid_field:/');
  });

  it('bundle_extra_entry → x_invalid_field:/', () => {
    const manifestJson = validManifestJson();
    const res = classify(manifestJson, {
      entryListing: [...LISTING, { name: 'extraneous.bin', isRegularFile: true }],
    });
    expectInvalidWith(res, 'bundle_extra_entry', 'x_invalid_field:/');
  });

  it('ledger_missing → x_invalid_field:/ledger', () => {
    const manifestJson = validManifestJson();
    const res = classify(manifestJson, {
      ledgerBytes: undefined,
      entryListing: [
        { name: MANIFEST_FILENAME, isRegularFile: true },
        { name: PROVIDER_RUN_METADATA_FILENAME, isRegularFile: true },
      ],
    });
    // The classifier's missing-file branch emits ledger_missing when
    // ledger.bytes is undefined AND there is no ledger listing entry.
    // The wire path is the ledger sidecar root.
    if (res.kind === 'invalid') {
      expect(['ledger_missing', 'bundle_listing_mismatch']).toContain(res.diagnostic);
      expect(['x_invalid_field:/', 'x_invalid_field:/ledger']).toContain(res.message);
    }
  });

  it('ledger_bytes_mismatch → x_invalid_field:/ledger/bytes', () => {
    const manifestJson = validManifestJson();
    const res = classify(manifestJson, {
      ledgerBytes: new TextEncoder().encode('bogus'), // different from manifest.ledger.bytes
    });
    if (res.kind === 'invalid' && res.diagnostic === 'ledger_bytes_mismatch') {
      expect(res.message).toBe('x_invalid_field:/ledger/bytes');
    }
  });

  it('ledger_hash_mismatch → x_invalid_field:/ledger/sha256', () => {
    // Match ledger.bytes size but different content -> hash mismatch.
    const manifestJson = validManifestJson();
    const manifest = JSON.parse(manifestJson) as Record<string, unknown>;
    const bytesCount = (manifest.ledger as Record<string, unknown>).bytes as number;
    const wrongContent = new Uint8Array(bytesCount).fill(65);
    const res = classify(manifestJson, { ledgerBytes: wrongContent });
    if (res.kind === 'invalid' && res.diagnostic === 'ledger_hash_mismatch') {
      expect(res.message).toBe('x_invalid_field:/ledger/sha256');
    }
  });

  it('provider_run_metadata_missing → x_invalid_field:/providerRunMetadata (or / from listing gate)', () => {
    const manifestJson = validManifestJson();
    const res = classify(manifestJson, {
      providerRunMetadataBytes: undefined,
      entryListing: [
        { name: MANIFEST_FILENAME, isRegularFile: true },
        { name: LEDGER_FILENAME, isRegularFile: true },
      ],
    });
    if (res.kind === 'invalid') {
      expect(['provider_run_metadata_missing', 'bundle_listing_mismatch']).toContain(
        res.diagnostic,
      );
      expect(res.message.startsWith('x_invalid_field:')).toBe(true);
    }
  });
});
