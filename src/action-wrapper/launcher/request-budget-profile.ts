import { fail } from './validation.js';

export const R4_REQUEST_BUDGET_PROFILE_ENVIRONMENT_VARIABLE =
  'AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE';

const TRUSTED_PROOF_PREPARED_PAYLOAD_BUILD_DISCRIMINATOR = 'r4-w2';

/**
 * The only profile admitted while the trusted proof is measuring its request
 * envelope. `final` is deliberately named and rejected: it cannot be enabled
 * until the later freeze supplies the corresponding immutable allocations.
 */
export type TrustedProofRequestBudgetProfile = 'measurement';

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
