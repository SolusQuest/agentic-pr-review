/**
 * Cost-evaluation decimal arithmetic.
 *
 * Scaled bigint (scale 1e9) for all cost computation; ratio classification by
 * cross-multiplication (no division, exact boundaries). Owned by issue #54.
 *
 * No JavaScript `number` participates in cost or ratio math. Token values from
 * #51 metadata are safe integers (<= 2^53-1) and are converted to bigint at the
 * boundary. `arithmetic_overflow` is an internal invariant guard on this helper
 * (enforced at three sites: per-run cost in `runInputCost`, run-count + per-run
 * + leg-total in `sumLegCosts`, and leg-cost operands in `classifyRatio`),
 * unit-tested with synthetic oversized bigint operands; legal run-evidence
 * cannot reach it (it would first violate a token/weight/run-count bound,
 * reported as `invalid` upstream).
 *
 * Frozen contract: see protocol/schemas/cost-evaluation/v1/ and issue #54
 * § Numeric semantics.
 */

export const DECIMAL_SCALE_EXPONENT = 9 as const;
/** Scale factor: weights and costs are integers scaled by 1e9. */
export const DECIMAL_SCALE = 10n ** BigInt(DECIMAL_SCALE_EXPONENT); // 1_000_000_000n

/**
 * Weight grammar (non-negative; no exponent / -0 / leading zeros / ".5" / "1.").
 * Up to 4 integer digits and 9 fractional digits. A separate value-cap check
 * rejects weights > 1000.
 */
export const WEIGHT_GRAMMAR = /^(0|[1-9][0-9]{0,3})(\.[0-9]{1,9})?$/;

/** Maximum weight value (weights are non-negative and <= 1000). */
export const MAX_WEIGHT_VALUE = 1000n;
/** Maximum scaled weight = 1000 * 1e9 = 10^12. */
export const MAX_SCALED_WEIGHT = MAX_WEIGHT_VALUE * DECIMAL_SCALE;

/** Maximum total input tokens per run (per #51 safe-integer token domain). */
export const MAX_TOTAL_INPUT_TOKENS = 2n ** 53n - 1n;
/** Maximum output tokens per run. */
export const MAX_OUTPUT_TOKENS = 2n ** 53n - 1n;
/** Maximum runs per leg. */
export const MAX_RUNS_PER_LEG = 32n;

/**
 * Reachable cost caps (mathematical maxima of the legal input domain). Legal
 * parser-validated run-evidence cannot exceed these; exceeding them would first
 * violate a token / weight / run-count bound. Used for `limit-1/limit/limit+1`
 * boundary tests on the decimal helper's internal guard, not as public
 * run-evidence outcomes.
 */
export const MAX_RUN_INPUT_COST = MAX_TOTAL_INPUT_TOKENS * MAX_SCALED_WEIGHT;
export const MAX_LEG_INPUT_COST = MAX_RUNS_PER_LEG * MAX_RUN_INPUT_COST;
/** Maximum cross-multiplication operand: 105 (regression threshold) * leg cost. */
export const MAX_RATIO_CROSS_PRODUCT = 105n * MAX_LEG_INPUT_COST;
export const MAX_RUN_OUTPUT_COST = MAX_OUTPUT_TOKENS * MAX_SCALED_WEIGHT;
export const MAX_LEG_OUTPUT_COST = MAX_RUNS_PER_LEG * MAX_RUN_OUTPUT_COST;

export type WeightParseReason = 'grammar' | 'value_cap';
export type WeightParseResult =
  | { readonly ok: true; readonly scaled: bigint }
  | { readonly ok: false; readonly reason: WeightParseReason };

/**
 * Internal invariant guard. Raised by the checked accumulator when a synthetic
 * oversized bigint operand exceeds a reachable cap. Not a public run-evidence
 * outcome (legal run-evidence cannot reach it).
 */
export class ArithmeticOverflow extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'ArithmeticOverflow';
  }
}

/** Parse a profile weight decimal string into a scaled bigint. */
export function parseWeight(decimal: string): WeightParseResult {
  if (typeof decimal !== 'string' || !WEIGHT_GRAMMAR.test(decimal)) {
    return { ok: false, reason: 'grammar' };
  }
  const dot = decimal.indexOf('.');
  const intPart = dot === -1 ? decimal : decimal.slice(0, dot);
  const fracRaw = dot === -1 ? '' : decimal.slice(dot + 1);
  if (fracRaw.length > DECIMAL_SCALE_EXPONENT) {
    return { ok: false, reason: 'grammar' };
  }
  const fracPadded = fracRaw.padEnd(DECIMAL_SCALE_EXPONENT, '0');
  const scaled = BigInt(intPart) * DECIMAL_SCALE + BigInt(fracPadded || '0');
  if (scaled > MAX_SCALED_WEIGHT) {
    return { ok: false, reason: 'value_cap' };
  }
  return { ok: true, scaled };
}

/** Canonicalize a weight decimal string (trailing zeros removed). Throws if invalid. */
export function canonicalWeightString(decimal: string): string {
  const parsed = parseWeight(decimal);
  if (!parsed.ok) {
    throw new Error(`cannot canonicalize invalid weight: ${decimal}`);
  }
  return scaledToDecimalString(parsed.scaled);
}

/**
 * Convert a #51 metadata token value (`number | null`, safe integer) to bigint.
 * Throws `ArithmeticOverflow` for out-of-domain values; #51 validation ensures
 * this never fires for parser-produced metadata.
 */
export function tokenToBigint(value: number | null): bigint | null {
  if (value === null) return null;
  if (!Number.isInteger(value) || value < 0 || value > Number.MAX_SAFE_INTEGER) {
    throw new ArithmeticOverflow(`token out of safe-integer domain: ${value}`);
  }
  return BigInt(value);
}

