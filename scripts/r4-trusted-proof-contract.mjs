import crypto from 'node:crypto';

export const expectedProductRoles = Object.freeze([
  'repository-locator-root',
  'normal-lineage-head',
  'stale-lineage-head',
  'bootstrap-candidate',
  'continuation-candidate',
  'bootstrap-acceptance',
  'continuation-acceptance',
]);

export const cleanupPhases = Object.freeze([
  'settle-runs',
  'remove-proof-control',
  'delete-observed-state',
  'enumerate-empty-state',
  'remove-authorization-and-secrets',
  'restore-environment',
  'retire-fixtures',
  'read-back-sticky',
  'remove-local-credentials',
  'finalize-private-manifest',
]);

const expectedProductRoleShape = Object.freeze([
  ['repository-locator-root', 'repository', 'locator_root'],
  ['normal-lineage-head', 'normal', 'lineage_head'],
  ['stale-lineage-head', 'stale', 'lineage_head'],
  ['bootstrap-candidate', 'normal', 'candidate'],
  ['continuation-candidate', 'normal', 'candidate'],
  ['bootstrap-acceptance', 'normal', 'acceptance'],
  ['continuation-acceptance', 'normal', 'acceptance'],
]);

const expectedCleanupShape = Object.freeze([
  ['locator_root', 'repository', 0],
  ['lineage_head', 'normal', 0],
  ['lineage_head', 'stale', 1],
  ['candidate', 'normal', 0],
  ['candidate', 'normal', 0],
  ['acceptance', 'normal', 0],
  ['acceptance', 'normal', 0],
  ['publication_intent', 'normal', 0],
  ['publication_intent', 'normal', 0],
  ['publication_intent', 'normal', 0],
  ['publication_intent', 'stale', 1],
  ['cleanup', 'normal', 0],
  ['cleanup', 'normal', 0],
  ['cleanup', 'stale', 1],
  ['cleanup', 'stale', 1],
]);

const hex40 = /^[0-9a-f]{40}$/u;
const hex64 = /^[0-9a-f]{64}$/u;
const decimal = /^[1-9][0-9]*$/u;
const sourceKinds = new Set([
  'github-rest',
  'github-ui-attestation',
  'descendant-receipt',
  'production-codec',
  'cleanup-readback',
  'public-leak-scan',
  'pinned-code-order',
]);

function invalid(code) {
  throw new Error(`APR_R4_E3_EVIDENCE_INVALID ${code}`);
}

export function sha256(value) {
  return crypto.createHash('sha256').update(value).digest('hex');
}

export function canonicalJson(value) {
  return `${JSON.stringify(value)}\n`;
}

function exactKeys(value, keys, code) {
  if (
    value === null ||
    Array.isArray(value) ||
    typeof value !== 'object' ||
    JSON.stringify(Object.keys(value)) !== JSON.stringify(keys)
  ) {
    invalid(code);
  }
}

function exactArray(value, expected, code) {
  if (!Array.isArray(value) || JSON.stringify(value) !== JSON.stringify(expected)) invalid(code);
}

function interval(value, code) {
  exactKeys(value, ['request_started', 'response_received'], code);
  if (
    !Number.isSafeInteger(value.request_started) ||
    !Number.isSafeInteger(value.response_received) ||
    value.request_started < 0 ||
    value.response_received < value.request_started
  ) {
    invalid(code);
  }
}

function sourceTimestamp(value, code) {
  exactKeys(value, ['kind', 'value'], code);
  if (value.kind !== 'source-emitted' || !Number.isSafeInteger(value.value) || value.value < 0) {
    invalid(code);
  }
}

function validateSourceMap(value) {
  exactKeys(value, ['kind', 'api_version', 'entries'], 'source-map-shape');
  if (
    value.kind !== 'apr-r4-e3-source-map-v1' ||
    value.api_version !== '2026-03-10' ||
    !Array.isArray(value.entries)
  ) {
    invalid('source-map-values');
  }
  const expected = [
    ['/identities', 'descendant-receipt'],
    ['/authorizations', 'pinned-code-order'],
    ['/environment', 'github-ui-attestation'],
    ['/approval_transitions', 'github-rest'],
    ['/concurrency', 'github-rest'],
    ['/proof_control', 'github-rest'],
    ['/inventories', 'production-codec'],
    ['/cleanup', 'cleanup-readback'],
    ['/canaries/live', 'github-rest'],
    ['/canaries/cross_sink', 'descendant-receipt'],
    ['/canaries/public_leak_scan', 'public-leak-scan'],
    ['/restricted_package', 'cleanup-readback'],
  ];
  const observed = value.entries.map((entry) => {
    exactKeys(entry, ['pointer', 'source'], 'source-map-entry-shape');
    if (!sourceKinds.has(entry.source)) invalid('source-map-source');
    return [entry.pointer, entry.source];
  });
  if (JSON.stringify(observed) !== JSON.stringify(expected)) invalid('source-map-coverage');
}

