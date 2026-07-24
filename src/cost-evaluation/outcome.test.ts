import { describe, expect, it } from 'vitest';
import {
  evaluateScenario,
  perLegRatioEligibility,
  reasonToOutcome,
  suiteReducer,
  type LegMetadataView,
  type ScenarioReason,
  type ScenarioOutcome,
} from './outcome.js';

function eligibleResumedMetadata(over: Partial<LegMetadataView> = {}): LegMetadataView {
  return {
    capability: { mode: 'standard', statelessProof: null },
    telemetryCompleteness: {
      usage: 'complete',
      cache: 'complete',
      statelessProof: 'notApplicable',
      aggregate: 'complete',
    },
    normalizedUsage: {
      aggregate: {
        totalInputTokens: 1000,
        uncachedInputTokens: 100,
        cacheWriteInputTokens: 0,
        cacheReadInputTokens: 900,
      },
    },
    ...over,
  };
}

function eligibleStatelessMetadata(over: Partial<LegMetadataView> = {}): LegMetadataView {
  return {
    capability: { mode: 'stateless', statelessProof: { kind: 'synthetic', verified: true } },
    telemetryCompleteness: {
      usage: 'complete',
      cache: 'complete',
      statelessProof: 'complete',
      aggregate: 'complete',
    },
    normalizedUsage: {
      aggregate: {
        totalInputTokens: 1000,
        uncachedInputTokens: 1000,
        cacheWriteInputTokens: 0,
        cacheReadInputTokens: 0,
      },
    },
    ...over,
  };
}

describe('reasonToOutcome (closed map)', () => {
  it('maps invalid-class reasons to invalid', () => {
    for (const r of [
      'malformed_metadata',
      'equivalence_mismatch',
      'zero_denominator',
      'oracle_unavailable',
      'review_target_mismatch',
    ] as ScenarioReason[]) {
      expect(reasonToOutcome(r)).toBe('invalid');
    }
  });
  it('maps prefix_drift to contract_regression', () => {
    expect(reasonToOutcome('prefix_drift')).toBe('contract_regression');
  });
  it('maps completeness + gray-band reasons to inconclusive', () => {
    for (const r of [
      'telemetry_incomplete',
      'cache_completeness_unknown',
      'stateless_proof_unverified',
      'partition_null',
      'no_evaluable_requests',
      'ratio_gray_band',
    ] as ScenarioReason[]) {
      expect(reasonToOutcome(r)).toBe('inconclusive');
    }
  });
  it('maps ratio_regression / ratio_pass / legal_invalidation', () => {
    expect(reasonToOutcome('ratio_regression')).toBe('regression');
    expect(reasonToOutcome('ratio_pass')).toBe('pass');
    expect(reasonToOutcome('legal_invalidation')).toBe('not_applicable');
  });
});

