import crypto from 'node:crypto';
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
const stateFamilies = [
  'locator_root',
  'lineage_head',
  'candidate',
  'publication_intent',
  'acceptance',
  'publication_failure',
  'abandonment',
  'reset',
  'expiry_transition',
  'cleanup',
];

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

function sha256(value) {
  return crypto.createHash('sha256').update(value).digest('hex');
}

function commentIdFromUrl(value) {
  return /^https:\/\/github\.com\/SolusQuest\/agentic-pr-review\/pull\/[1-9][0-9]*#issuecomment-([1-9][0-9]*)$/u.exec(
    value,
  )?.[1];
}

function isEmptyInventory(state) {
  return (
    state.repository_root.pagination_complete === true &&
    state.repository_root.locator_root.length === 0 &&
    state.normal.pagination_complete === true &&
    state.stale.pagination_complete === true &&
    stateFamilies.every(
      (family) =>
        state.normal.families[family].length === 0 && state.stale.families[family].length === 0,
    )
  );
}

function manifestBytes(manifest) {
  return JSON.stringify({
    kind: manifest.kind,
    repository_id: manifest.repository_id,
    repository: manifest.repository,
    pr_number: manifest.pr_number,
    fixture_head_sha: manifest.fixture_head_sha,
    operation_id: manifest.operation_id,
    workflow_sha: manifest.workflow_sha,
    action_source_sha: manifest.action_source_sha,
    payload_sha256: manifest.payload_sha256,
  });
}

function environmentSnapshotBytes(environment) {
  return JSON.stringify({
    repository: environment.repository,
    name: environment.name,
    exists: environment.exists,
    deployment_branch: environment.deployment_branch,
    designated_reviewer_id: environment.designated_reviewer_id,
    reviewer_permission: environment.reviewer_permission,
    prevent_self_review: environment.prevent_self_review,
    administrator_bypass: environment.administrator_bypass,
    secret_names: environment.secret_names,
    token_permissions: environment.token_permissions,
  });
}

function proofCommentPreimage(record) {
  const field = (name, value) => `\"${name}\":${JSON.stringify(value)}`;
  const numeric = (name, value) => `\"${name}\":${value}`;
  return `{${[
    field('contract', record.contract),
    field('kind', record.kind),
    field('operation_id', record.operation_id),
    numeric('repository_id', record.repository_id),
    field('repository', record.repository),
    numeric('pr_number', record.pr_number),
    field('fixture_head_sha', record.fixture_head_sha),
    field('workflow_sha', record.workflow_sha),
    field('action_source_sha', record.action_source_sha),
    field('payload_sha256', record.payload_sha256),
    numeric('run_id', record.producing_run_id),
    numeric('run_attempt', record.producing_run_attempt),
    record.predecessor_comment_id === null
      ? '\"predecessor_comment_id\":null'
      : numeric('predecessor_comment_id', record.predecessor_comment_id),
    field('body_sha256', ''),
  ].join(',')}}`;
}

function exactProofComment(record, kind, run, coordinates, predecessorCommentId, permission) {
  return (
    record.kind === kind &&
    record.operation_id === coordinates.operationId &&
    record.repository_id === coordinates.repositoryId &&
    record.repository === coordinates.repository &&
    record.pr_number === coordinates.prNumber &&
    record.fixture_head_sha === coordinates.headSha &&
    record.workflow_sha === coordinates.workflowSha &&
    record.action_source_sha === coordinates.actionSourceSha &&
    record.payload_sha256 === coordinates.payloadSha256 &&
    record.predecessor_comment_id === predecessorCommentId &&
    record.actor_permission === permission &&
    record.actor_id === coordinates.reviewerId &&
    record.body_sha256 === sha256(proofCommentPreimage(record)) &&
    record.body_sha256 === record.readback_body_sha256 &&
    record.producing_run_id === run.run_id &&
    record.producing_run_attempt === run.run_attempt
  );
}

