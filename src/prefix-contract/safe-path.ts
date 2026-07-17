/**
 * Safe diagnostic path encoding (issue #50): mirrors the C# PrefixSafePath.
 * Unknown property names are never echoed into diagnostics.
 */

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
  adapter: new Set(['adapterBuildVersion', 'capabilityProfileVersion', 'schemaVersion']),
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
 * Unified canonical-domain + structural-bounds scan (issue #50): a single
 * deterministic traversal (arrays ascending, object keys in unsigned UTF-16
 * order) with first-defect-wins semantics and an ancestor-only cycle guard,
 * mirroring the C# JsonElementCanonicalizer.
 */
export type CanonicalScanViolation = {
  readonly segments: readonly PrefixPathSegment[];
  readonly reason:
    | 'lone-surrogate'
    | 'non-finite'
    | 'cyclic'
    | 'non-plain-object'
    | 'symbol-key'
    | 'accessor-property'
    | 'non-enumerable-property'
    | 'depth-exceeded'
    | 'property-count-exceeded'
    | 'array-length-exceeded';
};

export interface CanonicalScanBounds {
  readonly maxDepth: number;
  readonly maxProperties: number;
  readonly maxArrayItems: number;
}

interface ScanFrame {
  readonly value: unknown;
  readonly segments: readonly PrefixPathSegment[];
  readonly depth: number;
  readonly exit: boolean;
}

export function scanCanonicalDomainAndBounds(
  root: unknown,
  bounds: CanonicalScanBounds,
): CanonicalScanViolation | null {
  const ancestors = new Set<object>();
  const stack: ScanFrame[] = [{ value: root, segments: [], depth: 0, exit: false }];

  while (stack.length > 0) {
    const frame = stack.pop()!;
    const { value, segments, depth, exit } = frame;

    if (exit) {
      ancestors.delete(value as object);
      continue;
    }

    if (typeof value === 'string') {
      if (hasUnpairedSurrogate(value)) {
        return { segments, reason: 'lone-surrogate' };
      }
      continue;
    }
    if (typeof value === 'number') {
      if (!Number.isFinite(value)) {
        return { segments, reason: 'non-finite' };
      }
      continue;
    }
    if (typeof value !== 'object' || value === null) {
      continue;
    }

    if (ancestors.has(value)) {
      return { segments, reason: 'cyclic' };
    }

    if (Array.isArray(value)) {
      if (depth > bounds.maxDepth) {
        return { segments, reason: 'depth-exceeded' };
      }
      if (value.length > bounds.maxArrayItems) {
        return { segments, reason: 'array-length-exceeded' };
      }
      ancestors.add(value);
      stack.push({ value, segments, depth, exit: true });
      for (let i = value.length - 1; i >= 0; i--) {
        stack.push({
          value: value[i],
          segments: [...segments, { name: String(i), isIndex: true }],
          depth: depth + 1,
          exit: false,
        });
      }
      continue;
    }

    const proto = Object.getPrototypeOf(value);
    if (proto !== Object.prototype && proto !== null) {
      return { segments, reason: 'non-plain-object' };
    }
    if (Object.getOwnPropertySymbols(value).length > 0) {
      return { segments, reason: 'symbol-key' };
    }

    if (depth > bounds.maxDepth) {
      return { segments, reason: 'depth-exceeded' };
    }
    const names = Object.getOwnPropertyNames(value);
    if (names.length > bounds.maxProperties) {
      return { segments, reason: 'property-count-exceeded' };
    }

    // Recurse children in unsigned UTF-16 order; property-name defects are
    // checked in that same order (first defect wins).
    const sorted = [...names].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
    ancestors.add(value);
    stack.push({ value, segments, depth, exit: true });

    // Exit children in reverse so they pop in UTF-16 order. A property-name
    // violation must abort immediately, so evaluate names eagerly here and
    // only push children when the whole key set is clean.
    const childFrames: ScanFrame[] = [];
    let nameViolation: CanonicalScanViolation | null = null;
    for (const name of sorted) {
      if (hasUnpairedSurrogate(name)) {
        nameViolation = { segments: [...segments, { name }], reason: 'lone-surrogate' };
        break;
      }
      const descriptor = Object.getOwnPropertyDescriptor(value, name);
      if (descriptor === undefined) {
        continue;
      }
      if ('get' in descriptor || 'set' in descriptor) {
        nameViolation = { segments: [...segments, { name }], reason: 'accessor-property' };
        break;
      }
      if (!descriptor.enumerable) {
        nameViolation = { segments: [...segments, { name }], reason: 'non-enumerable-property' };
        break;
      }
      childFrames.push({
        value: (value as Record<string, unknown>)[name],
        segments: [...segments, { name }],
        depth: depth + 1,
        exit: false,
      });
    }

    if (nameViolation !== null) {
      ancestors.delete(value);
      // Remove the exit frame we just pushed.
      stack.pop();
      return nameViolation;
    }

    for (let i = childFrames.length - 1; i >= 0; i--) {
      stack.push(childFrames[i]);
    }
  }

  return null;
}