function validateAuthorization(value) {
  exactKeys(value, ['setup', 'execution', 'cleanup'], 'authorization-shape');
  exactKeys(
    value.setup,
    ['kind', 'phase', 'capabilities', 'branches'],
    'setup-authorization-shape',
  );
  if (
    value.setup.kind !== 'apr-r4-e3-setup-authorization-v1' ||
    value.setup.phase !== 'setup' ||
    !Array.isArray(value.setup.branches) ||
    value.setup.branches.length !== 2 ||
    value.setup.branches.some((branch) => {
      exactKeys(branch, ['ref', 'head_sha', 'parent_sha'], 'setup-authorization-branch-shape');
      return (
        !hex40.test(branch.head_sha) ||
        !hex40.test(branch.parent_sha) ||
        typeof branch.ref !== 'string'
      );
    })
  ) {
    invalid('setup-authorization-values');
  }
  exactArray(
    value.setup.capabilities,
    [
      'configure-environment-baseline',
      'push-two-precomputed-heads',
      'open-two-frozen-fixture-prs',
      'observe-secret-free-ci-and-inert-preflight',
      'bounded-setup-rollback',
    ],
    'setup-authorization-capabilities',
  );

  exactKeys(
    value.execution,
    ['kind', 'phase', 'fixture_prs', 'operation_ids', 'credential_files', 'destination_identity'],
    'execution-authorization-shape',
  );
  if (
    value.execution.kind !== 'apr-r4-e3-execution-authorization-v1' ||
    value.execution.phase !== 'execution' ||
    value.execution.fixture_prs.length !== 2 ||
    value.execution.fixture_prs.some(
      (fixture) =>
        !decimal.test(fixture.id) ||
        !decimal.test(fixture.number) ||
        !hex40.test(fixture.head_sha) ||
        fixture.base_ref !== 'main' ||
        !hex40.test(fixture.base_sha),
    ) ||
    value.execution.operation_ids.length !== 2 ||
    value.execution.operation_ids.some((item) => !hex64.test(item)) ||
    !hex64.test(value.execution.destination_identity)
  ) {
    invalid('execution-authorization-values');
  }
  exactArray(
    value.execution.credential_files,
    ['github-token', 'current-state-key', 'previous-state-key'],
    'execution-authorization-credentials',
  );

  exactKeys(value.cleanup, ['kind', 'phase', 'plan_sha256'], 'cleanup-authorization-shape');
  if (
    value.cleanup.kind !== 'apr-r4-e3-cleanup-authorization-v1' ||
    value.cleanup.phase !== 'cleanup' ||
    !hex64.test(value.cleanup.plan_sha256)
  ) {
    invalid('cleanup-authorization-values');
  }
}

function validateApproval(approval, phase) {
  exactKeys(
    approval,
    ['phase', 'run_id', 'run_attempt', 'pending', 'approval', 'protected_job'],
    `approval-${phase}-shape`,
  );
  if (approval.phase !== phase || !decimal.test(approval.run_id) || approval.run_attempt !== 1) {
    invalid(`approval-${phase}-identity`);
  }
  for (const [kind, capture] of [
    ['pending', approval.pending],
    ['approval', approval.approval],
  ]) {
    exactKeys(
      capture,
      ['run_id', 'environment_id', 'environment_name', 'reviewer_ids', 'state', 'observation'],
      `approval-${phase}-${kind}-shape`,
    );
    interval(capture.observation, `approval-${phase}-${kind}-interval`);
    if (
      capture.run_id !== approval.run_id ||
      !decimal.test(capture.environment_id) ||
      capture.environment_name !== 'r4-trusted-proof' ||
      JSON.stringify(capture.reviewer_ids) !== JSON.stringify(['16307884']) ||
      capture.state !== (kind === 'pending' ? 'pending' : 'approved')
    ) {
      invalid(`approval-${phase}-${kind}-values`);
    }
  }
  if (approval.pending.environment_id !== approval.approval.environment_id) {
    invalid(`approval-${phase}-environment`);
  }
  exactKeys(
    approval.protected_job,
    ['run_id', 'run_attempt', 'name', 'started'],
    `approval-${phase}-job-shape`,
  );
  sourceTimestamp(approval.protected_job.started, `approval-${phase}-job-started`);
  if (
    approval.protected_job.run_id !== approval.run_id ||
    approval.protected_job.run_attempt !== 1 ||
    !['workflow-run-review', 'workflow-dispatch-review'].includes(approval.protected_job.name) ||
    approval.approval.observation.response_received > approval.protected_job.started.value
  ) {
    invalid(`approval-${phase}-job-values`);
  }
}

