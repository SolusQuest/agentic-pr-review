/**
 * Small shared helpers for provider-metadata (issue #51). Kept separate so the
 * schema stage and semantic validator both consume the same predicates
 * without pulling in the entire shared-safe-path module.
 */

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

const encoder = new TextEncoder();

export function utf8ByteLength(s: string): number {
  return encoder.encode(s).byteLength;
}
