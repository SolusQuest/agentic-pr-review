import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import Ajv from 'ajv';

const root = path.resolve(import.meta.dirname, '..');
const fixtureRoot = path.join(root, 'runtime', 'tests', 'fixtures', 'action-host', 'trusted-proof');
const ajv = new Ajv({ allErrors: true, strict: true });
const hostSchema = JSON.parse(
  fs.readFileSync(
    path.join(fixtureRoot, 'schemas', 'host-restricted-evidence.schema.json'),
    'utf8',
  ),
);
const publicSchema = JSON.parse(
  fs.readFileSync(path.join(fixtureRoot, 'schemas', 'public-safe-evidence.schema.json'), 'utf8'),
);
const validateHost = ajv.compile(hostSchema);
const validatePublic = ajv.compile(publicSchema);

function reject(code) {
  throw new Error(`APR_R4_E3_EVIDENCE_INVALID ${code}`);
}

function exactSet(left, right) {
  return (
    left.length === new Set(left).size &&
    right.length === new Set(right).size &&
    JSON.stringify([...left].sort()) === JSON.stringify([...right].sort())
  );
}

function comparePositiveIds(left, right) {
  const a = BigInt(left);
  const b = BigInt(right);
  return a < b ? -1 : a > b ? 1 : 0;
}

export function assertPublicSafeEvidence(value) {
  if (!validatePublic(value)) reject('public-schema');
  const serialized = JSON.stringify(value);
  const forbidden = [
    'artifact_id',
    'artifact_name',
    'artifact_digest',
    'candidate',
    'acceptance_receipt',
    'predecessor',
    'lineage',
    'ciphertext',
    'continuation_content',
    'run_attempt',
    'job_id',
    'event_id',
    'comment_id',
    'phase_key',
    'creation_phase',
  ];
  if (forbidden.some((token) => serialized.includes(token))) reject('public-forbidden-data');
  return true;
}

