/**
 * Workstream-local safe-diagnostic-path helpers. `finalizePath` enforces the
 * issue #51 `MetadataError.path` bounds (`MAX_METADATA_PATH_CHARS = 256` UTF-16
 * code units and `MAX_METADATA_PATH_UTF8_BYTES = 1024`) using the frozen
 * final-segment-preserving truncation from the shared safe-diagnostic-path
 * subsection. Segments are ALREADY sanitized (via the shared resolver /
 * sanitizer) before being handed to this helper; this module never turns a
 * caller-controlled property name into a wire segment.
 */

import { MAX_METADATA_PATH_CHARS, MAX_METADATA_PATH_UTF8_BYTES } from './types.js';

const encoder = new TextEncoder();

export function containsOtherControlChar(s: string): boolean {
  for (let i = 0; i < s.length; i += 1) {
    const c = s.charCodeAt(i);
    if (c === 0x0000) continue;
    if ((c >= 0x0001 && c <= 0x001f) || c === 0x007f) return true;
  }
  return false;
}

export function rfc6901Escape(segment: string): string {
  return segment.replace(/~/g, '~0').replace(/\//g, '~1');
}

export function utf8ByteLength(s: string): number {
  return encoder.encode(s).byteLength;
}

const PATH_TRUNCATED_SEGMENT = '/<path-truncated>';

/**
 * Compose a JSON-Pointer-style safe path from pre-sanitized segments and apply
 * the shared `MetadataError.path` bounds. If the composed path fits within
 * both caps it is returned unchanged; otherwise the leading segments are
 * dropped from the front (preserving the final segment) and `<path-truncated>`
 * is prepended to the retained suffix per the shared algorithm.
 */
export function finalizePath(segments: readonly string[]): string {
  if (segments.length === 0) return '';
  const full = '/' + segments.join('/');
  if (
    full.length <= MAX_METADATA_PATH_CHARS &&
    utf8ByteLength(full) <= MAX_METADATA_PATH_UTF8_BYTES
  ) {
    return full;
  }
  return truncatePath(segments);
}

function truncatePath(segments: readonly string[]): string {
  const finalSegment = segments[segments.length - 1]!;
  const leading = segments.slice(0, segments.length - 1);
  const suffix = PATH_TRUNCATED_SEGMENT + '/' + finalSegment;
  const suffixChars = suffix.length;
  const suffixBytes = utf8ByteLength(suffix);
  const budgetChars = MAX_METADATA_PATH_CHARS - suffixChars;
  const budgetBytes = MAX_METADATA_PATH_UTF8_BYTES - suffixBytes;

  let accepted = '';
  let acceptedBytes = 0;
  for (const seg of leading) {
    const chunk = '/' + seg;
    const chunkBytes = utf8ByteLength(chunk);
    if (accepted.length + chunk.length > budgetChars || acceptedBytes + chunkBytes > budgetBytes) {
      break;
    }
    accepted += chunk;
    acceptedBytes += chunkBytes;
  }
  return accepted + suffix;
}
