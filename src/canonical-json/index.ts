/**
 * Canonical JSON (RFC 8785 JCS) helper.
 *
 * Owned initially by issue #48. Sibling issues #49 / #50 / #51 consume this
 * helper rather than implementing a second canonicalizer. If the helper needs
 * to be swapped for a battle-tested library later, the swap happens in place
 * and the export shape stays the same.
 *
 * Accepted input domain: `CanonicalJsonValue`. Everything else is rejected
 * with a typed `CanonicalJsonInputError`.
 *
 * Output: RFC 8785 canonical UTF-8 JSON bytes. No BOM. No whitespace. No
 * trailing newline. IEEE-754 negative zero serializes as `0` per RFC 8785.
 */

export const CANONICAL_JSON_VERSION = 1 as const;

export type CanonicalJsonValue =
  | null
  | boolean
  | number
  | string
  | readonly CanonicalJsonValue[]
  | { readonly [key: string]: CanonicalJsonValue };

export class CanonicalJsonInputError extends Error {
  readonly path: string;
  readonly reason: string;

  constructor(reason: string, path: string) {
    super(`canonical JSON input rejected at ${path}: ${reason}`);
    this.name = 'CanonicalJsonInputError';
    this.path = path;
    this.reason = reason;
  }
}

/**
 * Canonicalize a `CanonicalJsonValue` into RFC 8785 canonical UTF-8 bytes.
 *
 * The primary public signature accepts `CanonicalJsonValue`. A secondary
 * overload accepts `unknown` so caller-side unknown or `JSON.parse`
 * values can be fed in without a manual cast; runtime rejection still
 * fires for any value outside the canonical accepted domain.
 */
export function canonicalJsonBytes(value: CanonicalJsonValue): Uint8Array;
export function canonicalJsonBytes(value: unknown): Uint8Array;
export function canonicalJsonBytes(value: unknown): Uint8Array {
  const seen = new WeakSet<object>();
  const text = encodeValue(value, '$', seen);
  return new TextEncoder().encode(text);
}

function encodeValue(value: unknown, path: string, seen: WeakSet<object>): string {
  if (value === null) return 'null';
  if (typeof value === 'boolean') return value ? 'true' : 'false';
  if (typeof value === 'number') return encodeNumber(value, path);
  if (typeof value === 'string') return encodeString(value, path);
  if (Array.isArray(value)) return encodeArray(value, path, seen);
  if (typeof value === 'object') return encodeObject(value, path, seen);

  // Rejected runtime types
  const type = value === undefined ? 'undefined' : typeof value;
  const label =
    type === 'bigint'
      ? 'bigint values are not permitted in canonical JSON'
      : type === 'symbol'
        ? 'symbol values are not permitted in canonical JSON'
        : type === 'function'
          ? 'function values are not permitted in canonical JSON'
          : 'undefined values are not permitted in canonical JSON';
  throw new CanonicalJsonInputError(label, path);
}

function encodeNumber(value: number, path: string): string {
  if (Number.isNaN(value)) {
    throw new CanonicalJsonInputError('NaN is not a JSON number', path);
  }
  if (!Number.isFinite(value)) {
    throw new CanonicalJsonInputError('Infinity is not a JSON number', path);
  }
  // RFC 8785: negative zero serializes as `0`.
  if (Object.is(value, -0)) return '0';
  // Numbers are emitted using ECMAScript ToString, which is the JCS
  // reference algorithm on JSON's number domain.
  return String(value);
}

function encodeString(value: string, path: string): string {
  return escapeJsonString(value, path);
}

function escapeJsonString(value: string, path: string): string {
  // Reject lone surrogates (invalid UTF-16 sequences); ECMA-262 well-formed
  // string check.
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i);
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = i + 1 < value.length ? value.charCodeAt(i + 1) : 0;
      if (next < 0xdc00 || next > 0xdfff) {
        throw new CanonicalJsonInputError('lone high surrogate', path);
      }
      i += 1;
    } else if (code >= 0xdc00 && code <= 0xdfff) {
      throw new CanonicalJsonInputError('lone low surrogate', path);
    }
  }

  let out = '"';
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i);
    switch (code) {
      case 0x22:
        out += '\\"';
        continue;
      case 0x5c:
        out += '\\\\';
        continue;
      case 0x08:
        out += '\\b';
        continue;
      case 0x09:
        out += '\\t';
        continue;
      case 0x0a:
        out += '\\n';
        continue;
      case 0x0c:
        out += '\\f';
        continue;
      case 0x0d:
        out += '\\r';
        continue;
      default:
        break;
    }
    if (code < 0x20) {
      out += '\\u' + code.toString(16).padStart(4, '0');
    } else {
      out += value[i];
    }
  }
  out += '"';
  return out;
}

