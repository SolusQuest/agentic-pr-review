import { describe, expect, it } from 'vitest';
import {
  buildLiveObservationReport,
  buildSuiteReport,
  serializeReport,
  type ScenarioCompleteness,
  type ScenarioResultEntry,
} from './report.js';
import { evaluateGraduationWindow } from './graduation.js';
import {
  reportSha256Of,
  type CostEvaluationStrategyIdentityV1,
  type WindowPartitionInputs,
} from './domain.js';

const resumedIdentity: CostEvaluationStrategyIdentityV1 = {
  schemaVersion: 1,
  adapterId: 'adapter-resumed',
  cacheConfigId: 'cache-resumed',
  capabilityMode: 'standard',
  statelessProofKind: null,
};
const statelessIdentity: CostEvaluationStrategyIdentityV1 = {
  schemaVersion: 1,
  adapterId: 'adapter-stateless',
  cacheConfigId: 'cache-stateless',
  capabilityMode: 'stateless',
  statelessProofKind: 'synthetic',
};
const windowInputs: WindowPartitionInputs = {
  profileDigest: 'profile-digest',
  providerId: 'prov',
  modelId: 'model',
  resumedStrategyIdentity: resumedIdentity,
  statelessStrategyIdentity: statelessIdentity,
  fixtureSuiteDigest: 'fixture-suite-digest',
  prefixContractVersion: 1,
  harnessVersion: 'h1',
  mode: 'synthetic',
};

const complete: ScenarioCompleteness = { resumed: 'complete', stateless: 'complete' };

function passEntry(
  scenarioId: string,
  numerator: string,
  denominator: string,
  displayRatio: string,
): ScenarioResultEntry {
  return {
    scenarioId,
    outcome: 'pass',
    reason: 'ratio_pass',
    numerator,
    denominator,
    displayRatio,
    totalCost: null,
    completeness: complete,
  };
}

const scenarioResults: readonly ScenarioResultEntry[] = [
  passEntry('large_prior_small_delta', '10', '1000', '0.010000'),
];
const totals = { numerator: '10', denominator: '1000', displayRatio: '0.010000', totalCost: null };

function suiteInput(
  over: { evidenceOccurrenceId?: string; provenance?: Record<string, string | null> } = {},
) {
  return {
    suiteId: 'synthetic-v1',
    suiteVersion: '1',
    windowInputs,
    scenarioResults,
    totals,
    evidenceOccurrenceId: over.evidenceOccurrenceId ?? 'occ-1',
    provenance: {
      sourceCommit: over.provenance?.sourceCommit ?? 'sha',
      producingRunId: over.provenance?.producingRunId ?? 'run',
      runnerIdentity: over.provenance?.runnerIdentity ?? 'runner',
      os: over.provenance?.os ?? 'linux',
      nodeVersion: over.provenance?.nodeVersion ?? 'v20',
      timestamp: over.provenance?.timestamp ?? '2026-01-01T00:00:00Z',
    },
  };
}

/** A fresh, locally-owned windowInputs (no shared-ref mutation pollution). */
function freshWindowInputs(): WindowPartitionInputs {
  return {
    ...windowInputs,
    resumedStrategyIdentity: { ...resumedIdentity },
    statelessStrategyIdentity: { ...statelessIdentity },
  };
}