function exactArtifact(records, physicalId, objectClass, objectIdentity) {
  return records.some(
    (record) =>
      record.physical_artifact_id === physicalId &&
      record.object_class === objectClass &&
      record.object_identity === objectIdentity,
  );
}

function findArtifact(records, objectClass, objectIdentity) {
  return records.find(
    (record) => record.object_class === objectClass && record.object_identity === objectIdentity,
  );
}

function exactAcceptanceReceipt(transition, predecessorIdentity) {
  const receipt = transition.acceptance_receipt;
  return (
    receipt.acceptance_object_identity === transition.acceptance_object_identity &&
    receipt.candidate_object_identity === transition.candidate_object_identity &&
    receipt.predecessor_acceptance_object_identity === predecessorIdentity &&
    receipt.publication_comment_id === transition.sticky_comment_id &&
    receipt.publication_comment_url === transition.sticky_comment_url &&
    receipt.publication_body_sha256 === transition.sticky_body_sha256 &&
    receipt.accepted_generation === transition.lineage_generation
  );
}

export function assertPublicSafeEvidence(value) {
  if (!validatePublic(value)) reject('public-schema');
  const serialized = JSON.stringify(value);
  const forbidden = [
    'artifact_id',
    'artifact_name',
    'artifact_digest',
    'object_identity',
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
  const {
    identities,
    fixture,
    authorization,
    protected_environment: environment,
    proof_control: proofControl,
    runs,
    observation,
    product,
    state,
    cleanup,
    canaries,
  } = input;

  if (
    runs.bootstrap.run_id === runs.continuation.run_id ||
    runs.bootstrap.workflow_sha !== identities.workflow_sha ||
    runs.continuation.workflow_sha !== identities.workflow_sha ||
    runs.bootstrap.reviewed_head_sha !== identities.normal_head_sha ||
    runs.continuation.reviewed_head_sha !== identities.normal_head_sha ||
    runs.bootstrap.concurrency_group !== runs.continuation.concurrency_group ||
    observation.equal_evaluated_group !== true ||
    !(
      runs.bootstrap.protected_job_started_at < runs.bootstrap.barrier_ready_at &&
      runs.bootstrap.barrier_ready_at <= runs.continuation.created_at &&
      runs.continuation.created_at <= observation.observed_at &&
      observation.observed_at < proofControl.normal.barrier_released_at &&
      proofControl.normal.barrier_released_at < runs.bootstrap.completed_at &&
      runs.bootstrap.completed_at < runs.continuation.protected_job_started_at &&
      runs.continuation.protected_job_started_at < runs.continuation.completed_at
    )
  ) {
    reject('serialization');
  }

  if (
    identities.reviewed_base_sha !== identities.workflow_sha ||
    fixture.normal_parent_sha !== identities.reviewed_base_sha ||
    fixture.stale_initial_parent_sha !== identities.reviewed_base_sha ||
    fixture.stale_advanced_parent_sha !== identities.stale_admitted_head_sha ||
    fixture.normal_operation_id === fixture.stale_operation_id ||
    fixture.normal_pr_number === fixture.stale_pr_number
  ) {
    reject('fixture-commit-graph');
  }

  const manifest = authorization.manifest;
  const manifestSha256 = sha256(manifestBytes(manifest));
  const staleManifest = authorization.stale_manifest;
  const staleManifestSha256 = sha256(manifestBytes(staleManifest));
  const approvedSecrets = [
    'AGENTIC_PR_REVIEW_PREVIOUS_STATE_KEY',
    'AGENTIC_PR_REVIEW_STATE_KEY',
    'DEEPSEEK_API_KEY',
  ];
  const environmentSha256 = sha256(environmentSnapshotBytes(environment));
  if (
    manifestSha256 !== authorization.manifest_sha256 ||
    manifestSha256 !== authorization.repository_variable_readback_sha256 ||
    manifest.repository_id !== identities.repository_id ||
    manifest.repository !== identities.repository ||
    manifest.pr_number !== fixture.normal_pr_number ||
    manifest.fixture_head_sha !== identities.normal_head_sha ||
    manifest.operation_id !== fixture.normal_operation_id ||
    manifest.workflow_sha !== identities.workflow_sha ||
    manifest.action_source_sha !== identities.action_source_sha ||
    manifest.payload_sha256 !== identities.payload_sha256 ||
    !(
      authorization.pre_enable_absent_read_back_at < authorization.normal_set_at &&
      authorization.normal_set_at <= authorization.normal_read_back_at &&
      authorization.normal_read_back_at < authorization.normal_first_privileged_job_started_at &&
      authorization.normal_first_privileged_job_started_at ===
        runs.bootstrap.protected_job_started_at &&
      authorization.normal_pair_terminal_at >= runs.continuation.completed_at &&
      authorization.normal_pair_terminal_at < authorization.normal_removed_at &&
      authorization.normal_removed_at <= authorization.normal_absent_read_back_at &&
      authorization.normal_absent_read_back_at < authorization.stale_set_at &&
      authorization.stale_set_at <= authorization.stale_read_back_at &&
      authorization.stale_read_back_at < authorization.stale_first_privileged_job_started_at &&
      authorization.stale_first_privileged_job_started_at ===
        product.stale.authorized_stale_run.protected_job_started_at &&
      authorization.stale_operation_terminal_at >=
        Math.max(
          product.stale.authorized_stale_run.completed_at,
          product.stale.unauthorized_follow_on_run.completed_at,
        ) &&
      authorization.stale_operation_terminal_at < authorization.stale_removed_at &&
      authorization.stale_removed_at <= authorization.post_operation_absent_read_back_at
    ) ||
    staleManifestSha256 !== authorization.stale_manifest_sha256 ||
    staleManifestSha256 !== authorization.stale_repository_variable_readback_sha256 ||
    staleManifest.repository_id !== identities.repository_id ||
    staleManifest.repository !== identities.repository ||
    staleManifest.pr_number !== fixture.stale_pr_number ||
    staleManifest.fixture_head_sha !== identities.stale_admitted_head_sha ||
    staleManifest.operation_id !== fixture.stale_operation_id ||
    staleManifest.workflow_sha !== identities.workflow_sha ||
    staleManifest.action_source_sha !== identities.action_source_sha ||
    staleManifest.payload_sha256 !== identities.payload_sha256 ||
    environmentSha256 !== environment.snapshot_sha256 ||
    environmentSha256 !== environment.normal_snapshot_readback_sha256 ||
    environmentSha256 !== environment.stale_snapshot_readback_sha256 ||
    environment.normal_read_back_at >= environment.normal_first_privileged_job_started_at ||
    environment.normal_first_privileged_job_started_at !==
      runs.bootstrap.protected_job_started_at ||
    environment.stale_read_back_at >= environment.stale_first_privileged_job_started_at ||
    environment.stale_first_privileged_job_started_at !==
      product.stale.authorized_stale_run.protected_job_started_at ||
    environment.repository !== identities.repository ||
    JSON.stringify([...environment.secret_names].sort()) !== JSON.stringify(approvedSecrets)
  ) {
    reject('authorization-environment');
  }

  const normalCoordinates = {
    operationId: fixture.normal_operation_id,
    repositoryId: identities.repository_id,
    repository: identities.repository,
    prNumber: fixture.normal_pr_number,
    headSha: identities.normal_head_sha,
    workflowSha: identities.workflow_sha,
    actionSourceSha: identities.action_source_sha,
    payloadSha256: identities.payload_sha256,
    reviewerId: environment.designated_reviewer_id,
  };
  const staleCoordinates = {
    ...normalCoordinates,
    operationId: fixture.stale_operation_id,
    prNumber: fixture.stale_pr_number,
    headSha: identities.stale_admitted_head_sha,
  };
  const normalCommentIds = [
    proofControl.normal.ready.comment_id,
    proofControl.normal.release.comment_id,
  ];
  const staleCommentIds = [
    proofControl.stale.ready.comment_id,
    proofControl.stale.release.comment_id,
  ];
  if (
    !exactProofComment(
      proofControl.normal.ready,
      'ready',
      runs.bootstrap,
      normalCoordinates,
      null,
      null,
    ) ||
    !exactProofComment(
      proofControl.normal.release,
      'release',
      runs.bootstrap,
      normalCoordinates,
      proofControl.normal.ready.comment_id,
      environment.reviewer_permission,
    ) ||
    proofControl.normal.ready.observed_at !== runs.bootstrap.barrier_ready_at ||
    proofControl.normal.release.observed_at !== proofControl.normal.barrier_released_at ||
    proofControl.normal.dispatch_verify_completed.operation_id !== fixture.normal_operation_id ||
    proofControl.normal.dispatch_verify_completed.ready_comment_id !==
      proofControl.normal.ready.comment_id ||
    proofControl.normal.dispatch_verify_completed.release_comment_id !==
      proofControl.normal.release.comment_id ||
    proofControl.normal.dispatch_verify_completed.bootstrap_run_id !== runs.bootstrap.run_id ||
    proofControl.normal.dispatch_verify_completed.continuation_run_id !==
      runs.continuation.run_id ||
    proofControl.normal.dispatch_verify_completed.observed_at < runs.continuation.completed_at ||
    proofControl.normal.cleanup_receipt.receipt.operation_id !== fixture.normal_operation_id ||
    !exactSet(
      proofControl.normal.cleanup_receipt.receipt.comment_outcomes.map(
        ({ comment_id }) => comment_id,
      ),
      normalCommentIds,
    ) ||
    proofControl.normal.cleanup_receipt.final_absence_read_back_at <=
      proofControl.normal.dispatch_verify_completed.observed_at ||
    !exactProofComment(
      proofControl.stale.ready,
      'stale-ready',
      product.stale.authorized_stale_run,
      staleCoordinates,
      null,
      null,
    ) ||
    !exactProofComment(
      proofControl.stale.release,
      'stale-release',
      product.stale.authorized_stale_run,
      staleCoordinates,
      proofControl.stale.ready.comment_id,
      environment.reviewer_permission,
    ) ||
    proofControl.stale.cleanup_receipt.receipt.operation_id !== fixture.stale_operation_id ||
    !exactSet(
      proofControl.stale.cleanup_receipt.receipt.comment_outcomes.map(
        ({ comment_id }) => comment_id,
      ),
      staleCommentIds,
    ) ||
    proofControl.stale.cleanup_receipt.final_absence_read_back_at <=
      product.stale.authorized_stale_run.completed_at ||
    new Set([...normalCommentIds, ...staleCommentIds]).size !== 4
  ) {
    reject('proof-control');
  }

  const bootstrap = product.bootstrap;
  const continuation = product.continuation;
  if (
    bootstrap.accepted_head_sha !== identities.normal_head_sha ||
    continuation.accepted_head_sha !== identities.normal_head_sha ||
    bootstrap.sticky_comment_id !== continuation.sticky_comment_id ||
    bootstrap.sticky_comment_url !== continuation.sticky_comment_url ||
    commentIdFromUrl(bootstrap.sticky_comment_url) !== bootstrap.sticky_comment_id ||
    bootstrap.sticky_body_sha256 !== bootstrap.sticky_readback_body_sha256 ||
    bootstrap.sticky_marker !== bootstrap.sticky_readback_marker ||
    continuation.sticky_body_sha256 !== continuation.sticky_readback_body_sha256 ||
    continuation.sticky_marker !== continuation.sticky_readback_marker ||
    !bootstrap.sticky_marker.includes(`body_sha256=${bootstrap.sticky_body_sha256}`) ||
    !continuation.sticky_marker.includes(`body_sha256=${continuation.sticky_body_sha256}`) ||
    !bootstrap.sticky_marker.endsWith(`head_sha=${identities.normal_head_sha} -->`) ||
    !continuation.sticky_marker.endsWith(`head_sha=${identities.normal_head_sha} -->`) ||
    continuation.predecessor_acceptance_object_identity !== bootstrap.acceptance_object_identity ||
    !exactAcceptanceReceipt(bootstrap, null) ||
    !exactAcceptanceReceipt(continuation, bootstrap.acceptance_object_identity) ||
    bootstrap.base_scope_digest !== continuation.base_scope_digest ||
    bootstrap.lineage_head_object_identity !== continuation.lineage_head_object_identity ||
    bootstrap.lineage_epoch !== continuation.lineage_epoch ||
    bootstrap.lineage_generation !== 1 ||
    continuation.lineage_generation !== 2 ||
    cleanup.terminal_resources.product_sticky.comment_id !== continuation.sticky_comment_id ||
    cleanup.terminal_resources.product_sticky.comment_url !== continuation.sticky_comment_url ||
    cleanup.terminal_resources.product_sticky.body_sha256 !== continuation.sticky_body_sha256 ||
    cleanup.terminal_resources.product_sticky.marker !== continuation.sticky_marker
  ) {
    reject('sticky-publication');
  }

  const stale = product.stale;
  const staleRun = stale.authorized_stale_run;
  const followOn = stale.unauthorized_follow_on_run;
  if (
    new Set([runs.bootstrap.run_id, runs.continuation.run_id, staleRun.run_id, followOn.run_id])
      .size !== 4 ||
    staleRun.workflow_sha !== identities.workflow_sha ||
    followOn.workflow_sha !== identities.workflow_sha ||
    staleRun.reviewed_head_sha !== identities.stale_admitted_head_sha ||
    followOn.reviewed_head_sha !== identities.stale_advanced_head_sha ||
    proofControl.stale.ready.observed_at !== staleRun.stale_ready_at ||
    proofControl.stale.release.observed_at !== staleRun.stale_release_at ||
    proofControl.stale.barrier_released_at !== staleRun.stale_release_at ||
    !(
      staleRun.protected_job_started_at < staleRun.stale_ready_at &&
      staleRun.stale_ready_at < staleRun.head_advanced_at &&
      staleRun.head_advanced_at <= followOn.created_at &&
      followOn.created_at <= followOn.pending_observation.observed_at &&
      followOn.pending_observation.observed_at < staleRun.completed_at &&
      staleRun.completed_at < followOn.workflow_started_at &&
      followOn.workflow_started_at <= followOn.completed_at &&
      staleRun.head_advanced_at < staleRun.stale_release_at &&
      staleRun.stale_release_at < staleRun.provider_completed_at &&
      staleRun.provider_completed_at < staleRun.host_revalidated_at &&
      staleRun.host_revalidated_at <= staleRun.completed_at
    )
  ) {
    reject('stale-sequence');
  }

  const createdIds = state.created.map(({ physical_artifact_id }) => physical_artifact_id);
  const objectIdentities = state.created.map(({ object_identity }) => object_identity);
  const scopes = state.scopes;
  const scopeDigests = [
    scopes.repository_root.scope_digest,
    scopes.normal.base_scope_digest,
    scopes.stale.base_scope_digest,
  ];
  if (
    createdIds.length !== new Set(createdIds).size ||
    objectIdentities.length !== new Set(objectIdentities).size ||
    !exactSet(createdIds, cleanup.deleted_physical_artifact_ids) ||
    !isEmptyInventory(state.pre_state) ||
    !isEmptyInventory(state.final_state) ||
    new Set(scopeDigests).size !== 3 ||
    state.pre_state.repository_root.scope_digest !== scopes.repository_root.scope_digest ||
    state.final_state.repository_root.scope_digest !== scopes.repository_root.scope_digest ||
    state.pre_state.normal.base_scope_digest !== scopes.normal.base_scope_digest ||
    state.final_state.normal.base_scope_digest !== scopes.normal.base_scope_digest ||
    state.pre_state.stale.base_scope_digest !== scopes.stale.base_scope_digest ||
    state.final_state.stale.base_scope_digest !== scopes.stale.base_scope_digest ||
    bootstrap.base_scope_digest !== scopes.normal.base_scope_digest ||
    cleanup.state_key_removed_after_final_readback !== true
  ) {
    reject('state-inventory');
  }

  const phaseRuns = new Map([
    ['bootstrap', runs.bootstrap],
    ['continuation', runs.continuation],
    ['stale-setup', staleRun],
  ]);
  if (
    state.created.some((record) => {
      const owner = phaseRuns.get(record.creation_phase);
      const expectedScope =
        record.object_class === 'locator_root'
          ? 'repository_root'
          : record.creation_phase === 'stale-setup'
            ? 'stale'
            : 'normal';
      const expectedDigest =
        expectedScope === 'repository_root'
          ? scopes.repository_root.scope_digest
          : scopes[expectedScope].base_scope_digest;
      const expectedOpaqueName =
        expectedScope === 'repository_root'
          ? scopes.repository_root.locator_root_opaque_name
          : scopes[expectedScope].family_opaque_names[record.object_class];
      return (
        !owner ||
        record.producing_run_id !== owner.run_id ||
        record.producing_run_attempt !== owner.run_attempt ||
        record.scope !== expectedScope ||
        record.scope_digest !== expectedDigest ||
        record.opaque_name !== expectedOpaqueName
      );
    })
  ) {
    reject('state-producer');
  }

  const staleSetupIds = state.created
    .filter(({ creation_phase }) => creation_phase === 'stale-setup')
    .map(({ physical_artifact_id }) => physical_artifact_id);
  if (!exactSet(staleSetupIds, staleRun.state_setup_physical_artifact_ids)) {
    reject('stale-state-setup');
  }

  if (
    !exactArtifact(
      state.created,
      bootstrap.candidate_physical_artifact_id,
      'candidate',
      bootstrap.candidate_object_identity,
    ) ||
    !exactArtifact(
      state.created,
      bootstrap.acceptance_physical_artifact_id,
      'acceptance',
      bootstrap.acceptance_object_identity,
    ) ||
    !exactArtifact(
      state.created,
      continuation.candidate_physical_artifact_id,
      'candidate',
      continuation.candidate_object_identity,
    ) ||
    !exactArtifact(
      state.created,
      continuation.acceptance_physical_artifact_id,
      'acceptance',
      continuation.acceptance_object_identity,
    )
  ) {
    reject('product-state-binding');
  }

  const normalHeads = state.created.filter(
    ({ object_class, scope }) => object_class === 'lineage_head' && scope === 'normal',
  );
  const normalHead = normalHeads[0];
  const bootstrapCandidate = findArtifact(
    state.created,
    'candidate',
    bootstrap.candidate_object_identity,
  );
  const bootstrapIntent = state.created.find(
    ({ object_class, generation, scope }) =>
      object_class === 'publication_intent' && generation === 1 && scope === 'normal',
  );
  const predecessorAcceptance = findArtifact(
    state.created,
    'acceptance',
    bootstrap.acceptance_object_identity,
  );
  const continuationCandidate = findArtifact(
    state.created,
    'candidate',
    continuation.candidate_object_identity,
  );
  const continuationIntent = state.created.find(
    ({ object_class, generation, scope }) =>
      object_class === 'publication_intent' && generation === 2 && scope === 'normal',
  );
  const currentAcceptance = findArtifact(
    state.created,
    'acceptance',
    continuation.acceptance_object_identity,
  );
  const normalCleanup = state.created.find(
    ({ object_class, scope }) => object_class === 'cleanup' && scope === 'normal',
  );
  if (
    normalHeads.length !== 1 ||
    !normalHead ||
    normalHead.object_identity !== bootstrap.lineage_head_object_identity ||
    normalHead.object_identity !== continuation.lineage_head_object_identity ||
    normalHead.predecessor_identity !== null ||
    normalHead.successor_identity !== null ||
    normalHead.epoch !== bootstrap.lineage_epoch ||
    normalHead.generation !== 1 ||
    !bootstrapCandidate ||
    !bootstrapIntent ||
    !predecessorAcceptance ||
    !continuationCandidate ||
    !continuationIntent ||
    !currentAcceptance ||
    !normalCleanup ||
    bootstrapCandidate.predecessor_identity !== null ||
    bootstrapIntent.predecessor_identity !== bootstrapCandidate.object_identity ||
    predecessorAcceptance.predecessor_identity !== null ||
    continuationCandidate.predecessor_identity !== predecessorAcceptance.object_identity ||
    continuationIntent.predecessor_identity !== continuationCandidate.object_identity ||
    currentAcceptance.predecessor_identity !== predecessorAcceptance.object_identity ||
    normalCleanup.predecessor_identity !== currentAcceptance.object_identity
  ) {
    reject('lineage-binding');
  }

  if (staleSetupIds.length === 0) reject('stale-state-setup');

  const expectedCredentialBySink = new Map([
    ['github', ['github-token:pull-requests-write', true]],
    ['actions', ['github-token:actions-write', true]],
    ['provider', ['DEEPSEEK_API_KEY', true]],
    ['current_state', ['AGENTIC_PR_REVIEW_STATE_KEY', true]],
    ['previous_state', ['AGENTIC_PR_REVIEW_PREVIOUS_STATE_KEY', true]],
    ['unrelated_credentials', ['none', false]],
  ]);
  const credentialBySink = new Map(canaries.credential_by_sink.map((entry) => [entry.sink, entry]));
  if (
    credentialBySink.size !== expectedCredentialBySink.size ||
    [...expectedCredentialBySink].some(([sink, [credential, expectedPresent]]) => {
      const observed = credentialBySink.get(sink);
      return (
        !observed ||
        observed.authorized_credential !== credential ||
        observed.expected_present !== expectedPresent ||
        observed.observed_present !== expectedPresent ||
        observed.forbidden_credentials_absent !== true
      );
    }) ||
    cleanup.terminal_resources.host_restricted_evidence.approved_destination_kind !==
      'maintainer-approved-host-restricted-location'
  ) {
    reject('credential-destination');
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
      comment_url: continuation.sticky_comment_url,
      marker: continuation.sticky_marker,
      reviewed_head_sha: identities.normal_head_sha,
    },
    cleanup: {
      complete: true,
      final_state_inventory_count: 0,
      deleted_state_record_count: cleanup.deleted_physical_artifact_ids.length,
      authorization_absent: true,
      operation_created_secrets_absent: true,
      environment_restored: true,
      fixture_branches_absent: true,
      fixture_prs_closed: true,
      all_runs_terminal: true,
      all_follow_on_runs_terminal: true,
    },
    canaries: {
      github: credentialBySink.get('github').forbidden_credentials_absent ? 'absent' : 'present',
      actions: credentialBySink.get('actions').forbidden_credentials_absent ? 'absent' : 'present',
      provider: credentialBySink.get('provider').forbidden_credentials_absent
        ? 'absent'
        : 'present',
      current_state: credentialBySink.get('current_state').forbidden_credentials_absent
        ? 'absent'
        : 'present',
      previous_state: credentialBySink.get('previous_state').forbidden_credentials_absent
        ? 'absent'
        : 'present',
      unrelated_credentials: credentialBySink.get('unrelated_credentials')
        .forbidden_credentials_absent
        ? 'absent'
        : 'present',
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