function validateConcurrency(value, label, expectedIds, expectedGroup) {
  exactKeys(
    value,
    ['api_version', 'group', 'pagination_complete', 'observation', 'ahead_of_run', 'terminal'],
    `concurrency-${label}-shape`,
  );
  interval(value.observation, `concurrency-${label}-interval`);
  if (
    value.api_version !== '2026-03-10' ||
    value.group !== expectedGroup ||
    value.pagination_complete !== true ||
    value.ahead_of_run.length !== 2
  ) {
    invalid(`concurrency-${label}-values`);
  }
  const expected = [
    { run_id: expectedIds[0], position: 0, status: 'in_progress' },
    { run_id: expectedIds[1], position: 1, status: 'pending' },
  ];
  if (JSON.stringify(value.ahead_of_run) !== JSON.stringify(expected)) {
    invalid(`concurrency-${label}-members`);
  }
  exactKeys(
    value.terminal,
    ['holder', 'waiter', 'holder_cancelled'],
    `concurrency-${label}-terminal-shape`,
  );
  sourceTimestamp(value.terminal.holder, `concurrency-${label}-holder-terminal`);
  sourceTimestamp(value.terminal.waiter, `concurrency-${label}-waiter-terminal`);
  if (
    value.terminal.holder_cancelled !== false ||
    value.terminal.holder.value >= value.terminal.waiter.value
  ) {
    invalid(`concurrency-${label}-terminal-order`);
  }
}

function validateControls(value, operationId, expectedKinds, code) {
  exactKeys(value, ['operation_id', 'comments', 'cleanup_outcomes'], `${code}-shape`);
  if (value.operation_id !== operationId || value.comments.length !== expectedKinds.length) {
    invalid(`${code}-identity`);
  }
  const ids = [];
  for (let index = 0; index < expectedKinds.length; index += 1) {
    const comment = value.comments[index];
    exactKeys(
      comment,
      [
        'kind',
        'comment_id',
        'predecessor_comment_id',
        'operation_id',
        'run_id',
        'run_attempt',
        'body_sha256',
        'readback_sha256',
      ],
      `${code}-comment-shape`,
    );
    const expectedPredecessor = index % 2 === 1 ? ids[index - 1] : null;
    if (
      comment.kind !== expectedKinds[index] ||
      !decimal.test(comment.comment_id) ||
      ids.includes(comment.comment_id) ||
      comment.predecessor_comment_id !== expectedPredecessor ||
      comment.operation_id !== operationId ||
      !decimal.test(comment.run_id) ||
      comment.run_attempt !== 1 ||
      !hex64.test(comment.body_sha256) ||
      comment.readback_sha256 !== comment.body_sha256
    ) {
      invalid(`${code}-comment-values`);
    }
    ids.push(comment.comment_id);
  }
  const outcomes = value.cleanup_outcomes.map((item) => {
    exactKeys(item, ['comment_id', 'outcome'], `${code}-outcome-shape`);
    return [item.comment_id, item.outcome];
  });
  if (JSON.stringify(outcomes) !== JSON.stringify(ids.map((id) => [id, 'deleted-absent']))) {
    invalid(`${code}-cleanup`);
  }
}

function validateInventories(value, operationIds) {
  exactKeys(value, ['expected_success', 'observed_cleanup'], 'inventory-shape');
  if (value.expected_success.length !== expectedProductRoles.length)
    invalid('success-inventory-count');
  const roles = value.expected_success.map((record) => record.role);
  exactArray(roles, expectedProductRoles, 'success-inventory-roles');
  const successIds = new Set();
  for (const [index, record] of value.expected_success.entries()) {
    exactKeys(
      record,
      ['artifact_id', 'role', 'scope', 'object_class', 'authenticated', 'operation_owned'],
      'success-record-shape',
    );
    if (
      !decimal.test(record.artifact_id) ||
      successIds.has(record.artifact_id) ||
      !['repository', 'normal', 'stale'].includes(record.scope) ||
      JSON.stringify([record.role, record.scope, record.object_class]) !==
        JSON.stringify(expectedProductRoleShape[index]) ||
      record.authenticated !== true ||
      record.operation_owned !== true
    ) {
      invalid('success-record-values');
    }
    successIds.add(record.artifact_id);
  }
  const observedIds = new Set();
  for (const record of value.observed_cleanup) {
    exactKeys(
      record,
      [
        'artifact_id',
        'object_class',
        'scope',
        'operation_id',
        'authenticated',
        'operation_owned',
        'disposition',
      ],
      'observed-record-shape',
    );
    if (
      !decimal.test(record.artifact_id) ||
      observedIds.has(record.artifact_id) ||
      !['repository', 'normal', 'stale'].includes(record.scope) ||
      !operationIds.includes(record.operation_id) ||
      record.authenticated !== true ||
      record.operation_owned !== true ||
      !['delete', 'recovery-only-delete'].includes(record.disposition)
    ) {
      invalid('observed-record-values');
    }
    observedIds.add(record.artifact_id);
  }
  if ([...successIds].some((id) => !observedIds.has(id))) invalid('inventory-success-not-observed');
  const recovery = value.observed_cleanup.filter(
    (record) => record.disposition === 'recovery-only-delete',
  );
  const ordinary = value.observed_cleanup.filter((record) => record.disposition === 'delete');
  if (
    ordinary.length !== expectedCleanupShape.length ||
    JSON.stringify(
      ordinary.map((record) => [
        record.object_class,
        record.scope,
        operationIds.indexOf(record.operation_id),
      ]),
    ) !== JSON.stringify(expectedCleanupShape)
  ) {
    invalid('observed-inventory-shape');
  }
  for (const expected of value.expected_success) {
    const observed = value.observed_cleanup.find(
      ({ artifact_id }) => artifact_id === expected.artifact_id,
    );
    if (
      !observed ||
      observed.object_class !== expected.object_class ||
      observed.scope !== expected.scope ||
      observed.disposition !== 'delete'
    ) {
      invalid('inventory-success-mismatch');
    }
  }
  return { successIds, recoveryOnly: recovery.length > 0, observedIds };
}