/** Render a scaled bigint as a canonical decimal string (trailing zeros removed). */
export function scaledToDecimalString(scaled: bigint): string {
  const neg = scaled < 0n;
  const abs = neg ? -scaled : scaled;
  const intPart = abs / DECIMAL_SCALE;
  const frac = (abs % DECIMAL_SCALE).toString().padStart(DECIMAL_SCALE_EXPONENT, '0');
  const fracTrimmed = frac.replace(/0+$/, '');
  const sign = neg ? '-' : '';
  return fracTrimmed.length === 0 ? `${sign}${intPart}` : `${sign}${intPart}.${fracTrimmed}`;
}

export interface InputPartitions {
  readonly uncached: bigint | null;
  readonly cacheWrite: bigint | null;
  readonly cacheRead: bigint | null;
}
export interface InputWeights {
  readonly uncached: bigint;
  readonly cacheWrite: bigint;
  readonly cacheRead: bigint;
}

/**
 * Per-run normalized input cost = uncached*Wu + cacheWrite*Ww + cacheRead*Wr
 * over the parser-validated `normalizedUsage.aggregate` partitions (already
 * summed by #51; no per-attempt multiplication). Returns `null` if any
 * partition is null (null propagates; the leg cost is then uncomputable).
 *
 * Internal guard: legal partitions are mutually exclusive and sum to <=
 * `MAX_TOTAL_INPUT_TOKENS`, and weights <= `MAX_SCALED_WEIGHT`, so a legal
 * per-run cost never exceeds `MAX_RUN_INPUT_COST`; a synthetic oversized
 * operand throws `ArithmeticOverflow` here.
 */
export function runInputCost(partitions: InputPartitions, weights: InputWeights): bigint | null {
  if (
    partitions.uncached === null ||
    partitions.cacheWrite === null ||
    partitions.cacheRead === null
  ) {
    return null;
  }
  const cost =
    partitions.uncached * weights.uncached +
    partitions.cacheWrite * weights.cacheWrite +
    partitions.cacheRead * weights.cacheRead;
  if (cost > MAX_RUN_INPUT_COST) {
    throw new ArithmeticOverflow('per-run input cost exceeds reachable cap');
  }
  return cost;
}

/**
 * Sum per-run costs into a leg cost. Returns `null` if any run cost is `null`
 * (null propagates). Throws `ArithmeticOverflow` (internal guard) if the run
 * count exceeds `MAX_RUNS_PER_LEG`, any single run exceeds `MAX_RUN_INPUT_COST`,
 * or the leg total exceeds `MAX_LEG_INPUT_COST`; legal inputs cannot reach any
 * of these.
 */
export function sumLegCosts(runCosts: readonly (bigint | null)[]): bigint | null {
  if (runCosts.length > Number(MAX_RUNS_PER_LEG)) {
    throw new ArithmeticOverflow('run count exceeds reachable cap');
  }
  let total = 0n;
  for (const c of runCosts) {
    if (c === null) return null;
    if (c > MAX_RUN_INPUT_COST) {
      throw new ArithmeticOverflow('per-run input cost exceeds reachable cap');
    }
    total += c;
  }
  if (total > MAX_LEG_INPUT_COST) {
    throw new ArithmeticOverflow('leg input cost exceeds reachable cap');
  }
  return total;
}

export type RatioClass = 'pass' | 'inconclusive' | 'regression';
export type ClassifyRatioResult =
  | { readonly ok: true; readonly class: RatioClass }
  | { readonly ok: false; readonly reason: 'zero_denominator' };

/**
 * Classify resumed/stateless cost ratio by cross-multiplication (no division).
 * Frozen thresholds (owned by #29): pass <= 1.01, inconclusive (1.01, 1.05],
 * regression > 1.05. With num = resumed cost, den = stateless cost:
 *   pass iff 100*num <= 101*den
 *   regression iff 100*num > 105*den
 *   inconclusive iff 101*den < 100*num <= 105*den
 * Boundaries are exact.
 */
export function classifyRatio(num: bigint, den: bigint): ClassifyRatioResult {
  if (den === 0n) return { ok: false, reason: 'zero_denominator' };
  if (den < 0n || num < 0n) {
    // Costs are non-negative; a negative value is an upstream contract violation.
    throw new ArithmeticOverflow('negative cost operand in ratio classification');
  }
  // Leg-cost operand guard: legal leg costs <= MAX_LEG_INPUT_COST, so the
  // cross-products (100*num, 105*den) stay within MAX_RATIO_CROSS_PRODUCT. A
  // synthetic oversized operand throws here.
  if (num > MAX_LEG_INPUT_COST || den > MAX_LEG_INPUT_COST) {
    throw new ArithmeticOverflow('ratio operand exceeds reachable leg-cost cap');
  }
  const lhs = 100n * num;
  if (lhs <= 101n * den) return { ok: true, class: 'pass' };
  if (lhs > 105n * den) return { ok: true, class: 'regression' };
  return { ok: true, class: 'inconclusive' };
}

/**
 * Display ratio (num/den) rounded half-up to 6 decimal places. Display-only;
 * classification uses `classifyRatio`, never this value.
 */
export function displayRatio(num: bigint, den: bigint): string {
  if (den === 0n) return 'n/a';
  const neg = num < 0n;
  const absNum = neg ? -num : num;
  const scale6 = 10n ** 6n;
  const scaled = absNum * scale6;
  const floor = scaled / den;
  const rem = scaled % den;
  const rounded = floor + (2n * rem >= den ? 1n : 0n);
  const intPart = rounded / scale6;
  const frac = (rounded % scale6).toString().padStart(6, '0');
  return `${neg ? '-' : ''}${intPart}.${frac}`;
}
