/**
 * Safe diagnostic path encoding (issue #50): mirrors the C# PrefixSafePath.
 * Unknown property names are never echoed into diagnostics.
 */

import { canonicalViolationReason, isCanonicalViolationMarker } from './deep-snapshot.js';

const EMPTY_NAME = '<empty-name>';
const INVALID_UTF16 = '<invalid-utf16>';
const INVALID_NUL = '<invalid-nul>';
const INVALID_CONTROL = '<invalid-control>';
const UNTRUSTED_PROPERTY = '<untrusted-property>';
const PATH_TRUNCATED = '<path-truncated>';

export const MAX_DIAGNOSTIC_MESSAGE_CHARS = 256;
export const MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES = 1024;

export type EnvelopeKind = 'template' | 'policy' | 'tools' | 'cacheConfig' | 'adapter';

/**
 * A structured path segment. Only segments produced at an actual array
 * position may set isIndex; a numeric property name is still a name.
 */
export interface PrefixPathSegment {
  readonly name: string;
  readonly isIndex?: boolean;
}

const ENVELOPE_ROOT_KEYS: Record<EnvelopeKind, ReadonlySet<string>> = {
  template: new Set(['definition', 'schemaVersion', 'templateVersion']),
  policy: new Set(['constraints', 'instructions', 'policyVersion', 'schemaVersion']),
  tools: new Set(['definitions', 'schemaVersion', 'toolsetVersion']),
  cacheConfig: new Set([
    'cacheConfigVersion',
    'eligibility',
    'markerPolicy',
    'schemaVersion',
    'statelessMode',
  ]),
  adapter: new Set([
    'adapterBuildVersion',
    'capabilityProfileVersion',
    'requestContractSha256',
    'schemaVersion',
  ]),
};

const TOOL_WRAPPER_KEYS = new Set(['description', 'inputSchema', 'name', 'policyMetadata']);
const OPEN_JSON_ROOTS = new Set(['constraints', 'definition', 'inputSchema', 'policyMetadata']);

function hasUnpairedSurrogate(value: string): boolean {
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i);
    if (code >= 0xd800 && code <= 0xdbff) {
      if (i + 1 >= value.length) {
        return true;
      }
      const next = value.charCodeAt(i + 1);
      if (next < 0xdc00 || next > 0xdfff) {
        return true;
      }
      i++;
    } else if (code >= 0xdc00 && code <= 0xdfff) {
      return true;
    }
  }
  return false;
}

function sanitizeUnknownName(name: string): string {
  if (name.length === 0) {
    return EMPTY_NAME;
  }
  if (hasUnpairedSurrogate(name)) {
    return INVALID_UTF16;
  }
  if (name.includes('\u0000')) {
    return INVALID_NUL;
  }
  for (let i = 0; i < name.length; i++) {
    const code = name.charCodeAt(i);
    if (code <= 0x1f || code === 0x7f) {
      return INVALID_CONTROL;
    }
  }
  return UNTRUSTED_PROPERTY;
}

