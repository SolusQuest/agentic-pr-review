import { createHash } from 'node:crypto';
import { describe, expect, test } from 'vitest';
import {
  QUALITY,
  CORPUS,
  CANARIES,
  verifyCase,
  verifyQuality,
  verifySuccessor,
  verifyGrowth,
  verifyCleanup,
  verifyRecovery,
} from '../../runtime/tests/SolusEvaluationFixture/report.mjs';

// Authored report DATA tests only. These rows cannot create an EvaluationSubject
// or establish actual runtime/package execution; verify.mjs owns that evidence.
const h = (value: string) => createHash('sha256').update(value).digest('hex');
const source = { commit: '1'.repeat(40), tree: '2'.repeat(40), version: '0.1.0-internal.4' };
function row(index = 0): any {
  const [id, code, sends, tools] = QUALITY[index];
  const failed = code === 'ExecutionFailed';
  const evidenceFailed = ['RequiredToolMissing', 'RequiredObservationMissing'].includes(code);
  const safe = ['cs-safe', 'ts-safe', 'no-required-tool', 'irrelevant-tool'].includes(id);
  const findings = failed || safe ? 0 : code === 'DuplicateObservation' ? 2 : 1;
  const expected = ['cs-safe', 'ts-safe', 'safe-invention'].includes(id) ? 0 : 1;
  const missing = code === 'ExpectedFindingMissing' ? 1 : 0;
  return {
    schema: 'apr-solus-evaluation-case-v1',
    profile: 'cr1-solus-internal4-dev',
    package_closure_sha256: 'ade505cdff69a526ae95109b6258004304e7b504b2eb3b7c9b1fec3964d74949',
    host_status: null,
    preparation_failure: null,
    context_rejection: 'None',
    capture_status: 'NotRequested',
    mode: 'fresh',
    case_id: id,
    source_commit: source.commit,
    source_tree: source.tree,
    source_clean: true,
    package_version: source.version,
    context_format: 2,
    context_compatibility: 1,
    durable_accepted: false,
    recovered_without_work: false,
    readback_verified: false,
    product_accepted: !failed,
    candidate_eligible: !failed,
    sends,
    native_creates: 0,
    admission_guards: failed ? 0 : 10,
    admission: 'Fresh',
    termination: failed ? 'InvalidResult' : 'ReviewCompleted',
    outcome: {
      case_id: id,
      corpus_sha256: CORPUS,
      case_sha256: h('case-' + id),
      configuration_sha256: h('config'),
      attempt_sha256: h('attempt-' + id),
      execution_sha256: failed ? null : h('execution-' + id),
      source_commit: source.commit,
      source_tree: source.tree,
      source_clean: true,
      mode: 'deterministic',
      execution_status: failed ? 'Failed' : 'Completed',
      evidence_status: failed ? 'NotEvaluated' : evidenceFailed ? 'Failed' : 'Passed',
      scenario_status:
        failed || evidenceFailed ? 'NotEvaluated' : code === 'Scored' ? 'Passed' : 'Failed',
      model_status:
        failed || evidenceFailed ? 'NotEvaluated' : findings ? 'Unadjudicated' : 'Adjudicated',
      code,
      failure_source: failed ? 'Unknown' : 'None',
      failure_kind: failed ? 'Unknown' : 'None',
      finding_count: findings,
      tool_observation_count: failed ? 0 : tools,
      expected_defects: expected,
      structural_matches: failed || evidenceFailed ? 0 : expected - missing,
      structurally_missing_defects: failed || evidenceFailed ? 0 : missing,
      duplicate_observations: code === 'DuplicateObservation' ? 1 : 0,
      prohibited_observations: code === 'ProhibitedFinding' ? 1 : 0,
      adjudicated_true: 0,
      adjudicated_false: 0,
      adjudicated_defects: 0,
      unadjudicated_findings: findings,
    },
    capture_valid: null,
    current_attempts: sends,
    historical_attempts: 0,
    historical_rounds: 0,
    current_tools: tools,
    historical_tool_results: 0,
    provider_inputs: 28,
    logical_request_bytes: 20000,
    logical_response_bytes: 300,
    wire_request_bytes: 40000,
    wire_response_bytes: 2000,
    input_tokens: sends * 10,
    output_tokens: sends * 5,
    input_reservation: 8192,
    output_reservation: 1024,
    predecessor: null,
    capture: null,
  };
}
function captured(round = 1): any {
  return {
    Payload: h('payload-' + round),
    Bytes: round * 20000,
    Records: round * 10,
    Attempts: round * 5,
    Rounds: round,
    Execution: h('execution-' + round),
    Round: h('round-' + round),
    Work: h('work-' + round),
    Checkpoint: h('checkpoint-' + round),
    RuntimeConfiguration: h('runtime'),
    ProviderConfiguration: h('provider'),
  };
}
function seed(): any {
  return {
    ...row(),
    mode: 'seed',
    durable_accepted: true,
    capture_valid: true,
    capture: captured(),
  };
}
function successor(previous: any): any {
  const next = row();
  const round = previous.capture.Rounds + 1;
  return {
    ...next,
    mode: 'review',
    durable_accepted: true,
    capture_valid: true,
    admission: 'Supplied',
    historical_rounds: previous.capture.Rounds,
    historical_attempts: previous.capture.Attempts,
    historical_tool_results: 4,
    predecessor: previous.capture,
    capture: captured(round),
    outcome: {
      ...next.outcome,
      attempt_sha256: h('next-attempt-' + round),
      execution_sha256: h('next-execution-' + round),
    },
  };
}
function refused(previous: any): any {
  const value = row(8);
  return {
    ...value,
    mode: 'review',
    termination: 'ResourceLimit',
    case_id: 'cs-defect',
    predecessor: previous.capture,
    outcome: { ...value.outcome, case_id: 'cs-defect' },
  };
}

