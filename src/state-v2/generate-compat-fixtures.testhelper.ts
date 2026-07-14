import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import {
  buildStateBundleV2,
  type CompatibilityOutcome,
  type ExpectedStateManifestV2Context,
  type HeadRelationship,
  type Sha256Hex,
  type StateManifestV2,
} from './index.js';
import { makeStateKey, makeStateManifestV2Input } from './test-helpers.js';

const COMPAT_ROOT = path.resolve('protocol/fixtures/state-manifest-v2-compat');

const LEDGER = new TextEncoder().encode('compat-ledger');
const METADATA = new TextEncoder().encode('compat-metadata');

function baseManifest(): StateManifestV2 {
  return buildStateBundleV2(makeStateManifestV2Input(), LEDGER, METADATA).manifest;
}

function baseExpected(manifest: StateManifestV2): ExpectedStateManifestV2Context {
  return {
    stateKey: manifest.stateKey,
    expectedLedgerSchemaVersion: manifest.cacheContractIdentity.ledgerSchemaVersion,
    expectedPrefixContractVersion: manifest.cacheContractIdentity.prefixContractVersion,
    cacheContractIdentity: {
      providerId: manifest.cacheContractIdentity.providerId,
      modelId: manifest.cacheContractIdentity.modelId,
      adapterId: manifest.cacheContractIdentity.adapterId,
      templateId: manifest.cacheContractIdentity.templateId,
      policyId: manifest.cacheContractIdentity.policyId,
      toolDefinitionId: manifest.cacheContractIdentity.toolDefinitionId,
      cacheConfigId: manifest.cacheContractIdentity.cacheConfigId,
    },
    currentBaseSha: manifest.provenance.currentBaseSha,
    currentBaseRef: manifest.provenance.currentBaseRef,
    headRelationship: 'descendant',
    provenanceTrusted: true,
  };
}

export interface CompatFixture {
  readonly name: string;
  readonly manifest: StateManifestV2;
  readonly expected: ExpectedStateManifestV2Context;
  readonly outcome: CompatibilityOutcome;
}

function fixture(
  name: string,
  patch: (m: StateManifestV2, e: ExpectedStateManifestV2Context) => void,
  outcome: CompatibilityOutcome,
): CompatFixture {
  const manifest = baseManifest();
  const expected = baseExpected(manifest);
  patch(manifest, expected);
  return { name, manifest, expected, outcome };
}

export function buildCompatFixtures(): readonly CompatFixture[] {
  return [
    fixture('compat-continuation', () => {}, { kind: 'compatible_continuation' }),
    fixture(
      'compat-base-change',
      (_m, e) => {
        e.currentBaseSha = 'd'.repeat(40);
      },
      { kind: 'expected_invalidation', code: 'base_change' },
    ),
    fixture(
      'compat-nondescendant-head',
      (_m, e) => {
        e.headRelationship = 'non_descendant' satisfies HeadRelationship;
      },
      { kind: 'expected_invalidation', code: 'head_history_discontinuity' },
    ),
    fixture(
      'compat-unknown-ancestry',
      (_m, e) => {
        e.headRelationship = 'unknown' satisfies HeadRelationship;
      },
      { kind: 'expected_invalidation', code: 'head_history_discontinuity' },
    ),
    fixture(
      'compat-cache-contract-change',
      (_m, e) => {
        // Change one component of the cache-contract identity so the
        // comparator sees a semantic cache-contract diff without touching
        // the ledgerSchemaVersion / prefixContractVersion pair, which
        // would otherwise raise contract_version_incompatible first.
        e.cacheContractIdentity = {
          ...e.cacheContractIdentity,
          templateId: '1'.repeat(64) as Sha256Hex,
        };
      },
      { kind: 'expected_invalidation', code: 'cache_contract_change' },
    ),
    fixture(
      'compat-state-key-mismatch',
      (_m, e) => {
        e.stateKey = makeStateKey({ pullRequest: 999 });
      },
      { kind: 'incompatible', code: 'state_key_mismatch' },
    ),
    fixture(
      'compat-contract-version-mismatch',
      (_m, e) => {
        e.expectedLedgerSchemaVersion = 2;
      },
      { kind: 'incompatible', code: 'contract_version_incompatible' },
    ),
    fixture(
      'compat-unsafe-provenance',
      (_m, e) => {
        e.provenanceTrusted = false;
      },
      { kind: 'incompatible', code: 'unsafe_provenance' },
    ),
  ];
}

export async function generateAllCompatFixtures(): Promise<void> {
  await mkdir(COMPAT_ROOT, { recursive: true });
  for (const fx of buildCompatFixtures()) {
    const body = {
      description: `Compatibility comparator fixture: ${fx.outcome.kind}${
        fx.outcome.kind === 'compatible_continuation' ? '' : `:${fx.outcome.code}`
      }`,
      manifest: fx.manifest,
      expected: fx.expected,
      outcome: fx.outcome,
    };
    const path_ = path.join(COMPAT_ROOT, `${fx.name}.json`);
    await writeFile(path_, JSON.stringify(body, null, 2) + '\n');
  }
}
