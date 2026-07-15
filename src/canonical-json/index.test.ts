import { describe, expect, it } from 'vitest';
import { CanonicalJsonInputError, canonicalJsonBytes } from './index.js';

const dec = new TextDecoder();

describe('canonicalJsonBytes', () => {
  it('sorts object keys by UTF-16 code units', () => {
    const out = dec.decode(canonicalJsonBytes({ b: 1, a: 2, ['\u00e9']: 3, Z: 4 }));
    expect(out).toBe('{"Z":4,"a":2,"b":1,"\u00e9":3}');
  });

  it('emits ECMAScript ToString for numbers', () => {
    expect(dec.decode(canonicalJsonBytes(1))).toBe('1');
    expect(dec.decode(canonicalJsonBytes(1.5))).toBe('1.5');
    expect(dec.decode(canonicalJsonBytes(1e21))).toBe(String(1e21));
    expect(dec.decode(canonicalJsonBytes(-1.2e-10))).toBe(String(-1.2e-10));
  });

  it('serializes negative zero as 0 per RFC 8785', () => {
    expect(dec.decode(canonicalJsonBytes(-0))).toBe('0');
    expect(dec.decode(canonicalJsonBytes({ x: -0 }))).toBe('{"x":0}');
  });

  it('rejects NaN, Infinity, and -Infinity', () => {
    expect(() => canonicalJsonBytes(NaN)).toThrow(CanonicalJsonInputError);
    expect(() => canonicalJsonBytes(Infinity)).toThrow(CanonicalJsonInputError);
    expect(() => canonicalJsonBytes(-Infinity)).toThrow(CanonicalJsonInputError);
  });

  it('rejects undefined, bigint, symbol, function, Date, Map, Set, RegExp', () => {
    expect(() => canonicalJsonBytes(undefined)).toThrow(CanonicalJsonInputError);
    expect(() => canonicalJsonBytes(1n)).toThrow(CanonicalJsonInputError);
    expect(() => canonicalJsonBytes(Symbol('x'))).toThrow(CanonicalJsonInputError);
    expect(() => canonicalJsonBytes(() => 1)).toThrow(CanonicalJsonInputError);
    expect(() => canonicalJsonBytes(new Date(0))).toThrow(CanonicalJsonInputError);
    expect(() => canonicalJsonBytes(new Map())).toThrow(CanonicalJsonInputError);
    expect(() => canonicalJsonBytes(new Set())).toThrow(CanonicalJsonInputError);
    expect(() => canonicalJsonBytes(/x/)).toThrow(CanonicalJsonInputError);
  });

  it('rejects cyclic structures', () => {
    const obj: Record<string, unknown> = { a: 1 };
    obj.self = obj;
    expect(() => canonicalJsonBytes(obj)).toThrow(CanonicalJsonInputError);
    const arr: unknown[] = [];
    arr.push(arr);
    expect(() => canonicalJsonBytes(arr)).toThrow(CanonicalJsonInputError);
  });

  it('rejects sparse arrays', () => {
    // eslint-disable-next-line no-sparse-arrays
    const sparse = [1, , 3];
    expect(() => canonicalJsonBytes(sparse as unknown[])).toThrow(CanonicalJsonInputError);
  });

  it('rejects symbol-keyed own properties on objects and arrays', () => {
    const s = Symbol('k');
    const obj = { a: 1, [s]: 2 } as unknown;
    expect(() => canonicalJsonBytes(obj)).toThrow(CanonicalJsonInputError);
    const arr: unknown[] = [1];
    (arr as unknown as Record<symbol, unknown>)[s] = 2;
    expect(() => canonicalJsonBytes(arr)).toThrow(CanonicalJsonInputError);
  });

  it('rejects accessor properties (getter/setter) on plain objects', () => {
    const obj: Record<string, unknown> = {};
    Object.defineProperty(obj, 'x', { get: () => 1, enumerable: true, configurable: true });
    expect(() => canonicalJsonBytes(obj)).toThrow(CanonicalJsonInputError);
  });

  it('rejects non-enumerable own properties on plain objects', () => {
    const obj: Record<string, unknown> = {};
    Object.defineProperty(obj, 'x', { value: 1, enumerable: false, configurable: true });
    expect(() => canonicalJsonBytes(obj)).toThrow(CanonicalJsonInputError);
  });

  it('rejects plain objects with non-Object.prototype prototype', () => {
    class Custom {
      x = 1;
    }
    expect(() => canonicalJsonBytes(new Custom() as unknown)).toThrow(CanonicalJsonInputError);
  });

  it('rejects arrays with an extra string property', () => {
    const arr = [1, 2, 3] as unknown as Record<string, unknown>;
    arr.extra = 'nope';
    expect(() => canonicalJsonBytes(arr as unknown)).toThrow(CanonicalJsonInputError);
  });

  it('rejects arrays with an accessor index', () => {
    const arr: unknown[] = [1];
    Object.defineProperty(arr, '0', { get: () => 42, enumerable: true, configurable: true });
    expect(() => canonicalJsonBytes(arr)).toThrow(CanonicalJsonInputError);
  });

  it('rejects arrays with a non-Array.prototype prototype', () => {
    class MyArr extends Array {}
    const a = new MyArr();
    a.push(1);
    expect(() => canonicalJsonBytes(a as unknown as unknown[])).toThrow(CanonicalJsonInputError);
  });

  it('rejects lone surrogates in string values and in property names', () => {
    expect(() => canonicalJsonBytes('a\uD800b')).toThrow(CanonicalJsonInputError);
    expect(() => canonicalJsonBytes({ ['x\uDC00']: 1 })).toThrow(CanonicalJsonInputError);
  });

  it('produces byte-stable output on repeated calls', () => {
    const value = { z: [1, 2, { c: 'x', a: null, b: true }], a: 'hi', m: -0 };
    const a = canonicalJsonBytes(value);
    const b = canonicalJsonBytes(value);
    expect(a).toEqual(b);
    // Reversing the top-level key order in the input yields the same bytes.
    const swapped = { m: -0, z: [1, 2, { b: true, a: null, c: 'x' }], a: 'hi' };
    const c = canonicalJsonBytes(swapped);
    expect(c).toEqual(a);
  });

  it('does not import node:fs', async () => {
    const src = await import('node:fs/promises');
    const text = await src.readFile('src/canonical-json/index.ts', 'utf8');
    expect(text).not.toMatch(/from ['"]node:fs['"]/);
    expect(text).not.toMatch(/from ['"]node:fs\/promises['"]/);
    expect(text).not.toMatch(/require\(['"]fs['"]\)/);
  });
});