export function generateCleanupPlan(input) {
  exactKeys(
    input,
    ['operation_ids', 'proof_control', 'observed_cleanup', 'resources'],
    'cleanup-plan-input-shape',
  );
  if (
    input.operation_ids.length !== 2 ||
    input.operation_ids.some((id) => !hex64.test(id)) ||
    new Set(input.operation_ids).size !== 2 ||
    !Array.isArray(input.observed_cleanup) ||
    input.observed_cleanup.some(
      (record) =>
        JSON.stringify(Object.keys(record)) !==
          JSON.stringify([
            'artifact_id',
            'object_class',
            'scope',
            'operation_id',
            'authenticated',
            'operation_owned',
            'disposition',
          ]) ||
        record.authenticated !== true ||
        record.operation_owned !== true ||
        !input.operation_ids.includes(record.operation_id) ||
        !decimal.test(record.artifact_id) ||
        !['repository', 'normal', 'stale'].includes(record.scope) ||
        !['delete', 'recovery-only-delete'].includes(record.disposition),
    )
  ) {
    invalid('cleanup-plan-ownership');
  }
  if (
    new Set(input.observed_cleanup.map(({ artifact_id }) => artifact_id)).size !==
    input.observed_cleanup.length
  ) {
    invalid('cleanup-plan-ownership');
  }
  exactKeys(input.proof_control, ['normal', 'stale'], 'cleanup-plan-control-shape');
  const controlIds = [];
  for (const [family, operationId, expectedKinds] of [
    [input.proof_control.normal, input.operation_ids[0], ['ready', 'release']],
    [
      input.proof_control.stale,
      input.operation_ids[1],
      ['ready', 'release', 'stale-ready', 'stale-release'],
    ],
  ]) {
    exactKeys(
      family,
      ['operation_id', 'comments', 'cleanup_outcomes'],
      'cleanup-plan-control-family-shape',
    );
    if (
      family.operation_id !== operationId ||
      !Array.isArray(family.comments) ||
      family.comments.length !== expectedKinds.length
    ) {
      invalid('cleanup-plan-control-values');
    }
    const familyIds = [];
    for (let index = 0; index < expectedKinds.length; index += 1) {
      const comment = family.comments[index];
      const expectedPredecessor = index % 2 === 1 ? familyIds[index - 1] : null;
      if (
        comment.kind !== expectedKinds[index] ||
        !decimal.test(comment.comment_id) ||
        comment.operation_id !== operationId ||
        comment.predecessor_comment_id !== expectedPredecessor
      ) {
        invalid('cleanup-plan-control-values');
      }
      familyIds.push(comment.comment_id);
      controlIds.push(comment.comment_id);
    }
  }
  if (new Set(controlIds).size !== controlIds.length) invalid('cleanup-plan-control-values');
  exactKeys(
    input.resources,
    [
      'authorization_variable',
      'secret_names',
      'environment',
      'fixture_refs',
      'fixture_pr_numbers',
      'credential_copies',
    ],
    'cleanup-plan-resource-shape',
  );
  if (
    input.resources.authorization_variable !== 'R4_TRUSTED_PROOF_AUTHORIZATION' ||
    JSON.stringify(input.resources.secret_names) !==
      JSON.stringify([
        'DEEPSEEK_API_KEY',
        'AGENTIC_PR_REVIEW_STATE_KEY',
        'AGENTIC_PR_REVIEW_PREVIOUS_STATE_KEY',
      ]) ||
    input.resources.environment !== 'r4-trusted-proof' ||
    input.resources.fixture_refs.length !== 2 ||
    new Set(input.resources.fixture_refs).size !== 2 ||
    input.resources.fixture_refs.some(
      (value) => !/^refs\/heads\/[a-z0-9][a-z0-9-]{0,127}$/u.test(value),
    ) ||
    input.resources.fixture_pr_numbers.length !== 2 ||
    input.resources.fixture_pr_numbers.some((value) => !decimal.test(value)) ||
    new Set(input.resources.fixture_pr_numbers).size !== 2 ||
    JSON.stringify(input.resources.credential_copies) !==
      JSON.stringify(['github-token', 'current-state-key', 'previous-state-key'])
  ) {
    invalid('cleanup-plan-resource-values');
  }
  const stateIds = input.observed_cleanup
    .map((record) => record.artifact_id)
    .sort((a, b) => a.localeCompare(b, 'en', { numeric: true }));
  const plan = {
    kind: 'apr-r4-e3-cleanup-plan-v1',
    operation_ids: [...input.operation_ids],
    phases: cleanupPhases.map((phase) => ({
      phase,
      precondition: 'exact-readback-required',
      postcondition: 'exact-readback-required',
    })),
    targets: {
      control_comment_ids: controlIds,
      state_artifact_ids: stateIds,
      authorization_variable: input.resources.authorization_variable,
      secret_names: [...input.resources.secret_names],
      environment: input.resources.environment,
      fixture_refs: [...input.resources.fixture_refs],
      fixture_pr_numbers: [...input.resources.fixture_pr_numbers],
      credential_copies: [...input.resources.credential_copies],
    },
  };
  return { plan, canonical: canonicalJson(plan), digest: sha256(canonicalJson(plan)) };
}