describe('buildSuiteReport', () => {
  it('computes a 64-hex reportSha256 over the semantic envelope', () => {
    const built = buildSuiteReport(suiteInput());
    expect(built.reportSha256).toMatch(/^[0-9a-f]{64}$/);
    // reportSha256 matches recomputation from the semantic envelope
    expect(built.reportSha256).toBe(reportSha256Of(built.semanticEnvelope));
  });

  it('semantic envelope excludes reportSha256, evidenceOccurrenceId, and provenance', () => {
    const env = buildSuiteReport(suiteInput()).semanticEnvelope as Record<string, unknown>;
    expect(env.reportSha256).toBeUndefined();
    expect(env.evidenceOccurrenceId).toBeUndefined();
    expect(env.provenance).toBeUndefined();
    expect(env.reportKind).toBe('suite');
    expect(env.windowPartitionKey).toMatch(/^[0-9a-f]{64}$/);
  });

  it('derives harnessVersion and mode from windowInputs (single source of truth)', () => {
    const built = buildSuiteReport(suiteInput());
    const env = built.semanticEnvelope as Record<string, unknown>;
    // SuiteReportBuildInput has no top-level harnessVersion/mode; the envelope
    // root binds to windowInputs (the same source as the window-partition key).
    expect(env.harnessVersion).toBe(windowInputs.harnessVersion);
    expect(env.mode).toBe(windowInputs.mode);
  });

  it('reportSha256 is unaffected by evidenceOccurrenceId or provenance (determinism)', () => {
    const a = buildSuiteReport(
      suiteInput({ evidenceOccurrenceId: 'occ-1', provenance: { sourceCommit: 'sha-a' } }),
    );
    const b = buildSuiteReport(
      suiteInput({ evidenceOccurrenceId: 'occ-2', provenance: { sourceCommit: 'sha-b' } }),
    );
    expect(a.reportSha256).toBe(b.reportSha256);
  });

  it('view.suiteOutcome is derived from scenario results via the suite reducer', () => {
    const built = buildSuiteReport(suiteInput());
    expect(built.view.suiteOutcome).toBe('pass'); // single pass scenario
    expect(built.suiteOutcome).toBe('pass');
    expect(built.view.reportSha256).toBe(built.reportSha256);
    expect(built.view.evidenceOccurrenceId).toBe('occ-1');
  });

  it('includes per-scenario completeness in the semantic envelope', () => {
    const built = buildSuiteReport(suiteInput());
    const env = built.semanticEnvelope as Record<string, unknown>;
    const results = env.scenarioResults as Array<Record<string, unknown>>;
    expect(results[0].completeness).toEqual({ resumed: 'complete', stateless: 'complete' });
  });

  it('serializeReport produces canonical JSON bytes containing reportSha256', () => {
    const built = buildSuiteReport(suiteInput());
    const bytes = serializeReport(built.report);
    const text = new TextDecoder().decode(bytes);
    expect(text).toContain(`"reportSha256":"${built.reportSha256}"`);
    expect(text.endsWith('}')).toBe(true);
  });
});

describe('ScenarioResultEntry closed reason->outcome invariant (B2)', () => {
  it('accepts each outcome variant with its matching reason subtype', () => {
    const entries: ScenarioResultEntry[] = [
      {
        scenarioId: 'p',
        outcome: 'pass',
        reason: 'ratio_pass',
        numerator: '1',
        denominator: '1',
        displayRatio: '1.000000',
        totalCost: null,
        completeness: complete,
      },
      {
        scenarioId: 'r',
        outcome: 'regression',
        reason: 'ratio_regression',
        numerator: '2',
        denominator: '1',
        displayRatio: '2.000000',
        totalCost: null,
        completeness: complete,
      },
      {
        scenarioId: 'i',
        outcome: 'inconclusive',
        reason: 'telemetry_incomplete',
        numerator: null,
        denominator: null,
        displayRatio: null,
        totalCost: null,
        completeness: complete,
      },
      {
        scenarioId: 'c',
        outcome: 'contract_regression',
        reason: 'prefix_drift',
        numerator: null,
        denominator: null,
        displayRatio: null,
        totalCost: null,
        completeness: complete,
      },
      {
        scenarioId: 'v',
        outcome: 'invalid',
        reason: 'equivalence_mismatch',
        numerator: null,
        denominator: null,
        displayRatio: null,
        totalCost: null,
        completeness: complete,
      },
    ];
    const built = buildSuiteReport({ ...suiteInput(), scenarioResults: entries });
    // mixed invalid-present suite -> suite reducer yields invalid
    expect(built.suiteOutcome).toBe('invalid');
  });

  it('rejects a contradictory (outcome, reason) entry at compile time', () => {
    // @ts-expect-error - 'prefix_drift' is not a valid reason for outcome 'pass'
    const bad: ScenarioResultEntry = {
      scenarioId: 'x',
      outcome: 'pass',
      reason: 'prefix_drift',
      numerator: null,
      denominator: null,
      displayRatio: null,
      totalCost: null,
      completeness: complete,
    };
    expect(bad).toBeDefined();
  });
});

