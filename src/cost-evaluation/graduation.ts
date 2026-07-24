/**
 * In-process three-suite graduation simulation (#54 § Suite & graduation).
 *
 * `evaluateGraduationWindow` is a pure function over a validated suite-report
 * array (caller order authoritative). It recomputes each `reportSha256`,
 * recomputes each window partition key from stored strategy identities (mixed
 * -> invalid), dedups by `evidenceOccurrenceId` (same occurrence + different
 * digest -> `conflicting_evidence`), and runs the streak state machine. It does
 * NOT re-load source sidecars and is NOT authoritative graduation.
 */
import type { ScenarioOutcome } from './outcome.js';
import { reportSha256Of, windowPartitionKeyDigest, type SuiteReportView } from './domain.js';

export const GRADUATION_RESULT_SCHEMA_VERSION = 1 as const;

export type GraduationInvalidReason =
  | 'report_sha_mismatch'
  | 'mixed_window'
  | 'conflicting_evidence';

export interface GraduationSimulationResultV1 {
  readonly schemaVersion: 1;
  readonly passStreak: number;
  readonly regressionStreak: number;
  readonly costRegressionBlocked: boolean;
  readonly contractViolationBlocked: boolean;
  readonly graduationBlocked: boolean;
  readonly candidateEligible: boolean;
  readonly windowPartitionKey: string | null;
  readonly invalid: boolean;
  readonly invalidReason?: GraduationInvalidReason;
  readonly observationCount: number;
}

function invalidResult(
  reason: GraduationInvalidReason,
  observationCount: number,
): GraduationSimulationResultV1 {
  return {
    schemaVersion: GRADUATION_RESULT_SCHEMA_VERSION,
    passStreak: 0,
    regressionStreak: 0,
    costRegressionBlocked: false,
    contractViolationBlocked: false,
    graduationBlocked: false,
    candidateEligible: false,
    windowPartitionKey: null,
    invalid: true,
    invalidReason: reason,
    observationCount,
  };
}

/**
 * Run the graduation simulation over an ordered array of validated suite
 * reports. Streak state machine:
 *   pass -> passStreak++, regressionStreak = 0
 *   regression -> regressionStreak++, passStreak = 0
 *   contract_regression -> contractViolationBlocked = true, passStreak = 0
 *   inconclusive / invalid -> skip (no increment, no reset)
 * `costRegressionBlocked = regressionStreak >= 3`;
 * `graduationBlocked = costRegressionBlocked || contractViolationBlocked`;
 * `candidateEligible = passStreak >= 3 && !graduationBlocked`.
 */
export function evaluateGraduationWindow(
  reports: readonly SuiteReportView[],
): GraduationSimulationResultV1 {
  // 1. Recompute + verify each reportSha256 from the semantic envelope.
  for (const r of reports) {
    if (reportSha256Of(r.semanticEnvelope) !== r.reportSha256) {
      return invalidResult('report_sha_mismatch', reports.length);
    }
  }
  // 2. Recompute window partition keys; require all equal.
  const keys = reports.map((r) => windowPartitionKeyDigest(r.windowPartitionInputs));
  const firstKey = keys.length > 0 ? keys[0] : null;
  for (let i = 1; i < keys.length; i++) {
    if (keys[i] !== firstKey) {
      return invalidResult('mixed_window', reports.length);
    }
  }
  // 3. Dedup by evidenceOccurrenceId (keep first position).
  const seen = new Map<string, string>();
  const deduped: SuiteReportView[] = [];
  for (const r of reports) {
    const prev = seen.get(r.evidenceOccurrenceId);
    if (prev !== undefined) {
      if (prev !== r.reportSha256) {
        return invalidResult('conflicting_evidence', reports.length);
      }
      continue; // duplicate occurrence + same digest -> count once
    }
    seen.set(r.evidenceOccurrenceId, r.reportSha256);
    deduped.push(r);
  }

  // 4. Streak state machine.
  let passStreak = 0;
  let regressionStreak = 0;
  let costRegressionBlocked = false;
  let contractViolationBlocked = false;
  for (const r of deduped) {
    const outcome: ScenarioOutcome = r.suiteOutcome;
    if (outcome === 'pass') {
      passStreak += 1;
      regressionStreak = 0;
    } else if (outcome === 'regression') {
      regressionStreak += 1;
      passStreak = 0;
    } else if (outcome === 'contract_regression') {
      contractViolationBlocked = true;
      passStreak = 0;
    }
    // inconclusive / invalid -> skip
    if (regressionStreak >= 3) {
      costRegressionBlocked = true;
    }
  }
  const graduationBlocked = costRegressionBlocked || contractViolationBlocked;
  const candidateEligible = passStreak >= 3 && !graduationBlocked;
  return {
    schemaVersion: GRADUATION_RESULT_SCHEMA_VERSION,
    passStreak,
    regressionStreak,
    costRegressionBlocked,
    contractViolationBlocked,
    graduationBlocked,
    candidateEligible,
    windowPartitionKey: firstKey,
    invalid: false,
    observationCount: deduped.length,
  };
}