export function validateHostEvidence(input) {
  exactKeys(
    input,
    [
      'kind',
      'identities',
      'source_map',
      'authorizations',
      'environment',
      'approval_transitions',
      'concurrency',
      'proof_control',
      'inventories',
      'cleanup',
      'canaries',
      'restricted_package',
    ],
    'host-shape',
  );
  if (input.kind !== 'apr-r4-e3-host-restricted-evidence-v1') invalid('host-kind');
  exactKeys(
    input.identities,
    [
      'workflow_sha',
      'action_source_sha',
      'payload_source_sha',
      'payload_sha256',
      'repository_id',
      'repository',
      'normal_pr_number',
      'stale_pr_number',
      'operation_ids',
    ],
    'identity-shape',
  );
  if (
    !hex40.test(input.identities.workflow_sha) ||
    input.identities.action_source_sha !== '5b5769753653bb3fd3e68cf8b7bb88a1bd350613' ||
    input.identities.payload_source_sha !== 'edc594c29a8a6b5fdacfab48643bf221277af200' ||
    input.identities.payload_sha256 !==
      'b6405d21987a549540b071215f215cf15339729cb3905ad3294c88bc2edf8c0e' ||
    !decimal.test(input.identities.repository_id) ||
    input.identities.repository !== 'SolusQuest/agentic-pr-review' ||
    !decimal.test(input.identities.normal_pr_number) ||
    !decimal.test(input.identities.stale_pr_number) ||
    input.identities.operation_ids.length !== 2 ||
    input.identities.operation_ids.some((id) => !hex64.test(id))
  ) {
    invalid('identity-values');
  }
  validateSourceMap(input.source_map);
  validateAuthorization(input.authorizations);
  exactKeys(
    input.environment,
    ['name', 'prevent_self_review', 'ui_attestation'],
    'environment-shape',
  );
  exactKeys(
    input.environment.ui_attestation,
    [
      'repository',
      'environment',
      'source_kind',
      'observation',
      'capture_sha256',
      'maintainer_id',
      'administrator_bypass',
    ],
    'ui-attestation-shape',
  );
  interval(input.environment.ui_attestation.observation, 'ui-attestation-interval');
  if (
    input.environment.name !== 'r4-trusted-proof' ||
    input.environment.prevent_self_review !== false ||
    input.environment.ui_attestation.repository !== input.identities.repository ||
    input.environment.ui_attestation.environment !== input.environment.name ||
    input.environment.ui_attestation.source_kind !== 'github-environment-ui' ||
    !hex64.test(input.environment.ui_attestation.capture_sha256) ||
    input.environment.ui_attestation.maintainer_id !== '16307884' ||
    input.environment.ui_attestation.administrator_bypass !== false
  ) {
    invalid('ui-attestation-values');
  }
  exactKeys(input.approval_transitions, ['bootstrap', 'continuation', 'stale'], 'approval-shape');
  validateApproval(input.approval_transitions.bootstrap, 'bootstrap');
  validateApproval(input.approval_transitions.continuation, 'continuation');
  validateApproval(input.approval_transitions.stale, 'stale');
  const runIds = [
    input.approval_transitions.bootstrap.run_id,
    input.approval_transitions.continuation.run_id,
    input.approval_transitions.stale.run_id,
  ];
  if (new Set(runIds).size !== runIds.length) invalid('approval-run-reuse');
  exactKeys(input.concurrency, ['normal', 'stale'], 'concurrency-shape');
  validateConcurrency(
    input.concurrency.normal,
    'normal',
    [runIds[0], runIds[1]],
    `agentic-pr-review-r4-${input.identities.repository_id}-pr-${input.identities.normal_pr_number}`,
  );
  const staleFollowOn = input.concurrency.stale.ahead_of_run?.[1]?.run_id;
  validateConcurrency(
    input.concurrency.stale,
    'stale',
    [runIds[2], staleFollowOn],
    `agentic-pr-review-r4-${input.identities.repository_id}-pr-${input.identities.stale_pr_number}`,
  );
  if (
    input.concurrency.normal.group === input.concurrency.stale.group ||
    input.concurrency.normal.ahead_of_run.some((member) =>
      input.concurrency.stale.ahead_of_run.some((other) => other.run_id === member.run_id),
    )
  ) {
    invalid('concurrency-cross-group');
  }
  exactKeys(input.proof_control, ['normal', 'stale'], 'proof-control-shape');
  validateControls(
    input.proof_control.normal,
    input.identities.operation_ids[0],
    ['ready', 'release'],
    'proof-control-normal',
  );
  validateControls(
    input.proof_control.stale,
    input.identities.operation_ids[1],
    ['ready', 'release', 'stale-ready', 'stale-release'],
    'proof-control-stale',
  );
  const inventory = validateInventories(input.inventories, input.identities.operation_ids);
  const generated = generateCleanupPlan({
    operation_ids: input.identities.operation_ids,
    proof_control: input.proof_control,
    observed_cleanup: input.inventories.observed_cleanup,
    resources: input.cleanup.resources,
  });
  if (
    generated.digest !== input.authorizations.cleanup.plan_sha256 ||
    generated.digest !== input.cleanup.plan_sha256
  )
    invalid('cleanup-plan-digest');
  exactKeys(
    input.cleanup,
    ['plan_sha256', 'resources', 'entry_gate', 'ordered_readbacks', 'projection_gate'],
    'cleanup-shape',
  );
  exactKeys(
    input.cleanup.entry_gate,
    [
      'all_runs_terminal',
      'no_runs_queued_or_active',
      'captures_complete',
      'inventory_sealed',
      'artifacts_captured',
      'plan_approved',
    ],
    'cleanup-entry-shape',
  );
  if (Object.values(input.cleanup.entry_gate).some((value) => value !== true))
    invalid('cleanup-entry');
  exactArray(
    input.cleanup.ordered_readbacks.map((item) => item.phase),
    cleanupPhases,
    'cleanup-order',
  );
  if (input.cleanup.ordered_readbacks.some((item) => item.complete !== true))
    invalid('cleanup-readback');
  exactKeys(
    input.cleanup.projection_gate,
    [
      'exact_seven_success',
      'control_absent',
      'state_empty_complete',
      'authorization_absent',
      'secret_names_absent',
      'environment_restored',
      'fixtures_terminal',
      'all_runs_terminal',
      'sticky_exact',
      'credential_copies_absent',
      'private_manifest_finalized',
    ],
    'projection-gate-shape',
  );
  if (Object.values(input.cleanup.projection_gate).some((value) => value !== true))
    invalid('projection-gate');
  if (inventory.recoveryOnly) invalid('recovery-only-no-projection');
  exactKeys(input.canaries, ['live', 'cross_sink', 'public_leak_scan'], 'canary-shape');
  exactKeys(input.canaries.live, ['source', 'facts'], 'canary-live-shape');
  exactKeys(input.canaries.cross_sink, ['source', 'result'], 'canary-cross-sink-shape');
  exactKeys(input.canaries.public_leak_scan, ['source', 'results'], 'canary-leak-shape');
  exactArray(
    input.canaries.live.facts,
    ['github-route-observed', 'provider-route-observed', 'state-route-observed'],
    'canary-live-facts',
  );
  exactKeys(
    input.canaries.public_leak_scan.results,
    [
      'authorization',
      'state_keys',
      'session_plaintext',
      'provider_content',
      'tool_data',
      'host_evidence',
    ],
    'canary-leak-results-shape',
  );
  if (
    input.canaries.live.source !== 'checked-runtime-observations' ||
    input.canaries.cross_sink.source !== 'descendant-v2-receipt' ||
    input.canaries.cross_sink.result !== 'isolated' ||
    input.canaries.public_leak_scan.source !== 'post-cleanup-repository-and-output-scan' ||
    Object.values(input.canaries.public_leak_scan.results).some((value) => value !== 'absent')
  ) {
    invalid('canary-values');
  }
  exactKeys(
    input.restricted_package,
    [
      'destination_kind',
      'destination_identity_sha256',
      'capture_manifest_sha256',
      'oracle_result_sha256',
      'token_copy_absent',
      'current_key_copy_absent',
      'previous_key_copy_absent',
      'manifest_finalized',
    ],
    'restricted-package-shape',
  );
  if (
    input.restricted_package.destination_kind !== 'maintainer-approved-host-restricted-location' ||
    [
      input.restricted_package.destination_identity_sha256,
      input.restricted_package.capture_manifest_sha256,
      input.restricted_package.oracle_result_sha256,
    ].some((item) => !hex64.test(item)) ||
    input.restricted_package.token_copy_absent !== true ||
    input.restricted_package.current_key_copy_absent !== true ||
    input.restricted_package.previous_key_copy_absent !== true ||
    input.restricted_package.manifest_finalized !== true ||
    input.cleanup.projection_gate.private_manifest_finalized !== true
  ) {
    invalid('restricted-package-values');
  }
  return { cleanupPlan: generated.plan, inventory };
}

