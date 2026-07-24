import { createHash } from 'node:crypto';
import { describe, expect, it } from 'vitest';
import { digestId, sha256Hex } from './hash.js';

/** Independent oracle: must NOT use the helper under test. */
function oracleSha256(bytes: Uint8Array): string {
  return createHash('sha256').update(bytes).digest('hex');
}

describe('sha256Hex', () => {
  it('matches an independent node:crypto oracle for known vectors', () => {
    expect(sha256Hex(new Uint8Array())).toBe(oracleSha256(new Uint8Array()));
    expect(sha256Hex(new TextEncoder().encode('abc'))).toBe(
      oracleSha256(new TextEncoder().encode('abc')),
    );
    const withNul = new Uint8Array([0x61, 0x00, 0x62]);
    expect(sha256Hex(withNul)).toBe(oracleSha256(withNul));
  });

  it('matches the RFC 6234 empty-input SHA-256 vector', () => {
    expect(sha256Hex(new Uint8Array())).toBe(
      'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855',
    );
  });
});

describe('digestId (domain-tagged)', () => {
  it('matches an independent UTF8(tag)||0x00||body oracle', () => {
    const tag = 'agentic-pr-review/cost-eval/profile/v1';
    const body = new TextEncoder().encode('{"a":1}');
    const oracle = oracleSha256(
      Buffer.concat([Buffer.from(tag, 'utf8'), Buffer.from([0x00]), Buffer.from(body)]),
    );
    expect(digestId(tag, body)).toBe(oracle);
  });

  it('rejects a tag containing a NUL octet (start / middle / end)', () => {
    expect(() => digestId('\x00ab', new Uint8Array())).toThrow();
    expect(() => digestId('a\x00b', new Uint8Array())).toThrow();
    expect(() => digestId('ab\x00', new Uint8Array())).toThrow();
  });

  it('domain-separates: same body, different tag -> different digest', () => {
    const body = new TextEncoder().encode('x');
    expect(digestId('tag-a', body)).not.toBe(digestId('tag-b', body));
  });

  it('is deterministic', () => {
    const body = new TextEncoder().encode('x');
    expect(digestId('tag', body)).toBe(digestId('tag', body));
  });
});
