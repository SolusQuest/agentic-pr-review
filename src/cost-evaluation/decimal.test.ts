import { describe, expect, it } from 'vitest';
import {
  ArithmeticOverflow,
  MAX_LEG_INPUT_COST,
  MAX_SCALED_WEIGHT,
  canonicalWeightString,
  classifyRatio,
  displayRatio,
  parseWeight,
  runInputCost,
  scaledToDecimalString,
  sumLegCosts,
  tokenToBigint,
} from './decimal.js';

describe('parseWeight', () => {
  it('accepts valid non-negative weights within value cap', () => {
    expect(parseWeight('0')).toEqual({ ok: true, scaled: 0n });
    expect(parseWeight('1')).toEqual({ ok: true, scaled: 1_000_000_000n });
    expect(parseWeight('0.5')).toEqual({ ok: true, scaled: 500_000_000n });
    expect(parseWeight('1.25')).toEqual({ ok: true, scaled: 1_250_000_000n });
    expect(parseWeight('1000')).toEqual({ ok: true, scaled: MAX_SCALED_WEIGHT });
    expect(parseWeight('999.999999999')).toEqual({
      ok: true,
      scaled: 999_999_999_999n,
    });
    expect(parseWeight('0.010000000')).toEqual({ ok: true, scaled: 10_000_000n });
  });

  it('rejects grammar violations', () => {
    for (const bad of ['1.', '.5', '-1', '-0', '01', '1e2', '', '1.0.0', 'abc', '1000.']) {
      expect(parseWeight(bad)).toEqual({ ok: false, reason: 'grammar' });
    }
  });

  it('rejects weights exceeding the value cap (1000)', () => {
    expect(parseWeight('1001')).toEqual({ ok: false, reason: 'value_cap' });
    expect(parseWeight('9999')).toEqual({ ok: false, reason: 'value_cap' });
    expect(parseWeight('1000.000000001')).toEqual({ ok: false, reason: 'value_cap' });
  });
});

describe('canonicalWeightString', () => {
  it('removes trailing zeros', () => {
    expect(canonicalWeightString('1.250')).toBe('1.25');
    expect(canonicalWeightString('0.500000000')).toBe('0.5');
    expect(canonicalWeightString('1000.0')).toBe('1000');
    expect(canonicalWeightString('0.010000000')).toBe('0.01');
    expect(canonicalWeightString('0')).toBe('0');
  });
});

describe('scaledToDecimalString', () => {
  it('renders scaled bigint as decimal with trailing zeros removed', () => {
    expect(scaledToDecimalString(0n)).toBe('0');
    expect(scaledToDecimalString(1_250_000_000n)).toBe('1.25');
    expect(scaledToDecimalString(10_000_000n)).toBe('0.01');
    expect(scaledToDecimalString(1_000_000_000_000n)).toBe('1000');
  });
});

describe('tokenToBigint', () => {
  it('converts safe non-negative integers and preserves null', () => {
    expect(tokenToBigint(null)).toBeNull();
    expect(tokenToBigint(0)).toBe(0n);
    expect(tokenToBigint(42)).toBe(42n);
    expect(tokenToBigint(Number.MAX_SAFE_INTEGER)).toBe(2n ** 53n - 1n);
  });

  it('throws for out-of-domain values', () => {
    expect(() => tokenToBigint(-1)).toThrow(ArithmeticOverflow);
    expect(() => tokenToBigint(1.5)).toThrow(ArithmeticOverflow);
  });
});

function w(s: string): bigint {
  const r = parseWeight(s);
  if (!r.ok) throw new Error(`bad weight: ${s}`);
  return r.scaled;
}

