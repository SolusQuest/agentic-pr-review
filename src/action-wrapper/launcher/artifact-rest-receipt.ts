import { parseStrictJson } from './strict-json.js';
import {
  artifactRestRequestBudgetProfile,
  type TrustedProofRequestBudgetProfile,
} from './request-budget-profile.js';

export const ARTIFACT_REST_RECEIPT_PREFIX = 'APR_R4_E2P_ARTIFACT_REST_BUDGET ';
export interface ArtifactRestReceiptIdentity {
  repository: string;
  repositoryId: string;
  workflowSha: string;
  actionSourceSha: string;
  payloadSha256: string;
  buildDiscriminator: string;
  runId: string;
  runAttempt: string;
}

// Mechanical private proof framing only. C# owns all counters, reservations and
// dispatch decisions. Never reconstruct missing observations in the wrapper.
export function canonicalArtifactRestReceipt(
  line: string,
  profile: TrustedProofRequestBudgetProfile,
  identity?: ArtifactRestReceiptIdentity,
): string | undefined {
  if (!line.startsWith(ARTIFACT_REST_RECEIPT_PREFIX) || line.length > 4096) return undefined;
  let parsed: unknown;
  try {
    parsed = parseStrictJson(
      Buffer.from(line.slice(ARTIFACT_REST_RECEIPT_PREFIX.length).trimEnd(), 'utf8'),
      4096,
    );
  } catch {
    return undefined;
  }
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return undefined;
  const value = parsed as Record<string, unknown>;
  const keys = [
    'kind',
    'protected_route',
    'maximum_total_authenticated_api_requests',
    'total_authenticated_api_requests',
    'maximum_primary_rate_limit_requests',
    'primary_rate_limit_requests',
    'conditional_not_modified_requests',
    'secondary_limit_points',
    'permission_denied',
    'remaining_total_authenticated_api_requests',
    'remaining_primary_rate_limit_requests',
    'disposition',
    'repository',
    'repository_id',
    'workflow_sha',
    'action_source_sha',
    'payload_sha256',
    'build_discriminator',
    'run_id',
    'run_attempt',
    'cap_profile',
    'measurement_only',
    'remaining_tail_required',
    'remaining_tail_reserve',
  ];
  if (Object.keys(value).length !== keys.length || keys.some((key) => !(key in value)))
    return undefined;
  const expected = artifactRestRequestBudgetProfile(profile);
  const numeric = keys.slice(2, 11);
  if (
    numeric.some(
      (key) =>
        !Number.isSafeInteger(value[key]) ||
        Number(value[key]) < 0 ||
        Number(value[key]) > 1_000_000,
    )
  )
    return undefined;
  if (
    value.kind !== 'apr-r4-trusted-proof-artifact-rest-budget-v2' ||
    value.protected_route !== true ||
    value.maximum_total_authenticated_api_requests !==
      expected.limits.maximumTotalAuthenticatedApiRequests ||
    value.maximum_primary_rate_limit_requests !== expected.limits.maximumPrimaryRateLimitRequests ||
    value.remaining_total_authenticated_api_requests !==
      Number(value.maximum_total_authenticated_api_requests) -
        Number(value.total_authenticated_api_requests) ||
    value.remaining_primary_rate_limit_requests !==
      Number(value.maximum_primary_rate_limit_requests) -
        Number(value.primary_rate_limit_requests) ||
    Number(value.primary_rate_limit_requests) + Number(value.conditional_not_modified_requests) !==
      value.total_authenticated_api_requests ||
    Number(value.permission_denied) > Number(value.total_authenticated_api_requests) ||
    Number(value.secondary_limit_points) < Number(value.total_authenticated_api_requests) ||
    Number(value.secondary_limit_points) > 5 * Number(value.total_authenticated_api_requests) ||
    value.measurement_only !== expected.measurementOnly ||
    value.cap_profile !== expected.capProfile ||
    value.remaining_tail_required !== expected.remainingTailRequired ||
    value.remaining_tail_reserve !== expected.remainingTailReserve ||
    ![
      'active',
      'total_exhausted',
      'primary_exhausted',
      'rate_limited',
      'primary_and_secondary_rate_limited',
      'invalid_rate_limit_headers',
    ].includes(String(value.disposition))
  )
    return undefined;
  if (
    typeof value.repository !== 'string' ||
    !/^[A-Za-z0-9._-]+\/[A-Za-z0-9._-]+$/.test(value.repository) ||
    value.repository.length > 256 ||
    !['repository_id', 'run_id', 'run_attempt'].every(
      (key) => typeof value[key] === 'string' && /^[1-9][0-9]{0,18}$/.test(value[key] as string),
    ) ||
    !['workflow_sha', 'action_source_sha'].every(
      (key) => typeof value[key] === 'string' && /^[0-9a-f]{40}$/.test(value[key] as string),
    ) ||
    typeof value.payload_sha256 !== 'string' ||
    !/^[0-9a-f]{64}$/.test(value.payload_sha256) ||
    value.build_discriminator !== 'r4-w2'
  )
    return undefined;
  if (
    identity &&
    Object.entries({
      repository: identity.repository,
      repository_id: identity.repositoryId,
      workflow_sha: identity.workflowSha,
      action_source_sha: identity.actionSourceSha,
      payload_sha256: identity.payloadSha256,
      build_discriminator: identity.buildDiscriminator,
      run_id: identity.runId,
      run_attempt: identity.runAttempt,
    }).some(([key, expectedValue]) => value[key] !== expectedValue)
  )
    return undefined;
  return ARTIFACT_REST_RECEIPT_PREFIX + JSON.stringify(value) + '\n';
}
