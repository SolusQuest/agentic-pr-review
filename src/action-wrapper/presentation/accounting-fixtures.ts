// Synthetic wire fixtures shared by presentation tests; never used as a production fallback.
import {
  ACCOUNTING_COUNT_KEYS,
  ACCOUNTING_TOKEN_KEYS,
  type ActionHostAccountingDocument,
} from './accounting.js';

export const unavailableAccounting = Object.freeze(
  Object.fromEntries([
    ...[...ACCOUNTING_COUNT_KEYS, ...ACCOUNTING_TOKEN_KEYS].map((key) => [key, null]),
    ['attempt_accounting_completeness', 'unavailable'],
    ['usage_completeness', 'unavailable'],
  ]),
) as ActionHostAccountingDocument;
export const zeroAccounting = Object.freeze(
  Object.fromEntries([
    ...[...ACCOUNTING_COUNT_KEYS, ...ACCOUNTING_TOKEN_KEYS].map((key) => [key, '0']),
    ['attempt_accounting_completeness', 'complete'],
    ['usage_completeness', 'complete'],
  ]),
) as ActionHostAccountingDocument;