function encodeArray(value: readonly unknown[], path: string, seen: WeakSet<object>): string {
  const array = value as unknown[] & object;
  if (seen.has(array)) {
    throw new CanonicalJsonInputError('cyclic structure', path);
  }
  seen.add(array);
  try {
    if (Object.getPrototypeOf(array) !== Array.prototype) {
      throw new CanonicalJsonInputError('array with non-Array.prototype', path);
    }
    // Detect symbol keys.
    if (Object.getOwnPropertySymbols(array).length > 0) {
      throw new CanonicalJsonInputError('array with symbol-keyed own property', path);
    }
    // Iterate own string keys; only `length` may be non-enumerable, and any
    // string key other than a numeric index in [0, length) is forbidden.
    const ownNames = Object.getOwnPropertyNames(array);
    for (const name of ownNames) {
      if (name === 'length') continue;
      const desc = Object.getOwnPropertyDescriptor(array, name);
      if (!desc) continue;
      if ('get' in desc || 'set' in desc) {
        throw new CanonicalJsonInputError(
          `array accessor property at index or key '${name}'`,
          path,
        );
      }
      if (!isNonNegativeIntegerString(name) || Number(name) >= array.length) {
        throw new CanonicalJsonInputError(`array extra own property '${name}'`, path);
      }
      if (!desc.enumerable) {
        throw new CanonicalJsonInputError(
          `array non-enumerable index '${name}' is not allowed`,
          path,
        );
      }
    }
    // Length property must be the default non-enumerable data descriptor.
    const lenDesc = Object.getOwnPropertyDescriptor(array, 'length');
    if (!lenDesc || lenDesc.enumerable || 'get' in lenDesc || 'set' in lenDesc) {
      throw new CanonicalJsonInputError('array length descriptor is not standard', path);
    }
    // Reject sparse arrays.
    for (let i = 0; i < array.length; i++) {
      const idx = String(i);
      if (!Object.prototype.hasOwnProperty.call(array, idx)) {
        throw new CanonicalJsonInputError(`sparse array: missing index ${i}`, path);
      }
    }

    const parts: string[] = [];
    for (let i = 0; i < array.length; i++) {
      parts.push(encodeValue(array[i], `${path}[${i}]`, seen));
    }
    return '[' + parts.join(',') + ']';
  } finally {
    seen.delete(array);
  }
}

function encodeObject(value: object, path: string, seen: WeakSet<object>): string {
  if (seen.has(value)) {
    throw new CanonicalJsonInputError('cyclic structure', path);
  }
  seen.add(value);
  try {
    const proto = Object.getPrototypeOf(value);
    if (proto !== Object.prototype && proto !== null) {
      throw new CanonicalJsonInputError('non-plain object', path);
    }
    if (Object.getOwnPropertySymbols(value).length > 0) {
      throw new CanonicalJsonInputError('symbol-keyed own property', path);
    }
    const keys: string[] = [];
    for (const name of Object.getOwnPropertyNames(value)) {
      const desc = Object.getOwnPropertyDescriptor(value, name);
      if (!desc) continue;
      if ('get' in desc || 'set' in desc) {
        throw new CanonicalJsonInputError(`accessor property '${name}'`, path);
      }
      if (!desc.enumerable) {
        throw new CanonicalJsonInputError(`non-enumerable own property '${name}'`, path);
      }
      // Also enforce that property names themselves are well-formed UTF-16
      // (no lone surrogate). encodeString will reject the name.
      keys.push(name);
    }
    // RFC 8785 sorts keys by UTF-16 code units.
    keys.sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
    const parts: string[] = [];
    for (const key of keys) {
      const encodedKey = escapeJsonString(key, `${path}.${key}`);
      const child = (value as Record<string, unknown>)[key];
      const encodedChild = encodeValue(child, `${path}.${key}`, seen);
      parts.push(`${encodedKey}:${encodedChild}`);
    }
    return '{' + parts.join(',') + '}';
  } finally {
    seen.delete(value);
  }
}

function isNonNegativeIntegerString(name: string): boolean {
  return /^(?:0|[1-9]\d*)$/.test(name);
}
