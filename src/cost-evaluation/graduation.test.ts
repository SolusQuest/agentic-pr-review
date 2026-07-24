import { describe, expect, it } from 'vitest';
import { evaluateGraduationWindow } from './graduation.js';
import {
  reportSha256Of,
  type CostEvaluationStrategyIdentityV1,
  type SuiteReportView,
  type WindowPartitionInputs,
} from './domain.js';
import type { ScenarioOutcome } from './outcome.js';

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
const baseWindowInputs: WindowPartitionInputs = {
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
const otherWindowInputs: WindowPartitionInputs = {
  ...baseWindowInputs,
  profileDigest: 'other-profile',
};

function suiteReport(
  outcome: ScenarioOutcome,
  occurrenceId: string,
  over: Partial<SuiteReportView> = {},
): SuiteReportView {
  const semanticEnvelope = { reportKind: 'suite', outcome };
  return {
    reportSha256: reportSha256Of(semanticEnvelope),
    semanticEnvelope,
    evidenceOccurrenceId: occurrenceId,
    suiteOutcome: outcome,
    windowPartitionInputs: baseWindowInputs,
    ...over,
  };
}

describe('evaluateGraduationWindow', () => {
  it('three distinct-occurrence passes -> candidateEligible', () => {
    const r = evaluateGraduationWindow([
      suiteReport('pass', 'a'),
      suiteReport('pass', 'b'),
      suiteReport('pass', 'c'),
    ]);
    expect(r.invalid).toBe(false);
    expect(r.passStreak).toBe(3);
    expect(r.candidateEligible).toBe(true);
    expect(r.graduationBlocked).toBe(false);
    expect(r.observationCount).toBe(3);
    expect(r.windowPartitionKey).toMatch(/^[0-9a-f]{64}$/);
  });

  it('regression interrupts the pass streak (not eligible)', () => {
    const r = evaluateGraduationWindow([
      suiteReport('pass', 'a'),
      suiteReport('pass', 'b'),
      suiteReport('regression', 'c'),
      suiteReport('pass', 'd'),
      suiteReport('pass', 'e'),
    ]);
    expect(r.passStreak).toBe(2);
    expect(r.candidateEligible).toBe(false);
    expect(r.regressionStreak).toBe(0); // reset by later passes
  });

  it('three consecutive regressions -> costRegressionBlocked', () => {
    const r = evaluateGraduationWindow([
      suiteReport('regression', 'a'),
      suiteReport('regression', 'b'),
      suiteReport('regression', 'c'),
    ]);
    expect(r.regressionStreak).toBe(3);
    expect(r.costRegressionBlocked).toBe(true);
    expect(r.graduationBlocked).toBe(true);
    expect(r.candidateEligible).toBe(false);
  });

  it('costRegressionBlocked is a latch: three later passes cannot clear it', () => {
    const r = evaluateGraduationWindow([
      suiteReport('regression', 'a'),
      suiteReport('regression', 'b'),
      suiteReport('regression', 'c'), // latch costRegressionBlocked
      suiteReport('pass', 'd'),
      suiteReport('pass', 'e'),
      suiteReport('pass', 'f'), // passStreak reaches 3, but the block persists
    ]);
    expect(r.costRegressionBlocked).toBe(true);
    expect(r.graduationBlocked).toBe(true);
    expect(r.passStreak).toBe(3);
    expect(r.candidateEligible).toBe(false);
  });

  it('report_sha_mismatch takes precedence over conflicting_evidence', () => {
    // First report carries a wrong sha; the second shares its occurrenceId
    // with a different outcome (which would otherwise be conflicting_evidence).
    // The sha check (step 1) runs before the dedup check (step 3).
    const r = evaluateGraduationWindow([
      suiteReport('pass', 'a', { reportSha256: 'deadbeef' }),
      suiteReport('regression', 'a'),
    ]);
    expect(r.invalid).toBe(true);
    expect(r.invalidReason).toBe('report_sha_mismatch');
  });

  it('mixed_window takes precedence over conflicting_evidence', () => {
    // Two reports share occurrenceId 'a' (would be conflicting_evidence) AND
    // have different window inputs. The window check (step 2) runs first.
    const r = evaluateGraduationWindow([
      suiteReport('pass', 'a'),
      suiteReport('regression', 'a', { windowPartitionInputs: otherWindowInputs }),
    ]);
    expect(r.invalid).toBe(true);
    expect(r.invalidReason).toBe('mixed_window');
  });

  it('contract_regression -> contractViolationBlocked (blocks even with 3 passes)', () => {
    const r = evaluateGraduationWindow([
      suiteReport('pass', 'a'),
      suiteReport('contract_regression', 'b'),
      suiteReport('pass', 'c'),
      suiteReport('pass', 'd'),
      suiteReport('pass', 'e'),
    ]);
    expect(r.contractViolationBlocked).toBe(true);
    expect(r.graduationBlocked).toBe(true);
    expect(r.candidateEligible).toBe(false);
  });

  it('inconclusive skips without resetting the pass streak', () => {
    const r = evaluateGraduationWindow([
      suiteReport('pass', 'a'),
      suiteReport('inconclusive', 'b'),
      suiteReport('pass', 'c'),
      suiteReport('pass', 'd'),
    ]);
    expect(r.passStreak).toBe(3);
    expect(r.candidateEligible).toBe(true);
  });

  it('duplicate occurrence + same digest counts once', () => {
    const dup = suiteReport('pass', 'a');
    const r = evaluateGraduationWindow([
      dup,
      { ...dup },
      suiteReport('pass', 'b'),
      suiteReport('pass', 'c'),
    ]);
    expect(r.observationCount).toBe(3);
    expect(r.candidateEligible).toBe(true);
  });

  it('same occurrence + different digest -> conflicting_evidence (invalid)', () => {
    const r = evaluateGraduationWindow([
      suiteReport('pass', 'a'),
      suiteReport('regression', 'a'), // same occurrenceId, different outcome/digest
    ]);
    expect(r.invalid).toBe(true);
    expect(r.invalidReason).toBe('conflicting_evidence');
  });

  it('mixed window partition -> invalid', () => {
    const r = evaluateGraduationWindow([
      suiteReport('pass', 'a'),
      suiteReport('pass', 'b', { windowPartitionInputs: otherWindowInputs }),
    ]);
    expect(r.invalid).toBe(true);
    expect(r.invalidReason).toBe('mixed_window');
  });

  it('reportSha256 mismatch -> invalid', () => {
    const r = evaluateGraduationWindow([suiteReport('pass', 'a', { reportSha256: 'deadbeef' })]);
    expect(r.invalid).toBe(true);
    expect(r.invalidReason).toBe('report_sha_mismatch');
  });

  it('empty window -> valid but not eligible', () => {
    const r = evaluateGraduationWindow([]);
    expect(r.invalid).toBe(false);
    expect(r.candidateEligible).toBe(false);
    expect(r.windowPartitionKey).toBeNull();
  });
});
