import { fail } from './validation.js';
import type { ArtifactRestRequestBudgetProfile } from '../artifact-bridge/artifact-rest-request-budget.js';

export const R4_REQUEST_BUDGET_PROFILE_ENVIRONMENT_VARIABLE =
  'AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE';

const TRUSTED_PROOF_PREPARED_PAYLOAD_BUILD_DISCRIMINATOR = 'r4-w2';

/**
 * The only profile admitted while the trusted proof is measuring its request
 * envelope. `final` is deliberately named and rejected: it cannot be enabled
 * until the later freeze supplies the corresponding immutable allocations.
 */
export type TrustedProofRequestBudgetProfile = 'measurement';

export const TRUSTED_PROOF_MEASUREMENT_ARTIFACT_REST_REQUEST_BUDGET_PROFILE: ArtifactRestRequestBudgetProfile =
  Object.freeze({
    capProfile: 'apr-r4-artifact-rest-request-budget-v2',
    limits: Object.freeze({
      maximumTotalAuthenticatedApiRequests: 512,
      maximumPrimaryRateLimitRequests: 256,
    }),
    remainingTailRequired: 0,
    remainingTailReserve: 1,
    measurementOnly: true,
  });

/**
 * This remains the single selection boundary between the verified payload and
 * the process-wide artifact REST budget. `final` intentionally has no
 * definition until exact measured allocations are frozen.
 */
export function artifactRestRequestBudgetProfile(
  profile: TrustedProofRequestBudgetProfile,
): ArtifactRestRequestBudgetProfile {
  switch (profile) {
    case 'measurement':
      return TRUSTED_PROOF_MEASUREMENT_ARTIFACT_REST_REQUEST_BUDGET_PROFILE;
  }
}

export function readTrustedProofRequestBudgetProfile(
  buildDiscriminator: string,
  environment: NodeJS.ProcessEnv = process.env,
): TrustedProofRequestBudgetProfile | undefined {
  if (buildDiscriminator !== TRUSTED_PROOF_PREPARED_PAYLOAD_BUILD_DISCRIMINATOR) {
    return undefined;
  }

  switch (environment[R4_REQUEST_BUDGET_PROFILE_ENVIRONMENT_VARIABLE]) {
    case 'measurement':
      return 'measurement';
    case 'final':
      return fail('wrapper_request_budget_profile_unfrozen');
    default:
      return fail('wrapper_request_budget_profile_invalid');
  }
}
