import { createHash } from 'node:crypto';
import { describe, expect, it } from 'vitest';
import { canonicalJsonBytes } from '../canonical-json/index.js';
import { computeAdapterId, computeCacheConfigId } from '../prefix-contract/digest.js';
import {
  DEEPSEEK_CACHE_CONTRACT_ENVELOPES,
  DEEPSEEK_CACHE_CONTRACT_IDENTITY,
  DEEPSEEK_REQUEST_CONTRACT,
  DEEPSEEK_REQUEST_CONTRACT_SHA256,
  deepSeekContractForMaxFindings,
} from './deepseek-contract.js';

describe('DeepSeek live contract', () => {
  it('uses the frozen cache semantics and binds both envelope identities', () => {
    expect(DEEPSEEK_CACHE_CONTRACT_ENVELOPES.cacheConfig).toMatchObject({
      eligibility: 'automatic',
      markerPolicy: 'none',
      statelessMode: false,
    });
    expect(DEEPSEEK_CACHE_CONTRACT_ENVELOPES.adapter.schemaVersion).toBe(2);
    expect(computeCacheConfigId(DEEPSEEK_CACHE_CONTRACT_ENVELOPES.cacheConfig)).toEqual({
      ok: true,
      value: DEEPSEEK_CACHE_CONTRACT_IDENTITY.cacheConfigId,
    });
    expect(computeAdapterId(DEEPSEEK_CACHE_CONTRACT_ENVELOPES.adapter)).toEqual({
      ok: true,
      value: DEEPSEEK_CACHE_CONTRACT_IDENTITY.adapterId,
    });
  });

  it('includes request-affecting transport and parser policy in requestContractSha256', () => {
    expect(DEEPSEEK_REQUEST_CONTRACT_SHA256).toBe(
      createHash('sha256').update(canonicalJsonBytes(DEEPSEEK_REQUEST_CONTRACT)).digest('hex'),
    );
    expect(DEEPSEEK_REQUEST_CONTRACT_SHA256).toBe(
      '312f55d0038a4bcefb26703158edcf196bdb1e6a458c6ec88f3f08b1211f0356',
    );
    const changed = {
      ...DEEPSEEK_REQUEST_CONTRACT,
      transport: { ...DEEPSEEK_REQUEST_CONTRACT.transport, oneAttempt: false },
    };
    expect(createHash('sha256').update(canonicalJsonBytes(changed)).digest('hex')).not.toBe(
      DEEPSEEK_REQUEST_CONTRACT_SHA256,
    );
    expect(DEEPSEEK_REQUEST_CONTRACT.headers.names).toEqual(['Authorization', 'Content-Type']);
    expect(DEEPSEEK_REQUEST_CONTRACT.transport.connectTimeoutSeconds).toBe(15);
  });

  it('binds the selected finding cap into the policy identity', () => {
    const defaultContract = deepSeekContractForMaxFindings(50);
    const lowerContract = deepSeekContractForMaxFindings(7);
    expect(defaultContract.identity.policyId).toBe(DEEPSEEK_CACHE_CONTRACT_IDENTITY.policyId);
    expect(lowerContract.envelopes.policy.constraints.maxFindings).toBe(7);
    expect(lowerContract.identity.policyId).not.toBe(defaultContract.identity.policyId);
    expect(lowerContract.identity.adapterId).toBe(defaultContract.identity.adapterId);
  });
});
