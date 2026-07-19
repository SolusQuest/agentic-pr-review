import { MAX_SENSITIVE_VALUES, MAX_SENSITIVE_VALUES_TOTAL_UTF8_BYTES } from './constants.js';
import { LiveRuntimeInvocationError } from './errors.js';

export function copySensitiveValues(values: readonly string[] | undefined): readonly Uint8Array[] {
  const source = values ? [...values] : [];
  if (source.length > MAX_SENSITIVE_VALUES || source.some((value) => value.length === 0)) {
    throw new LiveRuntimeInvocationError({
      kind: 'options-invalid',
      message: 'sensitiveValues is empty or exceeds its entry cap.',
    });
  }
  const encoded = source.map((value) => new TextEncoder().encode(value));
  const total = encoded.reduce((sum, value) => sum + value.byteLength, 0);
  if (total > MAX_SENSITIVE_VALUES_TOTAL_UTF8_BYTES || new Set(source).size !== source.length) {
    throw new LiveRuntimeInvocationError({
      kind: 'options-invalid',
      message: 'sensitiveValues exceeds its byte cap or contains duplicates.',
    });
  }
  return encoded.map((value) => new Uint8Array(value));
}

export function assertPrivateBytes(
  channels: readonly Uint8Array[],
  sensitiveValues: readonly Uint8Array[],
): void {
  for (const channel of channels) {
    for (const secret of sensitiveValues) {
      if (secret.byteLength > 0 && contains(channel, secret)) {
        throw new LiveRuntimeInvocationError({
          kind: 'privacy-violation',
          message: 'Sensitive content crossed the live runtime boundary.',
        });
      }
    }
  }
}

function contains(haystack: Uint8Array, needle: Uint8Array): boolean {
  outer: for (let start = 0; start <= haystack.length - needle.length; start += 1) {
    for (let offset = 0; offset < needle.length; offset += 1) {
      if (haystack[start + offset] !== needle[offset]) continue outer;
    }
    return true;
  }
  return false;
}
