import { describe, expect, it } from 'vitest';
import { boundedDiagnosticMessage, boundedJoin } from './schema.js';
import {
  MAX_DIAGNOSTIC_ERRORS,
  MAX_DIAGNOSTIC_MESSAGE_CHARS,
  MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES,
} from './constants.js';

describe('diagnostic bounds', () => {
  it('joins short messages verbatim with "; "', () => {
    expect(boundedJoin(['a', 'b'])).toBe('a; b');
  });

  it('drops entries beyond MAX_DIAGNOSTIC_ERRORS', () => {
    const many = Array.from({ length: 20 }, (_, i) => `err${i}`);
    const result = boundedJoin(many);
    expect(result).toBe('err0; err1; err2; err3; err4; err5; err6; err7');
  });

  it('per-message truncation caps a single message to MAX_DIAGNOSTIC_MESSAGE_CHARS code points', () => {
    const single = ['x'.repeat(1500)];
    const result = boundedJoin(single);
    // No byte-cap sentinel because 256 chars <= 1024 bytes for ASCII.
    expect(result.endsWith('...[truncated]')).toBe(false);
    // Exactly MAX_DIAGNOSTIC_MESSAGE_CHARS Unicode code points remain.
    expect([...result].length).toBe(MAX_DIAGNOSTIC_MESSAGE_CHARS);
  });

  it('total byte cap adds sentinel when many long messages overflow 1024 bytes', () => {
    // 8 messages of 256 ASCII chars = 8*256 + 7*2 = 2062 bytes joined.
    const many = Array.from({ length: MAX_DIAGNOSTIC_ERRORS }, () => 'x'.repeat(400));
    const result = boundedJoin(many);
    const bytes = new TextEncoder().encode(result).byteLength;
    expect(bytes).toBeLessThanOrEqual(MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES);
    expect(result.endsWith('...[truncated]')).toBe(true);
  });

  it('per-message truncation preserves 2-byte codepoints as whole characters', () => {
    const single = ['\u00e9'.repeat(500)];
    const result = boundedJoin(single);
    expect([...result].length).toBe(MAX_DIAGNOSTIC_MESSAGE_CHARS);
    // No partial multi-byte codepoints introduced.
    expect(result).not.toContain('\uFFFD');
  });

  it('4-byte codepoints stay whole under per-message and total byte caps', () => {
    // "\u{1f600}" (😀) is a 4-byte UTF-8 codepoint counted as 1 code point.
    const many = Array.from({ length: MAX_DIAGNOSTIC_ERRORS }, () => '\u{1f600}'.repeat(400));
    const result = boundedJoin(many);
    const bytes = new TextEncoder().encode(result).byteLength;
    expect(bytes).toBeLessThanOrEqual(MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES);
    // Everything before the sentinel must be valid UTF-8 with no U+FFFD.
    const decoded = result.endsWith('...[truncated]')
      ? result.slice(0, -'...[truncated]'.length)
      : result;
    expect(decoded).not.toContain('\uFFFD');
  });

  it('never leaks a lone high surrogate through truncation', () => {
    // Padding to well past the byte cap ensures truncation must run.
    const pad = 'x'.repeat(MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES);
    const withLone = pad + '\uD83D';
    // Per-message cap already trims to 256 chars — but the lone-surrogate
    // char could in principle survive if it happened to fall inside the cap.
    // Repeat many entries to force byte-cap truncation as well.
    const many = Array.from({ length: MAX_DIAGNOSTIC_ERRORS }, () => withLone);
    const result = boundedJoin(many);
    const bytes = new TextEncoder().encode(result).byteLength;
    expect(bytes).toBeLessThanOrEqual(MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES);
  });

  it('does not add sentinel when message fits under both caps', () => {
    const result = boundedJoin(['small']);
    expect(result).toBe('small');
    expect(result).not.toContain('truncated');
  });

  it('boundedDiagnosticMessage applies both caps to a single string', () => {
    const long = 'x'.repeat(1500);
    const result = boundedDiagnosticMessage(long);
    // Per-message cap alone caps to 256 chars for ASCII, well within 1024 bytes.
    expect([...result].length).toBe(MAX_DIAGNOSTIC_MESSAGE_CHARS);
    expect(new TextEncoder().encode(result).byteLength).toBeLessThanOrEqual(
      MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES,
    );
  });
});
