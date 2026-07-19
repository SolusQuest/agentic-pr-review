export const LIVE_CONTEXT_FILENAME = 'live-context.json' as const;
export const LIVE_CONTEXT_MAX_BYTES = 2_097_152 as const;
export const MAX_SENSITIVE_VALUES = 64 as const;
export const MAX_SENSITIVE_VALUES_TOTAL_UTF8_BYTES = 65_536 as const;
export const LIVE_STREAM_MAX_BYTES = 1_048_576 as const;

export const LIVE_OUTPUT_FILENAMES = {
  input: 'input.json',
  context: LIVE_CONTEXT_FILENAME,
  predecessorLedger: 'predecessor-ledger.json',
  result: 'result.json',
  trace: 'trace.json',
  candidateLedger: 'candidate-ledger.json',
  providerRunMetadata: 'provider-run-metadata.json',
} as const;
