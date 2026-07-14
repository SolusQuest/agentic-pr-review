import { readFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  buildStateBundleV2,
  classifyStateBundleV2,
  serializeStateManifestV2,
  type EntryDescriptor,
  type EpochId,
  type StateManifestV2Transition,
} from './index.js';
import { makeStateManifestV2Input, sha256Hex } from './test-helpers.js';

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

const enc = new TextEncoder();
const PRED_MANIFEST = sha256Hex('pred-manifest');
const PRED_LEDGER = sha256Hex('pred-ledger');

// Regeneration inputs are declared here (not imported from the maintainer
// script) so the test does not depend on any regeneration entry point. If
// buildStateBundleV2 output ever drifts from the committed fixture bytes,
// the "regenerates to committed bytes" assertion below fails and the
// maintainer must re-run `scripts/regenerate-state-v2-fixtures.mjs` and
// commit the update.
interface PositiveSpec {
  readonly name: string;
  readonly makeInput: () => Parameters<typeof makeStateManifestV2Input>[0];
  readonly ledger: Uint8Array;
  readonly metadata: Uint8Array;
}

const POSITIVES: readonly PositiveSpec[] = [
  {
    name: 'positive-bootstrap',
    makeInput: () => ({}),
    ledger: enc.encode('positive-bootstrap-ledger'),
    metadata: enc.encode('positive-bootstrap-metadata'),
  },
  {
    name: 'positive-continuation',
    makeInput: () => ({
      transition: {
        kind: 'continuation',
        predecessorManifestSha256: PRED_MANIFEST,
        predecessorLedgerSha256: PRED_LEDGER,
        predecessorStateGeneration: 3,
        predecessorLedgerEpoch: 'AAAAAAAAAAAAAAAAAAAAAA' as EpochId,
      } satisfies StateManifestV2Transition,
      generation: { stateGeneration: 4, ledgerEpoch: 'AAAAAAAAAAAAAAAAAAAAAA' as EpochId },
      transaction: { interactionOrdinal: 4 },
    }),
    ledger: enc.encode('positive-continuation-ledger'),
    metadata: enc.encode('positive-continuation-metadata'),
  },
  {
    name: 'positive-reset',
    makeInput: () => ({
      transition: {
        kind: 'reset',
        predecessorManifestSha256: PRED_MANIFEST,
        predecessorLedgerSha256: PRED_LEDGER,
        predecessorStateGeneration: 3,
        predecessorLedgerEpoch: 'AAAAAAAAAAAAAAAAAAAAAA' as EpochId,
        reason: 'base_change',
      } satisfies StateManifestV2Transition,
      generation: { stateGeneration: 4, ledgerEpoch: 'BBBBBBBBBBBBBBBBBBBBBB' as EpochId },
      transaction: { interactionOrdinal: 0 },
    }),
    ledger: enc.encode('positive-reset-ledger'),
    metadata: enc.encode('positive-reset-metadata'),
  },
  {
    name: 'positive-recovery-root',
    makeInput: () => ({
      sessionEpoch: 'S00000000000000000000B' as EpochId,
      transition: {
        kind: 'recovery_root',
        predecessorManifestSha256: 'bootstrap',
        predecessorLedgerSha256: 'bootstrap',
        reason: 'corrupt_accepted_artifact',
      } satisfies StateManifestV2Transition,
      generation: { stateGeneration: 0, ledgerEpoch: 'CCCCCCCCCCCCCCCCCCCCCC' as EpochId },
      transaction: { interactionOrdinal: 0 },
    }),
    ledger: enc.encode('positive-recovery-root-ledger'),
    metadata: enc.encode('positive-recovery-root-metadata'),
  },
];

describe('state-v2 positive fixtures (read-only golden verification)', () => {
  it('all expected fixture directories exist on disk', async () => {
    const entries = await readdir(FIXTURES_ROOT);
    for (const spec of POSITIVES) {
      expect(entries).toContain(spec.name);
    }
  });

  for (const spec of POSITIVES) {
    it(`${spec.name}: committed bundle classifies as valid`, async () => {
      const [bundle, expected] = await Promise.all([
        readBundle(spec.name),
        readExpected(spec.name),
      ]);
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

    it(`${spec.name}: regenerates to committed bytes (drift guard)`, async () => {
      const bundle = await readBundle(spec.name);
      const input = makeStateManifestV2Input(spec.makeInput());
      const built = buildStateBundleV2(input, spec.ledger, spec.metadata);
      expect(built.manifestBytes).toEqual(bundle.manifestBytes);
      expect(built.ledgerBytes).toEqual(bundle.ledgerBytes);
      expect(built.providerRunMetadataBytes).toEqual(bundle.providerRunMetadataBytes);
    });
  }
});
