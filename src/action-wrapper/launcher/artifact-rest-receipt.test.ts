import { describe, expect, it } from 'vitest';
import {
  ARTIFACT_REST_RECEIPT_PREFIX,
  canonicalArtifactRestReceipt,
  type ArtifactRestReceiptIdentity,
} from './artifact-rest-receipt.js';
import {
  artifactRestRequestBudgetProfile,
  type TrustedProofRequestBudgetProfile,
} from './request-budget-profile.js';

const identity: ArtifactRestReceiptIdentity = {
  repository: 'owner/repo',
  repositoryId: '42',
  workflowSha: 'a'.repeat(40),
  actionSourceSha: 'b'.repeat(40),
  payloadSha256: 'c'.repeat(64),
  buildDiscriminator: 'r4-w2',
  runId: '7001',
  runAttempt: '2',
};
function receipt(profile: TrustedProofRequestBudgetProfile = 'measurement', change = {}) {
  const expected = artifactRestRequestBudgetProfile(profile);
  return (
    ARTIFACT_REST_RECEIPT_PREFIX +
    JSON.stringify({
      kind: 'apr-r4-trusted-proof-artifact-rest-budget-v2',
      protected_route: true,
      maximum_total_authenticated_api_requests:
        expected.limits.maximumTotalAuthenticatedApiRequests,
      total_authenticated_api_requests: 3,
      maximum_primary_rate_limit_requests: 256,
      primary_rate_limit_requests: 2,
      conditional_not_modified_requests: 1,
      secondary_limit_points: 7,
      permission_denied: 0,
      remaining_total_authenticated_api_requests:
        expected.limits.maximumTotalAuthenticatedApiRequests - 3,
      remaining_primary_rate_limit_requests: 254,
      disposition: 'active',
      repository: identity.repository,
      repository_id: identity.repositoryId,
      workflow_sha: identity.workflowSha,
      action_source_sha: identity.actionSourceSha,
      payload_sha256: identity.payloadSha256,
      build_discriminator: identity.buildDiscriminator,
      run_id: identity.runId,
      run_attempt: identity.runAttempt,
      cap_profile: expected.capProfile,
      measurement_only: expected.measurementOnly,
      remaining_tail_required: expected.remainingTailRequired,
      remaining_tail_reserve: expected.remainingTailReserve,
      ...change,
    })
  );
}

describe('C# artifact REST receipt forwarding', () => {
  it.each(['measurement', 'final-bootstrap', 'final-continuation', 'final-stale'] as const)(
    'forwards the bound Host receipt without reconstructing counters (%s)',
    (profile) => {
      const line = receipt(profile);
      expect(canonicalArtifactRestReceipt(line, profile, identity)).toBe(line + '\n');
    },
  );
  it.each([
    { run_id: '7002' },
    { payload_sha256: 'd'.repeat(64) },
    { repository: 'other/repo' },
    { maximum_primary_rate_limit_requests: 257 },
    { remaining_tail_reserve: 80 },
    { remaining_total_authenticated_api_requests: 2304 },
    { primary_rate_limit_requests: -1 },
    { conditional_not_modified_requests: 4 },
    { secondary_limit_points: 16 },
    { permission_denied: 4 },
    { extra: true },
    { disposition: 'unknown' },
  ])('rejects contradictory, unbound or unexpected Host fields %j', (change) => {
    expect(
      canonicalArtifactRestReceipt(receipt('measurement', change), 'measurement', identity),
    ).toBeUndefined();
  });
  it('rejects duplicates, truncation, oversize, missing counters and wrong profiles', () => {
    const line = receipt();
    for (const invalid of [
      line.slice(0, -1),
      line.replace('"protected_route":true', '"protected_route":true,"protected_route":true'),
      line.replace('"permission_denied":0,', ''),
      line + ' '.repeat(4096),
    ])
      expect(canonicalArtifactRestReceipt(invalid, 'measurement', identity)).toBeUndefined();
    expect(canonicalArtifactRestReceipt(line, 'final-bootstrap', identity)).toBeUndefined();
  });
});
