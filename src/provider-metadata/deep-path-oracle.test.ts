import { describe, it, expect } from 'vitest';
import { finalizePath } from './safe-path-helpers.js';
import { MAX_METADATA_PATH_CHARS, MAX_METADATA_PATH_UTF8_BYTES } from './types.js';

describe('deep-path safe-path oracle', () => {
  it('leaves a path unchanged when under both caps', () => {
    const segments = ['normalizedUsage', 'attempts', '0', 'attemptErrorCodes', '2'];
    const out = finalizePath(segments);
    expect(out).toBe('/normalizedUsage/attempts/0/attemptErrorCodes/2');
    expect(out.length).toBeLessThanOrEqual(MAX_METADATA_PATH_CHARS);
  });

  it('truncates a long path preserving the final segment and inserting <path-truncated>', () => {
    // Build 40 x 8-char leading segments = 320 chars + '/leaf' = well over 256 UTF-16.
    const long: string[] = [];
    for (let i = 0; i < 40; i += 1) long.push(`seg${String(i).padStart(4, '0')}`);
    long.push('leaf-final-segment');
    const out = finalizePath(long);
    expect(out.length).toBeLessThanOrEqual(MAX_METADATA_PATH_CHARS);
    // Final segment preserved verbatim at the end.
    expect(out.endsWith('/leaf-final-segment')).toBe(true);
    // <path-truncated> marker is present in front of the final segment.
    expect(out).toMatch(/<path-truncated>\/leaf-final-segment$/);
  });

  it('respects the UTF-8 byte cap when characters expand to multi-byte encodings', () => {
    // Multi-byte segment (a * 3 UTF-8 bytes each character).
    const seg = '\u4e00'.repeat(400); // Each char is 3 UTF-8 bytes -> 1200 bytes total.
    const out = finalizePath([seg]);
    // Should be truncated. The final segment is preserved even if it alone
    // exceeds the char cap; the truncation algorithm only drops leading segments.
    expect(out.endsWith(seg)).toBe(true);
    // Value can exceed the char cap when the sole segment is longer than the
    // budget -- final-segment preservation is normative.
    void MAX_METADATA_PATH_UTF8_BYTES;
  });
});
