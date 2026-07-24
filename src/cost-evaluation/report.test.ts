import { describe, expect, it } from 'vitest';
import { buildLiveObservationReport, buildSuiteReport, serializeReport } from './report.js';
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
  prefixContractVersion: 'pcv1',
  harnessVersion: 'h1',
  mode: 'synthetic',
};
const scenarioResults = [
  {
    scenarioId: 'large_prior_small_delta',
    outcome: 'pass' as const,
    reason: 'ratio_pass' as const,
    numerator: '10',
    denominator: '1000',
    displayRatio: '0.010000',
    totalCost: null,
  },
];
const totals = { numerator: '10', denominator: '1000', displayRatio: '0.010000', totalCost: null };

function suiteInput(
  over: { evidenceOccurrenceId?: string; provenance?: Record<string, string | null> } = {},
) {
  return {
    harnessVersion: 'h1',
    suiteId: 'synthetic-v1',
    suiteVersion: '1',
    mode: 'synthetic',
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

  it('serializeReport produces canonical JSON bytes containing reportSha256', () => {
    const built = buildSuiteReport(suiteInput());
    const bytes = serializeReport(built.report);
    const text = new TextDecoder().decode(bytes);
    expect(text).toContain(`"reportSha256":"${built.reportSha256}"`);
    expect(text.endsWith('}')).toBe(true);
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
