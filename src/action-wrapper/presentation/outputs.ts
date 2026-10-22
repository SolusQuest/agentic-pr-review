import type { ActionHostCompletionDocument } from './completion.js';

export const ACTION_OUTPUT_NAMES = Object.freeze([
  'status',
  'termination-reason',
  'model-calls',
  'provider-attempts',
  'provider-retries',
  'provider-failed-attempts',
  'provider-unknown-usage-attempts',
  'provider-unknown-cache-partition-attempts',
  'attempt-accounting-completeness',
  'usage-completeness',
  'input-tokens',
  'input-cache-hit-tokens',
  'input-cache-miss-tokens',
  'output-tokens',
] as const);
export type ActionOutputName = (typeof ACTION_OUTPUT_NAMES)[number];
export type ActionOutputs = Readonly<Partial<Record<ActionOutputName, string>>>;

// The Host already supplied the projection. Null filtering only controls omission.
export function projectCompletionOutputs(completion: ActionHostCompletionDocument): ActionOutputs {
  return Object.fromEntries(
    Object.entries(completion.outputs).filter(([, value]) => value !== null),
  );
}

export const FIXED_WRAPPER_FAILURE_OUTPUTS: ActionOutputs = Object.freeze({
  status: 'failed',
  'termination-reason': 'host_failure',
  'attempt-accounting-completeness': 'unavailable',
  'usage-completeness': 'unavailable',
});
