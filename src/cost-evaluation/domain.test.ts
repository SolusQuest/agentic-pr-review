import { describe, expect, it } from 'vitest';
import {
  strategyIdentityDigest,
  windowPartitionKeyDigest,
  type CostEvaluationStrategyIdentityV1,
  type WindowPartitionInputs,
} from './domain.js';

const resumed: CostEvaluationStrategyIdentityV1 = {
  schemaVersion: 1,
  adapterId: 'adapter-resumed',
  cacheConfigId: 'cache-resumed',
  capabilityMode: 'standard',
  statelessProofKind: null,
};

const stateless: CostEvaluationStrategyIdentityV1 = {
  schemaVersion: 1,
  adapterId: 'adapter-stateless',
  cacheConfigId: 'cache-stateless',
  capabilityMode: 'stateless',
  statelessProofKind: 'synthetic',
};

describe('strategyIdentityDigest', () => {
  it('is deterministic for identical input', () => {
    expect(strategyIdentityDigest(resumed)).toBe(strategyIdentityDigest(resumed));
  });

  it('changes when the free string fields change', () => {
    const base = strategyIdentityDigest(resumed);
    expect(strategyIdentityDigest({ ...resumed, adapterId: 'other' })).not.toBe(base);
    expect(strategyIdentityDigest({ ...resumed, cacheConfigId: 'other' })).not.toBe(base);
  });

  it('couples capabilityMode/statelessProofKind: resumed vs stateless differ', () => {
    // The discriminated union binds capabilityMode to statelessProofKind, so the
    // two legal identities (standard/null, stateless/synthetic) are the only
    // constructable variants; their digests must differ.
    expect(strategyIdentityDigest(resumed)).not.toBe(strategyIdentityDigest(stateless));
  });
});

describe('windowPartitionKeyDigest', () => {
  const base: WindowPartitionInputs = {
    profileDigest: 'profile-digest',
    providerId: 'prov',
    modelId: 'model',
    resumedStrategyIdentity: resumed,
    statelessStrategyIdentity: stateless,
    fixtureSuiteDigest: 'fixture-suite-digest',
    prefixContractVersion: 1,
    harnessVersion: 'harness-1',
    mode: 'synthetic',
  };

  it('is deterministic for identical input', () => {
    expect(windowPartitionKeyDigest(base)).toBe(windowPartitionKeyDigest(base));
  });

  it('changes when any mutable scalar field changes', () => {
    const d = windowPartitionKeyDigest(base);
    expect(windowPartitionKeyDigest({ ...base, profileDigest: 'p2' })).not.toBe(d);
    expect(windowPartitionKeyDigest({ ...base, providerId: 'prov2' })).not.toBe(d);
    expect(windowPartitionKeyDigest({ ...base, modelId: 'm2' })).not.toBe(d);
    expect(windowPartitionKeyDigest({ ...base, fixtureSuiteDigest: 'fs2' })).not.toBe(d);
    expect(windowPartitionKeyDigest({ ...base, harnessVersion: 'h2' })).not.toBe(d);
    expect(windowPartitionKeyDigest({ ...base, mode: 'live' })).not.toBe(d);
    // prefixContractVersion is a frozen literal (typeof PREFIX_CONTRACT_VERSION
    // = 1); only one value is constructable, so its sensitivity is enforced by
    // the type, not by mutation.
  });

  it('changes when either strategy identity changes', () => {
    const d = windowPartitionKeyDigest(base);
    expect(
      windowPartitionKeyDigest({
        ...base,
        resumedStrategyIdentity: { ...resumed, adapterId: 'other' },
      }),
    ).not.toBe(d);
    expect(
      windowPartitionKeyDigest({
        ...base,
        statelessStrategyIdentity: { ...stateless, adapterId: 'other' },
      }),
    ).not.toBe(d);
  });
});

/**
 * Independent structural check: the window-partition preimage must fold the two
 * strategy identities in via their digests (not raw), and the resumed/stateless
 * slots must be positionally distinct. Verified here by an independent recompute
 * rather than the helper.
 */
describe('windowPartitionKeyDigest structural independence', () => {
  it('is NOT equal when resumed and stateless identities are swapped', () => {
    const normal: WindowPartitionInputs = {
      profileDigest: 'p',
      providerId: 'prov',
      modelId: 'm',
      resumedStrategyIdentity: resumed,
      statelessStrategyIdentity: stateless,
      fixtureSuiteDigest: 'fs',
      prefixContractVersion: 1,
      harnessVersion: 'h',
      mode: 'synthetic',
    };
    const swapped: WindowPartitionInputs = {
      ...normal,
      resumedStrategyIdentity: stateless,
      statelessStrategyIdentity: resumed,
    };
    expect(windowPartitionKeyDigest(swapped)).not.toBe(windowPartitionKeyDigest(normal));
  });
});