function validateCaptureManifest(value, host, captureManifestSha256) {
  exactKeys(
    value,
    [
      'kind',
      'repository_id',
      'repository',
      'operation_ids',
      'source_map_sha256',
      'destination_identity_sha256',
      'sources',
      'artifacts',
      'finalized',
    ],
    'capture-manifest-shape',
  );
  if (
    value.kind !== 'apr-r4-e3-capture-manifest-v1' ||
    value.repository_id !== host.identities.repository_id ||
    value.repository !== host.identities.repository ||
    JSON.stringify(value.operation_ids) !== JSON.stringify(host.identities.operation_ids) ||
    value.source_map_sha256 !== sha256(canonicalJson(host.source_map)) ||
    value.destination_identity_sha256 !== host.restricted_package.destination_identity_sha256 ||
    value.finalized !== true ||
    !Array.isArray(value.sources) ||
    value.sources.length === 0 ||
    !Array.isArray(value.artifacts) ||
    value.artifacts.length === 0 ||
    captureManifestSha256 !== host.restricted_package.capture_manifest_sha256
  ) {
    invalid('capture-manifest-values');
  }
  const sourceIds = new Set();
  const sourcePaths = new Set();
  for (const source of value.sources) {
    exactKeys(
      source,
      [
        'source_id',
        'route',
        'page',
        'status',
        'body_path',
        'body_sha256',
        'body_size',
        'safe_headers_sha256',
        'request_started_unix_milliseconds',
        'response_received_unix_milliseconds',
        'next_route',
      ],
      'capture-source-shape',
    );
    if (
      sourceIds.has(source.source_id) ||
      sourcePaths.has(source.body_path) ||
      !source.route.startsWith(`/repos/${host.identities.repository}/`) ||
      !Number.isSafeInteger(source.page) ||
      source.page < 1 ||
      source.status !== 200 ||
      !/^source-[0-9]{4}\.json$/u.test(source.body_path) ||
      !decimal.test(source.body_size) ||
      !hex64.test(source.body_sha256) ||
      !hex64.test(source.safe_headers_sha256) ||
      !Number.isSafeInteger(source.request_started_unix_milliseconds) ||
      !Number.isSafeInteger(source.response_received_unix_milliseconds) ||
      source.response_received_unix_milliseconds < source.request_started_unix_milliseconds ||
      (source.next_route !== null &&
        !source.next_route.startsWith(`/repos/${host.identities.repository}/`))
    ) {
      invalid('capture-source-values');
    }
    sourceIds.add(source.source_id);
    sourcePaths.add(source.body_path);
  }
  const artifactIds = new Set();
  const artifactNames = new Set();
  const artifactsById = new Map();
  for (const artifact of value.artifacts) {
    exactKeys(
      artifact,
      [
        'artifact_id',
        'artifact_name',
        'expected_role',
        'scope',
        'opaque_name',
        'producing_run_id',
        'producing_run_attempt',
        'download_route',
        'download_safe_headers_sha256',
        'download_request_started_unix_milliseconds',
        'download_response_received_unix_milliseconds',
        'archive_path',
        'archive_sha256',
        'archive_size',
        'encrypted_object_path',
        'encrypted_object_sha256',
        'encrypted_object_size',
      ],
      'capture-artifact-shape',
    );
    if (
      artifactIds.has(artifact.artifact_id) ||
      artifactNames.has(artifact.artifact_name.toLowerCase()) ||
      !decimal.test(artifact.artifact_id) ||
      !decimal.test(artifact.producing_run_id) ||
      !decimal.test(artifact.producing_run_attempt) ||
      !['repository', 'normal', 'stale'].includes(artifact.scope) ||
      !artifact.download_route.startsWith(
        `/repos/${host.identities.repository}/actions/artifacts/`,
      ) ||
      !hex64.test(artifact.download_safe_headers_sha256) ||
      !Number.isSafeInteger(artifact.download_request_started_unix_milliseconds) ||
      !Number.isSafeInteger(artifact.download_response_received_unix_milliseconds) ||
      artifact.download_response_received_unix_milliseconds <
        artifact.download_request_started_unix_milliseconds ||
      artifact.archive_path !== `artifact-${artifact.artifact_id}.zip` ||
      artifact.encrypted_object_path !== `artifact-${artifact.artifact_id}.bin` ||
      !hex64.test(artifact.archive_sha256) ||
      !hex64.test(artifact.encrypted_object_sha256) ||
      !decimal.test(artifact.archive_size) ||
      !decimal.test(artifact.encrypted_object_size)
    ) {
      invalid('capture-artifact-values');
    }
    artifactIds.add(artifact.artifact_id);
    artifactNames.add(artifact.artifact_name.toLowerCase());
    artifactsById.set(artifact.artifact_id, artifact);
  }
  const expectedIds = host.inventories.observed_cleanup.map(({ artifact_id }) => artifact_id);
  exactArray(
    [...artifactIds].sort((a, b) => a.localeCompare(b, 'en', { numeric: true })),
    [...expectedIds].sort((a, b) => a.localeCompare(b, 'en', { numeric: true })),
    'capture-artifact-inventory',
  );
  return artifactsById;
}

