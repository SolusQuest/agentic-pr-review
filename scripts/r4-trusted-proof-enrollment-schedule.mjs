// This is deliberately a descriptive, local-rehearsal contract. It does not invoke
// the Node executor, the restricted C# materializers, HTTP, or credential-bearing
// processes. Production callers keep using those existing primitives directly.

export const R4_TRUSTED_PROOF_ENROLLMENT_ROLES = Object.freeze([
  'normal-bootstrap',
  'normal-continuation',
  'stale-protected',
  'stale-follow-on',
]);

export const R4_TRUSTED_PROOF_ROLE_CAPTURE_REQUIREMENTS = Object.freeze({
  'normal-bootstrap': Object.freeze([
    'terminal',
    'jobs',
    'discovery',
    'pull',
    'pending',
    'approval',
  ]),
  'normal-continuation': Object.freeze([
    'terminal',
    'jobs',
    'discovery',
    'pull',
    'pending',
    'approval',
  ]),
  'stale-protected': Object.freeze([
    'terminal',
    'jobs',
    'discovery',
    'pull',
    'pending',
    'approval',
  ]),
  'stale-follow-on': Object.freeze(['terminal', 'jobs', 'discovery', 'pull']),
});

const positiveDecimal = /^[1-9][0-9]*$/u;
const scheduleKind = 'apr-r4-e2p-fixed-four-role-schedule-v2';

function fail(code) {
  throw new Error(`APR_R4_TRUSTED_PROOF_ENROLLMENT_SCHEDULE_INVALID ${code}`);
}

function frozenStep(value) {
  return Object.freeze({
    ...value,
    requires: Object.freeze([...value.requires]),
    ...(value.phases ? { phases: Object.freeze([...value.phases]) } : {}),
    ...(value.capture_requirements
      ? { capture_requirements: Object.freeze([...value.capture_requirements]) }
      : {}),
  });
}

function roleCaptureAndObservationSteps(role, producerStepId, requires) {
  const captureRequirements = R4_TRUSTED_PROOF_ROLE_CAPTURE_REQUIREMENTS[role];
  return [
    frozenStep({
      id: `${role}-capture`,
      owner: 'phase-fragment-materializer',
      primitive: 'existing-phase-fragment-materializer',
      authority_action: `${role}-capture`,
      stage: 'capture',
      role,
      run_binding: `${producerStepId}.runtime_run_id`,
      capture_requirements: captureRequirements,
      requires,
    }),
    frozenStep({
      id: `${role}-observe`,
      owner: 'observation-materializer',
      primitive: 'existing-observation-materializer',
      authority_action: `${role}-observe`,
      stage: 'observe',
      role,
      run_binding: `${producerStepId}.runtime_run_id`,
      requires: [`${role}-capture`],
    }),
  ];
}

function roleSteps(role, requires) {
  return [
    frozenStep({
      id: `${role}-produce`,
      owner: 'producer-journal',
      primitive: 'existing-producer-journal',
      authority_action: `${role}-produce`,
      stage: 'produce',
      role,
      run_binding: 'producer-observed-runtime-run-id',
      requires,
    }),
    ...roleCaptureAndObservationSteps(role, `${role}-produce`, [`${role}-produce`]),
  ];
}

export const R4_TRUSTED_PROOF_FIXED_PRODUCER_TARGET_STEP_IDS = Object.freeze([
  'normal-bootstrap-produce',
  'normal-continuation-produce',
  'stale-protected-produce',
  'advance-stale-ref',
]);

export const R4_TRUSTED_PROOF_FIXED_PRODUCER_JOURNAL_LIFECYCLE_ACTIONS = Object.freeze([
  'producer-journal-create',
  'final-discovery-and-producer-journal-seal',
]);

export const R4_TRUSTED_PROOF_FIXED_PRODUCER_JOURNAL_FINAL_PHASES = Object.freeze([
  'producer-discovery',
  'producer-journal-seal',
]);

