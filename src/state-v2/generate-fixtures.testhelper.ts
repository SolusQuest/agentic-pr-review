import { mkdir, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import {
  buildStateBundleV2,
  type EntryDescriptor,
  type StateManifestV2Transition,
} from './index.js';
import { makeStateManifestV2Input, sha256Hex } from './test-helpers.js';

const FIXTURES_ROOT = path.resolve('protocol/fixtures/state-manifest-v2');

interface PositiveSpec {
  name: string;
  makeInput: () => Parameters<typeof makeStateManifestV2Input>[0];
  ledger: Uint8Array;
  metadata: Uint8Array;
}

const enc = new TextEncoder();

const PRED_MANIFEST = sha256Hex('pred-manifest');
const PRED_LEDGER = sha256Hex('pred-ledger');

const POSITIVES: PositiveSpec[] = [
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
        predecessorLedgerEpoch: 'AAAAAAAAAAAAAAAAAAAAAA',
      } satisfies StateManifestV2Transition,
      generation: { stateGeneration: 4, ledgerEpoch: 'AAAAAAAAAAAAAAAAAAAAAA' },
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
        predecessorLedgerEpoch: 'AAAAAAAAAAAAAAAAAAAAAA',
        reason: 'base_change',
      } satisfies StateManifestV2Transition,
      generation: { stateGeneration: 4, ledgerEpoch: 'BBBBBBBBBBBBBBBBBBBBBB' },
      transaction: { interactionOrdinal: 0 },
    }),
    ledger: enc.encode('positive-reset-ledger'),
    metadata: enc.encode('positive-reset-metadata'),
  },
  {
    name: 'positive-recovery-root',
    makeInput: () => ({
      sessionEpoch: 'S00000000000000000000B',
      transition: {
        kind: 'recovery_root',
        predecessorManifestSha256: 'bootstrap',
        predecessorLedgerSha256: 'bootstrap',
        reason: 'corrupt_accepted_artifact',
      } satisfies StateManifestV2Transition,
      generation: { stateGeneration: 0, ledgerEpoch: 'CCCCCCCCCCCCCCCCCCCCCC' },
      transaction: { interactionOrdinal: 0 },
    }),
    ledger: enc.encode('positive-recovery-root-ledger'),
    metadata: enc.encode('positive-recovery-root-metadata'),
  },
];

async function writeBundle(spec: PositiveSpec): Promise<void> {
  const dir = path.join(FIXTURES_ROOT, spec.name);
  const bundle = path.join(dir, 'bundle');
  const expected = path.join(dir, 'expected');
  await rm(dir, { recursive: true, force: true });
  await mkdir(bundle, { recursive: true });
  await mkdir(expected, { recursive: true });

  const input = makeStateManifestV2Input(spec.makeInput());
  const result = buildStateBundleV2(input, spec.ledger, spec.metadata);

  await writeFile(path.join(bundle, 'manifest.json'), result.manifestBytes);
  await writeFile(path.join(bundle, 'ledger.json'), result.ledgerBytes);
  await writeFile(path.join(bundle, 'provider-run-metadata.json'), result.providerRunMetadataBytes);

  const listing: EntryDescriptor[] = [
    { name: 'manifest.json', isRegularFile: true },
    { name: 'ledger.json', isRegularFile: true },
    { name: 'provider-run-metadata.json', isRegularFile: true },
  ];
  await writeFile(path.join(expected, 'entryListing.json'), JSON.stringify(listing, null, 2));
  await writeFile(path.join(expected, 'manifest.serialized.bin'), result.manifestBytes);
  await writeFile(
    path.join(expected, 'manifest.pretty.json'),
    JSON.stringify(result.manifest as unknown as Record<string, unknown>, null, 2),
  );
}

export async function generateAllPositiveFixtures(): Promise<void> {
  for (const spec of POSITIVES) {
    await writeBundle(spec);
  }
}

if (
  typeof process !== 'undefined' &&
  process.argv &&
  process.argv[1] &&
  process.argv[1].endsWith('generate-state-v2-fixtures.ts')
) {
  await generateAllPositiveFixtures();
}
