/**
 * Evidence report builder (#54 § Report schema).
 *
 * Discriminated `oneOf` [suite, liveObservation] reports. `reportSha256` is
 * computed over the semantic envelope (root minus `reportSha256`, human
 * summary, `evidenceOccurrenceId`, and provenance-only fields; `reportKind` is
 * included). Suite reports carry the full window-partition inputs so graduation
 * can recompute the key; they do NOT carry `candidateEligible` (window-level).
 * Live-observation reports carry no `windowPartitionKey` and no suite fields.
 */
import { canonicalJsonBytes, type CanonicalJsonValue } from '../canonical-json/index.js';
import { displayRatio, scaledToDecimalString } from './decimal.js';
import {
  reportSha256Of,
  strategyIdentityDigest,
  windowPartitionKeyDigest,
  type CostEvaluationStrategyIdentityV1,
  type SuiteReportView,
  type WindowPartitionInputs,
} from './domain.js';
import { suiteReducer, type ScenarioOutcome, type ScenarioReason } from './outcome.js';

export const REPORT_SCHEMA_VERSION = 1 as const;

export interface ReportProvenance {
  readonly sourceCommit: string | null;
  readonly producingRunId: string | null;
  readonly runnerIdentity: string | null;
  readonly os: string | null;
  readonly nodeVersion: string | null;
  readonly timestamp: string | null;
}

export interface ScenarioResultEntry {
  readonly scenarioId: string;
  readonly outcome: ScenarioOutcome;
  readonly reason: ScenarioReason;
  readonly numerator: string | null;
  readonly denominator: string | null;
  readonly displayRatio: string | null;
  readonly totalCost: string | null;
}

export interface SuiteTotals {
  readonly numerator: string | null;
  readonly denominator: string | null;
  readonly displayRatio: string | null;
  readonly totalCost: string | null;
}

export interface SuiteReportBuildInput {
  readonly harnessVersion: string;
  readonly suiteId: string;
  readonly suiteVersion: string;
  readonly mode: string;
  readonly windowInputs: WindowPartitionInputs;
  readonly scenarioResults: readonly ScenarioResultEntry[];
  readonly totals: SuiteTotals;
  readonly evidenceOccurrenceId: string;
  readonly provenance: ReportProvenance;
}

export interface BuiltSuiteReport {
  readonly report: CanonicalJsonValue;
  readonly semanticEnvelope: CanonicalJsonValue;
  readonly reportSha256: string;
  readonly windowPartitionKey: string;
  readonly suiteOutcome: ScenarioOutcome;
  readonly view: SuiteReportView;
}

export interface LiveObservationReportBuildInput {
  readonly harnessVersion: string;
  readonly mode: 'live';
  readonly profileDigest: string;
  readonly resumedStrategyIdentity: CostEvaluationStrategyIdentityV1;
  readonly observationReason: 'live_stateless_comparison_unavailable';
  readonly evidenceOccurrenceId: string;
  readonly provenance: ReportProvenance;
}

export interface BuiltLiveObservationReport {
  readonly report: CanonicalJsonValue;
  readonly semanticEnvelope: CanonicalJsonValue;
  readonly reportSha256: string;
}

const SUITE_PROVENANCE_FIELDS = [
  'sourceCommit',
  'producingRunId',
  'runnerIdentity',
  'os',
  'nodeVersion',
  'timestamp',
] as const;

function provenanceObject(p: ReportProvenance): CanonicalJsonValue {
  const obj: Record<string, CanonicalJsonValue> = {};
  for (const k of SUITE_PROVENANCE_FIELDS) {
    obj[k] = (p as unknown as Record<string, string | null>)[k];
  }
  return obj;
}

/**
 * Deep-clone a caller-supplied JSON value into an owned `CanonicalJsonValue`
 * tree. The report builder must not retain live references into the caller's
 * input: a caller mutating its arrays/objects after `buildSuiteReport` would
 * otherwise silently break the `reportSha256` <-> serialized-bytes invariant
 * (the sha is a build-time snapshot over the envelope). Cloning at build time
 * makes the envelope self-contained.
 */
