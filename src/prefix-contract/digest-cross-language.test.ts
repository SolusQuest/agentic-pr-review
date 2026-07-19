import { describe, expect, it } from 'vitest';
import { computeCacheContractDigest, computeSubjectDigest } from './digest.js';

describe('M4 cross-language digest vectors', () => {
  it('uses the review-subject domain tag and one NUL separator', () => {
    expect(computeSubjectDigest({ pr: 55 })).toEqual({
      ok: true,
      value: 'a94ec7516d72513ee9e9c8d4ce1aa31067b52374abf5f4f21991d4bfe5343ed5',
    });
  });

  it('uses the exact untagged seven-field cache-contract object', () => {
    expect(
      computeCacheContractDigest({
        adapterId: 'a',
        cacheConfigId: 'c',
        modelId: 'm',
        policyId: 'p',
        providerId: 'v',
        templateId: 't',
        toolDefinitionId: 'd',
      }),
    ).toEqual({
      ok: true,
      value: '7e0c4adc9cc9b64a9866a37d6be4e007d727059117dd467aa1488a8f67a4f666',
    });
  });
});
