import assert from 'node:assert/strict';

// Declared outside the managed producer and its authored manifest. Negative
// oracle cases verify rejection; they are not successful quality observations.
export const QUALITY = [
  ['cs-defect', 'Scored', 5, 4],
  ['cs-safe', 'Scored', 3, 2],
  ['ts-defect', 'Scored', 4, 3],
  ['ts-safe', 'Scored', 3, 2],
  ['repository-rule', 'Scored', 5, 4],
  ['sticky-only', 'Scored', 3, 2],
  ['no-required-tool', 'RequiredToolMissing', 1, 0],
  ['irrelevant-tool', 'RequiredObservationMissing', 5, 4],
  ['wrong-evidence', 'ExecutionFailed', 5, 4],
  ['wrong-location', 'ExpectedFindingMissing', 5, 4],
  ['safe-invention', 'ProhibitedFinding', 3, 2],
  ['duplicate-proposal', 'DuplicateObservation', 5, 4],
  ['pathless-proposal', 'ExecutionFailed', 5, 4],
];
export const CORPUS = '55a4fa8cca7f081169c4f1f09503c8a5c8f23197c1960ca09fb6266bcb7375b3';
export const CANARIES = [
  'R5_SYNTHETIC_PROVIDER_KEY_PRIVATE',
  'R5_SYNTHETIC_GITHUB_KEY_PRIVATE',
  'R5_PRIVATE_REASONING_CANARY',
  'V1_PRIVATE_CAPTURE_CANARY',
  'S4_PRIOR_ONLY_CLASSIFIED_DATA',
  'S4_CURRENT_ROUND_CLASSIFIED_DATA',
  'APR242_TERMINAL_CANARY',
  'APR242_CANDIDATE_CANARY',
  'APR242_NOTES_CANARY',
  'APR242_POLICY_CANARY',
  'APR242_SOURCE_CANARY',
];
const hash = (value, length = 64) =>
  typeof value === 'string' && new RegExp(`^[a-f0-9]{${length}}$`, 'u').test(value);
const count = (value, maximum = Number.MAX_SAFE_INTEGER) =>
  Number.isSafeInteger(value) && value >= 0 && value <= maximum;
