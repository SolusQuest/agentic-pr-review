import { PREFIX_CODES, fail, ok, type PrefixResult } from './result.js';

/** Shared frozen identity domains (issue #50; design contract frozen vocabulary). */

export const MAX_IDENTITY_UTF8_BYTES = 256;
export const MAX_INTERACTION_ORDINAL = 1_000_000;

const LOWER_HEX_64 = /^[a-f0-9]{64}$/;
const GIT_SHA = /^([a-f0-9]{40}|[a-f0-9]{64})$/;
const EPOCH_ID = /^[A-Za-z0-9_-]{22}$/;

/** Shared identity-string domain: well-formed UTF-16, non-empty, bounded UTF-8, no controls. */
export function isValidIdentity(value: unknown): value is string {
  if (typeof value !== 'string' || value.length === 0) {
    return false;
  }
  let utf8Bytes = 0;
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i);
    if (code <= 0x1f || code === 0x7f) {
      return false;
    }
    if (code <= 0x7f) {
      utf8Bytes += 1;
    } else if (code <= 0x7ff) {
      utf8Bytes += 2;
    } else if (code >= 0xd800 && code <= 0xdbff) {
      if (i + 1 >= value.length) {
        return false;
      }
      const low = value.charCodeAt(i + 1);
      if (low < 0xdc00 || low > 0xdfff) {
        return false;
      }
      utf8Bytes += 4;
      i++;
    } else if (code >= 0xdc00 && code <= 0xdfff) {
      return false;
    } else {
      utf8Bytes += 3;
    }
    if (utf8Bytes > MAX_IDENTITY_UTF8_BYTES) {
      return false;
    }
  }
  return true;
}

export function validateIdentity(value: unknown): PrefixResult<string> {
  return isValidIdentity(value) ? ok(value) : fail(PREFIX_CODES.identityInvalid);
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
