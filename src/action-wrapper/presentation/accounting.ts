import { exactRecord, fail } from '../launcher/validation.js';

export const ACCOUNTING_COUNT_KEYS = [
  'model_calls',
  'provider_attempts',
  'provider_retries',
  'provider_failed_attempts',
  'provider_unknown_usage_attempts',
  'provider_unknown_cache_partition_attempts',
] as const;
export const ACCOUNTING_TOKEN_KEYS = [
  'input_tokens',
  'input_cache_hit_tokens',
  'input_cache_miss_tokens',
  'output_tokens',
] as const;
export type AccountingCompleteness = 'complete' | 'partial' | 'unavailable';
export type ActionHostAccountingDocument = Readonly<
  Record<
    (typeof ACCOUNTING_COUNT_KEYS)[number] | (typeof ACCOUNTING_TOKEN_KEYS)[number],
    string | null
  > & {
    attempt_accounting_completeness: AccountingCompleteness;
    usage_completeness: AccountingCompleteness;
  }
>;
export const TERMINATION_REASONS = [
  'review_completed',
  'not_started',
  'provider_failure',
  'cancelled',
  'deadline_exceeded',
  'model_limit',
  'tool_limit',
  'token_limit',
  'request_limit',
  'response_limit',
  'context_limit',
  'invalid_result',
  'host_failure',
] as const;
export type ActionHostTerminationReason = (typeof TERMINATION_REASONS)[number];

export function validateAccounting(value: unknown): ActionHostAccountingDocument {
  const record = exactRecord(
    value,
    [
      ...ACCOUNTING_COUNT_KEYS,
      ...ACCOUNTING_TOKEN_KEYS,
      'attempt_accounting_completeness',
      'usage_completeness',
    ],
    'wrapper_completion_invalid',
  );
  const counts = ACCOUNTING_COUNT_KEYS.map((key) => decimal(record[key]));
  const tokens = ACCOUNTING_TOKEN_KEYS.map((key) => decimal(record[key]));
  const attempts = completeness(record.attempt_accounting_completeness);
  const usage = completeness(record.usage_completeness);
  if (counts.every((value) => value === null)) {
    if (
      tokens.some((value) => value !== null) ||
      attempts !== 'unavailable' ||
      usage !== 'unavailable'
    )
      invalid();
  } else {
    if (counts.some((value) => value === null)) invalid();
    const [calls, sends, retries, failed, unknownUsage, unknownCache] = counts as bigint[];
    if (
      calls! > 128n ||
      sends! > 136n ||
      retries! > 8n ||
      retries! > failed! ||
      failed! > sends! ||
      unknownUsage! > sends! ||
      unknownCache! > sends! ||
      sends! - retries! > calls! ||
      sends! < retries! ||
      retries! > 2n * (sends! - retries!)
    )
      invalid();
    if (attempts === 'unavailable' && counts.slice(1).some((value) => value !== 0n)) invalid();
    if (
      usage === 'unavailable' &&
      (tokens.some((value) => value !== 0n) || unknownUsage! !== sends! || unknownCache! !== 0n)
    )
      invalid();
    const completeUsage =
      attempts === 'complete' &&
      unknownUsage! === 0n &&
      unknownCache! === 0n &&
      tokens.every((value) => value !== null);
    if ((usage === 'complete') !== completeUsage) invalid();
    const [input, hit, miss] = tokens;
    if (
      input != null &&
      ((hit != null && hit > input) ||
        (miss != null && miss > input) ||
        (hit != null && miss != null && miss > input - hit))
    )
      invalid();
    if (usage === 'complete' && miss !== input! - hit!) invalid();
    if (
      attempts === 'complete' &&
      sends! === 0n &&
      (usage !== 'complete' || tokens.some((value) => value !== 0n))
    )
      invalid();
    if (
      calls! === 0n &&
      (counts.some((value) => value !== 0n) ||
        tokens.some((value) => value !== 0n) ||
        attempts !== 'complete' ||
        usage !== 'complete')
    )
      invalid();
  }
  return record as unknown as ActionHostAccountingDocument;
}

export function validateTermination(
  value: unknown,
  accounting: ActionHostAccountingDocument,
  kind: 'reviewed' | 'skipped' | 'failure' | 'conflict',
): ActionHostTerminationReason {
  if (typeof value !== 'string' || !(TERMINATION_REASONS as readonly string[]).includes(value))
    invalid();
  if (value === 'not_started' && !isCompleteZero(accounting)) invalid();
  if (kind === 'skipped' && (value !== 'not_started' || !isCompleteZero(accounting))) invalid();
  if (kind === 'reviewed' && value !== 'review_completed' && value !== 'not_started') invalid();
  // Incomplete counts are lower bounds: no observed success does not prove failure.
  if (
    value === 'review_completed' &&
    accounting.attempt_accounting_completeness === 'complete' &&
    BigInt(accounting.provider_attempts!) <= BigInt(accounting.provider_failed_attempts!)
  )
    invalid();
  return value as ActionHostTerminationReason;
}

function isCompleteZero(value: ActionHostAccountingDocument): boolean {
  return (
    value.attempt_accounting_completeness === 'complete' &&
    value.usage_completeness === 'complete' &&
    [...ACCOUNTING_COUNT_KEYS, ...ACCOUNTING_TOKEN_KEYS].every((key) => value[key] === '0')
  );
}

function decimal(value: unknown): bigint | null {
  if (value === null) return null;
  if (typeof value !== 'string' || value.length > 19 || !/^(0|[1-9][0-9]*)$/.test(value)) invalid();
  const parsed = BigInt(value);
  if (parsed > 9223372036854775807n) invalid();
  return parsed;
}

function completeness(value: unknown): AccountingCompleteness {
  if (value !== 'complete' && value !== 'partial' && value !== 'unavailable') invalid();
  return value;
}

function invalid(): never {
  return fail('wrapper_completion_invalid');
}
