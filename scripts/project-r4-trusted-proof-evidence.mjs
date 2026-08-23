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
const scopedStateFamilies = [
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

function stateEnvelopeDigest(envelope) {
  return sha256(Buffer.concat([Buffer.from('apr.state-envelope.r2\0', 'ascii'), envelope]));
}

function uint16LittleEndian(value) {
  const bytes = Buffer.allocUnsafe(2);
  bytes.writeUInt16LE(value);
  return bytes;
}

function uint32LittleEndian(value) {
  const bytes = Buffer.allocUnsafe(4);
  bytes.writeUInt32LE(value);
  return bytes;
}

function int64LittleEndian(value) {
  const bytes = Buffer.allocUnsafe(8);
  bytes.writeBigInt64LE(BigInt(value));
  return bytes;
}

function uint64LittleEndian(value) {
  const bytes = Buffer.allocUnsafe(8);
  bytes.writeBigUInt64LE(BigInt(value));
  return bytes;
}

function oneByte(value) {
  return Buffer.from([value]);
}

function lineageBytes(value) {
  const bytes = Buffer.from(value);
  return Buffer.concat([uint32LittleEndian(bytes.length), bytes]);
}

function lineageString(value) {
  return lineageBytes(Buffer.from(value, 'utf8'));
}

function optionalLineageString(value) {
  return value === null ? oneByte(0) : Buffer.concat([oneByte(1), lineageString(value)]);
}

function opaqueMetadataBytes(value) {
  return Buffer.concat([
    lineageString(value.name),
    lineageString(value.object_id),
    lineageString(value.producing_run_identity),
    int64LittleEndian(value.producing_run_attempt),
    lineageString(value.archive_sha256),
    lineageString(value.encrypted_object_sha256),
    int64LittleEndian(value.expires_at_unix_seconds),
    int64LittleEndian(value.size),
  ]);
}

function compareText(left, right) {
  return left < right ? -1 : left > right ? 1 : 0;
}

function compareCleanupTargets(left, right) {
  return compareText(left.name, right.name) || compareText(left.object_id, right.object_id);
}

function compareInventoryEntries(left, right) {
  return (
    compareText(left.name, right.name) ||
    compareText(left.object_id, right.object_id) ||
    compareText(left.producing_run_identity, right.producing_run_identity) ||
    left.producing_run_attempt - right.producing_run_attempt ||
    compareText(left.archive_sha256, right.archive_sha256) ||
    compareText(left.encrypted_object_sha256, right.encrypted_object_sha256) ||
    left.expires_at_unix_seconds - right.expires_at_unix_seconds ||
    left.size - right.size
  );
}

function inventoryDigest(targets) {
  return sha256(Buffer.concat([...targets].sort(compareInventoryEntries).map(opaqueMetadataBytes)));
}

function cleanupOperationIdentity(record) {
  const core = Buffer.concat([
    lineageString('APRSCU01'),
    uint16LittleEndian(1),
    lineageString(record.terminal_acceptance_identity),
    lineageString(record.base_scope_digest),
    lineageString(record.epoch),
    lineageString(record.session_id),
    lineageString(record.pre_cleanup_inventory_digest),
    uint16LittleEndian(record.targets.length),
    ...record.targets.map(opaqueMetadataBytes),
  ]);
  return sha256(Buffer.concat([lineageString('apr.state-cleanup.s6'), lineageBytes(core)]));
}

function publicationRecoveryHeader(kind, record) {
  return Buffer.concat([
    lineageString('APR5RC01'),
    uint16LittleEndian(1),
    uint16LittleEndian(kind),
    lineageString(record.reviewed_head_sha),
    lineageString(record.scope_sha256),
    lineageString(record.body_sha256),
  ]);
}

function publicationRecoveryIdentity(core) {
  return sha256(
    Buffer.concat([lineageString('apr.publication-recovery.p5/v1'), lineageBytes(core)]),
  );
}

function stickyReadbackFields(record, includeIdentity) {
  return Buffer.concat([
    uint16LittleEndian(record.publication_operation),
    int64LittleEndian(record.repository_id),
    int64LittleEndian(record.pull_request_number),
    int64LittleEndian(record.comment_id),
    lineageString(record.comment_url),
    int64LittleEndian(record.observed_at_unix_seconds),
    lineageString(record.attempt_intent_record_identity),
    ...(includeIdentity ? [lineageString(record.sticky_readback_record_identity)] : []),
  ]);
}

function publicationIntentIdentity(record) {
  switch (record.record_kind) {
    case 'initial_intent':
      return publicationRecoveryIdentity(
        Buffer.concat([
          publicationRecoveryHeader(1, record),
          int64LittleEndian(record.created_at_unix_seconds),
        ]),
      );
    case 'sticky_readback':
      return publicationRecoveryIdentity(
        Buffer.concat([publicationRecoveryHeader(2, record), stickyReadbackFields(record, false)]),
      );
    case 'acceptance_recovery':
      return publicationRecoveryIdentity(
        Buffer.concat([
          publicationRecoveryHeader(5, record),
          stickyReadbackFields(record, true),
          lineageBytes(Buffer.from(record.acceptance_recovery_handoff_base64, 'base64')),
          int64LittleEndian(record.minimum_semantic_expires_at_unix_seconds),
        ]),
      );
    default:
      return null;
  }
}

function publicationPayloadDigest(value) {
  return sha256(
    Buffer.concat([
      lineageString('APRVPP01'),
      uint16LittleEndian(1),
      lineageBytes(Buffer.from(value.finalized_comment, 'utf8')),
      int64LittleEndian(value.repository_id),
      lineageString(value.repository_name),
      int64LittleEndian(value.pull_request_number),
      lineageString(value.scope_sha256),
      lineageString(value.body_sha256),
      lineageString(value.reviewed_head_sha),
      lineageString(value.policy_identity_sha256),
      lineageString(value.payload_sha256),
      lineageString(value.build_discriminator),
      lineageString(value.rendering_version),
    ]),
  );
}

function publicationPayloadBytes(value) {
  return Buffer.concat([
    lineageString('APRVPP01'),
    uint16LittleEndian(1),
    lineageBytes(Buffer.from(value.finalized_comment, 'utf8')),
    int64LittleEndian(value.repository_id),
    lineageString(value.repository_name),
    int64LittleEndian(value.pull_request_number),
    lineageString(value.scope_sha256),
    lineageString(value.body_sha256),
    lineageString(value.reviewed_head_sha),
    lineageString(value.policy_identity_sha256),
    lineageString(value.payload_sha256),
    lineageString(value.build_discriminator),
    lineageString(value.rendering_version),
  ]);
}

function generationPayloadBytes(value) {
  return Buffer.concat([
    lineageString('APRSGR01'),
    uint16LittleEndian(1),
    lineageBytes(Buffer.from(value.encrypted_state_envelope_base64, 'base64')),
    lineageString(value.state_envelope_sha256),
    lineageString(value.session_sha256),
    lineageString(value.producer_base_sha),
    lineageString(value.producer_head_sha),
    int64LittleEndian(value.session_generation),
    optionalLineageString(value.predecessor_envelope_sha256),
    optionalLineageString(value.previous_logical_generation_identity),
    int64LittleEndian(value.prepared_at_unix_seconds),
    int64LittleEndian(value.prepared_expires_at_unix_seconds),
    lineageBytes(publicationPayloadBytes(value.publication_payload)),
    lineageString(value.publication_payload_sha256),
    lineageString(value.policy_identity_sha256),
    lineageString(value.config_sha256),
    lineageString(value.instructions_sha256),
    lineageString(value.payload_sha256),
    lineageString(value.build_discriminator),
  ]);
}

function physicalCopyPayloadBytes(value) {
  return Buffer.concat([
    lineageString('APRACP01'),
    uint16LittleEndian(1),
    lineageBytes(Buffer.from(value.canonical_generation_base64, 'base64')),
    lineageString(value.logical_generation_identity),
    lineageString(value.original_candidate_object_identity),
    lineageString(value.source_artifact_id),
    lineageString(value.source_archive_sha256),
    lineageString(value.source_encrypted_envelope_sha256),
  ]);
}

function acceptancePayloadBytes(value) {
  return Buffer.concat([
    lineageString('APRACR01'),
    uint16LittleEndian(1),
    lineageString(value.logical_generation_identity),
    lineageString(value.original_candidate_object_identity),
    optionalLineageString(value.previous_logical_generation_identity),
    optionalLineageString(value.previous_acceptance_receipt_identity),
    lineageString(value.reviewed_head_sha),
    oneByte(value.publication_operation),
    int64LittleEndian(value.repository_id),
    int64LittleEndian(value.pull_request_number),
    int64LittleEndian(value.comment_id),
    lineageString(value.comment_url),
    lineageString(value.scope_sha256),
    lineageString(value.body_sha256),
    lineageString(value.publication_payload_sha256),
    lineageString(value.producing_run_identity),
    int64LittleEndian(value.producing_run_attempt),
    int64LittleEndian(value.accepted_at_unix_seconds),
    int64LittleEndian(value.logical_expires_at_unix_seconds),
  ]);
}

function publicationIntentPayloadBytes(value) {
  const header = publicationRecoveryHeader(
    value.record_kind === 'initial_intent' ? 1 : value.record_kind === 'sticky_readback' ? 2 : 5,
    value,
  );
  if (value.record_kind === 'initial_intent') {
    return Buffer.concat([
      header,
      int64LittleEndian(value.created_at_unix_seconds),
      lineageString(value.record_identity),
    ]);
  }
  return Buffer.concat([
    header,
    stickyReadbackFields(value, value.record_kind === 'acceptance_recovery'),
    ...(value.record_kind === 'acceptance_recovery'
      ? [
          lineageBytes(Buffer.from(value.acceptance_recovery_handoff_base64, 'base64')),
          int64LittleEndian(value.minimum_semantic_expires_at_unix_seconds),
        ]
      : []),
    lineageString(value.record_identity),
  ]);
}

function lineageHeadPayloadBytes(value, identityOnly = false) {
  return Buffer.concat([
    lineageString('APRSLH01'),
    uint16LittleEndian(1),
    oneByte(0),
    uint64LittleEndian(value.ordinal),
    lineageString(value.reviewed_base_sha),
    lineageString(value.reviewed_head_sha),
    optionalLineageString(value.previous_epoch),
    optionalLineageString(value.previous_head_identity),
    optionalLineageString(value.transition_evidence_identity),
    optionalLineageString(value.reset_authority_run_identity),
    oneByte(value.reset_authority_run_attempt === null ? 0 : 1),
    ...(value.reset_authority_run_attempt === null
      ? []
      : [int64LittleEndian(value.reset_authority_run_attempt)]),
    oneByte(value.expiry_boundary === null ? 0 : 1),
    ...(value.expiry_boundary === null ? [] : [int64LittleEndian(value.expiry_boundary)]),
    ...(identityOnly
      ? []
      : [
          uint32LittleEndian(value.physical_predecessors.length),
          uint32LittleEndian(value.physical_superseded.length),
          uint32LittleEndian(value.superseded.length),
          uint32LittleEndian(value.completed_cleanup.length),
        ]),
  ]);
}

function anchorPayloadBytes(value) {
  return Buffer.concat([
    lineageString('APROWA01'),
    uint16LittleEndian(1),
    lineageString(value.candidate_object_identity),
    lineageString(value.operation_identity),
    lineageString(value.object_class),
    optionalLineageString(value.predecessor_identity),
    optionalLineageString(value.successor_identity),
    int64LittleEndian(value.semantic_required_expires_at_unix_seconds),
    int64LittleEndian(value.required_platform_expires_at_unix_seconds),
    lineageString(value.producing_run_identity),
    int64LittleEndian(value.producing_run_attempt),
    lineageString(value.target_name),
    lineageString(value.target_object_identity),
    lineageBytes(Buffer.from(value.target_envelope_base64, 'base64')),
    lineageString(value.target_envelope_sha256),
    uint16LittleEndian(value.dispatch_phase),
    lineageString(value.target_payload_sha256),
  ]);
}

function cleanupPayloadBytes(value) {
  return Buffer.concat([
    lineageString('APRSCU01'),
    uint16LittleEndian(1),
    lineageString(value.terminal_acceptance_identity),
    lineageString(value.base_scope_digest),
    lineageString(value.epoch),
    lineageString(value.session_id),
    lineageString(value.pre_cleanup_inventory_digest),
    uint16LittleEndian(value.targets.length),
    ...value.targets.map(opaqueMetadataBytes),
    lineageString(value.operation_identity),
  ]);
}

function canonicalPayload(record) {
  switch (record.object_class) {
    case 'lineage_head':
      return lineageHeadPayloadBytes(record.decoded_record);
    case 'candidate':
      return record.decoded_record.record_kind === 'accepted_state_physical_copy'
        ? physicalCopyPayloadBytes(record.decoded_record)
        : generationPayloadBytes(record.decoded_record);
    case 'publication_intent':
      return publicationIntentPayloadBytes(record.decoded_record);
    case 'acceptance':
      return acceptancePayloadBytes(record.decoded_record);
    case 'cleanup':
      return record.decoded_record.record_kind === 'opaque_write_anchor'
        ? anchorPayloadBytes(record.decoded_record)
        : cleanupPayloadBytes(record.decoded_record);
    default:
      return null;
  }
}

function semanticHeaderBytes(record) {
  return Buffer.concat([
    lineageString('APRSCH01'),
    uint16LittleEndian(1),
    lineageString(record.scope_digest),
    lineageString(record.epoch),
    lineageString(record.session_id),
    lineageString(record.object_class),
    optionalLineageString(record.predecessor_identity),
    optionalLineageString(record.successor_identity),
    ...(['lineage_head', 'reset', 'expiry_transition'].includes(record.object_class)
      ? []
      : [int64LittleEndian(record.logical_expires_at_unix_seconds)]),
  ]);
}

function canonicalObjectIdentity(record, payload) {
  const identityPayload =
    record.object_class === 'lineage_head'
      ? lineageHeadPayloadBytes(record.decoded_record, true)
      : payload;
  return sha256(
    Buffer.concat([
      lineageString('apr.object-identity.s4'),
      lineageBytes(semanticHeaderBytes(record)),
      lineageBytes(Buffer.from(sha256(identityPayload), 'hex')),
    ]),
  );
}

function logicalGenerationIdentity(record, previousAcceptanceIdentity) {
  const generation = generationPayloadBytes(record.decoded_record);
  return sha256(
    Buffer.concat([
      lineageString('apr.logical-generation.s5'),
      lineageBytes(generation),
      lineageString(record.scope_digest),
      lineageString(record.epoch),
      lineageString(record.session_id),
      optionalLineageString(previousAcceptanceIdentity),
    ]),
  );
}

function acceptanceRecoveryHandoff(name, envelope, predecessorCopy = null) {
  return Buffer.concat([
    lineageString('APRSAR01'),
    uint16LittleEndian(1),
    lineageString(name),
    lineageBytes(envelope),
    uint16LittleEndian(predecessorCopy === null ? 0 : 1),
    ...(predecessorCopy === null
      ? []
      : [
          lineageString(predecessorCopy.decoded_record.logical_generation_identity),
          int64LittleEndian(predecessorCopy.logical_expires_at_unix_seconds),
          int64LittleEndian(predecessorCopy.required_platform_expires_at_unix_seconds),
          lineageString(predecessorCopy.opaque_name),
          lineageBytes(Buffer.from(predecessorCopy.encrypted_envelope_base64, 'base64')),
        ]),
  ]);
}

function exactMetadata(target, record) {
  return (
    target.name === record.opaque_name &&
    target.object_id === record.physical_artifact_id &&
    target.producing_run_identity === record.producing_run_id &&
    target.producing_run_attempt === record.producing_run_attempt &&
    target.archive_sha256 === record.archive_sha256 &&
    target.encrypted_object_sha256 === record.encrypted_object_sha256 &&
    target.expires_at_unix_seconds === record.expires_at_unix_seconds &&
    target.size === record.size
  );
}

function metadataForRecord(record) {
  return {
    name: record.opaque_name,
    object_id: record.physical_artifact_id,
    producing_run_identity: record.producing_run_id,
    producing_run_attempt: record.producing_run_attempt,
    archive_sha256: record.archive_sha256,
    encrypted_object_sha256: record.encrypted_object_sha256,
    expires_at_unix_seconds: record.expires_at_unix_seconds,
    size: record.size,
  };
}

function isCanonicalArtifactId(value) {
  return (
    /^[1-9][0-9]{0,15}$/u.test(value) &&
    BigInt(value) <= 9_007_199_254_740_991n &&
    BigInt(value).toString() === value
  );
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
    scopedStateFamilies.every(
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
    environment_approver_id: environment.environment_approver_id,
    environment_approver_permission: environment.environment_approver_permission,
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

function exactProofComment(record, kind, run, coordinates, predecessorCommentId, release) {
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
    (release
      ? record.actor_permission === 'write' || record.actor_permission === 'admin'
      : record.actor_permission === null) &&
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

function exactAcceptanceReceipt(transition, predecessorIdentity, run) {
  const receipt = transition.acceptance_receipt;
  return (
    receipt.original_candidate_object_identity === transition.candidate_object_identity &&
    receipt.previous_acceptance_receipt_identity === predecessorIdentity &&
    receipt.reviewed_head_sha === transition.accepted_head_sha &&
    receipt.comment_id === transition.sticky_comment_id &&
    receipt.comment_url === transition.sticky_comment_url &&
    receipt.body_sha256 === transition.sticky_body_sha256 &&
    receipt.producing_run_identity === run.run_id &&
    receipt.producing_run_attempt === run.run_attempt &&
    receipt.logical_expires_at_unix_seconds === receipt.accepted_at_unix_seconds + 604800
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

  const normalConcurrencyGroup = `agentic-pr-review-r4-${identities.repository_id}-pr-${fixture.normal_pr_number}`;
  if (
    runs.bootstrap.run_id === runs.continuation.run_id ||
    runs.bootstrap.workflow_sha !== identities.workflow_sha ||
    runs.continuation.workflow_sha !== identities.workflow_sha ||
    runs.bootstrap.reviewed_head_sha !== identities.normal_head_sha ||
    runs.continuation.reviewed_head_sha !== identities.normal_head_sha ||
    runs.bootstrap.pr_number !== fixture.normal_pr_number ||
    runs.continuation.pr_number !== fixture.normal_pr_number ||
    runs.bootstrap.concurrency_group !== normalConcurrencyGroup ||
    runs.continuation.concurrency_group !== normalConcurrencyGroup ||
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
      true,
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
      true,
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
    !exactAcceptanceReceipt(bootstrap, null, runs.bootstrap) ||
    !exactAcceptanceReceipt(
      continuation,
      bootstrap.acceptance_object_identity,
      runs.continuation,
    ) ||
    bootstrap.base_scope_digest !== continuation.base_scope_digest ||
    bootstrap.lineage_head_object_identity !== continuation.lineage_head_object_identity ||
    bootstrap.lineage_epoch !== continuation.lineage_epoch ||
    bootstrap.lineage_session_id !== continuation.lineage_session_id ||
    bootstrap.lineage_transition !== 'initial' ||
    continuation.lineage_transition !== 'initial' ||
    bootstrap.lineage_ordinal !== 0 ||
    continuation.lineage_ordinal !== 0 ||
    bootstrap.session_generation !== 0 ||
    continuation.session_generation !== 1 ||
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
  const staleConcurrencyGroup = `agentic-pr-review-r4-${identities.repository_id}-pr-${fixture.stale_pr_number}`;
  if (
    new Set([runs.bootstrap.run_id, runs.continuation.run_id, staleRun.run_id, followOn.run_id])
      .size !== 4 ||
    staleRun.workflow_sha !== identities.workflow_sha ||
    followOn.workflow_sha !== identities.workflow_sha ||
    staleRun.pr_number !== fixture.stale_pr_number ||
    followOn.pr_number !== fixture.stale_pr_number ||
    staleRun.concurrency_group !== staleConcurrencyGroup ||
    followOn.concurrency_group !== staleConcurrencyGroup ||
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
  const terminalIds = [
    ...cleanup.internally_reconciled_physical_artifact_ids,
    ...cleanup.e4_deleted_physical_artifact_ids,
    ...cleanup.self_deleted_cleanup_record_ids,
  ];
  if (
    createdIds.length !== 35 ||
    createdIds.length !== new Set(createdIds).size ||
    createdIds.some((value) => !isCanonicalArtifactId(value)) ||
    objectIdentities.length !== new Set(objectIdentities).size ||
    terminalIds.length !== new Set(terminalIds).size ||
    !exactSet(createdIds, terminalIds) ||
    !exactSet(
      cleanup.internally_reconciled_physical_artifact_ids,
      state.created
        .filter(
          ({ terminal_disposition }) => terminal_disposition === 'internally-reconciled-deleted',
        )
        .map(({ physical_artifact_id }) => physical_artifact_id),
    ) ||
    !exactSet(
      cleanup.e4_deleted_physical_artifact_ids,
      state.created
        .filter(({ terminal_disposition }) => terminal_disposition === 'e4-deleted')
        .map(({ physical_artifact_id }) => physical_artifact_id),
    ) ||
    !exactSet(
      cleanup.self_deleted_cleanup_record_ids,
      state.created
        .filter(({ terminal_disposition }) => terminal_disposition === 'cleanup-self-deleted')
        .map(({ physical_artifact_id }) => physical_artifact_id),
    ) ||
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
  const decodedMatchesClass = (record) => {
    const decoded = record.decoded_record;
    switch (record.object_class) {
      case 'locator_root':
        return Number.isInteger(decoded.generation) && decoded.root_sha256 !== undefined;
      case 'lineage_head':
        return decoded.transition !== undefined && decoded.ordinal !== undefined;
      case 'candidate':
        return decoded.record_kind === 'accepted_state_physical_copy'
          ? decoded.canonical_generation_base64 !== undefined &&
              decoded.source_artifact_id !== undefined
          : decoded.session_generation !== undefined &&
              stateEnvelopeDigest(
                Buffer.from(decoded.encrypted_state_envelope_base64, 'base64'),
              ) === decoded.state_envelope_sha256 &&
              publicationPayloadDigest(decoded.publication_payload) ===
                decoded.publication_payload_sha256;
      case 'publication_intent':
        return publicationIntentIdentity(decoded) === decoded.record_identity;
      case 'acceptance':
        return decoded.logical_generation_identity !== undefined;
      case 'publication_failure':
        return decoded.failed_at_unix_seconds !== undefined;
      case 'abandonment':
        return decoded.abandoned_at_unix_seconds !== undefined;
      case 'reset':
        return decoded.transition === 'reset';
      case 'expiry_transition':
        return decoded.transition === 'expiry';
      case 'cleanup':
        return decoded.record_kind === 'opaque_write_anchor'
          ? decoded.target_object_identity !== undefined && decoded.operation_identity !== undefined
          : decoded.terminal_acceptance_identity !== undefined &&
              decoded.operation_identity !== undefined;
      default:
        return false;
    }
  };
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
        record.terminal_at_unix_seconds < record.created_at_unix_seconds ||
        (record.terminal_disposition === 'cleanup-self-deleted' &&
          (record.object_class !== 'cleanup' ||
            record.decoded_record.record_kind === 'opaque_write_anchor')) ||
        (record.object_class === 'cleanup' &&
          record.decoded_record.record_kind !== 'opaque_write_anchor' &&
          record.terminal_disposition !== 'cleanup-self-deleted') ||
        (record.terminal_disposition === 'e4-deleted' &&
          record.terminal_phase !== 'e4-final-cleanup') ||
        (record.terminal_disposition === 'internally-reconciled-deleted' &&
          !['bootstrap-internal-cleanup', 'continuation-internal-cleanup'].includes(
            record.terminal_phase,
          )) ||
        record.producing_run_id !== owner.run_id ||
        record.producing_run_attempt !== owner.run_attempt ||
        record.archive_sha256 === record.encrypted_object_sha256 ||
        (record.object_class !== 'locator_root' &&
          (sha256(Buffer.from(record.encrypted_envelope_base64, 'base64')) !==
            record.encrypted_object_sha256 ||
            Buffer.from(record.encrypted_envelope_base64, 'base64').length !== record.size)) ||
        record.scope !== expectedScope ||
        record.scope_digest !== expectedDigest ||
        record.opaque_name !== expectedOpaqueName ||
        !decodedMatchesClass(record) ||
        (record.object_class !== 'locator_root' &&
          !(
            record.created_at_unix_seconds < record.logical_expires_at_unix_seconds &&
            record.logical_expires_at_unix_seconds <=
              record.required_platform_expires_at_unix_seconds &&
            record.required_platform_expires_at_unix_seconds <= record.expires_at_unix_seconds
          ))
      );
    })
  ) {
    reject('state-producer');
  }

  const scopedRecords = state.created.filter(({ object_class }) => object_class !== 'locator_root');
  const noncanonicalScoped = scopedRecords.find((record) => {
    const payload = canonicalPayload(record);
    return payload === null || canonicalObjectIdentity(record, payload) !== record.object_identity;
  });
  if (noncanonicalScoped) {
    reject('canonical-scoped-identity');
  }

  const anchors = scopedRecords.filter(
    ({ object_class, decoded_record }) =>
      object_class === 'cleanup' && decoded_record.record_kind === 'opaque_write_anchor',
  );
  const cleanupRecords = scopedRecords.filter(
    ({ object_class, decoded_record }) =>
      object_class === 'cleanup' && decoded_record.record_kind !== 'opaque_write_anchor',
  );
  const p5Records = scopedRecords.filter(
    ({ object_class }) => object_class === 'publication_intent',
  );
  const cleanupKinds = new Map(
    ['p5-anchor-cleanup', 'p5-record-cleanup', 's6-internal-cleanup', 's6-final-cleanup'].map(
      (kind) => [
        kind,
        cleanupRecords.filter(({ decoded_record }) => decoded_record.record_kind === kind),
      ],
    ),
  );
  const byId = new Map(state.created.map((record) => [record.physical_artifact_id, record]));
  const activeNormal = [];
  let inventoryValid = true;
  for (const record of [...state.created].sort((left, right) =>
    comparePositiveIds(left.physical_artifact_id, right.physical_artifact_id),
  )) {
    if (record.scope !== 'normal') continue;
    if (
      record.object_class === 'cleanup' &&
      record.decoded_record.record_kind !== 'opaque_write_anchor'
    ) {
      const inventoryIds = record.pre_cleanup_inventory_physical_artifact_ids;
      const decoded = record.decoded_record;
      const targets = decoded.targets.map(({ object_id }) => byId.get(object_id));
      if (
        !Array.isArray(inventoryIds) ||
        !exactSet(
          inventoryIds,
          activeNormal.map(({ physical_artifact_id }) => physical_artifact_id),
        ) ||
        decoded.pre_cleanup_inventory_digest !==
          inventoryDigest(activeNormal.map(metadataForRecord)) ||
        decoded.operation_identity !== cleanupOperationIdentity(decoded) ||
        decoded.targets.length !== targets.length ||
        targets.some(
          (target, index) =>
            !target ||
            !activeNormal.includes(target) ||
            !exactMetadata(decoded.targets[index], target),
        ) ||
        JSON.stringify(decoded.targets) !==
          JSON.stringify([...decoded.targets].sort(compareCleanupTargets)) ||
        record.predecessor_identity !== decoded.terminal_acceptance_identity ||
        record.terminal_disposition !== 'cleanup-self-deleted'
      ) {
        inventoryValid = false;
        break;
      }
      for (const target of targets) activeNormal.splice(activeNormal.indexOf(target), 1);
    } else {
      activeNormal.push(record);
    }
  }

  const exactOpaqueOperationIdentity = (anchor, target) =>
    sha256(
      Buffer.concat([
        lineageString('apr.retained-opaque-operation.s6'),
        lineageString('publication_intent'),
        lineageBytes(canonicalPayload(target)),
        lineageString(anchor.predecessor_identity),
        lineageString(anchor.successor_identity ?? ''),
        int64LittleEndian(anchor.semantic_required_expires_at_unix_seconds),
      ]),
    );
  const anchorTargets = anchors.map((anchor) =>
    p5Records.find(
      (target) =>
        target.object_identity === anchor.decoded_record.target_object_identity &&
        target.opaque_name === anchor.decoded_record.target_name,
    ),
  );
  const exactAnchor = (record, target) => {
    if (!target) return false;
    const decoded = record.decoded_record;
    const targetEnvelope = Buffer.from(target.encrypted_envelope_base64, 'base64');
    return (
      record.predecessor_identity === decoded.candidate_object_identity &&
      record.successor_identity === target.object_identity &&
      decoded.operation_identity === exactOpaqueOperationIdentity(decoded, target) &&
      decoded.object_class === target.object_class &&
      decoded.predecessor_identity === target.predecessor_identity &&
      decoded.successor_identity === target.successor_identity &&
      decoded.semantic_required_expires_at_unix_seconds ===
        target.logical_expires_at_unix_seconds &&
      decoded.required_platform_expires_at_unix_seconds ===
        target.required_platform_expires_at_unix_seconds &&
      decoded.producing_run_identity === target.producing_run_id &&
      decoded.producing_run_attempt === target.producing_run_attempt &&
      Buffer.from(decoded.target_envelope_base64, 'base64').equals(targetEnvelope) &&
      decoded.target_envelope_sha256 === sha256(targetEnvelope) &&
      decoded.target_payload_sha256 === sha256(canonicalPayload(target)) &&
      record.terminal_disposition === 'internally-reconciled-deleted'
    );
  };
  const cleanupTargetsExact = (records, expectedTargets) =>
    records.length === expectedTargets.length &&
    expectedTargets.every(
      (target) =>
        records.filter(
          ({ decoded_record }) =>
            decoded_record.targets.length === 1 &&
            decoded_record.targets[0].object_id === target.physical_artifact_id,
        ).length === 1,
    );
  const bootstrapAcceptanceRecord = scopedRecords.find(
    ({ creation_phase, object_class }) =>
      creation_phase === 'bootstrap' && object_class === 'acceptance',
  );
  const continuationAcceptanceRecord = scopedRecords.find(
    ({ creation_phase, object_class }) =>
      creation_phase === 'continuation' && object_class === 'acceptance',
  );
  const bootstrapCandidateRecord = scopedRecords.find(
    ({ creation_phase, object_class }) =>
      creation_phase === 'bootstrap' && object_class === 'candidate',
  );
  const continuationCandidateRecord = scopedRecords.find(
    ({ creation_phase, object_class, decoded_record }) =>
      creation_phase === 'continuation' &&
      object_class === 'candidate' &&
      decoded_record.record_kind !== 'accepted_state_physical_copy',
  );
  const predecessorCopyRecord = scopedRecords.find(
    ({ creation_phase, object_class, decoded_record }) =>
      creation_phase === 'continuation' &&
      object_class === 'candidate' &&
      decoded_record.record_kind === 'accepted_state_physical_copy',
  );
  const normalHeadRecord = scopedRecords.find(
    ({ scope, object_class }) => scope === 'normal' && object_class === 'lineage_head',
  );
  const exactRecoveryHandoff = (phase, acceptance, predecessorCopy = null) => {
    const recovery = p5Records.find(
      ({ creation_phase, decoded_record }) =>
        creation_phase === phase && decoded_record.record_kind === 'acceptance_recovery',
    );
    return (
      recovery &&
      Buffer.from(recovery.decoded_record.acceptance_recovery_handoff_base64, 'base64').equals(
        acceptanceRecoveryHandoff(
          acceptance.opaque_name,
          Buffer.from(acceptance.encrypted_envelope_base64, 'base64'),
          predecessorCopy,
        ),
      )
    );
  };
  const internalCleanups = cleanupKinds.get('s6-internal-cleanup');
  const internalCleanupFor = (phase) =>
    internalCleanups.find(({ creation_phase }) => creation_phase === phase);
  const bootstrapInternalCleanup = internalCleanupFor('bootstrap');
  const continuationInternalCleanup = internalCleanupFor('continuation');
  const finalCleanup = cleanupKinds.get('s6-final-cleanup')[0];
  if (
    !inventoryValid ||
    activeNormal.length !== 0 ||
    anchors.length !== 6 ||
    p5Records.length !== 6 ||
    cleanupKinds.get('p5-anchor-cleanup').length !== 6 ||
    cleanupKinds.get('p5-record-cleanup').length !== 6 ||
    internalCleanups.length !== 2 ||
    cleanupKinds.get('s6-final-cleanup').length !== 1 ||
    anchorTargets.some((target, index) => !exactAnchor(anchors[index], target)) ||
    new Set(anchorTargets).size !== 6 ||
    !cleanupTargetsExact(cleanupKinds.get('p5-anchor-cleanup'), anchors) ||
    !cleanupTargetsExact(cleanupKinds.get('p5-record-cleanup'), p5Records) ||
    !bootstrapAcceptanceRecord ||
    !continuationAcceptanceRecord ||
    !bootstrapCandidateRecord ||
    !continuationCandidateRecord ||
    !predecessorCopyRecord ||
    !normalHeadRecord ||
    !bootstrapInternalCleanup ||
    !continuationInternalCleanup ||
    bootstrapInternalCleanup.decoded_record.targets.length !== 0 ||
    !exactSet(
      continuationInternalCleanup.decoded_record.targets.map(({ object_id }) => object_id),
      [predecessorCopyRecord.physical_artifact_id],
    ) ||
    bootstrapInternalCleanup.decoded_record.terminal_acceptance_identity !==
      bootstrapAcceptanceRecord.object_identity ||
    continuationInternalCleanup.decoded_record.terminal_acceptance_identity !==
      continuationAcceptanceRecord.object_identity ||
    predecessorCopyRecord.predecessor_identity !== bootstrapCandidateRecord.predecessor_identity ||
    predecessorCopyRecord.decoded_record.logical_generation_identity !==
      bootstrapCandidateRecord.decoded_record.logical_generation_identity ||
    predecessorCopyRecord.decoded_record.original_candidate_object_identity !==
      bootstrapCandidateRecord.object_identity ||
    predecessorCopyRecord.decoded_record.source_artifact_id !==
      bootstrapCandidateRecord.physical_artifact_id ||
    predecessorCopyRecord.decoded_record.source_archive_sha256 !==
      bootstrapCandidateRecord.archive_sha256 ||
    predecessorCopyRecord.decoded_record.source_encrypted_envelope_sha256 !==
      bootstrapCandidateRecord.encrypted_object_sha256 ||
    !Buffer.from(predecessorCopyRecord.decoded_record.canonical_generation_base64, 'base64').equals(
      canonicalPayload(bootstrapCandidateRecord),
    ) ||
    !exactSet(
      finalCleanup.decoded_record.targets.map(({ object_id }) => object_id),
      [
        normalHeadRecord.physical_artifact_id,
        bootstrapCandidateRecord.physical_artifact_id,
        bootstrapAcceptanceRecord.physical_artifact_id,
        continuationCandidateRecord.physical_artifact_id,
        continuationAcceptanceRecord.physical_artifact_id,
      ],
    ) ||
    p5Records.some(
      ({ decoded_record, object_identity }) => decoded_record.record_identity === object_identity,
    ) ||
    logicalGenerationIdentity(bootstrapCandidateRecord, null) !==
      bootstrapCandidateRecord.decoded_record.logical_generation_identity ||
    logicalGenerationIdentity(
      continuationCandidateRecord,
      bootstrapAcceptanceRecord.object_identity,
    ) !== continuationCandidateRecord.decoded_record.logical_generation_identity ||
    !exactRecoveryHandoff('bootstrap', bootstrapAcceptanceRecord) ||
    !exactRecoveryHandoff('continuation', continuationAcceptanceRecord, predecessorCopyRecord)
  ) {
    reject('complete-physical-lifecycle');
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
    ({ object_class, creation_phase, scope, decoded_record }) =>
      object_class === 'publication_intent' &&
      creation_phase === 'bootstrap' &&
      scope === 'normal' &&
      decoded_record.record_kind === 'initial_intent',
  );
  const bootstrapReadback = state.created.find(
    ({ object_class, creation_phase, scope, decoded_record }) =>
      object_class === 'publication_intent' &&
      creation_phase === 'bootstrap' &&
      scope === 'normal' &&
      decoded_record.record_kind === 'sticky_readback',
  );
  const bootstrapRecovery = state.created.find(
    ({ object_class, creation_phase, scope, decoded_record }) =>
      object_class === 'publication_intent' &&
      creation_phase === 'bootstrap' &&
      scope === 'normal' &&
      decoded_record.record_kind === 'acceptance_recovery',
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
    ({ object_class, creation_phase, scope, decoded_record }) =>
      object_class === 'publication_intent' &&
      creation_phase === 'continuation' &&
      scope === 'normal' &&
      decoded_record.record_kind === 'initial_intent',
  );
  const continuationReadback = state.created.find(
    ({ object_class, creation_phase, scope, decoded_record }) =>
      object_class === 'publication_intent' &&
      creation_phase === 'continuation' &&
      scope === 'normal' &&
      decoded_record.record_kind === 'sticky_readback',
  );
  const continuationRecovery = state.created.find(
    ({ object_class, creation_phase, scope, decoded_record }) =>
      object_class === 'publication_intent' &&
      creation_phase === 'continuation' &&
      scope === 'normal' &&
      decoded_record.record_kind === 'acceptance_recovery',
  );
  const currentAcceptance = findArtifact(
    state.created,
    'acceptance',
    continuation.acceptance_object_identity,
  );
  const normalCleanup = state.created.find(
    ({ object_class, scope, decoded_record }) =>
      object_class === 'cleanup' &&
      scope === 'normal' &&
      decoded_record.record_kind === 's6-final-cleanup',
  );
  const staleHead = state.created.find(
    ({ object_class, scope }) => object_class === 'lineage_head' && scope === 'stale',
  );
  const bootstrapReceipt = bootstrap.acceptance_receipt;
  const continuationReceipt = continuation.acceptance_receipt;
  const normalP5 = state.created.filter(
    ({ object_class, scope }) => object_class === 'publication_intent' && scope === 'normal',
  );
  const exactSuccessfulP5Set = ['bootstrap', 'continuation'].every((phase) => {
    const records = normalP5.filter(({ creation_phase }) => creation_phase === phase);
    return (
      records.length === 3 &&
      exactSet(
        records.map(({ decoded_record }) => decoded_record.record_kind),
        ['initial_intent', 'sticky_readback', 'acceptance_recovery'],
      )
    );
  });
  const expectedCleanupTargets = state.created
    .filter(
      ({ scope, object_class, terminal_disposition }) =>
        scope === 'normal' && object_class !== 'cleanup' && terminal_disposition === 'e4-deleted',
    )
    .map(({ physical_artifact_id }) => physical_artifact_id);
  if (
    !exactSuccessfulP5Set ||
    normalHeads.length !== 1 ||
    !normalHead ||
    normalHead.object_identity !== bootstrap.lineage_head_object_identity ||
    normalHead.object_identity !== continuation.lineage_head_object_identity ||
    normalHead.predecessor_identity !== null ||
    normalHead.successor_identity !== null ||
    normalHead.epoch !== bootstrap.lineage_epoch ||
    normalHead.session_id !== bootstrap.lineage_session_id ||
    normalHead.decoded_record.transition !== 'initial' ||
    normalHead.decoded_record.ordinal !== 0 ||
    normalHead.decoded_record.reviewed_base_sha !== identities.reviewed_base_sha ||
    normalHead.decoded_record.reviewed_head_sha !== identities.normal_head_sha ||
    normalHead.decoded_record.previous_epoch !== null ||
    normalHead.decoded_record.previous_head_identity !== null ||
    normalHead.decoded_record.transition_evidence_identity !== null ||
    normalHead.decoded_record.expiry_boundary !== null ||
    normalHead.decoded_record.physical_predecessors.length !== 0 ||
    normalHead.decoded_record.physical_superseded.length !== 0 ||
    normalHead.decoded_record.superseded.length !== 0 ||
    normalHead.decoded_record.completed_cleanup.length !== 0 ||
    normalHead.decoded_record.reset_authority_run_identity !== null ||
    normalHead.decoded_record.reset_authority_run_attempt !== null ||
    !bootstrapCandidate ||
    !bootstrapIntent ||
    !bootstrapReadback ||
    !bootstrapRecovery ||
    !predecessorAcceptance ||
    !continuationCandidate ||
    !continuationIntent ||
    !continuationReadback ||
    !continuationRecovery ||
    !currentAcceptance ||
    !normalCleanup ||
    bootstrapCandidate.predecessor_identity !== null ||
    bootstrapIntent.predecessor_identity !== bootstrapCandidate.object_identity ||
    bootstrapReadback.predecessor_identity !== bootstrapCandidate.object_identity ||
    bootstrapRecovery.predecessor_identity !== bootstrapCandidate.object_identity ||
    predecessorAcceptance.predecessor_identity !== null ||
    continuationCandidate.predecessor_identity !== predecessorAcceptance.object_identity ||
    continuationIntent.predecessor_identity !== continuationCandidate.object_identity ||
    continuationReadback.predecessor_identity !== continuationCandidate.object_identity ||
    continuationRecovery.predecessor_identity !== continuationCandidate.object_identity ||
    currentAcceptance.predecessor_identity !== predecessorAcceptance.object_identity ||
    normalCleanup.predecessor_identity !== currentAcceptance.object_identity
  ) {
    reject('lineage-binding');
  }

  const exactCandidatePublication = (candidate, transition, receipt) => {
    const decoded = candidate.decoded_record;
    const publication = decoded.publication_payload;
    return (
      publication.finalized_comment === `${transition.summary}\n\n${transition.sticky_marker}` &&
      publication.repository_id === identities.repository_id &&
      publication.repository_name === identities.repository &&
      publication.pull_request_number === fixture.normal_pr_number &&
      publication.scope_sha256 === receipt.scope_sha256 &&
      publication.body_sha256 === transition.sticky_body_sha256 &&
      publication.reviewed_head_sha === identities.normal_head_sha &&
      publication.policy_identity_sha256 === identities.policy_identity_sha256 &&
      publication.payload_sha256 === identities.payload_sha256 &&
      publication.build_discriminator === identities.build_discriminator &&
      publication.rendering_version === 'r4-sticky-v1' &&
      publicationPayloadDigest(publication) === decoded.publication_payload_sha256 &&
      decoded.publication_payload_sha256 === receipt.publication_payload_sha256 &&
      decoded.policy_identity_sha256 === identities.policy_identity_sha256 &&
      decoded.config_sha256 === identities.config_sha256 &&
      decoded.instructions_sha256 === identities.instructions_sha256 &&
      decoded.payload_sha256 === identities.payload_sha256 &&
      decoded.build_discriminator === identities.build_discriminator &&
      decoded.build_discriminator === 'r4-w2' &&
      decoded.producer_base_sha === identities.reviewed_base_sha &&
      decoded.producer_head_sha === identities.normal_head_sha &&
      decoded.prepared_expires_at_unix_seconds === decoded.prepared_at_unix_seconds + 604800 &&
      candidate.created_at_unix_seconds === decoded.prepared_at_unix_seconds &&
      candidate.logical_expires_at_unix_seconds === decoded.prepared_expires_at_unix_seconds
    );
  };
  const exactSuccessfulRecoveryChain = (
    candidate,
    intent,
    readback,
    recovery,
    acceptance,
    transition,
    receipt,
  ) => {
    const initial = intent.decoded_record;
    const sticky = readback.decoded_record;
    const durable = recovery.decoded_record;
    const samePublication = (record) =>
      record.reviewed_head_sha === identities.normal_head_sha &&
      record.scope_sha256 === receipt.scope_sha256 &&
      record.body_sha256 === receipt.body_sha256;
    return (
      samePublication(initial) &&
      samePublication(sticky) &&
      samePublication(durable) &&
      sticky.attempt_intent_record_identity === initial.record_identity &&
      durable.attempt_intent_record_identity === initial.record_identity &&
      durable.sticky_readback_record_identity === sticky.record_identity &&
      sticky.publication_operation === receipt.publication_operation &&
      durable.publication_operation === receipt.publication_operation &&
      sticky.repository_id === identities.repository_id &&
      durable.repository_id === identities.repository_id &&
      sticky.pull_request_number === fixture.normal_pr_number &&
      durable.pull_request_number === fixture.normal_pr_number &&
      sticky.comment_id === transition.sticky_comment_id &&
      durable.comment_id === transition.sticky_comment_id &&
      sticky.comment_url === transition.sticky_comment_url &&
      durable.comment_url === transition.sticky_comment_url &&
      sticky.observed_at_unix_seconds === durable.observed_at_unix_seconds &&
      initial.created_at_unix_seconds < sticky.observed_at_unix_seconds &&
      sticky.observed_at_unix_seconds <= recovery.created_at_unix_seconds &&
      recovery.created_at_unix_seconds < receipt.accepted_at_unix_seconds &&
      durable.minimum_semantic_expires_at_unix_seconds ===
        receipt.logical_expires_at_unix_seconds &&
      intent.created_at_unix_seconds === initial.created_at_unix_seconds &&
      readback.created_at_unix_seconds === sticky.observed_at_unix_seconds &&
      intent.logical_expires_at_unix_seconds ===
        candidate.decoded_record.prepared_expires_at_unix_seconds + 900 &&
      readback.logical_expires_at_unix_seconds === intent.logical_expires_at_unix_seconds &&
      recovery.logical_expires_at_unix_seconds === receipt.logical_expires_at_unix_seconds &&
      acceptance.created_at_unix_seconds === receipt.accepted_at_unix_seconds &&
      acceptance.logical_expires_at_unix_seconds === receipt.logical_expires_at_unix_seconds &&
      intent.terminal_disposition === 'internally-reconciled-deleted' &&
      readback.terminal_disposition === 'internally-reconciled-deleted' &&
      recovery.terminal_disposition === 'internally-reconciled-deleted'
    );
  };

  if (
    !exactCandidatePublication(bootstrapCandidate, bootstrap, bootstrapReceipt) ||
    !exactCandidatePublication(continuationCandidate, continuation, continuationReceipt) ||
    !exactSuccessfulRecoveryChain(
      bootstrapCandidate,
      bootstrapIntent,
      bootstrapReadback,
      bootstrapRecovery,
      predecessorAcceptance,
      bootstrap,
      bootstrapReceipt,
    ) ||
    !exactSuccessfulRecoveryChain(
      continuationCandidate,
      continuationIntent,
      continuationReadback,
      continuationRecovery,
      currentAcceptance,
      continuation,
      continuationReceipt,
    ) ||
    bootstrapCandidate.terminal_disposition !== 'e4-deleted' ||
    predecessorAcceptance.terminal_disposition !== 'e4-deleted' ||
    continuationCandidate.terminal_disposition !== 'e4-deleted' ||
    currentAcceptance.terminal_disposition !== 'e4-deleted' ||
    bootstrapCandidate.decoded_record.session_generation !== 0 ||
    bootstrapCandidate.decoded_record.previous_logical_generation_identity !== null ||
    bootstrapCandidate.decoded_record.predecessor_envelope_sha256 !== null ||
    bootstrapCandidate.decoded_record.producer_base_sha !== identities.reviewed_base_sha ||
    bootstrapCandidate.decoded_record.producer_head_sha !== identities.normal_head_sha ||
    bootstrapCandidate.decoded_record.payload_sha256 !== identities.payload_sha256 ||
    bootstrapCandidate.decoded_record.prepared_at_unix_seconds >=
      bootstrapCandidate.decoded_record.prepared_expires_at_unix_seconds ||
    continuationCandidate.decoded_record.session_generation !== 1 ||
    continuationCandidate.decoded_record.previous_logical_generation_identity !==
      bootstrapCandidate.decoded_record.logical_generation_identity ||
    continuationCandidate.decoded_record.predecessor_envelope_sha256 !==
      bootstrapCandidate.decoded_record.state_envelope_sha256 ||
    continuationCandidate.decoded_record.session_sha256 ===
      bootstrapCandidate.decoded_record.session_sha256 ||
    continuationCandidate.decoded_record.producer_base_sha !== identities.reviewed_base_sha ||
    continuationCandidate.decoded_record.producer_head_sha !== identities.normal_head_sha ||
    continuationCandidate.decoded_record.payload_sha256 !== identities.payload_sha256 ||
    continuationCandidate.decoded_record.prepared_at_unix_seconds >=
      continuationCandidate.decoded_record.prepared_expires_at_unix_seconds ||
    bootstrapReceipt.logical_generation_identity !==
      bootstrapCandidate.decoded_record.logical_generation_identity ||
    continuationReceipt.logical_generation_identity !==
      continuationCandidate.decoded_record.logical_generation_identity ||
    bootstrapReceipt.previous_logical_generation_identity !== null ||
    continuationReceipt.previous_logical_generation_identity !==
      bootstrapReceipt.logical_generation_identity ||
    bootstrapReceipt.publication_operation !== 1 ||
    continuationReceipt.publication_operation !== 2 ||
    bootstrapReceipt.producing_run_identity !== runs.bootstrap.run_id ||
    continuationReceipt.producing_run_identity !== runs.continuation.run_id ||
    bootstrapReceipt.producing_run_attempt !== runs.bootstrap.run_attempt ||
    continuationReceipt.producing_run_attempt !== runs.continuation.run_attempt ||
    bootstrapReceipt.repository_id !== identities.repository_id ||
    continuationReceipt.repository_id !== identities.repository_id ||
    bootstrapReceipt.pull_request_number !== fixture.normal_pr_number ||
    continuationReceipt.pull_request_number !== fixture.normal_pr_number ||
    bootstrapIntent.decoded_record.reviewed_head_sha !== identities.normal_head_sha ||
    continuationIntent.decoded_record.reviewed_head_sha !== identities.normal_head_sha ||
    bootstrapIntent.decoded_record.scope_sha256 !== scopes.repository_root.scope_digest ||
    continuationIntent.decoded_record.scope_sha256 !== scopes.repository_root.scope_digest ||
    JSON.stringify(predecessorAcceptance.decoded_record) !== JSON.stringify(bootstrapReceipt) ||
    JSON.stringify(currentAcceptance.decoded_record) !== JSON.stringify(continuationReceipt) ||
    normalCleanup.decoded_record.terminal_acceptance_identity !==
      continuation.acceptance_object_identity ||
    normalCleanup.decoded_record.base_scope_digest !== scopes.normal.base_scope_digest ||
    normalCleanup.decoded_record.epoch !== bootstrap.lineage_epoch ||
    normalCleanup.decoded_record.session_id !== bootstrap.lineage_session_id ||
    normalCleanup.decoded_record.operation_identity === fixture.normal_operation_id ||
    normalCleanup.decoded_record.pre_cleanup_inventory_digest !==
      inventoryDigest(normalCleanup.decoded_record.targets) ||
    normalCleanup.decoded_record.operation_identity !==
      cleanupOperationIdentity(normalCleanup.decoded_record) ||
    JSON.stringify(normalCleanup.decoded_record.targets) !==
      JSON.stringify([...normalCleanup.decoded_record.targets].sort(compareCleanupTargets)) ||
    !exactSet(
      normalCleanup.decoded_record.targets.map(({ object_id }) => object_id),
      expectedCleanupTargets,
    ) ||
    normalCleanup.decoded_record.targets.some((target) => {
      const record = state.created.find(
        ({ physical_artifact_id }) => physical_artifact_id === target.object_id,
      );
      return (
        !record ||
        target.name !== record.opaque_name ||
        target.producing_run_identity !== record.producing_run_id ||
        target.producing_run_attempt !== record.producing_run_attempt ||
        target.archive_sha256 !== record.archive_sha256 ||
        target.encrypted_object_sha256 !== record.encrypted_object_sha256 ||
        target.expires_at_unix_seconds !== record.expires_at_unix_seconds ||
        target.size !== record.size
      );
    }) ||
    !staleHead ||
    staleHead.decoded_record.transition !== 'initial' ||
    staleHead.decoded_record.ordinal !== 0 ||
    staleHead.decoded_record.reviewed_head_sha !== identities.stale_admitted_head_sha
  ) {
    reject('decoded-state-contract');
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
      deleted_state_record_count: terminalIds.length,
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