describe('buildLiveObservationReport', () => {
  it('builds an inconclusive live-observation report with no window key or suite fields', () => {
    const built = buildLiveObservationReport({
      harnessVersion: 'h1',
      mode: 'live',
      profileDigest: 'profile-digest',
      resumedStrategyIdentity: resumedIdentity,
      observationReason: 'live_stateless_comparison_unavailable',
      evidenceOccurrenceId: 'live-1',
      provenance: {
        sourceCommit: 'sha',
        producingRunId: 'run',
        runnerIdentity: 'runner',
        os: 'linux',
        nodeVersion: 'v20',
        timestamp: '2026-01-01T00:00:00Z',
      },
    });
    expect(built.reportSha256).toMatch(/^[0-9a-f]{64}$/);
    const env = built.semanticEnvelope as Record<string, unknown>;
    expect(env.reportKind).toBe('liveObservation');
    expect(env.statelessStrategyIdentity).toBeNull();
    expect(env.outcome).toBe('inconclusive');
    expect(env.windowPartitionKey).toBeUndefined();
    expect(env.suiteId).toBeUndefined();
    expect(env.scenarioResults).toBeUndefined();
  });
});

describe('buildSuiteReport envelope integrity', () => {
  it('scenarioResults order affects reportSha256 (canonical arrays are order-sensitive)', () => {
    const two: ScenarioResultEntry[] = [
      passEntry('s1', '10', '1000', '0.010000'),
      passEntry('s2', '20', '1000', '0.020000'),
    ];
    const base = suiteInput();
    const a = buildSuiteReport({ ...base, scenarioResults: two });
    const b = buildSuiteReport({ ...base, scenarioResults: [two[1], two[0]] });
    expect(a.reportSha256).not.toBe(b.reportSha256);
  });

  it('owns its envelope: caller mutation after build does not change the snapshot', () => {
    const localResults = [passEntry('s1', '10', '1000', '0.010000')];
    const localTotals = {
      numerator: '10',
      denominator: '1000',
      displayRatio: '0.010000',
      totalCost: null,
    };
    const input = { ...suiteInput(), scenarioResults: localResults, totals: localTotals };
    const built = buildSuiteReport(input);
    const sha = built.reportSha256;
    // mutate the caller's input arrays/objects after the build
    localResults.push(passEntry('s9', '99', '1000', '0.099000'));
    localTotals.numerator = '999';
    // the built envelope + sha are unaffected (recompute matches the snapshot)
    expect(reportSha256Of(built.semanticEnvelope)).toBe(sha);
    expect(built.reportSha256).toBe(sha);
  });

  it('owns window inputs: post-build mutation does not affect report, view, or graduation key', () => {
    const localWindow = freshWindowInputs();
    const built = buildSuiteReport({ ...suiteInput(), windowInputs: localWindow });
    const sha = built.reportSha256;
    const viewSnapshot = built.view.windowPartitionInputs;
    // Simulate a caller mutating its (structurally shared) windowInputs after
    // build, bypassing readonly via a cast. The builder must have cloned.
    const mutable = localWindow as unknown as {
      harnessVersion: string;
      mode: string;
      profileDigest: string;
      resumedStrategyIdentity: { adapterId: string };
    };
    mutable.harnessVersion = 'mutated';
    mutable.mode = 'live';
    mutable.profileDigest = 'mutated';
    mutable.resumedStrategyIdentity.adapterId = 'mutated';
    // envelope + sha unaffected (envelope holds an owned clone)
    expect(reportSha256Of(built.semanticEnvelope)).toBe(sha);
    expect(built.reportSha256).toBe(sha);
    // view's owned snapshot unaffected
    expect(built.view.windowPartitionInputs).toEqual(viewSnapshot);
    // graduation recomputes the same partition key from the owned view snapshot
    const r = evaluateGraduationWindow([built.view, built.view, built.view]);
    expect(r.invalid).toBe(false);
  });

  it('windowPartitionKey in the envelope equals windowPartitionKeyDigest(windowInputs)', () => {
    const built = buildSuiteReport(suiteInput());
    const env = built.semanticEnvelope as Record<string, unknown>;
    expect(env.windowPartitionKey).toBe(built.windowPartitionKey);
  });
});

describe('report -> graduation closed loop (cross-module)', () => {
  it('three built suite-report views graduate to candidateEligible', () => {
    const views = ['occ-1', 'occ-2', 'occ-3'].map(
      (id) => buildSuiteReport(suiteInput({ evidenceOccurrenceId: id })).view,
    );
    const result = evaluateGraduationWindow(views);
    expect(result.invalid).toBe(false);
    expect(result.candidateEligible).toBe(true);
    expect(result.observationCount).toBe(3);
    expect(result.windowPartitionKey).toMatch(/^[0-9a-f]{64}$/);
  });
});