// Fixed authority graph only. It describes calls to existing primitives; it neither
// performs calls nor provides a reusable execution language.
export const R4_TRUSTED_PROOF_ENROLLMENT_SCHEDULE = Object.freeze([
  frozenStep({
    id: 'prepare',
    owner: 'node-executor',
    primitive: 'existing-node-phase',
    authority_action: 'prepare',
    phases: ['prepare'],
    requires: ['merged-default-branch authority'],
  }),
  frozenStep({
    id: 'refresh',
    owner: 'node-executor',
    primitive: 'existing-node-phase',
    authority_action: 'refresh',
    phases: ['refresh'],
    requires: ['prepare'],
  }),
  frozenStep({
    id: 'producer-journal-create',
    owner: 'producer-journal',
    primitive: 'existing-producer-journal',
    authority_action: 'producer-journal-create',
    requires: ['refresh'],
  }),
  frozenStep({
    id: 'baseline-secret-variable-capture',
    owner: 'phase-fragment-materializer',
    primitive: 'existing-phase-fragment-materializer',
    authority_action: 'baseline-secret-variable-capture',
    phases: ['baseline-normal', 'baseline-stale'],
    requires: ['producer-journal-create'],
  }),
  frozenStep({
    id: 'authorize-normal',
    owner: 'node-executor',
    primitive: 'existing-node-phase',
    authority_action: 'authorize-normal',
    phases: ['authorize-normal'],
    requires: ['baseline-secret-variable-capture'],
  }),
  frozenStep({
    id: 'normal-variable-readiness-capture',
    owner: 'phase-fragment-materializer',
    primitive: 'existing-phase-fragment-materializer',
    authority_action: 'normal-variable-readiness-capture',
    phases: ['normal-variable-readback', 'bootstrap-readiness', 'continuation-readiness'],
    requires: ['authorize-normal'],
  }),
  frozenStep({
    id: 'normal-bootstrap-produce',
    owner: 'producer-journal',
    primitive: 'existing-producer-journal',
    authority_action: 'normal-bootstrap-produce',
    stage: 'produce',
    role: 'normal-bootstrap',
    run_binding: 'producer-observed-runtime-run-id',
    result: 'producer-journal-command-committed',
    requires: ['normal-variable-readiness-capture'],
  }),
  frozenStep({
    id: 'normal-continuation-produce',
    owner: 'producer-journal',
    primitive: 'existing-producer-journal',
    authority_action: 'normal-continuation-produce',
    stage: 'produce',
    role: 'normal-continuation',
    run_binding: 'producer-observed-runtime-run-id',
    prerequisite_semantics: 'bootstrap-producer-command-committed-not-runtime-terminal',
    requires: ['normal-bootstrap-produce'],
  }),
  ...roleCaptureAndObservationSteps('normal-bootstrap', 'normal-bootstrap-produce', [
    'normal-bootstrap-produce',
  ]),
  ...roleCaptureAndObservationSteps('normal-continuation', 'normal-continuation-produce', [
    'normal-continuation-produce',
  ]),
  frozenStep({
    id: 'authorize-stale',
    owner: 'node-executor',
    primitive: 'existing-node-phase',
    authority_action: 'authorize-stale',
    phases: ['authorize-stale'],
    requires: ['normal-bootstrap-observe', 'normal-continuation-observe'],
  }),
  frozenStep({
    id: 'stale-variable-readiness-capture',
    owner: 'phase-fragment-materializer',
    primitive: 'existing-phase-fragment-materializer',
    authority_action: 'stale-variable-readiness-capture',
    phases: ['stale-variable-readback', 'stale-readiness'],
    requires: ['authorize-stale'],
  }),
  ...roleSteps('stale-protected', ['stale-variable-readiness-capture']),
  frozenStep({
    id: 'advance-stale-ref',
    owner: 'producer-journal',
    primitive: 'existing-producer-journal',
    authority_action: 'advance-stale-ref',
    mutation: 'advance-stale-ref',
    stage: 'produce',
    role: 'stale-follow-on',
    produced_role: 'stale-follow-on',
    run_binding: 'producer-observed-runtime-run-id',
    requires: ['stale-protected-observe'],
  }),
  frozenStep({
    id: 'advance-stale-readback',
    owner: 'node-executor',
    primitive: 'existing-node-phase',
    authority_action: 'advance-stale-readback',
    phases: ['advance-stale'],
    mode: 'readback-only',
    requires: ['advance-stale-ref'],
  }),
  ...roleCaptureAndObservationSteps('stale-follow-on', 'advance-stale-ref', [
    'advance-stale-readback',
  ]),
  frozenStep({
    id: 'final-discovery-and-producer-journal-seal',
    owner: 'producer-journal',
    primitive: 'existing-producer-journal',
    authority_action: 'final-discovery-and-producer-journal-seal',
    phases: R4_TRUSTED_PROOF_FIXED_PRODUCER_JOURNAL_FINAL_PHASES,
    requires: [
      'normal-bootstrap-observe',
      'normal-continuation-observe',
      'stale-protected-observe',
      'stale-follow-on-observe',
    ],
  }),
  frozenStep({
    id: 'cleanup',
    owner: 'node-executor',
    primitive: 'existing-node-phase',
    authority_action: 'cleanup',
    phases: ['cleanup'],
    requires: ['final-discovery-and-producer-journal-seal'],
  }),
]);

function exactKeys(value, expected, code) {
  if (
    !value ||
    typeof value !== 'object' ||
    Array.isArray(value) ||
    JSON.stringify(Object.keys(value).sort()) !== JSON.stringify([...expected].sort())
  ) {
    fail(code);
  }
}

