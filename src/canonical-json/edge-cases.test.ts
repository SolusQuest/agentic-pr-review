import { describe, expect, it } from 'vitest';
import { canonicalJsonBytes, CanonicalJsonInputError, CANONICAL_JSON_VERSION } from './index.js';

const dec = new TextDecoder();

describe('canonicalJsonBytes - RFC 8785 edge cases', () => {
  it('CANONICAL_JSON_VERSION is exported as 1', () => {
    expect(CANONICAL_JSON_VERSION).toBe(1);
  });

  it('positive zero and negative zero produce byte-equal output', () => {
    expect(canonicalJsonBytes(0)).toEqual(canonicalJsonBytes(-0));
    expect(canonicalJsonBytes({ n: -0 })).toEqual(canonicalJsonBytes({ n: 0 }));
  });

  it('accepts a null-prototype plain object', () => {
    const obj = Object.create(null) as Record<string, unknown>;
    obj.a = 1;
    obj.b = 'x';
    expect(dec.decode(canonicalJsonBytes(obj))).toBe('{"a":1,"b":"x"}');
  });

  it('accepts a repeated non-cyclic reference (DAG)', () => {
    const shared = { v: 1 };
    const parent = { a: shared, b: shared };
    expect(dec.decode(canonicalJsonBytes(parent))).toBe('{"a":{"v":1},"b":{"v":1}}');
  });

  it('sorts non-ASCII property names by UTF-16 code units, not by locale', () => {
    const value = { z: 1, a: 2, A: 3, ['\u00e9']: 4, ['\uD83D\uDE00']: 5 };
    // Expected order by UTF-16 code units: 'A' (65), 'a' (97), 'z' (122), 'é' (233), '\uD83D...' (0xD83D)
    const encoded = dec.decode(canonicalJsonBytes(value));
    // Extract key sequence by parsing:
    const parsed = JSON.parse(encoded) as Record<string, number>;
    expect(Object.keys(parsed)).toEqual(['A', 'a', 'z', '\u00e9', '\uD83D\uDE00']);
  });

  it('encodes ordinary Unicode without unnecessary escaping', () => {
    const encoded = dec.decode(canonicalJsonBytes('hello \u00e9 world'));
    expect(encoded).toBe('"hello \u00e9 world"');
  });

  it('encodes control characters with \\uXXXX escape', () => {
    const encoded = dec.decode(canonicalJsonBytes('\u0000\u0001\u001f'));
    expect(encoded).toBe('"\\u0000\\u0001\\u001f"');
  });

  it('encodes standard short escapes', () => {
    const encoded = dec.decode(canonicalJsonBytes('"\\/\b\f\n\r\t'));
    expect(encoded).toBe('"\\"\\\\/\\b\\f\\n\\r\\t"');
  });

  it('preserves supplementary-plane characters as valid surrogate pair', () => {
    // 😀 U+1F600
    const encoded = dec.decode(canonicalJsonBytes('\uD83D\uDE00'));
    expect(encoded).toBe('"\uD83D\uDE00"');
  });

  it('CanonicalJsonInputError includes a useful path for nested rejection', () => {
    const obj = { a: [1, { b: undefined }] } as unknown;
    try {
      canonicalJsonBytes(obj);
      throw new Error('expected to throw');
    } catch (err) {
      expect(err).toBeInstanceOf(CanonicalJsonInputError);
      const e = err as CanonicalJsonInputError;
      expect(e.path).toContain('a');
      expect(e.path).toContain('b');
    }
  });

  it('idempotence: canonicalize -> JSON.parse -> canonicalize is byte-stable', () => {
    const inputs: unknown[] = [
      { a: 1, b: [true, false, null], c: 'x' },
      [1, 2, 3, { z: 'z', y: 'y' }],
      { ['\u00e9']: 1, a: [{ b: 2 }, { b: 3 }] },
      'hello',
      42,
      null,
      true,
    ];
    for (const value of inputs) {
      const once = canonicalJsonBytes(value);
      const twice = canonicalJsonBytes(JSON.parse(dec.decode(once)));
      expect(twice).toEqual(once);
    }
  });

  it('output parses back to a semantically equal value via JSON.parse', () => {
    const value = { z: [1, 2, 3], a: null, b: true, c: 'x\u00e9y', d: 1.5, e: -0 };
    const bytes = canonicalJsonBytes(value);
    const parsed = JSON.parse(dec.decode(bytes)) as typeof value;
    expect(parsed).toEqual({ z: [1, 2, 3], a: null, b: true, c: 'x\u00e9y', d: 1.5, e: 0 });
  });

  it('completes at near-cap sizes without exponential blowup (perf smoke)', () => {
    const big: Record<string, string> = {};
    for (let i = 0; i < 10_000; i++) {
      big[String(i).padStart(6, '0')] = 'x';
    }
    const start = Date.now();
    const bytes = canonicalJsonBytes(big);
    const elapsed = Date.now() - start;
    expect(bytes.byteLength).toBeGreaterThan(50_000);
    expect(elapsed).toBeLessThan(2000);
  });
});
