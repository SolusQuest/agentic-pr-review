import { describe, expect, it } from 'vitest';
import { buildStateBundleV2, classifyStateBundleV2, type EntryDescriptor } from './index.js';
import { makeStateManifestV2Input } from './test-helpers.js';

const LEDGER = new TextEncoder().encode('ledger');
const METADATA = new TextEncoder().encode('metadata');

function buildValid() {
  return buildStateBundleV2(makeStateManifestV2Input(), LEDGER, METADATA);
}

describe('classifier v2 listing precedence (blocker #7)', () => {
  it('duplicate ledger.json beats non-regular ledger.json', () => {
    const { manifestBytes, ledgerBytes, providerRunMetadataBytes } = buildValid();
    const listing: EntryDescriptor[] = [
      { name: 'manifest.json', isRegularFile: true },
      { name: 'ledger.json', isRegularFile: false }, // non-regular
      { name: 'ledger.json', isRegularFile: true }, // and duplicate
      { name: 'provider-run-metadata.json', isRegularFile: true },
    ];
    const result = classifyStateBundleV2({
      entryListing: listing,
      manifestBytes,
      ledgerBytes,
      providerRunMetadataBytes,
    });
    expect(result.kind).toBe('invalid');
    if (result.kind === 'invalid') {
      // Duplicates take precedence over non-regular within remaining v2 layout.
      expect(result.diagnostic).toBe('bundle_listing_mismatch');
    }
  });

  it('duplicate ledger.json beats an extra entry', () => {
    const { manifestBytes, ledgerBytes, providerRunMetadataBytes } = buildValid();
    const listing: EntryDescriptor[] = [
      { name: 'manifest.json', isRegularFile: true },
      { name: 'ledger.json', isRegularFile: true },
      { name: 'ledger.json', isRegularFile: true }, // duplicate
      { name: 'provider-run-metadata.json', isRegularFile: true },
      { name: 'extra.txt', isRegularFile: true }, // extra
    ];
    const result = classifyStateBundleV2({
      entryListing: listing,
      manifestBytes,
      ledgerBytes,
      providerRunMetadataBytes,
    });
    expect(result.kind).toBe('invalid');
    if (result.kind === 'invalid') {
      expect(result.diagnostic).toBe('bundle_listing_mismatch');
    }
  });

  it('non-regular metadata beats an extra entry', () => {
    const { manifestBytes, ledgerBytes, providerRunMetadataBytes } = buildValid();
    const listing: EntryDescriptor[] = [
      { name: 'manifest.json', isRegularFile: true },
      { name: 'ledger.json', isRegularFile: true },
      { name: 'provider-run-metadata.json', isRegularFile: false }, // non-regular
      { name: 'extra.txt', isRegularFile: true }, // extra
    ];
    const result = classifyStateBundleV2({
      entryListing: listing,
      manifestBytes,
      ledgerBytes,
      providerRunMetadataBytes,
    });
    expect(result.kind).toBe('invalid');
    if (result.kind === 'invalid') {
      expect(result.diagnostic).toBe('bundle_path_unsafe');
    }
  });

  it('duplicate manifest.json is detected at step 1 before any other step', () => {
    const { manifestBytes, ledgerBytes, providerRunMetadataBytes } = buildValid();
    const listing: EntryDescriptor[] = [
      { name: 'manifest.json', isRegularFile: true },
      { name: 'manifest.json', isRegularFile: false }, // duplicate AND non-regular
      { name: 'ledger.json', isRegularFile: true },
      { name: 'provider-run-metadata.json', isRegularFile: true },
    ];
    const result = classifyStateBundleV2({
      entryListing: listing,
      manifestBytes,
      ledgerBytes,
      providerRunMetadataBytes,
    });
    expect(result.kind).toBe('invalid');
    if (result.kind === 'invalid') {
      expect(result.diagnostic).toBe('bundle_listing_mismatch');
    }
  });

  it('diagnostic messages for duplicate ledger.json do not leak the raw name', () => {
    // The message payload is a fixed structural label. `sanitizeName` still
    // permits the expected filename to appear literally (it is a public
    // contract name, not caller-controlled).
    const { manifestBytes, ledgerBytes, providerRunMetadataBytes } = buildValid();
    const listing: EntryDescriptor[] = [
      { name: 'manifest.json', isRegularFile: true },
      { name: 'ledger.json', isRegularFile: true },
      { name: 'ledger.json', isRegularFile: true },
      { name: 'provider-run-metadata.json', isRegularFile: true },
    ];
    const result = classifyStateBundleV2({
      entryListing: listing,
      manifestBytes,
      ledgerBytes,
      providerRunMetadataBytes,
    });
    expect(result.kind).toBe('invalid');
    if (result.kind === 'invalid') {
      // No attacker-controlled name can appear: the only names in the
      // message are drawn from the expected-name allow-list (`ledger.json`).
      expect(result.message).toBe('x_invalid_field:/');
    }
  });

  it('extra entries collapse to a fixed message that never echoes the extra name', () => {
    const { manifestBytes, ledgerBytes, providerRunMetadataBytes } = buildValid();
    const listing: EntryDescriptor[] = [
      { name: 'manifest.json', isRegularFile: true },
      { name: 'ledger.json', isRegularFile: true },
      { name: 'provider-run-metadata.json', isRegularFile: true },
      { name: 'attacker-picked-name.txt', isRegularFile: true },
    ];
    const result = classifyStateBundleV2({
      entryListing: listing,
      manifestBytes,
      ledgerBytes,
      providerRunMetadataBytes,
    });
    expect(result.kind).toBe('invalid');
    if (result.kind === 'invalid') {
      expect(result.diagnostic).toBe('bundle_extra_entry');
      expect(result.message).not.toContain('attacker-picked-name');
    }
  });
});
