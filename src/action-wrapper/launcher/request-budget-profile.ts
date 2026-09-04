import { fail } from './validation.js';
import type { ArtifactRestRequestBudgetProfile } from '../artifact-bridge/artifact-rest-request-budget.js';

export const R4_REQUEST_BUDGET_PROFILE_ENVIRONMENT_VARIABLE =
  'AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE';

const TRUSTED_PROOF_PREPARED_PAYLOAD_BUILD_DISCRIMINATOR = 'r4-w2';

export const TRUSTED_PROOF_OPERATION_PRIMARY_RESERVE = 64;
// Normal ledgers are process-local while one installation-token bucket is
// shared. Nine admitted cold starts, three more for the sole infrastructure
// retry, and one unpropagated Node/Host peer charge derive a maximum of 13;
// the rounded 16-request margin is not a measured role allocation.
export const TRUSTED_PROOF_UNCOORDINATED_PRIMARY_HEADROOM = 16;
export const TRUSTED_PROOF_NORMAL_PROCESS_PRIMARY_RESERVE =
  TRUSTED_PROOF_OPERATION_PRIMARY_RESERVE + TRUSTED_PROOF_UNCOORDINATED_PRIMARY_HEADROOM;

/**
 * Closed proof profiles. Final scenario labels share one bounded safety policy;
 * observed request counts do not define their capacity.
 */
export type TrustedProofRequestBudgetProfile =
  | 'measurement'
  | 'final-bootstrap'
  | 'final-continuation'
  | 'final-stale';

export const TRUSTED_PROOF_MEASUREMENT_ARTIFACT_REST_REQUEST_BUDGET_PROFILE: ArtifactRestRequestBudgetProfile =
  Object.freeze({
    capProfile: 'apr-r4-artifact-rest-request-budget-v2',
    limits: Object.freeze({
      // Retained non-final diagnostic profile; it cannot satisfy final evidence.
      maximumTotalAuthenticatedApiRequests: 2_304,
      maximumPrimaryRateLimitRequests: 256,
    }),
    remainingTailRequired: 0,
    remainingTailReserve: 1,
    measurementOnly: true,
  });

const finalArtifactProfile: ArtifactRestRequestBudgetProfile = Object.freeze({
  capProfile: 'apr-r4-artifact-rest-request-budget-v2',
  limits: Object.freeze({
    // Rounded runaway ceilings, not measured consumption or phase allocations.
    // Live remaining headers and mandatory command reservations still apply.
    maximumTotalAuthenticatedApiRequests: 4_096,
    maximumPrimaryRateLimitRequests: 256,
  }),
  remainingTailRequired: 0,
  remainingTailReserve: TRUSTED_PROOF_NORMAL_PROCESS_PRIMARY_RESERVE,
  measurementOnly: false,
});

/** Scenario labels do not change available capacity. */
export const TRUSTED_PROOF_FINAL_BOOTSTRAP_ARTIFACT_REST_REQUEST_BUDGET_PROFILE =
  finalArtifactProfile;
export const TRUSTED_PROOF_FINAL_CONTINUATION_ARTIFACT_REST_REQUEST_BUDGET_PROFILE =
  finalArtifactProfile;
export const TRUSTED_PROOF_FINAL_STALE_ARTIFACT_REST_REQUEST_BUDGET_PROFILE = finalArtifactProfile;

/** Backward source alias for focused bootstrap-boundary tests. */
export const TRUSTED_PROOF_FINAL_ARTIFACT_REST_REQUEST_BUDGET_PROFILE =
  TRUSTED_PROOF_FINAL_BOOTSTRAP_ARTIFACT_REST_REQUEST_BUDGET_PROFILE;

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

const finalHostReceiptProfile: TrustedProofHostReceiptProfile = Object.freeze({
  measurementOnly: false,
  remainingTailReserve: TRUSTED_PROOF_NORMAL_PROCESS_PRIMARY_RESERVE,
  hostHeadSourceRestTail: 0,
  hostOtherGitHubRestTail: 0,
  trustedControlRestTail: 0,
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
    case 'final-bootstrap':
      return TRUSTED_PROOF_FINAL_BOOTSTRAP_ARTIFACT_REST_REQUEST_BUDGET_PROFILE;
    case 'final-continuation':
      return TRUSTED_PROOF_FINAL_CONTINUATION_ARTIFACT_REST_REQUEST_BUDGET_PROFILE;
    case 'final-stale':
      return TRUSTED_PROOF_FINAL_STALE_ARTIFACT_REST_REQUEST_BUDGET_PROFILE;
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
    case 'final-bootstrap':
    case 'final-continuation':
    case 'final-stale':
      return finalHostReceiptProfile;
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
    case 'final-bootstrap':
      return 'final-bootstrap';
    case 'final-continuation':
      return 'final-continuation';
    case 'final-stale':
      return 'final-stale';
    default:
      return fail('wrapper_request_budget_profile_invalid');
  }
}
