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
 * paired with a safe path.
 *
 * Uses an own-property key check on the overrides object so tests may
 * pass an explicit `undefined` (missing bytes / missing listing) without
 * being silently overridden by the defaults.
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

interface Overrides {
  manifestBytes?: Uint8Array | undefined;
  ledgerBytes?: Uint8Array | undefined;
  providerRunMetadataBytes?: Uint8Array | undefined;
  entryListing?: readonly EntryDescriptor[];
}

function classify(
  manifestJson: string,
  overrides: Overrides = {},
): ReturnType<typeof classifyStateBundleV2> {
  const has = <K extends keyof Overrides>(k: K): boolean =>
    Object.prototype.hasOwnProperty.call(overrides, k);
  return classifyStateBundleV2({
    manifestBytes: has('manifestBytes')
      ? overrides.manifestBytes
      : new TextEncoder().encode(manifestJson),
    ledgerBytes: has('ledgerBytes') ? overrides.ledgerBytes : LEDGER,
    providerRunMetadataBytes: has('providerRunMetadataBytes')
      ? overrides.providerRunMetadataBytes
      : METADATA,
    entryListing: has('entryListing')
      ? (overrides.entryListing as readonly EntryDescriptor[])
      : LISTING,
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
  it('manifest_missing (no manifest bytes, no listing entry) is exactly x_invalid_field:/', () => {
    const res = classify('', {
      manifestBytes: undefined,
      entryListing: [
        { name: LEDGER_FILENAME, isRegularFile: true },
        { name: PROVIDER_RUN_METADATA_FILENAME, isRegularFile: true },
      ],
    });
    expectInvalidWith(res, 'manifest_missing', 'x_invalid_field:/');
  });

  it('manifest_invalid_json (undecodable bytes) is exactly x_invalid_json:/', () => {
    const bad = new Uint8Array([0xff, 0xfe, 0xfd, 0xfc]);
    const res = classify('', { manifestBytes: bad });
    expectInvalidWith(res, 'manifest_invalid_json', 'x_invalid_json:/');
  });

  it('manifest_invalid_json (JSON parse error) is exactly x_invalid_json:/', () => {
    const res = classify('{ not json');
    expectInvalidWith(res, 'manifest_invalid_json', 'x_invalid_json:/');
  });

  it('bundle_listing_mismatch (bytes present but no listing entry) is exactly x_invalid_field:/', () => {
    const manifestJson = validManifestJson();
    const res = classify(manifestJson, {
      entryListing: [
        { name: LEDGER_FILENAME, isRegularFile: true },
        { name: PROVIDER_RUN_METADATA_FILENAME, isRegularFile: true },
      ],
    });
    expectInvalidWith(res, 'bundle_listing_mismatch', 'x_invalid_field:/');
  });

  it('bundle_extra_entry is exactly x_invalid_field:/', () => {
    const manifestJson = validManifestJson();
    const res = classify(manifestJson, {
      entryListing: [...LISTING, { name: 'extraneous.bin', isRegularFile: true }],
    });
    expectInvalidWith(res, 'bundle_extra_entry', 'x_invalid_field:/');
  });

  it('ledger_missing (bytes absent AND no listing entry) is exactly x_invalid_field:/ledger', () => {
    const manifestJson = validManifestJson();
    const res = classify(manifestJson, {
      ledgerBytes: undefined,
      entryListing: [
        { name: MANIFEST_FILENAME, isRegularFile: true },
        { name: PROVIDER_RUN_METADATA_FILENAME, isRegularFile: true },
      ],
    });
    expectInvalidWith(res, 'ledger_missing', 'x_invalid_field:/ledger');
  });

  it('ledger_bytes_mismatch is exactly x_invalid_field:/ledger/bytes', () => {
    const manifestJson = validManifestJson();
    const res = classify(manifestJson, {
      ledgerBytes: new TextEncoder().encode('bogus'),
    });
    expectInvalidWith(res, 'ledger_bytes_mismatch', 'x_invalid_field:/ledger/bytes');
  });

  it('ledger_hash_mismatch is exactly x_invalid_field:/ledger/sha256', () => {
    const manifestJson = validManifestJson();
    const manifest = JSON.parse(manifestJson) as Record<string, unknown>;
    const bytesCount = (manifest.ledger as Record<string, unknown>).bytes as number;
    const wrongContent = new Uint8Array(bytesCount).fill(65);
    const res = classify(manifestJson, { ledgerBytes: wrongContent });
    expectInvalidWith(res, 'ledger_hash_mismatch', 'x_invalid_field:/ledger/sha256');
  });

  it('provider_run_metadata_missing (bytes absent AND no listing entry) is exactly x_invalid_field:/providerRunMetadata', () => {
    const manifestJson = validManifestJson();
    const res = classify(manifestJson, {
      providerRunMetadataBytes: undefined,
      entryListing: [
        { name: MANIFEST_FILENAME, isRegularFile: true },
        { name: LEDGER_FILENAME, isRegularFile: true },
      ],
    });
    expectInvalidWith(res, 'provider_run_metadata_missing', 'x_invalid_field:/providerRunMetadata');
  });

  it('provider_run_metadata_bytes_mismatch is exactly x_invalid_field:/providerRunMetadata/bytes', () => {
    const manifestJson = validManifestJson();
    const res = classify(manifestJson, {
      providerRunMetadataBytes: new TextEncoder().encode('bogus-metadata'),
    });
    expectInvalidWith(
      res,
      'provider_run_metadata_bytes_mismatch',
      'x_invalid_field:/providerRunMetadata/bytes',
    );
  });

  it('provider_run_metadata_hash_mismatch is exactly x_invalid_field:/providerRunMetadata/sha256', () => {
    const manifestJson = validManifestJson();
    const manifest = JSON.parse(manifestJson) as Record<string, unknown>;
    const bytesCount = (manifest.providerRunMetadata as Record<string, unknown>).bytes as number;
    const wrongContent = new Uint8Array(bytesCount).fill(66);
    const res = classify(manifestJson, { providerRunMetadataBytes: wrongContent });
    expectInvalidWith(
      res,
      'provider_run_metadata_hash_mismatch',
      'x_invalid_field:/providerRunMetadata/sha256',
    );
  });
});
