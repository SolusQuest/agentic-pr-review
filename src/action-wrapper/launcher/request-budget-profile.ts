import { fail } from './validation.js';
import type { ArtifactRestRequestBudgetProfile } from '../artifact-bridge/artifact-rest-request-budget.js';

export const R4_REQUEST_BUDGET_PROFILE_ENVIRONMENT_VARIABLE =
  'AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE';

const TRUSTED_PROOF_PREPARED_PAYLOAD_BUILD_DISCRIMINATOR = 'r4-w2';

/**
 * The only profile admitted while the trusted proof is measuring its request
 * envelope. Both variants are closed: callers cannot supply a wider or
 * partially specified allocation.
 */
export type TrustedProofRequestBudgetProfile = 'measurement' | 'final';

export const TRUSTED_PROOF_MEASUREMENT_ARTIFACT_REST_REQUEST_BUDGET_PROFILE: ArtifactRestRequestBudgetProfile =
  Object.freeze({
    capProfile: 'apr-r4-artifact-rest-request-budget-v2',
    limits: Object.freeze({
      // Bootstrap completed at 1,096 raw requests, while continuation's
      // frozen 2,042 state operations were still reconciling at 1,280 raw.
      // This measurement-only window covers those bounded operations plus 262
      // authenticated verification slots; final freezes the completed count.
      maximumTotalAuthenticatedApiRequests: 2_304,
      maximumPrimaryRateLimitRequests: 256,
    }),
    remainingTailRequired: 0,
    remainingTailReserve: 1,
    measurementOnly: true,
  });

/** The reviewed two-run allocation, including the Node domain's shared-token tail. */
export const TRUSTED_PROOF_FINAL_ARTIFACT_REST_REQUEST_BUDGET_PROFILE: ArtifactRestRequestBudgetProfile =
  Object.freeze({
    capProfile: 'apr-r4-artifact-rest-request-budget-v2',
    limits: Object.freeze({
      maximumTotalAuthenticatedApiRequests: 2_130,
      maximumPrimaryRateLimitRequests: 136,
    }),
    remainingTailRequired: 679,
    remainingTailReserve: 64,
    measurementOnly: false,
  });

export interface TrustedProofHostReceiptProfile {
  readonly measurementOnly: boolean;
  readonly remainingTailReserve: number;
  readonly hostHeadSourceRestTail: number;
  readonly hostOtherGitHubRestTail: number;
  readonly trustedControlRestTail: number;
}

const TRUSTED_PROOF_MEASUREMENT_HOST_RECEIPT_PROFILE: TrustedProofHostReceiptProfile =
  Object.freeze({
    measurementOnly: true,
    remainingTailReserve: 1,
    hostHeadSourceRestTail: 0,
    hostOtherGitHubRestTail: 0,
    trustedControlRestTail: 0,
  });

const TRUSTED_PROOF_FINAL_HOST_RECEIPT_PROFILE: TrustedProofHostReceiptProfile = Object.freeze({
  measurementOnly: false,
  remainingTailReserve: 64,
  hostHeadSourceRestTail: 863,
  hostOtherGitHubRestTail: 878,
  trustedControlRestTail: 888,
});

/**
 * This remains the single selection boundary between the verified payload and
 * the process-wide artifact REST budget.
 */
export function artifactRestRequestBudgetProfile(
  profile: TrustedProofRequestBudgetProfile,
): ArtifactRestRequestBudgetProfile {
  switch (profile) {
    case 'measurement':
      return TRUSTED_PROOF_MEASUREMENT_ARTIFACT_REST_REQUEST_BUDGET_PROFILE;
    case 'final':
      return TRUSTED_PROOF_FINAL_ARTIFACT_REST_REQUEST_BUDGET_PROFILE;
  }
}

/**
 * Host stderr is private and untrusted. Its receipt is admitted only against
 * the same verified profile that configured the child process.
 */
export function trustedProofHostReceiptProfile(
  profile: TrustedProofRequestBudgetProfile,
): TrustedProofHostReceiptProfile {
  switch (profile) {
    case 'measurement':
      return TRUSTED_PROOF_MEASUREMENT_HOST_RECEIPT_PROFILE;
    case 'final':
      return TRUSTED_PROOF_FINAL_HOST_RECEIPT_PROFILE;
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
      return 'final';
    default:
      return fail('wrapper_request_budget_profile_invalid');
  }
}