function validateOracleResult(
  value,
  host,
  captureManifestSha256,
  oracleResultSha256,
  capturedArtifacts,
) {
  exactKeys(
    value,
    ['kind', 'capture_manifest_sha256', 'exact_seven_success', 'recovery_only', 'records'],
    'oracle-result-shape',
  );
  if (
    value.kind !== 'apr-r4-e3-production-codec-oracle-result-v1' ||
    value.capture_manifest_sha256 !== captureManifestSha256 ||
    value.exact_seven_success !== true ||
    value.recovery_only !== false ||
    oracleResultSha256 !== host.restricted_package.oracle_result_sha256 ||
    !Array.isArray(value.records) ||
    value.records.length !== host.inventories.observed_cleanup.length
  ) {
    invalid('oracle-result-values');
  }
  const byId = new Map();
  for (const record of value.records) {
    exactKeys(
      record,
      [
        'artifact_id',
        'role',
        'scope',
        'object_class',
        'object_identity',
        'producing_run_identity',
        'producing_run_attempt',
        'payload_sha256',
      ],
      'oracle-record-shape',
    );
    if (
      byId.has(record.artifact_id) ||
      !decimal.test(record.artifact_id) ||
      !decimal.test(record.producing_run_identity) ||
      !decimal.test(record.producing_run_attempt) ||
      !hex64.test(record.object_identity) ||
      !hex64.test(record.payload_sha256)
    ) {
      invalid('oracle-record-values');
    }
    byId.set(record.artifact_id, record);
  }
  for (const expected of host.inventories.observed_cleanup) {
    const observed = byId.get(expected.artifact_id);
    const captured = capturedArtifacts.get(expected.artifact_id);
    if (
      !observed ||
      !captured ||
      observed.scope !== expected.scope ||
      observed.object_class !== expected.object_class ||
      captured.scope !== observed.scope ||
      captured.expected_role !== observed.role
    ) {
      invalid('oracle-cleanup-inventory');
    }
  }
  for (const expected of host.inventories.expected_success) {
    const observed = byId.get(expected.artifact_id);
    if (!observed || observed.role !== expected.role) invalid('oracle-success-inventory');
  }
  exactArray(
    value.records.filter(({ role }) => expectedProductRoles.includes(role)).map(({ role }) => role),
    expectedProductRoles,
    'oracle-success-roles',
  );
}