describe('runInputCost', () => {
  const weights = { uncached: w('1.0'), cacheWrite: w('1.25'), cacheRead: w('0.01') };

  it('computes exact scaled cost from non-null partitions', () => {
    const cost = runInputCost({ uncached: 1000n, cacheWrite: 100n, cacheRead: 9000n }, weights);
    // 1000*1.0 + 100*1.25 + 9000*0.01, scaled by 1e9
    expect(cost).toBe(
      1000n * weights.uncached + 100n * weights.cacheWrite + 9000n * weights.cacheRead,
    );
  });

  it('returns null when any partition is null (null propagates)', () => {
    expect(
      runInputCost({ uncached: 1000n, cacheWrite: null, cacheRead: 9000n }, weights),
    ).toBeNull();
    expect(
      runInputCost({ uncached: null, cacheWrite: 100n, cacheRead: 9000n }, weights),
    ).toBeNull();
    expect(
      runInputCost({ uncached: 1000n, cacheWrite: 100n, cacheRead: null }, weights),
    ).toBeNull();
  });
});

describe('sumLegCosts', () => {
  it('sums run costs and propagates null', () => {
    expect(sumLegCosts([1n, 2n, 3n])).toBe(6n);
    expect(sumLegCosts([1n, null, 3n])).toBeNull();
    expect(sumLegCosts([])).toBe(0n);
  });

  it('throws ArithmeticOverflow (internal guard) for synthetic oversized sums', () => {
    const oversized = MAX_LEG_INPUT_COST + 1n;
    expect(() => sumLegCosts([oversized])).toThrow(ArithmeticOverflow);
  });
});

describe('classifyRatio (cross-multiplication, exact boundaries)', () => {
  it('classifies pass at and under 1.01', () => {
    // 100*num <= 101*den
    expect(classifyRatio(101n, 100n)).toEqual({ ok: true, class: 'pass' }); // exactly 1.01 -> pass
    expect(classifyRatio(100n, 100n)).toEqual({ ok: true, class: 'pass' }); // 1.0
    expect(classifyRatio(0n, 100n)).toEqual({ ok: true, class: 'pass' }); // 0
  });

  it('classifies inconclusive just above 1.01 up to 1.05', () => {
    // 100*num = 101*den + 1 -> just above 1.01 -> inconclusive
    expect(classifyRatio(102n, 100n)).toEqual({ ok: true, class: 'inconclusive' });
    // exactly 1.05 -> 100*num = 105*den -> inconclusive (boundary inclusive)
    expect(classifyRatio(105n, 100n)).toEqual({ ok: true, class: 'inconclusive' });
  });

  it('classifies regression just above 1.05', () => {
    // 100*num = 105*den + 1 -> just above 1.05 -> regression
    expect(classifyRatio(106n, 100n)).toEqual({ ok: true, class: 'regression' });
    expect(classifyRatio(200n, 100n)).toEqual({ ok: true, class: 'regression' });
  });

  it('reports zero_denominator when den is 0', () => {
    expect(classifyRatio(100n, 0n)).toEqual({ ok: false, reason: 'zero_denominator' });
  });

  it('handles large exact boundaries without floating point', () => {
    // num = 101*10^18, den = 100*10^18 -> exactly 1.01 -> pass
    const base = 10n ** 18n;
    expect(classifyRatio(101n * base, 100n * base)).toEqual({ ok: true, class: 'pass' });
    // +1 -> inconclusive
    expect(classifyRatio(101n * base + 1n, 100n * base)).toEqual({
      ok: true,
      class: 'inconclusive',
    });
  });
});

describe('displayRatio', () => {
  it('renders half-up to 6 decimals (display-only)', () => {
    expect(displayRatio(1n, 1n)).toBe('1.000000');
    expect(displayRatio(101n, 100n)).toBe('1.010000');
    expect(displayRatio(105n, 100n)).toBe('1.050000');
    expect(displayRatio(106n, 100n)).toBe('1.060000');
    expect(displayRatio(1n, 3n)).toBe('0.333333'); // 0.3333333... -> half-up 0.333333
    expect(displayRatio(2n, 3n)).toBe('0.666667'); // 0.6666666... -> half-up 0.666667
    expect(displayRatio(0n, 100n)).toBe('0.000000');
    expect(displayRatio(1n, 0n)).toBe('n/a');
  });
});
