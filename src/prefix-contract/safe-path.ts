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

function isArrayIndex(segment: string): boolean {
  return segment.length > 0 && /^[0-9]+$/.test(segment);
}

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
 * Encodes raw path segments (property names or ASCII-decimal array indices,
 * root first) into a sanitized RFC 6901 path, applying the six-rule sanitizer
 * table and the greedy truncation algorithm with final-segment preservation.
 */
export function encodePrefixPath(rawSegments: readonly string[], kind: EnvelopeKind): string {
  const sanitized: string[] = [];
  let belowOpenJson = false;

  for (let i = 0; i < rawSegments.length; i++) {
    const segment = rawSegments[i];
    if (isArrayIndex(segment)) {
      sanitized.push(segment);
      continue;
    }

    if (belowOpenJson) {
      sanitized.push(sanitizeUnknownName(segment));
      continue;
    }

    if (i === 0) {
      if (ENVELOPE_ROOT_KEYS[kind].has(segment)) {
        sanitized.push(escapeRfc6901(segment));
        belowOpenJson = OPEN_JSON_ROOTS.has(segment);
      } else {
        sanitized.push(sanitizeUnknownName(segment));
        belowOpenJson = true;
      }
      continue;
    }

    if (
      i === 2 &&
      rawSegments[0] === 'definitions' &&
      ENVELOPE_ROOT_KEYS[kind].has('definitions')
    ) {
      if (TOOL_WRAPPER_KEYS.has(segment)) {
        sanitized.push(escapeRfc6901(segment));
        belowOpenJson = OPEN_JSON_ROOTS.has(segment);
      } else {
        sanitized.push(sanitizeUnknownName(segment));
        belowOpenJson = true;
      }
      continue;
    }

    sanitized.push(sanitizeUnknownName(segment));
    belowOpenJson = true;
  }

  return truncate(sanitized);
}

/** Truncates a sanitized path so code + ":" + path fits the dual caps. */
function truncate(segments: readonly string[]): string {
  // Evaluate against the longest producer code (prefix-canonical-input-rejected, 31 chars).
  const codePrefixChars = 31;
  const charBudget = MAX_DIAGNOSTIC_MESSAGE_CHARS - codePrefixChars - 1;
  const byteBudget = MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES - codePrefixChars - 1;

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

/** Parses a #48 canonical-json helper path (`$.a.b[0].c`) into raw segments. */
export function parseCanonicalHelperPath(path: string): string[] {
  if (path === '$' || path === '') {
    return [];
  }
  const body = path.startsWith('$.') ? path.slice(2) : path.replace(/^\$/, '');
  const segments: string[] = [];
  const re = /([^.[\]]+)|\[(\d+)\]/g;
  let match: RegExpExecArray | null;
  while ((match = re.exec(body)) !== null) {
    segments.push(match[1] ?? match[2]);
  }
  return segments;
}