export function assembleTrustedProofEvidence({
  host,
  captureManifest,
  captureManifestSha256,
  oracleResult,
  oracleResultSha256,
  credentialCopiesAbsent,
}) {
  if (credentialCopiesAbsent !== true) invalid('assembler-credential-copies');
  if (
    sha256(canonicalJson(captureManifest)) !== captureManifestSha256 ||
    sha256(canonicalJson(oracleResult)) !== oracleResultSha256
  ) {
    invalid('assembler-input-digest');
  }
  const capturedArtifacts = validateCaptureManifest(captureManifest, host, captureManifestSha256);
  validateOracleResult(
    oracleResult,
    host,
    captureManifestSha256,
    oracleResultSha256,
    capturedArtifacts,
  );
  validateHostEvidence(host);
  const publicEvidence = projectTrustedProofEvidence(host);
  assertPublicSafeEvidence(publicEvidence);
  return { host, publicEvidence };
}

export function projectTrustedProofEvidence(input) {
  validateHostEvidence(input);
  return {
    kind: 'apr-r4-e3-public-safe-evidence-v1',
    identities: {
      workflow_sha: input.identities.workflow_sha,
      action_source_sha: input.identities.action_source_sha,
      payload_source_sha: input.identities.payload_source_sha,
      payload_sha256: input.identities.payload_sha256,
    },
    participating_run_ids: [
      input.approval_transitions.bootstrap.run_id,
      input.approval_transitions.continuation.run_id,
      input.approval_transitions.stale.run_id,
      input.concurrency.stale.ahead_of_run[1].run_id,
    ],
    scheduling: {
      distinct_groups: true,
      holder_waiter_pairs_observed: 2,
      holders_uncancelled: true,
      waiters_started_after_holders: true,
    },
    state_outcomes: {
      bootstrap: 'passed',
      continuation: 'passed',
      stale_rejection: 'passed',
      accepted_generations: 2,
      product_anchor_count: expectedProductRoles.length,
    },
    cleanup: {
      complete: true,
      final_state_inventory_count: 0,
      authorization_absent: true,
      operation_created_secrets_absent: true,
      environment_restored: true,
      fixture_resources_terminal: true,
      credential_copies_absent: true,
      all_runs_terminal: true,
    },
    canaries: {
      public_surfaces: 'clear',
      nested_session_plaintext: 'absent',
      provider_content: 'absent',
      tool_data: 'absent',
      protected_digests: 'absent',
    },
  };
}

export function assertPublicSafeEvidence(value) {
  exactKeys(
    value,
    [
      'kind',
      'identities',
      'participating_run_ids',
      'scheduling',
      'state_outcomes',
      'cleanup',
      'canaries',
    ],
    'public-shape',
  );
  if (value.kind !== 'apr-r4-e3-public-safe-evidence-v1') invalid('public-kind');
  const serialized = JSON.stringify(value);
  const forbidden = [
    'artifact_id',
    'operation_id',
    'comment_id',
    'environment_id',
    'reviewer',
    'approval',
    'capture_sha256',
    'manifest_sha256',
    'plan_sha256',
    'recovery-only',
    'lineage',
    'candidate',
    'acceptance',
    'archive',
    'encrypted',
  ];
  if (forbidden.some((token) => serialized.includes(token))) invalid('public-forbidden-data');
  if (
    value.state_outcomes.product_anchor_count !== 7 ||
    value.cleanup.complete !== true ||
    value.cleanup.final_state_inventory_count !== 0 ||
    Object.values(value.canaries).some((item) => !['clear', 'absent'].includes(item))
  ) {
    invalid('public-values');
  }
  return true;
}
