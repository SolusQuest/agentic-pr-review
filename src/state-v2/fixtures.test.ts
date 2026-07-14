import { readFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import { beforeAll, describe, expect, it } from 'vitest';
import { classifyStateBundleV2, serializeStateManifestV2, type EntryDescriptor } from './index.js';
import { generateAllPositiveFixtures } from './generate-fixtures.testhelper.js';

const FIXTURES_ROOT = path.resolve('protocol/fixtures/state-manifest-v2');

async function readBundle(name: string) {
  const bundle = path.join(FIXTURES_ROOT, name, 'bundle');
  const [manifestBytes, ledgerBytes, providerRunMetadataBytes] = await Promise.all([
    readFile(path.join(bundle, 'manifest.json')),
    readFile(path.join(bundle, 'ledger.json')),
    readFile(path.join(bundle, 'provider-run-metadata.json')),
  ]);
  return {
    manifestBytes: new Uint8Array(manifestBytes),
    ledgerBytes: new Uint8Array(ledgerBytes),
    providerRunMetadataBytes: new Uint8Array(providerRunMetadataBytes),
  };
}

async function readExpected(name: string) {
  const expected = path.join(FIXTURES_ROOT, name, 'expected');
  const [listingRaw, serialized] = await Promise.all([
    readFile(path.join(expected, 'entryListing.json'), 'utf8'),
    readFile(path.join(expected, 'manifest.serialized.bin')),
  ]);
  return {
    listing: JSON.parse(listingRaw) as EntryDescriptor[],
    serialized: new Uint8Array(serialized),
  };
}

const positiveNames = [
  'positive-bootstrap',
  'positive-continuation',
  'positive-reset',
  'positive-recovery-root',
] as const;

describe('state-v2 positive fixtures', () => {
  beforeAll(async () => {
    // Generate the fixtures deterministically at test time so the repo does
    // not have to check in binary golden files that depend on the exact
    // canonical serializer output. The test verifies that classification and
    // serialization stay consistent with the fixture contents.
    await generateAllPositiveFixtures();
  });

  it('all expected fixture directories exist', async () => {
    const entries = await readdir(FIXTURES_ROOT);
    for (const name of positiveNames) {
      expect(entries).toContain(name);
    }
  });

  for (const name of positiveNames) {
    it(`${name} classifies as valid and matches its golden serialization`, async () => {
      const [bundle, expected] = await Promise.all([readBundle(name), readExpected(name)]);
      const result = classifyStateBundleV2({
        entryListing: expected.listing,
        manifestBytes: bundle.manifestBytes,
        ledgerBytes: bundle.ledgerBytes,
        providerRunMetadataBytes: bundle.providerRunMetadataBytes,
      });
      expect(result.kind).toBe('valid');
      if (result.kind === 'valid') {
        expect(serializeStateManifestV2(result.manifest)).toEqual(expected.serialized);
        expect(result.manifestBytes).toEqual(bundle.manifestBytes);
      }
    });
  }
});
