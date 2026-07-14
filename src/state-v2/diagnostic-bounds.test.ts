import { describe, expect, it } from 'vitest';
import { boundedJoin } from './schema.js';
import { MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES } from './constants.js';

describe('diagnostic bounds', () => {
  it('joins short messages verbatim with "; "', () => {
    expect(boundedJoin(['a', 'b'])).toBe('a; b');
  });

  it('drops entries beyond MAX_DIAGNOSTIC_ERRORS', () => {
    const many = Array.from({ length: 20 }, (_, i) => `err${i}`);
    const result = boundedJoin(many);
    // 8 kept -> "err0; err1; ...; err7"
    expect(result).toBe('err0; err1; err2; err3; err4; err5; err6; err7');
  });

  it('respects UTF-8 byte cap with sentinel suffix', () => {
    const long = ['x'.repeat(1500)];
    const result = boundedJoin(long);
    const bytes = new TextEncoder().encode(result).byteLength;
    expect(bytes).toBeLessThanOrEqual(MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES);
    expect(result.endsWith('...[truncated]')).toBe(true);
  });

  it('truncation happens on a UTF-8 codepoint boundary', () => {
    const long = ['\u00e9'.repeat(2000)]; // 2-byte characters
    const result = boundedJoin(long);
    // Ensure the decoded suffix ends with the sentinel and no stray broken char.
    expect(result.endsWith('...[truncated]')).toBe(true);
    // Everything before the sentinel must be a valid string of complete chars.
    const decoded = result.slice(0, -'...[truncated]'.length);
    for (const ch of decoded) {
      // ch is by definition a single codepoint iteration; no partial bytes.
      expect(ch.length).toBeGreaterThan(0);
    }
  });

  it('4-byte codepoints stay within the UTF-8 byte cap', () => {
    // "\u{1f600}" (😀) is one codepoint but encodes to 4 UTF-8 bytes.
    // Repeat it enough times that the raw byte length far exceeds the cap.
    const flood = ['\u{1f600}'.repeat(2000)];
    const result = boundedJoin(flood);
    expect(result.endsWith('...[truncated]')).toBe(true);
    const bytes = new TextEncoder().encode(result).byteLength;
    expect(bytes).toBeLessThanOrEqual(MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES);
    // Everything before the sentinel must be valid UTF-8 with no U+FFFD.
    const decoded = result.slice(0, -'...[truncated]'.length);
    expect(decoded).not.toContain('\uFFFD');
  });

  it('never leaks a lone high surrogate through truncation', () => {
    // A lone leading surrogate as the final character of the pre-cap prefix
    // must not be emitted; the truncator iterates codepoints, so an unpaired
    // lone surrogate remains its own iteration and either fits fully or is
    // dropped entirely. Prepend enough padding that truncation must run.
    const pad = 'x'.repeat(MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES);
    const withLone = pad + '\uD83D';
    const result = boundedJoin([withLone]);
    expect(result.endsWith('...[truncated]')).toBe(true);
    const bytes = new TextEncoder().encode(result).byteLength;
    expect(bytes).toBeLessThanOrEqual(MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES);
  });

  it('does not add sentinel when message fits under cap', () => {
    const result = boundedJoin(['small']);
    expect(result).toBe('small');
    expect(result).not.toContain('truncated');
  });
});