function cloneJson(value: unknown): CanonicalJsonValue {
  if (value === null) return null;
  const type = typeof value;
  if (type === 'boolean' || type === 'number' || type === 'string') {
    return value as CanonicalJsonValue;
  }
  if (Array.isArray(value)) {
    return value.map(cloneJson);
  }
  if (type === 'object') {
    const obj: Record<string, CanonicalJsonValue> = {};
    for (const key of Object.keys(value as object)) {
      obj[key] = cloneJson((value as Record<string, unknown>)[key]);
    }
    return obj;
  }
  // Non-JSON values (bigint/function/symbol/undefined) are rejected downstream
  // by canonicalJsonBytes; surface them unchanged so the error is attributed
  // to the field that carried them.
  return value as unknown as CanonicalJsonValue;
}

/** Build a suite report, its semantic envelope, reportSha256, and graduation view. */
export function buildSuiteReport(input: SuiteReportBuildInput): BuiltSuiteReport {
  const suiteOutcome = suiteReducer(input.scenarioResults.map((r) => r.outcome));
  const windowPartitionKey = windowPartitionKeyDigest(input.windowInputs);
  const semanticEnvelope: Record<string, CanonicalJsonValue> = {
    schemaVersion: REPORT_SCHEMA_VERSION,
    harnessVersion: input.harnessVersion,
    reportKind: 'suite',
    suiteId: input.suiteId,
    suiteVersion: input.suiteVersion,
    mode: input.mode,
    profileDigest: input.windowInputs.profileDigest,
    providerId: input.windowInputs.providerId,
    modelId: input.windowInputs.modelId,
    fixtureSuiteDigest: input.windowInputs.fixtureSuiteDigest,
    prefixContractVersion: input.windowInputs.prefixContractVersion,
    resumedStrategyIdentity: cloneJson(input.windowInputs.resumedStrategyIdentity),
    statelessStrategyIdentity: cloneJson(input.windowInputs.statelessStrategyIdentity),
    windowPartitionKey,
    scenarioResults: cloneJson(input.scenarioResults),
    totals: cloneJson(input.totals),
  };
  const reportSha256 = reportSha256Of(semanticEnvelope);
  const report: Record<string, CanonicalJsonValue> = {
    ...semanticEnvelope,
    evidenceOccurrenceId: input.evidenceOccurrenceId,
    reportSha256,
    provenance: provenanceObject(input.provenance),
  };
  const view: SuiteReportView = {
    reportSha256,
    semanticEnvelope,
    evidenceOccurrenceId: input.evidenceOccurrenceId,
    suiteOutcome,
    windowPartitionInputs: input.windowInputs,
  };
  return { report, semanticEnvelope, reportSha256, windowPartitionKey, suiteOutcome, view };
}

/** Build a live-observation report (no windowPartitionKey, no suite fields). */
export function buildLiveObservationReport(
  input: LiveObservationReportBuildInput,
): BuiltLiveObservationReport {
  const semanticEnvelope: Record<string, CanonicalJsonValue> = {
    schemaVersion: REPORT_SCHEMA_VERSION,
    harnessVersion: input.harnessVersion,
    reportKind: 'liveObservation',
    mode: input.mode,
    profileDigest: input.profileDigest,
    resumedStrategyIdentity: cloneJson(input.resumedStrategyIdentity),
    statelessStrategyIdentity: null,
    observationReason: input.observationReason,
    outcome: 'inconclusive',
  };
  const reportSha256 = reportSha256Of(semanticEnvelope);
  const report: Record<string, CanonicalJsonValue> = {
    ...semanticEnvelope,
    evidenceOccurrenceId: input.evidenceOccurrenceId,
    reportSha256,
    provenance: provenanceObject(input.provenance),
  };
  return { report, semanticEnvelope, reportSha256 };
}

/** Serialize a full report to canonical JSON bytes (for stdout / file output). */
export function serializeReport(report: CanonicalJsonValue): Uint8Array {
  return canonicalJsonBytes(report);
}

// Re-export helpers used by callers that build scenario entries from bigint costs.
export { displayRatio, scaledToDecimalString, strategyIdentityDigest };
export type { CostEvaluationStrategyIdentityV1, WindowPartitionInputs } from './domain.js';