describe('perLegRatioEligibility', () => {
  it('eligibility for a valid resumed leg', () => {
    expect(perLegRatioEligibility(eligibleResumedMetadata(), 'resumed')).toEqual({
      eligible: true,
      reason: null,
    });
  });
  it('eligibility for a valid stateless leg', () => {
    expect(perLegRatioEligibility(eligibleStatelessMetadata(), 'stateless')).toEqual({
      eligible: true,
      reason: null,
    });
  });
  it('rejects incomplete usage/aggregate telemetry', () => {
    expect(
      perLegRatioEligibility(
        eligibleResumedMetadata({
          telemetryCompleteness: {
            usage: 'partial',
            cache: 'complete',
            statelessProof: 'notApplicable',
            aggregate: 'complete',
          },
        }),
        'resumed',
      ),
    ).toMatchObject({ reason: 'telemetry_incomplete' });
  });
  it('rejects null partitions (partition_null)', () => {
    expect(
      perLegRatioEligibility(
        eligibleResumedMetadata({
          normalizedUsage: {
            aggregate: {
              totalInputTokens: null,
              uncachedInputTokens: null,
              cacheWriteInputTokens: 0,
              cacheReadInputTokens: 0,
            },
          },
        }),
        'resumed',
      ),
    ).toMatchObject({ reason: 'partition_null' });
  });
  it('rejects cache_completeness_unknown with non-null partitions', () => {
    expect(
      perLegRatioEligibility(
        eligibleResumedMetadata({
          telemetryCompleteness: {
            usage: 'complete',
            cache: 'unknown',
            statelessProof: 'notApplicable',
            aggregate: 'complete',
          },
        }),
        'resumed',
      ),
    ).toMatchObject({ reason: 'cache_completeness_unknown' });
  });
  it('rejects resumed leg with stateless capability', () => {
    expect(perLegRatioEligibility(eligibleStatelessMetadata(), 'resumed')).toMatchObject({
      reason: 'capability_stateless_unsupported',
    });
  });
  it('rejects stateless leg with unverified proof', () => {
    expect(
      perLegRatioEligibility(
        eligibleStatelessMetadata({
          capability: { mode: 'stateless', statelessProof: { kind: 'synthetic', verified: false } },
        }),
        'stateless',
      ),
    ).toMatchObject({ reason: 'stateless_proof_unverified' });
  });
  it('rejects zero total input (no_evaluable_requests)', () => {
    expect(
      perLegRatioEligibility(
        eligibleResumedMetadata({
          normalizedUsage: {
            aggregate: {
              totalInputTokens: 0,
              uncachedInputTokens: 0,
              cacheWriteInputTokens: 0,
              cacheReadInputTokens: 0,
            },
          },
        }),
        'resumed',
      ),
    ).toMatchObject({ reason: 'no_evaluable_requests' });
  });
});

describe('evaluateScenario (deterministic precedence)', () => {
  const ok = (over: Partial<Parameters<typeof evaluateScenario>[0]> = {}) =>
    evaluateScenario({
      validityReason: null,
      prefixDrift: false,
      resumedEligibility: { eligible: true, reason: null },
      statelessEligibility: { eligible: true, reason: null },
      num: 100n,
      den: 100n,
      ...over,
    });

  it('validity reason wins (invalid)', () => {
    expect(ok({ validityReason: 'equivalence_mismatch' }).outcome).toBe('invalid');
  });
  it('prefix drift -> contract_regression (over eligibility/ratio)', () => {
    expect(ok({ prefixDrift: true }).outcome).toBe('contract_regression');
  });
  it('resumed eligibility failure -> inconclusive', () => {
    expect(ok({ resumedEligibility: { eligible: false, reason: 'partition_null' } }).outcome).toBe(
      'inconclusive',
    );
  });
  it('ratio pass / gray band / regression', () => {
    expect(ok({ num: 100n, den: 100n }).outcome).toBe('pass');
    expect(ok({ num: 102n, den: 100n }).outcome).toBe('inconclusive'); // gray band
    expect(ok({ num: 106n, den: 100n }).outcome).toBe('regression');
  });
  it('zero denominator -> invalid', () => {
    expect(ok({ num: 100n, den: 0n }).outcome).toBe('invalid');
  });
});

describe('suiteReducer (total)', () => {
  const cases: Array<{ scenarios: ScenarioOutcome[]; expected: ScenarioOutcome }> = [
    {
      scenarios: ['pass', 'pass', 'pass', 'pass', 'pass', 'pass', 'pass', 'pass'],
      expected: 'pass',
    },
    { scenarios: ['pass', 'inconclusive'], expected: 'inconclusive' },
    { scenarios: ['pass', 'regression'], expected: 'regression' },
    { scenarios: ['regression', 'contract_regression'], expected: 'contract_regression' },
    { scenarios: ['contract_regression', 'invalid'], expected: 'invalid' },
    { scenarios: ['pass', 'invalid'], expected: 'invalid' },
  ];
  for (const { scenarios, expected } of cases) {
    it(`${scenarios.join(',')} -> ${expected}`, () => {
      expect(suiteReducer(scenarios)).toBe(expected);
    });
  }
});
