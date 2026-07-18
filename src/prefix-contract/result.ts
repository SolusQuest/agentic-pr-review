/**
 * Prefix-contract result and error types (issue #50, D9).
 * Every public helper returns PrefixResult; no throw crosses the boundary.
 */

export interface PrefixError {
  readonly code: string;
  readonly path: string;
}

export type PrefixResult<T> =
  | { readonly ok: true; readonly value: T }
  | { readonly ok: false; readonly errors: readonly PrefixError[] };

export function ok<T>(value: T): PrefixResult<T> {
  return { ok: true, value };
}

export function fail<T>(code: string, path = ''): PrefixResult<T> {
  return { ok: false, errors: [{ code, path }] };
}

/** Diagnostic codes — mechanical kebab-case mirrors of the C# codes (D10). */
export const PREFIX_CODES = {
  identityInvalid: 'prefix-identity-invalid',
  modelAliasLiteral: 'prefix-model-alias-literal',
  digestInvalid: 'prefix-digest-invalid',
  gitShaInvalid: 'prefix-git-sha-invalid',
  ordinalInvalid: 'prefix-ordinal-invalid',
  epochInvalid: 'prefix-epoch-invalid',
  envelopeInvalid: 'prefix-envelope-invalid',
  canonicalInputRejected: 'prefix-canonical-input-rejected',
  envelopeTooLarge: 'prefix-envelope-too-large',
  cacheContractIdMismatch: 'prefix-cache-contract-id-mismatch',
  identityMismatch: 'prefix-identity-mismatch',
  currentContextInvalid: 'prefix-current-context-invalid',
  segmentTooLarge: 'prefix-segment-too-large',
  streamTooLarge: 'prefix-stream-too-large',
  lengthOverflow: 'prefix-length-overflow',
} as const;
