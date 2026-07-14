import { describe, expect, it } from 'vitest';
import {
  buildStateBundleV2,
  checkStateManifestV2Compatibility,
  type ExpectedStateManifestV2Context,
  type StateManifestV2,
} from './index.js';
import { makeStateKey, makeStateManifestV2Input, sha256Hex } from './test-helpers.js';

const LEDGER = new TextEncoder().encode('ledger-bytes');
const METADATA = new TextEncoder().encode('metadata-bytes');

function built(): StateManifestV2 {
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

describe('checkStateManifestV2Compatibility', () => {
  it('same head is a compatible continuation', () => {
    const m = built();
    const outcome = checkStateManifestV2Compatibility(m, {
      ...baseExpected(m),
      headRelationship: 'same',
    });
    expect(outcome.kind).toBe('compatible_continuation');
  });

  it('descendant head is compatible', () => {
    const m = built();
    const outcome = checkStateManifestV2Compatibility(m, {
      ...baseExpected(m),
      headRelationship: 'descendant',
    });
    expect(outcome.kind).toBe('compatible_continuation');
  });

  it('base sha change -> expected_invalidation:base_change', () => {
    const m = built();
    const outcome = checkStateManifestV2Compatibility(m, {
      ...baseExpected(m),
      currentBaseSha: 'd'.repeat(40),
    });
    expect(outcome).toEqual({ kind: 'expected_invalidation', code: 'base_change' });
  });

  it('base ref change -> expected_invalidation:base_change', () => {
    const m = built();
    const outcome = checkStateManifestV2Compatibility(m, {
      ...baseExpected(m),
      currentBaseRef: 'refs/heads/other',
    });
    expect(outcome).toEqual({ kind: 'expected_invalidation', code: 'base_change' });
  });

  it('non_descendant head -> head_history_discontinuity', () => {
    const m = built();
    const outcome = checkStateManifestV2Compatibility(m, {
      ...baseExpected(m),
      headRelationship: 'non_descendant',
    });
    expect(outcome).toEqual({
      kind: 'expected_invalidation',
      code: 'head_history_discontinuity',
    });
  });

  it('unknown head ancestry -> head_history_discontinuity', () => {
    const m = built();
    const outcome = checkStateManifestV2Compatibility(m, {
      ...baseExpected(m),
      headRelationship: 'unknown',
    });
    expect(outcome).toEqual({
      kind: 'expected_invalidation',
      code: 'head_history_discontinuity',
    });
  });

  it('cache contract change -> expected_invalidation:cache_contract_change', () => {
    const m = built();
    const expected = baseExpected(m);
    expected.cacheContractIdentity = {
      ...expected.cacheContractIdentity,
      policyId: sha256Hex('different-policy'),
    };
    const outcome = checkStateManifestV2Compatibility(m, expected);
    expect(outcome).toEqual({ kind: 'expected_invalidation', code: 'cache_contract_change' });
  });

  it('state key mismatch -> incompatible:state_key_mismatch', () => {
    const m = built();
    const expected = baseExpected(m);
    expected.stateKey = makeStateKey({ repository: 'someone-else/repo' });
    const outcome = checkStateManifestV2Compatibility(m, expected);
    expect(outcome).toEqual({ kind: 'incompatible', code: 'state_key_mismatch' });
  });

  it('contract version incompatible -> incompatible:contract_version_incompatible', () => {
    const m = built();
    const outcome = checkStateManifestV2Compatibility(m, {
      ...baseExpected(m),
      expectedLedgerSchemaVersion: 2,
    });
    expect(outcome).toEqual({ kind: 'incompatible', code: 'contract_version_incompatible' });
  });

  it('unsafe provenance -> incompatible:unsafe_provenance', () => {
    const m = built();
    const outcome = checkStateManifestV2Compatibility(m, {
      ...baseExpected(m),
      provenanceTrusted: false,
    });
    expect(outcome).toEqual({ kind: 'incompatible', code: 'unsafe_provenance' });
  });

  it('state_key_mismatch has precedence over unsafe_provenance', () => {
    const m = built();
    const expected = baseExpected(m);
    expected.stateKey = makeStateKey({ repository: 'someone-else/repo' });
    expected.provenanceTrusted = false;
    const outcome = checkStateManifestV2Compatibility(m, expected);
    expect(outcome.kind).toBe('incompatible');
    if (outcome.kind === 'incompatible') expect(outcome.code).toBe('state_key_mismatch');
  });
});
