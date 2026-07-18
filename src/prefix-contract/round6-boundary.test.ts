import { describe, expect, it } from 'vitest';
import { computeTemplateId, PREFIX_CODES } from './index.js';

/**
 * Round-6 review coverage: exact bounded byte accounting and first-defect
 * ordering with invalid property names.
 */

describe('bounded sink: exact byte accounting', () => {
  it('short escapes count as 2 bytes, not 6', () => {
    // ~200 KB canonical — well under the cap; must be accepted.
    for (const escapee of ['\b', '\t', '\n', '\f', '\r']) {
      const result = computeTemplateId({
        schemaVersion: 1,
        templateVersion: 1,
        definition: escapee.repeat(100_000),
      });
      expect(result.ok).toBe(true);
    }
    // The same count of a 6-byte C0 escape (~600 KB canonical) is rejected.
    const overCap = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: '\u0001'.repeat(100_000),
    });
    expect(overCap.ok).toBe(false);
  });

  it('cap overrun driven only by container punctuation is caught', () => {
    // 1000 objects x 256 single-char properties: within every structural
    // bound, but ~2 MB of punctuation-heavy canonical output.
    const wide = Object.fromEntries(Array.from({ length: 256 }, (_, j) => ['k' + j, 1]));
    const many = Array.from({ length: 1000 }, () => wide);
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: many,
    });
    expect(result).toEqual({ ok: false, errors: [{ code: PREFIX_CODES.envelopeTooLarge }] });
  });

  it('very long property names are counted exactly', () => {
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { ['k'.repeat(300_000)]: 1 },
    });
    expect(result).toEqual({ ok: false, errors: [{ code: PREFIX_CODES.envelopeTooLarge }] });
  });

  it('exact cap boundary on short-escape content', () => {
    const make = (n: number) =>
      computeTemplateId({ schemaVersion: 1, templateVersion: 1, definition: '\n'.repeat(n) });
    let lo = 100_000;
    let hi = 200_000;
    while (lo < hi) {
      const mid = (lo + hi) >> 1;
      if (make(mid).ok) {
        lo = mid + 1;
      } else {
        hi = mid;
      }
    }
    expect(make(lo - 1).ok).toBe(true);
    expect(make(lo).ok).toBe(false);
  });
});

describe('first-defect order with invalid property names', () => {
  it('an earlier non-finite value beats a later invalid name', () => {
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { a: 1e999, [String.fromCharCode(0xd800)]: 1 },
    });
    expect(result).toEqual({
      ok: false,
      errors: [
        { code: PREFIX_CODES.canonicalInputRejected, path: '/definition/<untrusted-property>' },
      ],
    });
  });

  it('an earlier surrogate value beats a later invalid name', () => {
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { a: String.fromCharCode(0xd800), [String.fromCharCode(0xd801)]: 1 },
    });
    expect(result).toEqual({
      ok: false,
      errors: [
        { code: PREFIX_CODES.canonicalInputRejected, path: '/definition/<untrusted-property>' },
      ],
    });
  });

  it('an invalid name wins at its own sorted position', () => {
    const result = computeTemplateId({
      schemaVersion: 1,
      templateVersion: 1,
      definition: { [String.fromCharCode(0xd800)]: 1, ['\u{1F600}']: Number.NaN },
    });
    expect(result).toEqual({
      ok: false,
      errors: [{ code: PREFIX_CODES.canonicalInputRejected, path: '/definition/<invalid-utf16>' }],
    });
  });
});
