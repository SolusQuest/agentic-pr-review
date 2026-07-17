import { PREFIX_CODES, fail, ok, type PrefixResult } from './result.js';

/** Shared frozen identity domains (issue #50; design contract frozen vocabulary). */

export const MAX_IDENTITY_UTF8_BYTES = 256;
export const MAX_INTERACTION_ORDINAL = 1_000_000;

const LOWER_HEX_64 = /^[a-f0-9]{64}$/;
const GIT_SHA = /^([a-f0-9]{40}|[a-f0-9]{64})$/;
const EPOCH_ID = /^[A-Za-z0-9_-]{22}$/;

/** Shared identity-string domain: non-empty, ≤ 256 UTF-8 bytes, no control characters. */
export function isValidIdentity(value: unknown): value is string {
  if (typeof value !== 'string' || value.length === 0) {
    return false;
  }
  if (new TextEncoder().encode(value).byteLength > MAX_IDENTITY_UTF8_BYTES) {
    return false;
  }
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i);
    if (code <= 0x1f || code === 0x7f) {
      return false;
    }
  }
  return true;
}

export function validateIdentity(value: unknown): PrefixResult<string> {
  return isValidIdentity(value)
    ? ok(value)
    : fail(PREFIX_CODES.identityInvalid);
}

export function validateModelSnapshot(modelId: unknown): PrefixResult<string> {
  if (!isValidIdentity(modelId)) {
    return fail(PREFIX_CODES.identityInvalid);
  }
  if (modelId === 'latest') {
    return fail(PREFIX_CODES.modelAliasLiteral);
  }
  return ok(modelId);
}

export function isValidDigest(value: unknown): value is string {
  return typeof value === 'string' && LOWER_HEX_64.test(value);
}

export function isValidGitSha(value: unknown): value is string {
  return typeof value === 'string' && GIT_SHA.test(value);
}

export function isValidEpoch(value: unknown): value is string {
  return typeof value === 'string' && EPOCH_ID.test(value);
}

export function isValidOrdinal(value: unknown): value is number {
  return (
    typeof value === 'number' &&
    Number.isInteger(value) &&
    value >= 0 &&
    value <= MAX_INTERACTION_ORDINAL
  );
}