describe('managed Solus independent report admission', () => {
  test('admits the complete declared positive and rejection population', () => {
    expect(
      verifyQuality(
        QUALITY.map((_: unknown, index: number) => row(index)),
        source,
      ),
    ).toBe(true);
  });
  test.each([
    'removed',
    'duplicate',
    'reordered',
    'wrong-code',
    'source',
    'pin',
    'dirty',
    'unknown-field',
    'missing-field',
    'empty-success',
  ])('rejects %s quality evidence', (mutation) => {
    const rows = QUALITY.map((_: unknown, index: number) => row(index));
    if (mutation === 'removed') rows.pop();
    if (mutation === 'duplicate') rows[1] = rows[0];
    if (mutation === 'reordered') [rows[0], rows[1]] = [rows[1], rows[0]];
    if (mutation === 'wrong-code') rows[0].outcome.code = 'ExpectedFindingMissing';
    if (mutation === 'source') rows[0].outcome.source_commit = 'f'.repeat(40);
    if (mutation === 'pin') rows[0].package_version = '0.1.0-internal.3';
    if (mutation === 'dirty') rows[0].source_clean = false;
    if (mutation === 'unknown-field') rows[0].private_source = 'unapproved';
    if (mutation === 'missing-field') delete rows[0].current_attempts;
    if (mutation === 'empty-success') rows[8].outcome.execution_status = 'Completed';
    expect(() => verifyQuality(rows, source)).toThrow();
  });
  test.each(CANARIES)('rejects private canary %s in the ordinary channel', (canary) => {
    const value = row();
    value.admission = canary;
    expect(() => verifyCase(value, source, 'fresh', 'cs-defect')).toThrow();
  });
  test('keeps unknown usage distinct from a measured zero', () => {
    const value = row();
    value.input_tokens = null;
    value.output_tokens = 0;
    expect(verifyCase(value, source, 'fresh', 'cs-defect').input_tokens).toBeNull();
    value.input_tokens = -1;
    expect(() => verifyCase(value, source, 'fresh', 'cs-defect')).toThrow();
  });
  test('admits a separately captured new round and bounded growth refusal', () => {
    const first = seed();
    const second = successor(first);
    expect(verifySuccessor(first, second, source)).toBe(true);
    expect(verifyGrowth([first, second], refused(second), source, false)).toBe(true);
  });
  test.each([
    'old-execution',
    'old-round',
    'wrong-parent',
    'shrunk-history',
    'missing-growth',
    'cohort',
    'historical-as-current',
  ])('rejects %s restored evidence', (mutation) => {
    const first = seed();
    const second = successor(first);
    if (mutation === 'old-execution') second.capture.Execution = first.capture.Execution;
    if (mutation === 'old-round') second.capture.Round = first.capture.Round;
    if (mutation === 'wrong-parent') second.predecessor = captured(3);
    if (mutation === 'shrunk-history') second.historical_attempts = 0;
    if (mutation === 'missing-growth') second.capture.Records = first.capture.Records;
    if (mutation === 'cohort') second.outcome.configuration_sha256 = h('different');
    if (mutation === 'historical-as-current') second.current_tools += 4;
    expect(() => verifySuccessor(first, second, source)).toThrow();
  });
  test('cannot turn recovery-only or incomplete work into a new quality review', () => {
    const first = seed();
    const next = successor(first);
    next.recovered_without_work = true;
    next.outcome = null;
    next.sends = 0;
    expect(() => verifySuccessor(first, next, source)).toThrow();
    const second = successor(first);
    const refusal = refused(second);
    refusal.outcome.model_status = 'Adjudicated';
    expect(() => verifyGrowth([first, second], refusal, source, false)).toThrow();
  });
  test('cannot label a refusal as injected after actual dispatch', () => {
    const first = seed();
    const second = successor(first);
    const refusal = refused(second);
    refusal.mode = 'input-over';
    expect(() => verifyGrowth([first, second], refusal, source, true)).toThrow();
  });
  test('requires actual cleanup acknowledgement', () => {
    expect(verifyCleanup('removed')).toBe(true);
    expect(() => verifyCleanup('retained')).toThrow();
  });
});

// Recovery preserves logical work/effects while allowing existing R4 physical
// predecessor retention. Whole-storage equality would reject valid recovery.
describe('recovery effect attribution', () => {
  const before = [{ operation: 'sticky', written: true }];
  const recovered = {
    recovered_without_work: true,
    sends: 0,
    outcome: null,
    current_attempts: null,
    current_tools: null,
  };
  const retained = { operation: 'upload', kind: 'candidate', from_current_capture: false };
  test('allows physical predecessor retention without inventing a new review', () => {
    expect(verifyRecovery(before, [...before, retained], recovered)).toEqual({ candidate: 1 });
  });
  test.each([
    'sticky',
    'inline',
    'claim',
    'current-capture',
    'provider',
    'tool',
    'rewritten-prefix',
  ])('rejects %s replay', (kind) => {
    const state = { ...recovered };
    const after = [...before, { ...retained }];
    if (kind === 'sticky' || kind === 'inline') after[1].operation = kind;
    if (kind === 'claim') after[1].kind = 'context_claim';
    if (kind === 'current-capture') after[1].from_current_capture = true;
    if (kind === 'provider') state.sends = 1;
    if (kind === 'tool') state.current_tools = 1;
    if (kind === 'rewritten-prefix') after[0] = { operation: 'sticky', written: false };
    expect(() => verifyRecovery(before, after, state)).toThrow();
  });
});
