import type { CacheContractIdentityV2, StateKeyV2, StateManifestV2 } from './manifest.js';

export type HeadRelationship = 'same' | 'descendant' | 'non_descendant' | 'unknown';

export interface ExpectedStateManifestV2Context {
  stateKey: StateKeyV2;
  expectedLedgerSchemaVersion: number;
  expectedPrefixContractVersion: number;
  cacheContractIdentity: Omit<
    CacheContractIdentityV2,
    'ledgerSchemaVersion' | 'prefixContractVersion'
  >;
  currentBaseSha: string;
  currentBaseRef: string;
  headRelationship: HeadRelationship;
  provenanceTrusted: boolean;
}

export type ExpectedInvalidationCode =
  | 'base_change'
  | 'head_history_discontinuity'
  | 'cache_contract_change';

export type IncompatibilityCode =
  | 'state_key_mismatch'
  | 'contract_version_incompatible'
  | 'unsafe_provenance';

export type CompatibilityOutcome =
  | { kind: 'compatible_continuation' }
  | { kind: 'expected_invalidation'; code: ExpectedInvalidationCode }
  | { kind: 'incompatible'; code: IncompatibilityCode };

/**
 * Pure host-compatibility comparator.
 *
 * Preconditions: #53 has already selected a candidate manifest for the host's
 * expected state key. A `state_key_mismatch` outcome therefore indicates the
 * selected artifact's manifest disagrees with the expected state key (a
 * corrupt or misfiled accepted artifact). Legitimate new-scope invocations
 * never reach this comparator; the host directly clean-bootstraps.
 */
export function checkStateManifestV2Compatibility(
  manifest: StateManifestV2,
  expected: ExpectedStateManifestV2Context,
): CompatibilityOutcome {
  if (!stateKeyEquals(manifest.stateKey, expected.stateKey)) {
    return { kind: 'incompatible', code: 'state_key_mismatch' };
  }
  if (!expected.provenanceTrusted) {
    return { kind: 'incompatible', code: 'unsafe_provenance' };
  }
  if (
    expected.expectedLedgerSchemaVersion !== manifest.cacheContractIdentity.ledgerSchemaVersion ||
    expected.expectedPrefixContractVersion !== manifest.cacheContractIdentity.prefixContractVersion
  ) {
    return { kind: 'incompatible', code: 'contract_version_incompatible' };
  }
  if (!cacheContractEquals(manifest.cacheContractIdentity, expected.cacheContractIdentity)) {
    return { kind: 'expected_invalidation', code: 'cache_contract_change' };
  }
  if (
    expected.currentBaseSha !== manifest.provenance.currentBaseSha ||
    expected.currentBaseRef !== manifest.provenance.currentBaseRef
  ) {
    return { kind: 'expected_invalidation', code: 'base_change' };
  }
  if (expected.headRelationship === 'non_descendant' || expected.headRelationship === 'unknown') {
    return { kind: 'expected_invalidation', code: 'head_history_discontinuity' };
  }
  return { kind: 'compatible_continuation' };
}

function stateKeyEquals(a: StateKeyV2, b: StateKeyV2): boolean {
  return (
    a.namespace === b.namespace &&
    a.repository === b.repository &&
    a.headRepository === b.headRepository &&
    a.pullRequest === b.pullRequest &&
    a.workflowIdentity === b.workflowIdentity &&
    a.trustedExecutionDomain === b.trustedExecutionDomain
  );
}

function cacheContractEquals(
  a: CacheContractIdentityV2,
  b: Omit<CacheContractIdentityV2, 'ledgerSchemaVersion' | 'prefixContractVersion'>,
): boolean {
  return (
    a.providerId === b.providerId &&
    a.modelId === b.modelId &&
    a.adapterId === b.adapterId &&
    a.templateId === b.templateId &&
    a.policyId === b.policyId &&
    a.toolDefinitionId === b.toolDefinitionId &&
    a.cacheConfigId === b.cacheConfigId
  );
}
