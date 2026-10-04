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

// A second projection of the admitted H2 document, never an accounting collector.
export function projectCompletionOutputs(completion: ActionHostCompletionDocument): ActionOutputs {
  const values: Record<ActionOutputName, string | null> = {
    status: completion.status,
    'termination-reason': completion.termination_reason,
    'model-calls': completion.accounting.model_calls,
    'provider-attempts': completion.accounting.provider_attempts,
    'provider-retries': completion.accounting.provider_retries,
    'provider-failed-attempts': completion.accounting.provider_failed_attempts,
    'provider-unknown-usage-attempts': completion.accounting.provider_unknown_usage_attempts,
    'provider-unknown-cache-partition-attempts':
      completion.accounting.provider_unknown_cache_partition_attempts,
    'attempt-accounting-completeness': completion.accounting.attempt_accounting_completeness,
    'usage-completeness': completion.accounting.usage_completeness,
    'input-tokens': completion.accounting.input_tokens,
    'input-cache-hit-tokens': completion.accounting.input_cache_hit_tokens,
    'input-cache-miss-tokens': completion.accounting.input_cache_miss_tokens,
    'output-tokens': completion.accounting.output_tokens,
  };
  return Object.fromEntries(Object.entries(values).filter(([, value]) => value !== null));
}

export const FIXED_WRAPPER_FAILURE_OUTPUTS: ActionOutputs = Object.freeze({
  status: 'failed',
  'termination-reason': 'host_failure',
  'attempt-accounting-completeness': 'unavailable',
  'usage-completeness': 'unavailable',
});