const keys = (value, names) => assert.deepEqual(Object.keys(value).sort(), names.slice().sort());
const CASE_KEYS = [
  'schema',
  'profile',
  'package_closure_sha256',
  'host_status',
  'preparation_failure',
  'context_rejection',
  'capture_status',
  'mode',
  'case_id',
  'source_commit',
  'source_tree',
  'source_clean',
  'package_version',
  'context_format',
  'context_compatibility',
  'durable_accepted',
  'recovered_without_work',
  'readback_verified',
  'product_accepted',
  'candidate_eligible',
  'sends',
  'native_creates',
  'admission_guards',
  'admission',
  'termination',
  'outcome',
  'capture_valid',
  'current_attempts',
  'historical_attempts',
  'historical_rounds',
  'current_tools',
  'historical_tool_results',
  'provider_inputs',
  'logical_request_bytes',
  'logical_response_bytes',
  'wire_request_bytes',
  'wire_response_bytes',
  'input_tokens',
  'output_tokens',
  'input_reservation',
  'output_reservation',
  'predecessor',
  'capture',
];
const OUTCOME_KEYS = [
  'case_id',
  'corpus_sha256',
  'case_sha256',
  'configuration_sha256',
  'attempt_sha256',
  'execution_sha256',
  'source_commit',
  'source_tree',
  'source_clean',
  'mode',
  'execution_status',
  'evidence_status',
  'scenario_status',
  'model_status',
  'code',
  'failure_source',
  'failure_kind',
  'finding_count',
  'tool_observation_count',
  'expected_defects',
  'structural_matches',
  'structurally_missing_defects',
  'duplicate_observations',
  'prohibited_observations',
  'adjudicated_true',
  'adjudicated_false',
  'adjudicated_defects',
  'unadjudicated_findings',
];
const CAPTURE_KEYS = [
  'Payload',
  'Bytes',
  'Records',
  'Attempts',
  'Rounds',
  'Execution',
  'Round',
  'Work',
  'Checkpoint',
  'RuntimeConfiguration',
  'ProviderConfiguration',
];
export function verifyCapture(value) {
  assert.ok(value && typeof value === 'object');
  keys(value, CAPTURE_KEYS);
  for (const key of [
    'Payload',
    'Execution',
    'Round',
    'Work',
    'Checkpoint',
    'RuntimeConfiguration',
    'ProviderConfiguration',
  ])
    assert.ok(hash(value[key]));
  assert.ok(count(value.Bytes, 196608) && value.Bytes > 0);
  assert.ok(count(value.Records, 64) && value.Records > 0);
  assert.ok(count(value.Attempts, 64) && value.Attempts > 0);
  assert.ok(count(value.Rounds, 16) && value.Rounds > 0);
}
export function verifyCase(row, source, mode, id) {
  assert.ok(row && typeof row === 'object');
  keys(row, CASE_KEYS);
  assert.equal(row.schema, 'apr-solus-evaluation-case-v1');
  assert.equal(row.mode, mode);
  assert.equal(row.case_id, id);
  assert.equal(row.source_commit, source.commit);
  assert.equal(row.source_tree, source.tree);
  assert.equal(row.source_clean, true);
  assert.ok(hash(source.commit, 40) && hash(source.tree, 40));
  assert.equal(row.package_version, source.version);
  assert.equal(row.profile, 'cr1-solus-internal4-dev');
  assert.equal(
    row.package_closure_sha256,
    'ade505cdff69a526ae95109b6258004304e7b504b2eb3b7c9b1fec3964d74949',
  );
  assert.equal(row.context_format, 2);
  assert.equal(row.context_compatibility, 1);
  assert.equal(row.native_creates, 0);
  assert.ok(
    [
      null,
      'Reviewed',
      'ReviewedWithInlineWarnings',
      'SkippedUntrustedEvent',
      'SkippedFork',
      'SkippedDraft',
      'SkippedClosed',
      'ConfigurationInvalid',
      'AuthorizationFailed',
      'CredentialsMissing',
      'SnapshotIncomplete',
      'ProviderFailed',
      'AgentResultInvalid',
      'StaleHead',
      'StateConflict',
      'StickyPublicationFailed',
      'OutcomeAmbiguous',
      'Cancelled',
      'InternalFailure',
    ].includes(row.host_status),
  );
  assert.ok(
    [
      null,
      'None',
      'AuthorityUnavailable',
      'CredentialsMissing',
      'InputProfileRejected',
      'HostFailure',
      'Cancelled',
    ].includes(row.preparation_failure),
  );
  assert.ok(
    [
      null,
      'None',
      'UnsupportedContext',
      'ImplementationMismatch',
      'UnsupportedFormat',
      'IncompatibleContext',
      'InvalidContext',
      'InvalidRunTransition',
    ].includes(row.context_rejection),
  );
  assert.ok(
    [null, 'NotRequested', 'Unavailable', 'Delivered', 'Failed'].includes(row.capture_status),
  );
  assert.ok([null, 'Fresh', 'Supplied', 'NotAttempted', 'Rejected'].includes(row.admission));
  assert.ok(
    [
      null,
      'ReviewCompleted',
      'NotStarted',
      'ProviderFailure',
      'Cancelled',
      'DeadlineExceeded',
      'ModelLimit',
      'ToolLimit',
      'TokenLimit',
      'RequestLimit',
      'ResponseLimit',
      'ContextLimit',
      'InvalidResult',
      'HostFailure',
      'ResourceLimit',
    ].includes(row.termination),
  );
  for (const key of [
    'durable_accepted',
    'recovered_without_work',
    'readback_verified',
    'product_accepted',
    'candidate_eligible',
  ])
    assert.equal(typeof row[key], 'boolean');
  assert.ok(row.capture_valid === null || typeof row.capture_valid === 'boolean');
  for (const key of ['sends', 'admission_guards', 'wire_request_bytes', 'wire_response_bytes'])
    assert.ok(count(row[key]));
  assert.ok(row.sends <= 6);
  for (const key of [
    'current_attempts',
    'historical_attempts',
    'historical_rounds',
    'current_tools',
    'historical_tool_results',
    'provider_inputs',
    'logical_request_bytes',
    'logical_response_bytes',
    'input_tokens',
    'output_tokens',
    'input_reservation',
    'output_reservation',
  ])
    assert.ok(row[key] === null || count(row[key]));
  assert.ok(row.current_tools === null || row.current_tools <= 6);
  assert.ok(row.provider_inputs === null || row.provider_inputs <= 32);
  assert.ok(row.logical_request_bytes === null || row.logical_request_bytes <= 65536);
  assert.ok(row.logical_response_bytes === null || row.logical_response_bytes <= 16384);
  if (row.capture !== null) verifyCapture(row.capture);
  if (row.predecessor !== null) verifyCapture(row.predecessor);
  if (row.durable_accepted && !row.recovered_without_work) {
    assert.equal(row.product_accepted, true);
    assert.equal(row.candidate_eligible, true);
    assert.equal(row.capture_valid, true);
    assert.ok(row.capture);
    assert.equal(row.recovered_without_work, false);
  }
  if (row.recovered_without_work || row.readback_verified) {
    assert.equal(row.sends, 0);
    assert.equal(row.outcome, null);
    assert.equal(row.current_attempts, null);
  } else {
    const outcome = row.outcome;
    assert.ok(outcome);
    keys(outcome, OUTCOME_KEYS);
    assert.equal(
      outcome.case_id,
      ['ahead', 'unverified-ahead'].includes(mode) ? 'ahead-ts-defect' : id,
    );
    assert.equal(outcome.corpus_sha256, CORPUS);
    assert.ok(hash(outcome.case_sha256));
    assert.equal(outcome.mode, row.sends > 0 ? 'deterministic' : null);
    if (row.sends > 0) {
      assert.ok(hash(outcome.configuration_sha256) && hash(outcome.attempt_sha256));
      assert.equal(outcome.source_commit, source.commit);
      assert.equal(outcome.source_tree, source.tree);
      assert.equal(outcome.source_clean, true);
    }
    for (const key of [
      'finding_count',
      'expected_defects',
      'structural_matches',
      'structurally_missing_defects',
      'duplicate_observations',
      'prohibited_observations',
      'adjudicated_true',
      'adjudicated_false',
      'adjudicated_defects',
      'unadjudicated_findings',
    ])
      assert.ok(count(outcome[key], 20));
    assert.ok(count(outcome.tool_observation_count, 6));
    if (outcome.execution_status === 'Completed') {
      assert.equal(row.product_accepted, true);
      assert.ok(hash(outcome.execution_sha256));
      assert.ok(['Passed', 'Failed'].includes(outcome.evidence_status));
      assert.ok(['Passed', 'Failed', 'NotEvaluated'].includes(outcome.scenario_status));
      assert.ok(['Adjudicated', 'Unadjudicated', 'NotEvaluated'].includes(outcome.model_status));
      assert.equal(outcome.failure_source, 'None');
      assert.equal(outcome.failure_kind, 'None');
      assert.equal(row.current_attempts, row.sends);
      assert.ok(row.admission_guards >= 9);
      assert.equal(outcome.tool_observation_count, row.current_tools);
      if (outcome.evidence_status === 'Passed') {
        const expectedCode =
          outcome.prohibited_observations > 0
            ? 'ProhibitedFinding'
            : outcome.duplicate_observations > 0
              ? 'DuplicateObservation'
              : outcome.structurally_missing_defects > 0
                ? 'ExpectedFindingMissing'
                : 'Scored';
        assert.equal(outcome.code, expectedCode);
        assert.equal(outcome.scenario_status, expectedCode === 'Scored' ? 'Passed' : 'Failed');
        assert.equal(outcome.adjudicated_true, 0);
        assert.equal(outcome.adjudicated_false, 0);
        assert.equal(outcome.adjudicated_defects, 0);
        assert.equal(outcome.unadjudicated_findings, outcome.finding_count);
        assert.equal(
          outcome.model_status,
          outcome.finding_count === 0 ? 'Adjudicated' : 'Unadjudicated',
        );
        assert.equal(
          outcome.structural_matches + outcome.structurally_missing_defects,
          outcome.expected_defects,
        );
        assert.equal(
          outcome.adjudicated_true + outcome.adjudicated_false + outcome.unadjudicated_findings,
          outcome.finding_count,
        );
      } else {
        assert.equal(outcome.evidence_status, 'Failed');
        assert.equal(outcome.scenario_status, 'NotEvaluated');
        assert.equal(outcome.model_status, 'NotEvaluated');
      }
    } else {
      assert.ok(['Failed', 'Invalid'].includes(outcome.execution_status));
      assert.equal(outcome.execution_sha256, null);
      assert.equal(outcome.finding_count, 0);
      assert.equal(outcome.evidence_status, 'NotEvaluated');
      assert.equal(outcome.scenario_status, 'NotEvaluated');
      assert.equal(outcome.model_status, 'NotEvaluated');
    }
  }
  const serialized = JSON.stringify(row);
  for (const canary of CANARIES) assert.ok(!serialized.includes(canary));
  return row;
}
export function verifyQuality(rows, source) {
  assert.equal(rows.length, QUALITY.length);
  rows.forEach((row, index) => {
    const [id, expected, sends, tools] = QUALITY[index];
    verifyCase(row, source, 'fresh', id);
    assert.equal(row.outcome.code, expected);
    assert.equal(row.sends, sends);
    assert.equal(row.current_tools, tools);
    assert.equal(row.durable_accepted, false);
    assert.equal(row.recovered_without_work, false);
  });
  assert.equal(new Set(rows.map((r) => r.outcome.attempt_sha256)).size, QUALITY.length);
  return true;
}
export function verifySuccessor(previous, current, source, { ahead = false } = {}) {
  verifyCase(current, source, ahead ? 'ahead' : 'review', ahead ? 'ts-defect' : 'cs-defect');
  assert.equal(current.durable_accepted, true);
  assert.equal(current.outcome.code, 'Scored');
  assert.equal(current.outcome.finding_count, 1);
  assert.equal(current.admission, 'Supplied');
  assert.equal(current.outcome.model_status, 'Unadjudicated');
  assert.deepEqual(current.predecessor, previous.capture);
  assert.notEqual(current.capture.Execution, previous.capture.Execution);
  assert.notEqual(current.capture.Round, previous.capture.Round);
  assert.notEqual(current.capture.Work, previous.capture.Work);
  assert.notEqual(current.capture.Payload, previous.capture.Payload);
  assert.equal(current.historical_rounds, previous.capture.Rounds);
  assert.equal(current.historical_attempts, previous.capture.Attempts);
  assert.equal(current.capture.Rounds, previous.capture.Rounds + 1);
  assert.equal(current.capture.Attempts, previous.capture.Attempts + current.current_attempts);
  assert.ok(current.capture.Records > previous.capture.Records);
  assert.ok(current.capture.Bytes > previous.capture.Bytes);
  assert.ok(current.historical_tool_results > 0);
  assert.equal(current.current_tools, ahead ? 3 : 4);
  assert.equal(current.current_attempts, ahead ? 4 : 5);
  assert.equal(current.capture.RuntimeConfiguration, previous.capture.RuntimeConfiguration);
  assert.equal(current.capture.ProviderConfiguration, previous.capture.ProviderConfiguration);
  assert.equal(current.outcome.configuration_sha256, previous.outcome.configuration_sha256);
  assert.notEqual(current.outcome.attempt_sha256, previous.outcome.attempt_sha256);
  assert.notEqual(current.outcome.execution_sha256, previous.outcome.execution_sha256);
  return true;
}
export function verifyGrowth(rows, refusal, source, injected) {
  assert.ok(rows.length >= 2 && rows.length <= 16);
  assert.equal(rows[0].mode, 'seed');
  assert.equal(rows[0].durable_accepted, true);
  for (let index = 1; index < rows.length; index++)
    verifySuccessor(rows[index - 1], rows[index], source);
  verifyCase(refusal, source, injected ? 'input-over' : 'review', 'cs-defect');
  assert.equal(refusal.durable_accepted, false);
  assert.equal(refusal.recovered_without_work, false);
  classifyRefusal(refusal, injected);
  assert.deepEqual(refusal.predecessor, rows.at(-1).capture);
  if (injected) assert.equal(refusal.sends, 0);
  return true;
}
export function classifyRefusal(row, injected) {
  if (injected) {
    assert.equal(row.mode, 'input-over');
    assert.equal(row.sends, 0);
    assert.equal(row.preparation_failure, 'InputProfileRejected');
    assert.notEqual(row.outcome?.execution_status, 'Completed');
    assert.equal(row.outcome?.model_status, 'NotEvaluated');
    return 'injected_input_bound';
  }
  if (
    [
      'ResourceLimit',
      'ContextLimit',
      'RequestLimit',
      'ModelLimit',
      'ToolLimit',
      'TokenLimit',
    ].includes(row.termination)
  ) {
    assert.notEqual(row.outcome?.execution_status, 'Completed');
    assert.equal(row.outcome?.model_status, 'NotEvaluated');
    return 'observed_resource_refusal';
  }
  if (row.admission === 'Rejected') {
    assert.equal(row.sends, 0);
    assert.notEqual(row.context_rejection, 'None');
    assert.notEqual(row.context_rejection, null);
    assert.notEqual(row.outcome?.execution_status, 'Completed');
    assert.equal(row.outcome?.model_status, 'NotEvaluated');
    return 'observed_context_rejection_unattributed';
  }
  assert.equal(row.product_accepted, true);
  assert.equal(row.capture_status, 'Unavailable');
  assert.equal(row.capture_valid, false);
  assert.equal(row.outcome.execution_status, 'Completed');
  return 'observed_capture_refusal_unattributed';
}
export function verifyCleanup(value) {
  assert.equal(value, 'removed');
  return true;
}