export function projectTrustedProofEvidence(input) {
  if (!validateHost(input)) reject('host-schema');
  const { identities, fixture, runs, observation, product, state, cleanup, canaries } = input;
  if (
    runs.bootstrap.run_id === runs.continuation.run_id ||
    runs.bootstrap.reviewed_head_sha !== identities.normal_head_sha ||
    runs.continuation.reviewed_head_sha !== identities.normal_head_sha ||
    runs.bootstrap.concurrency_group !== runs.continuation.concurrency_group ||
    observation.equal_evaluated_group !== true ||
    !(
      runs.bootstrap.barrier_ready_at <= runs.continuation.created_at &&
      runs.continuation.created_at <= observation.observed_at &&
      observation.observed_at < runs.bootstrap.completed_at &&
      runs.bootstrap.completed_at < runs.continuation.protected_job_started_at &&
      runs.continuation.protected_job_started_at < runs.continuation.completed_at
    )
  ) {
    reject('serialization');
  }
  if (
    new Set([
      identities.reviewed_base_sha,
      identities.normal_head_sha,
      identities.stale_admitted_head_sha,
      identities.stale_advanced_head_sha,
    ]).size !== 4 ||
    fixture.normal_operation_id === fixture.stale_operation_id ||
    fixture.normal_pr_number === fixture.stale_pr_number ||
    product.bootstrap.accepted_head_sha !== identities.normal_head_sha ||
    product.continuation.accepted_head_sha !== identities.normal_head_sha ||
    product.bootstrap.sticky_comment_id !== product.continuation.sticky_comment_id ||
    product.continuation.predecessor_acceptance_receipt_id !==
      product.bootstrap.acceptance_receipt_id ||
    !product.bootstrap.sticky_comment_url.includes(`/pull/${fixture.normal_pr_number}#`) ||
    !product.bootstrap.sticky_marker.endsWith(`head_sha=${identities.normal_head_sha} -->`)
  ) {
    reject('fixture-product-lineage');
  }
  const createdIds = state.created.map(({ artifact_id }) => artifact_id);
  const createdNames = state.created.map(({ artifact_name }) => artifact_name);
  const createdDigests = state.created.map(({ artifact_digest }) => artifact_digest);
  if (
    createdIds.length !== new Set(createdIds).size ||
    createdNames.length !== new Set(createdNames).size ||
    createdDigests.length !== new Set(createdDigests).size ||
    !exactSet(createdIds, cleanup.deleted_state_ids) ||
    !createdIds.includes(product.bootstrap.candidate_artifact_id) ||
    !createdIds.includes(product.bootstrap.acceptance_receipt_id) ||
    !createdIds.includes(product.continuation.candidate_artifact_id) ||
    !createdIds.includes(product.continuation.acceptance_receipt_id)
  ) {
    reject('state-inventory');
  }
  const requiredClasses = [
    'locator-root',
    'lineage-head',
    'candidate',
    'publication-intent',
    'acceptance',
    'cleanup',
    'transaction',
  ];
  if (
    !requiredClasses.every((value) =>
      state.created.some(({ artifact_class }) => artifact_class === value),
    ) ||
    state.pre_state.inventory.length !== 0 ||
    state.final_state.inventory.length !== 0 ||
    cleanup.state_key_removed_after_final_readback !== true
  ) {
    reject('state-cleanup');
  }
  const publicEvidence = {
    kind: 'apr-r4-e3-public-safe-evidence-v1',
    identities: {
      workflow_sha: identities.workflow_sha,
      action_source_sha: identities.action_source_sha,
      payload_sha256: identities.payload_sha256,
      reviewed_base_sha: identities.reviewed_base_sha,
      normal_head_sha: identities.normal_head_sha,
      stale_admitted_head_sha: identities.stale_admitted_head_sha,
      stale_advanced_head_sha: identities.stale_advanced_head_sha,
    },
    participating_run_ids: [runs.bootstrap.run_id, runs.continuation.run_id].sort(
      comparePositiveIds,
    ),
    scheduling: {
      concurrency_group_equal: true,
      run_one_observed_running: true,
      run_one_uncancelled: true,
      run_two_observed_pending: true,
      run_two_outside_protected_execution: true,
      barrier_to_queue_delay_ms: runs.continuation.created_at - runs.bootstrap.barrier_ready_at,
      observation_to_run_one_completion_ms: runs.bootstrap.completed_at - observation.observed_at,
      completion_to_run_two_start_ms:
        runs.continuation.protected_job_started_at - runs.bootstrap.completed_at,
    },
    state_outcomes: {
      bootstrap: 'passed',
      continuation: 'passed',
      stale_rejection: 'passed',
      accepted_generations: 2,
      encrypted_state_record_count: state.created.length,
    },
    publication: {
      comment_url: product.bootstrap.sticky_comment_url,
      marker: product.bootstrap.sticky_marker,
      reviewed_head_sha: identities.normal_head_sha,
    },
    cleanup: {
      complete: true,
      final_state_inventory_count: state.final_state.inventory.length,
      deleted_state_record_count: cleanup.deleted_state_ids.length,
      authorization_absent: true,
      operation_created_secrets_absent: true,
      environment_restored: true,
      fixture_branches_absent: true,
      fixture_prs_closed: true,
      all_runs_terminal: true,
      no_follow_on_runs: true,
    },
    canaries: {
      secret_material: canaries.secret_material_absent ? 'absent' : 'present',
      plaintext_session: canaries.plaintext_session_absent ? 'absent' : 'present',
      provider_content: canaries.provider_content_absent ? 'absent' : 'present',
      tool_data: canaries.tool_data_absent ? 'absent' : 'present',
      permission_set: canaries.permission_set_exact ? 'exact' : 'unexpected',
    },
  };
  assertPublicSafeEvidence(publicEvidence);
  return publicEvidence;
}

function readCanonicalHostEvidence(pathname) {
  const bytes = fs.readFileSync(pathname);
  if (
    bytes.length === 0 ||
    bytes.length > 1024 * 1024 ||
    bytes.at(-1) !== 0x0a ||
    bytes.includes(0x0d)
  ) {
    reject('input-encoding');
  }
  let value;
  try {
    value = JSON.parse(bytes.toString('utf8'));
  } catch {
    reject('input-json');
  }
  if (`${JSON.stringify(value)}\n` !== bytes.toString('utf8')) reject('input-canonical');
  return value;
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(import.meta.filename)) {
  try {
    if (process.argv.length !== 3) reject('usage');
    const publicEvidence = projectTrustedProofEvidence(readCanonicalHostEvidence(process.argv[2]));
    process.stdout.write(`${JSON.stringify(publicEvidence)}\n`);
  } catch (error) {
    process.stderr.write(
      `${error instanceof Error ? error.message : 'APR_R4_E3_EVIDENCE_INVALID'}\n`,
    );
    process.exitCode = 1;
  }
}