function validateScheduleDefinition() {
  const ids = new Set();
  const authorityActions = new Set();
  const producedRoles = [];
  const capturedRoles = [];
  const observedRoles = [];
  let staleWriter = 0;
  for (const step of R4_TRUSTED_PROOF_ENROLLMENT_SCHEDULE) {
    if (!/^[a-z][a-z0-9-]*$/u.test(step.id) || ids.has(step.id)) fail('definition-id');
    if (authorityActions.has(step.authority_action)) fail('definition-owner');
    if (
      step.requires.some(
        (requirement) => requirement !== 'merged-default-branch authority' && !ids.has(requirement),
      )
    ) {
      fail('definition-order');
    }
    ids.add(step.id);
    authorityActions.add(step.authority_action);
    if (step.role && !R4_TRUSTED_PROOF_ENROLLMENT_ROLES.includes(step.role)) {
      fail('definition-role');
    }
    if (step.stage === 'produce') producedRoles.push(step.role);
    if (step.stage === 'capture') capturedRoles.push(step.role);
    if (step.stage === 'observe') observedRoles.push(step.role);
    if (step.mutation === 'advance-stale-ref') {
      staleWriter += 1;
      if (step.owner !== 'producer-journal') fail('definition-stale-writer');
    }
    if (step.id === 'advance-stale-readback' && step.mode !== 'readback-only') {
      fail('definition-stale-readback');
    }
  }
  if (
    JSON.stringify(producedRoles) !== JSON.stringify(R4_TRUSTED_PROOF_ENROLLMENT_ROLES) ||
    JSON.stringify(capturedRoles) !== JSON.stringify(R4_TRUSTED_PROOF_ENROLLMENT_ROLES) ||
    JSON.stringify(observedRoles) !== JSON.stringify(R4_TRUSTED_PROOF_ENROLLMENT_ROLES) ||
    staleWriter !== 1
  ) {
    fail('definition-four-roles');
  }
  const finalStep = R4_TRUSTED_PROOF_ENROLLMENT_SCHEDULE.at(-1);
  if (
    finalStep.id !== 'cleanup' ||
    finalStep.requires[0] !== 'final-discovery-and-producer-journal-seal'
  ) {
    fail('definition-final-order');
  }
}

validateScheduleDefinition();

export function fixedEnrollmentSchedule() {
  return R4_TRUSTED_PROOF_ENROLLMENT_SCHEDULE;
}

function validateTraceStep(value, step, runIds) {
  const roleStep = Boolean(step.role);
  exactKeys(
    value,
    roleStep
      ? ['id', 'owner', 'primitive', 'runtime_run_id', 'attempt']
      : ['id', 'owner', 'primitive'],
    'trace-shape',
  );
  if (value.id !== step.id || value.owner !== step.owner || value.primitive !== step.primitive) {
    fail('trace-step');
  }
  if (!roleStep) return;
  if (
    value.attempt !== '1' ||
    typeof value.runtime_run_id !== 'string' ||
    !positiveDecimal.test(value.runtime_run_id)
  ) {
    fail('trace-run-binding');
  }
  if (step.stage === 'produce') {
    if (runIds.has(step.role)) fail('trace-role-duplicate');
    if ([...runIds.values()].includes(value.runtime_run_id)) fail('trace-run-duplicate');
    runIds.set(step.role, value.runtime_run_id);
    return;
  }
  if (runIds.get(step.role) !== value.runtime_run_id) fail('trace-role-readback');
}

function validateSegment(events, offset, runIds) {
  if (!Array.isArray(events)) fail('trace-array');
  for (const [index, value] of events.entries()) {
    const step = R4_TRUSTED_PROOF_ENROLLMENT_SCHEDULE[offset + index];
    if (!step) fail('trace-overrun');
    validateTraceStep(value, step, runIds);
  }
}

/**
 * Validate a fake-primitives rehearsal trace. A non-empty `resumed` segment is
 * recovery only: it may continue an exact completed prefix, never replay, skip, or
 * restart one. The function has no I/O and cannot be used as a production runner.
 */
export function rehearseFixedEnrollmentSchedule({ completed = [], resumed = [] } = {}) {
  const runIds = new Map();
  if (!Array.isArray(completed) || !Array.isArray(resumed)) fail('trace-input');
  if (resumed.length === 0 && completed.length !== R4_TRUSTED_PROOF_ENROLLMENT_SCHEDULE.length) {
    fail('recovery-required');
  }
  if (
    resumed.length > 0 &&
    (completed.length === 0 || completed.length >= R4_TRUSTED_PROOF_ENROLLMENT_SCHEDULE.length)
  ) {
    fail('recovery-prefix');
  }
  validateSegment(completed, 0, runIds);
  validateSegment(resumed, completed.length, runIds);
  if (completed.length + resumed.length !== R4_TRUSTED_PROOF_ENROLLMENT_SCHEDULE.length) {
    fail('trace-incomplete');
  }
  if (runIds.size !== R4_TRUSTED_PROOF_ENROLLMENT_ROLES.length) fail('trace-roles');
  return Object.freeze({
    kind: scheduleKind,
    recovery: resumed.length > 0,
    role_run_ids: Object.freeze(Object.fromEntries(runIds)),
    steps: Object.freeze([...completed, ...resumed].map((step) => Object.freeze({ ...step }))),
  });
}
