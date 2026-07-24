import { describe, expect, it } from 'vitest';
import {
  ArithmeticOverflow,
  MAX_LEG_INPUT_COST,
  MAX_RUN_INPUT_COST,
  MAX_RUNS_PER_LEG,
  MAX_SCALED_WEIGHT,
  MAX_TOTAL_INPUT_TOKENS,
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

  it('rejects NaN, Infinity, and integers above MAX_SAFE_INTEGER', () => {
    expect(() => tokenToBigint(Number.NaN)).toThrow(ArithmeticOverflow);
    expect(() => tokenToBigint(Number.POSITIVE_INFINITY)).toThrow(ArithmeticOverflow);
    expect(() => tokenToBigint(Number.NEGATIVE_INFINITY)).toThrow(ArithmeticOverflow);
    expect(() => tokenToBigint(Number.MAX_SAFE_INTEGER + 1)).toThrow(ArithmeticOverflow);
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

  it('rounds exactly-half up and carries across the decimal point', () => {
    expect(displayRatio(1n, 2_000_000n)).toBe('0.000001'); // exactly 0.0000005 -> up
    expect(displayRatio(9_999_995n, 10_000_000n)).toBe('1.000000'); // 0.9999995 -> carry to 1
  });
});

describe('displayRatio vs classifyRatio differential', () => {
  // displayRatio rounds half-up to 6 decimals; two ratios can share an
  // identical display string yet fall on opposite sides of a frozen threshold.
  // This proves classification must use cross-multiplication, never the
  // displayed value.
  const base = 10n ** 18n;

  it('identical display at the 1.01 boundary, different classification', () => {
    expect(displayRatio(101n * base, 100n * base)).toBe('1.010000');
    expect(displayRatio(101n * base + 1n, 100n * base)).toBe('1.010000');
    expect(classifyRatio(101n * base, 100n * base)).toEqual({ ok: true, class: 'pass' });
    expect(classifyRatio(101n * base + 1n, 100n * base)).toEqual({
      ok: true,
      class: 'inconclusive',
    });
  });

  it('identical display at the 1.05 boundary, different classification', () => {
    expect(displayRatio(105n * base, 100n * base)).toBe('1.050000');
    expect(displayRatio(105n * base + 1n, 100n * base)).toBe('1.050000');
    expect(classifyRatio(105n * base, 100n * base)).toEqual({
      ok: true,
      class: 'inconclusive',
    });
    expect(classifyRatio(105n * base + 1n, 100n * base)).toEqual({
      ok: true,
      class: 'regression',
    });
  });
});

describe('runInputCost internal guard (per-run cap)', () => {
  // weight 1000 = MAX_SCALED_WEIGHT; tokens = MAX_TOTAL_INPUT_TOKENS ->
  // per-run cost = MAX_RUN_INPUT_COST exactly (the reachable cap).
  const weights = { uncached: w('1000'), cacheWrite: w('0'), cacheRead: w('0') };

  it('accepts a per-run cost at the reachable cap (limit)', () => {
    expect(
      runInputCost({ uncached: MAX_TOTAL_INPUT_TOKENS, cacheWrite: 0n, cacheRead: 0n }, weights),
    ).toBe(MAX_RUN_INPUT_COST);
  });

  it('throws ArithmeticOverflow just over the cap (limit+1)', () => {
    expect(() =>
      runInputCost(
        { uncached: MAX_TOTAL_INPUT_TOKENS + 1n, cacheWrite: 0n, cacheRead: 0n },
        weights,
      ),
    ).toThrow(ArithmeticOverflow);
  });
});

describe('sumLegCosts internal guard (run-count + per-run + leg-total)', () => {
  it('accepts MAX_RUNS_PER_LEG runs (limit, count)', () => {
    const runs = Array<bigint>(Number(MAX_RUNS_PER_LEG)).fill(1n);
    expect(sumLegCosts(runs)).toBe(BigInt(Number(MAX_RUNS_PER_LEG)));
  });

  it('throws ArithmeticOverflow when run count exceeds MAX_RUNS_PER_LEG (limit+1)', () => {
    const runs = Array<bigint>(Number(MAX_RUNS_PER_LEG) + 1).fill(1n);
    expect(() => sumLegCosts(runs)).toThrow(ArithmeticOverflow);
  });

  it('throws ArithmeticOverflow when a single run exceeds MAX_RUN_INPUT_COST', () => {
    expect(() => sumLegCosts([MAX_RUN_INPUT_COST + 1n])).toThrow(ArithmeticOverflow);
  });

  it('throws ArithmeticOverflow when the leg total exceeds MAX_LEG_INPUT_COST', () => {
    expect(() => sumLegCosts([MAX_LEG_INPUT_COST + 1n])).toThrow(ArithmeticOverflow);
  });
});

describe('classifyRatio internal guard (leg-cost operand cap)', () => {
  it('accepts operands at MAX_LEG_INPUT_COST (limit)', () => {
    // num == den == cap -> 100*num <= 101*den -> pass
    expect(classifyRatio(MAX_LEG_INPUT_COST, MAX_LEG_INPUT_COST)).toEqual({
      ok: true,
      class: 'pass',
    });
  });

  it('throws ArithmeticOverflow when either operand exceeds MAX_LEG_INPUT_COST (limit+1)', () => {
    expect(() => classifyRatio(MAX_LEG_INPUT_COST + 1n, 1n)).toThrow(ArithmeticOverflow);
    expect(() => classifyRatio(1n, MAX_LEG_INPUT_COST + 1n)).toThrow(ArithmeticOverflow);
  });
});