function escapeRfc6901(name: string): string {
  return name.replace(/~/g, '~0').replace(/\//g, '~1');
}

function utf8Length(value: string): number {
  return new TextEncoder().encode(value).byteLength;
}

/**
 * Encodes structured path segments into a sanitized RFC 6901 path, applying
 * the six-rule sanitizer table and the greedy truncation algorithm with
 * final-segment preservation. The budget derives from the actual diagnostic
 * code; the empty segment list yields the root path "".
 */
export function encodePrefixPath(
  rawSegments: readonly PrefixPathSegment[],
  kind: EnvelopeKind,
  code: string,
): string {
  if (rawSegments.length === 0) {
    return '';
  }

  const sanitized: string[] = [];
  let belowOpenJson = false;

  for (let i = 0; i < rawSegments.length; i++) {
    const segment = rawSegments[i];
    if (segment.isIndex === true) {
      sanitized.push(segment.name);
      continue;
    }

    if (belowOpenJson) {
      sanitized.push(sanitizeUnknownName(segment.name));
      continue;
    }

    if (i === 0) {
      if (ENVELOPE_ROOT_KEYS[kind].has(segment.name)) {
        sanitized.push(escapeRfc6901(segment.name));
        belowOpenJson = OPEN_JSON_ROOTS.has(segment.name);
      } else {
        sanitized.push(sanitizeUnknownName(segment.name));
        belowOpenJson = true;
      }
      continue;
    }

    if (
      i === 2 &&
      rawSegments[0].name === 'definitions' &&
      ENVELOPE_ROOT_KEYS[kind].has('definitions')
    ) {
      if (TOOL_WRAPPER_KEYS.has(segment.name)) {
        sanitized.push(escapeRfc6901(segment.name));
        belowOpenJson = OPEN_JSON_ROOTS.has(segment.name);
      } else {
        sanitized.push(sanitizeUnknownName(segment.name));
        belowOpenJson = true;
      }
      continue;
    }

    sanitized.push(sanitizeUnknownName(segment.name));
    belowOpenJson = true;
  }

  return truncate(sanitized, code);
}

/** Truncates a sanitized path so code + ":" + path fits the dual caps for the actual code. */
function truncate(segments: readonly string[], code: string): string {
  const charBudget = MAX_DIAGNOSTIC_MESSAGE_CHARS - code.length - 1;
  const byteBudget = MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES - utf8Length(code) - 1;

  const joined = '/' + segments.join('/');
  if (joined.length <= charBudget && utf8Length(joined) <= byteBudget) {
    return joined;
  }

  const finalSegment = segments.length > 0 ? segments[segments.length - 1] : '';
  const reserved = ('/' + finalSegment).length + ('/' + PATH_TRUNCATED).length;
  const reservedBytes = utf8Length('/' + finalSegment) + utf8Length('/' + PATH_TRUNCATED);

  let prefix = '';
  for (let i = 0; i < segments.length - 1; i++) {
    const candidate = prefix + '/' + segments[i];
    if (
      candidate.length > charBudget - reserved ||
      utf8Length(candidate) > byteBudget - reservedBytes
    ) {
      break;
    }
    prefix = candidate;
  }

  return prefix + '/' + PATH_TRUNCATED + '/' + finalSegment;
}

/**
 * Canonical-domain scan (issue #50): a single recursive depth-first
 * traversal — arrays ascending, object keys in unsigned UTF-16 order,
 * first-defect-wins — with an ancestor-only cycle guard, mirroring the C#
 * JsonElementCanonicalizer. Structural bounds are owned by the structure
 * stage; this scan owns only canonical-domain rejections.
 */
export type CanonicalScanViolation = {
  readonly segments: readonly PrefixPathSegment[];
  readonly reason:
    | 'lone-surrogate'
    | 'non-finite'
    | 'cyclic'
    | 'non-json-type'
    | 'non-plain-object'
    | 'symbol-key'
    | 'accessor-property'
    | 'non-enumerable-property';
};

export function scanCanonicalDomainAndBounds(root: unknown): CanonicalScanViolation | null {
  const validated = new WeakSet<object>();
  return scan(root, [], new Set<object>());

  function scan(
    value: unknown,
    segments: PrefixPathSegment[],
    ancestors: Set<object>,
  ): CanonicalScanViolation | null {
    if (typeof value === 'string') {
      return hasUnpairedSurrogate(value) ? { segments, reason: 'lone-surrogate' } : null;
    }
    if (typeof value === 'number') {
      return Number.isFinite(value) ? null : { segments, reason: 'non-finite' };
    }
    if (typeof value === 'boolean' || value === null) {
      return null;
    }
    if (typeof value !== 'object') {
      // undefined, bigint, symbol, function: outside the JSON domain.
      return { segments, reason: 'non-json-type' };
    }

    if (ancestors.has(value)) {
      return { segments, reason: 'cyclic' };
    }
    if (isCanonicalViolationMarker(value)) {
      return { segments, reason: canonicalViolationReason(value) };
    }
    if (validated.has(value)) {
      // The descriptor snapshot preserves legal DAG aliases. Once a snapshot
      // node has been fully validated, later non-ancestor occurrences have
      // identical content and need not be rescanned.
      return null;
    }
    ancestors.add(value);
    try {
      if (Array.isArray(value)) {
        if (Object.getPrototypeOf(value) !== Array.prototype) {
          return { segments, reason: 'non-plain-object' };
        }
        if (Object.getOwnPropertySymbols(value).length > 0) {
          return { segments, reason: 'symbol-key' };
        }
        const ownNames = Object.getOwnPropertyNames(value);
        for (const name of ownNames) {
          if (name === 'length') {
            continue;
          }
          if (!/^\d+$/.test(name) || Number(name) >= value.length) {
            return { segments: [...segments, { name }], reason: 'non-enumerable-property' };
          }
        }
        const lengthDescriptor = Object.getOwnPropertyDescriptor(value, 'length');
        if (
          lengthDescriptor === undefined ||
          lengthDescriptor.enumerable ||
          'get' in lengthDescriptor ||
          'set' in lengthDescriptor
        ) {
          return { segments, reason: 'non-plain-object' };
        }
        for (let i = 0; i < value.length; i++) {
          const descriptor = Object.getOwnPropertyDescriptor(value, String(i));
          if (descriptor === undefined) {
            return {
              segments: [...segments, { name: String(i), isIndex: true }],
              reason: 'non-enumerable-property',
            };
          }
          if ('get' in descriptor || 'set' in descriptor) {
            return {
              segments: [...segments, { name: String(i), isIndex: true }],
              reason: 'accessor-property',
            };
          }
          if (!descriptor.enumerable) {
            return {
              segments: [...segments, { name: String(i), isIndex: true }],
              reason: 'non-enumerable-property',
            };
          }
          const child = scan(
            descriptor.value,
            [...segments, { name: String(i), isIndex: true }],
            ancestors,
          );
          if (child !== null) {
            return child;
          }
        }
        validated.add(value);
        return null;
      }

      const proto = Object.getPrototypeOf(value);
      if (proto !== Object.prototype && proto !== null) {
        return { segments, reason: 'non-plain-object' };
      }
      if (Object.getOwnPropertySymbols(value).length > 0) {
        return { segments, reason: 'symbol-key' };
      }
      const sorted = Object.getOwnPropertyNames(value).sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
      for (const name of sorted) {
        if (hasUnpairedSurrogate(name)) {
          return { segments: [...segments, { name }], reason: 'lone-surrogate' };
        }
        const descriptor = Object.getOwnPropertyDescriptor(value, name);
        if (descriptor === undefined) {
          continue;
        }
        if ('get' in descriptor || 'set' in descriptor) {
          return { segments: [...segments, { name }], reason: 'accessor-property' };
        }
        if (!descriptor.enumerable) {
          return { segments: [...segments, { name }], reason: 'non-enumerable-property' };
        }
        const child = scan(descriptor.value, [...segments, { name }], ancestors);
        if (child !== null) {
          return child;
        }
      }
      validated.add(value);
      return null;
    } finally {
      ancestors.delete(value);
    }
  }
}
