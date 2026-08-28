import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import Ajv from 'ajv';
import { describe, expect, test } from 'vitest';
import { pinnedFileIdentity, verifyCapturedFiles } from './assemble-r4-trusted-proof-evidence.mjs';
import {
  assembleTrustedProofEvidence,
  assertPublicSafeEvidence,
  buildFinalizedPrivatePackageManifest,
  buildProtectedScanInput,
  canonicalJson,
  cleanupPhases,
  generateCleanupPlan,
  projectTrustedProofEvidence,
  projectFinalizedTrustedProofEvidence,
  protectedCanaryCategories,
  scanPublicCandidate,
  sha256,
  validateHostEvidence,
} from './r4-trusted-proof-contract.mjs';

const root = path.resolve(import.meta.dirname, '..');
const fixtureRoot = path.join(root, 'runtime', 'tests', 'fixtures', 'action-host', 'trusted-proof');
const host = JSON.parse(
  fs.readFileSync(path.join(fixtureRoot, 'templates', 'host-restricted-evidence.json'), 'utf8'),
);
const expectedPublic = JSON.parse(
  fs.readFileSync(path.join(fixtureRoot, 'templates', 'public-safe-evidence.json'), 'utf8'),
);

function copy<T>(value: T): T {
  return structuredClone(value);
}

function projectMutation(mutator: (candidate: any) => void) {
  const candidate = copy(host);
  mutator(candidate);
  return () => projectTrustedProofEvidence(candidate);
}

function syntheticAssembly(input = host) {
  const candidate = copy(input);
  const stickyBody = '<!-- apr-r4-e3-sticky {"result":"retained"} -->';
  candidate.cleanup.resources.sticky.body_sha256 = sha256(Buffer.from(stickyBody, 'utf8'));
  candidate.cleanup.resources.sticky.marker_sha256 = sha256(
    Buffer.from('{"result":"retained"}', 'utf8'),
  );
  const restoredEnvironment = {
    id: Number(candidate.environment.protection_snapshot.environment_id),
    name: candidate.environment.name,
    protection_rules: [
      {
        type: 'required_reviewers',
        required_approvals: candidate.environment.protection_snapshot.required_approvals,
        reviewers: candidate.environment.protection_snapshot.required_reviewer_ids.map(
          (id: string) => ({ type: 'User', reviewer: { id: Number(id) } }),
        ),
      },
    ],
    deployment_branch_policy: { protected_branches: false, custom_branch_policies: true },
  };
  candidate.cleanup.resources.environment_snapshot_sha256 = sha256(
    canonicalJson(restoredEnvironment),
  );
  const oracleBinaries = {
    oracle_assembly_path:
      'oracle-build/AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceOracle.dll',
    oracle_assembly_sha256: 'b'.repeat(64),
    production_assembly_path: 'oracle-build/AgenticPrReview.Runtime.dll',
    production_assembly_sha256: 'c'.repeat(64),
  };
  const oracleBuildReceipt = {
    kind: 'apr-r4-e3-independent-oracle-build-receipt-v2',
    source_commit: candidate.identities.oracle_source_sha,
    source_tree: candidate.identities.oracle_source_tree,
    ...oracleBinaries,
    result: 'passed',
  };
  candidate.authorizations.execution.oracle_build = {
    source_commit: oracleBuildReceipt.source_commit,
    source_tree: oracleBuildReceipt.source_tree,
    build_receipt_sha256: sha256(canonicalJson(oracleBuildReceipt)),
    oracle_assembly_sha256: oracleBinaries.oracle_assembly_sha256,
    production_assembly_sha256: oracleBinaries.production_assembly_sha256,
  };
  const payloadReceipt = JSON.parse(
    fs.readFileSync(path.join(fixtureRoot, 'trusted-proof-payload-receipt-v2.json'), 'utf8'),
  );
  const payloadReceiptSha256 = sha256(canonicalJson(payloadReceipt));
  candidate.authorizations.execution.protected_scan_input.sha256 = sha256(
    canonicalJson(
      buildProtectedScanInput(candidate.authorizations, candidate.identities.repository),
    ),
  );
  const roleById = new Map(
    candidate.inventories.expected_success.map((record: any) => [record.artifact_id, record.role]),
  );
  for (const record of candidate.inventories.observed_cleanup) {
    record.ownership_evidence_sha256 = sha256(
      canonicalJson({
        artifact_id: record.artifact_id,
        artifact_name: record.artifact_name,
        scope: record.scope,
        object_class: record.object_class,
        operation_id: record.operation_id,
        producing_run_id: record.producing_run_id,
        producing_run_attempt: String(record.producing_run_attempt),
        archive_sha256: record.archive_sha256,
        encrypted_object_sha256: record.encrypted_object_sha256,
        encrypted_object_size: record.encrypted_object_size,
      }),
    );
  }
  const generatedCleanup = generateCleanupPlan({
    operation_ids: candidate.identities.operation_ids,
    proof_control: candidate.proof_control,
    observed_cleanup: candidate.inventories.observed_cleanup,
    resources: candidate.cleanup.resources,
  });
  candidate.cleanup.plan_sha256 = generatedCleanup.digest;
  candidate.authorizations.cleanup.plan_sha256 = generatedCleanup.digest;
  const metadataRunIds = [
    ...new Set(
      candidate.inventories.observed_cleanup.map((record: any) => record.producing_run_id),
    ),
  ];
  const sourceDigests = new Map<string, string>();
  const sourceRoutes = new Map<string, string>();
  const sourceObservations = new Map<
    string,
    { request_started: number; response_received: number }
  >();
  const capturedSourceBodies = new Map<string, { text: string }>();
  const registerCapture = (
    sourceId: string,
    route: string,
    value: any,
    observation = { request_started: 1, response_received: 2 },
  ) => {
    const bytes = Buffer.from(canonicalJson(value), 'utf8');
    const digest = sha256(bytes);
    sourceDigests.set(sourceId, digest);
    sourceRoutes.set(sourceId, route);
    sourceObservations.set(sourceId, observation);
    capturedSourceBodies.set(sourceId, { text: bytes.toString('utf8') });
    return digest;
  };
  for (const phase of ['cleanup', 'execution', 'setup']) {
    const expected = candidate.authorizations[phase];
    const { source: oldSource, ...authorization } = expected;
    const marker = {
      contract: 'apr-r4-e3-maintainer-authorization-v1',
      phase,
      repository: candidate.identities.repository,
      issue_number: Number(oldSource.issue_number),
      authorization,
    };
    const body = `<!-- apr-r4-e3-authorization ${JSON.stringify(marker)} -->`;
    const commentSourceId = `authorization-${phase}-comment-${oldSource.comment_id}:page:1`;
    const permissionSourceId = `authorization-${phase}-permission-maintainer:page:1`;
    const commentDigest = registerCapture(
      commentSourceId,
      `/repos/${candidate.identities.repository}/issues/comments/${oldSource.comment_id}`,
      {
        id: Number(oldSource.comment_id),
        body,
        user: { id: Number(oldSource.author_id), login: 'maintainer' },
        created_at: '2026-08-25T00:00:00Z',
        updated_at: '2026-08-25T00:00:00Z',
      },
      oldSource.observation,
    );
    registerCapture(
      permissionSourceId,
      `/repos/${candidate.identities.repository}/collaborators/maintainer/permission`,
      {
        permission: oldSource.author_permission,
        user: { id: Number(oldSource.author_id), login: 'maintainer' },
      },
    );
    expected.source.capture_body_sha256 = commentDigest;
    expected.source.body_sha256 = sha256(Buffer.from(body, 'utf8'));
    expected.source.readback_sha256 = expected.source.body_sha256;
  }
  candidate.environment.protection_snapshot.readback_sha256 = registerCapture(
    'environment-protection:page:1',
    `/repos/${candidate.identities.repository}/environments/r4-trusted-proof`,
    restoredEnvironment,
    candidate.environment.protection_snapshot.observation,
  );
  const uiAttestation = {
    repository: candidate.environment.ui_attestation.repository,
    environment: candidate.environment.ui_attestation.environment,
    source_kind: candidate.environment.ui_attestation.source_kind,
    observation: candidate.environment.ui_attestation.observation,
    capture_sha256: candidate.environment.ui_attestation.capture_sha256,
    maintainer_id: candidate.environment.ui_attestation.maintainer_id,
    prevent_self_review: candidate.environment.prevent_self_review,
    administrator_bypass: candidate.environment.ui_attestation.administrator_bypass,
  };
  const uiAttestationSha256 = sha256(canonicalJson(uiAttestation));
  for (const [phase, transition] of Object.entries<any>(candidate.approval_transitions)) {
    const runId = transition.run_id;
    registerCapture(
      `transition-${phase}-pending-run-${runId}:page:1`,
      `/repos/${candidate.identities.repository}/actions/runs/${runId}/pending_deployments`,
      [
        {
          environment: {
            id: Number(transition.pending.environment_id),
            name: transition.pending.environment_name,
          },
          reviewers: transition.pending.reviewer_ids.map((id: string) => ({
            type: 'User',
            reviewer: { id: Number(id) },
          })),
        },
      ],
      transition.pending.observation,
    );
    registerCapture(
      `transition-${phase}-approvals-run-${runId}:page:1`,
      `/repos/${candidate.identities.repository}/actions/runs/${runId}/approvals`,
      [
        {
          state: 'approved',
          user: { id: Number(transition.approval.approving_user_id) },
          environments: [
            {
              id: Number(transition.approval.environment_id),
              name: transition.approval.environment_name,
            },
          ],
        },
      ],
      transition.approval.observation,
    );
    registerCapture(
      `transition-${phase}-jobs-run-${runId}:page:1`,
      `/repos/${candidate.identities.repository}/actions/runs/${runId}/attempts/1/jobs`,
      {
        total_count: 1,
        jobs: [
          {
            id: Number(`91${runId}`),
            run_id: Number(runId),
            run_attempt: 1,
            name: transition.protected_job.name,
            status: 'completed',
            conclusion: 'success',
            started_at: new Date(transition.protected_job.started.value).toISOString(),
          },
        ],
      },
      {
        request_started: transition.protected_job.started.value,
        response_received: transition.protected_job.started.value + 1,
      },
    );
  }
  for (const [scope, concurrency] of Object.entries<any>(candidate.concurrency)) {
    const holderRunId = concurrency.terminal.holder_run_id;
    registerCapture(
      `concurrency-${scope}-run-${holderRunId}:page:1`,
      `/repos/${candidate.identities.repository}/actions/runs/${holderRunId}/concurrency_group`,
      {
        group: concurrency.group,
        cancel_in_progress: false,
        ahead_of_run: concurrency.ahead_of_run.map((member: any) => ({
          run_id: Number(member.run_id),
          position: member.position,
          status: member.status,
        })),
      },
      concurrency.observation,
    );
    for (const [runId, kind] of [
      [concurrency.terminal.holder_run_id, 'holder'],
      [concurrency.terminal.waiter_run_id, 'waiter'],
    ]) {
      const terminal =
        kind === 'holder'
          ? {
              id: Number(runId),
              status: 'completed',
              conclusion: 'success',
              run_started_at: new Date(concurrency.observation.request_started).toISOString(),
              updated_at: new Date(concurrency.terminal.holder_completed.value).toISOString(),
            }
          : {
              id: Number(runId),
              status: 'completed',
              conclusion: scope === 'stale' ? 'failure' : 'success',
              event: scope === 'stale' ? 'workflow_run' : 'workflow_dispatch',
              head_sha:
                scope === 'stale'
                  ? candidate.identities.unauthorized_follow_on.advanced_head_sha
                  : candidate.authorizations.execution.fixture_prs[0].head_sha,
              run_started_at: new Date(concurrency.terminal.waiter_started.value).toISOString(),
              updated_at: new Date(concurrency.terminal.waiter_started.value + 1).toISOString(),
            };
      registerCapture(
        `run-terminal-${runId}:page:1`,
        `/repos/${candidate.identities.repository}/actions/runs/${runId}`,
        terminal,
      );
    }
  }
  for (const family of Object.values<any>(candidate.proof_control)) {
    for (const comment of family.comments) {
      const marker = JSON.parse(comment.body_preimage);
      const body = `<!-- apr-r4-e2p-control ${JSON.stringify({
        ...marker,
        body_sha256: comment.body_sha256,
      })} -->`;
      const readyActor = comment.kind === 'ready' || comment.kind === 'stale-ready';
      const sourceId = `proof-control-comment-${comment.comment_id}:page:1`;
      comment.capture_body_sha256 = registerCapture(
        sourceId,
        `/repos/${candidate.identities.repository}/issues/comments/${comment.comment_id}`,
        {
          id: Number(comment.comment_id),
          body,
          user: {
            id: Number(comment.actor_id),
            login: readyActor ? 'github-actions[bot]' : 'maintainer',
          },
          created_at: '2026-08-25T00:00:00Z',
          updated_at: '2026-08-25T00:00:00Z',
        },
        comment.observation,
      );
      if (!readyActor) {
        registerCapture(
          `proof-control-permission-${comment.comment_id}-maintainer:page:1`,
          `/repos/${candidate.identities.repository}/collaborators/maintainer/permission`,
          {
            permission: comment.actor_permission,
            user: { id: Number(comment.actor_id), login: 'maintainer' },
          },
          comment.observation,
        );
      }
    }
  }
  const evidenceSources = [...sourceDigests].map(([sourceId, bodySha256], index) => ({
    source_id: sourceId,
    route:
      sourceRoutes.get(sourceId) ??
      `/repos/SolusQuest/agentic-pr-review/evidence/${encodeURIComponent(sourceId)}`,
    page: 1,
    status: 200,
    body_path: `source-${String(metadataRunIds.length + index + 1).padStart(4, '0')}.json`,
    body_sha256: bodySha256,
    body_size: capturedSourceBodies.has(sourceId)
      ? String(Buffer.byteLength(capturedSourceBodies.get(sourceId)!.text, 'utf8'))
      : '3',
    body_file_identity: '8'.repeat(64),
    safe_headers_sha256: '2'.repeat(64),
    request_started_unix_milliseconds: sourceObservations.get(sourceId)?.request_started ?? 1,
    response_received_unix_milliseconds: sourceObservations.get(sourceId)?.response_received ?? 2,
    next_route: null,
  }));
  const captureManifest = {
    kind: 'apr-r4-e3-capture-manifest-v1',
    repository_id: candidate.identities.repository_id,
    repository: candidate.identities.repository,
    operation_ids: candidate.identities.operation_ids,
    operation_runs: candidate.authorizations.execution.operation_runs.map((run: any) => ({
      ...run,
      run_attempt: String(run.run_attempt),
    })),
    source_map_sha256: sha256(canonicalJson(candidate.source_map)),
    destination_identity_sha256: candidate.restricted_package.destination_identity_sha256,
    sources: [
      ...metadataRunIds.map((runId, index) => ({
        source_id: `artifacts-run-${runId}:page:1`,
        route: `/repos/SolusQuest/agentic-pr-review/actions/runs/${runId}/artifacts?per_page=100`,
        page: 1,
        status: 200,
        body_path: `source-${String(index + 1).padStart(4, '0')}.json`,
        body_sha256: '1'.repeat(64),
        body_size: '3',
        body_file_identity: '8'.repeat(64),
        safe_headers_sha256: '2'.repeat(64),
        request_started_unix_milliseconds: 1,
        response_received_unix_milliseconds: 2,
        next_route: null,
      })),
      ...evidenceSources,
    ],
    artifacts: candidate.inventories.observed_cleanup.map((record: any) => ({
      artifact_id: record.artifact_id,
      artifact_name: record.artifact_name,
      metadata_source_id: `artifacts-run-${record.producing_run_id}`,
      metadata_body_sha256: '1'.repeat(64),
      producing_run_id: record.producing_run_id,
      producing_run_attempt: '1',
      download_route: `/repos/SolusQuest/agentic-pr-review/actions/artifacts/${record.artifact_id}/zip`,
      download_safe_headers_sha256: '7'.repeat(64),
      download_request_started_unix_milliseconds: 3,
      download_response_received_unix_milliseconds: 4,
      archive_path: `artifact-${record.artifact_id}.zip`,
      archive_sha256: record.archive_sha256,
      archive_size: '100',
      archive_file_identity: '9'.repeat(64),
      encrypted_object_path: `artifact-${record.artifact_id}.bin`,
      encrypted_object_sha256: record.encrypted_object_sha256,
      encrypted_object_size: record.encrypted_object_size,
      encrypted_object_file_identity: 'a'.repeat(64),
    })),
    finalized: true,
  };
  const captureManifestSha256 = sha256(canonicalJson(captureManifest));
  const oracleResult = {
    kind: 'apr-r4-e3-production-codec-oracle-result-v1',
    capture_manifest_sha256: captureManifestSha256,
    oracle_source_sha: candidate.identities.oracle_source_sha,
    oracle_source_tree: candidate.identities.oracle_source_tree,
    oracle_assembly_sha256: 'b'.repeat(64),
    production_assembly_sha256: 'c'.repeat(64),
    exact_seven_success: !candidate.inventories.observed_cleanup.some(
      (record: any) => record.disposition === 'recovery-only-delete',
    ),
    recovery_only: candidate.inventories.observed_cleanup.some(
      (record: any) => record.disposition === 'recovery-only-delete',
    ),
    records: candidate.inventories.observed_cleanup.map((record: any) => {
      const ownershipEvidenceSha256 = sha256(
        canonicalJson({
          artifact_id: record.artifact_id,
          artifact_name: record.artifact_name,
          scope: record.scope,
          object_class: record.object_class,
          operation_id: record.operation_id,
          producing_run_id: record.producing_run_id,
          producing_run_attempt: '1',
          archive_sha256: record.archive_sha256,
          encrypted_object_sha256: record.encrypted_object_sha256,
          encrypted_object_size: record.encrypted_object_size,
        }),
      );
      return {
        artifact_id: record.artifact_id,
        role: roleById.get(record.artifact_id) ?? 'internal-record',
        scope: record.scope,
        base_scope_digest:
          record.scope === 'repository' ? '' : (record.scope === 'normal' ? 'd' : 'e').repeat(64),
        object_class: record.object_class,
        object_identity: '5'.repeat(64),
        producing_run_identity: record.producing_run_id,
        producing_run_attempt: '1',
        operation_id: record.operation_id,
        ownership_evidence_sha256: ownershipEvidenceSha256,
        payload_sha256: '6'.repeat(64),
      };
    }),
  };
  const oracleResultSha256 = sha256(canonicalJson(oracleResult));
  const postCleanupCapturedSourceBodies = new Map<string, { text: string }>();
  const postCleanupSources: any[] = [];
  let postObservation = 200;
  const registerPostCleanup = (sourceId: string, route: string, value: any) => {
    const text = canonicalJson(value);
    const observation = postObservation;
    postObservation += 6;
    postCleanupCapturedSourceBodies.set(`${sourceId}:page:1`, { text });
    postCleanupSources.push({
      source_id: `${sourceId}:page:1`,
      route,
      page: 1,
      status: 200,
      body_path: `post-cleanup-${postCleanupSources.length + 1}.json`,
      body_sha256: sha256(Buffer.from(text, 'utf8')),
      body_size: String(Buffer.byteLength(text, 'utf8')),
      body_file_identity: String(postCleanupSources.length + 1)
        .padStart(64, 'a')
        .slice(-64),
      safe_headers_sha256: 'd'.repeat(64),
      request_started_unix_milliseconds: observation,
      response_received_unix_milliseconds: observation + 1,
      next_route: null,
    });
  };
  registerPostCleanup(
    `post-cleanup-control-comments-normal-pr-${candidate.identities.normal_pr_number}`,
    `/repos/${candidate.identities.repository}/issues/${candidate.identities.normal_pr_number}/comments`,
    [{ id: Number(candidate.cleanup.resources.sticky.comment_id), body: stickyBody }],
  );
  registerPostCleanup(
    `post-cleanup-control-comments-stale-pr-${candidate.identities.stale_pr_number}`,
    `/repos/${candidate.identities.repository}/issues/${candidate.identities.stale_pr_number}/comments`,
    [],
  );
  for (const run of candidate.authorizations.execution.operation_runs) {
    registerPostCleanup(
      `post-cleanup-state-delete-run-${run.run_id}`,
      `/repos/${candidate.identities.repository}/actions/runs/${run.run_id}/artifacts`,
      { total_count: 0, artifacts: [] },
    );
  }
  for (const run of candidate.authorizations.execution.operation_runs) {
    registerPostCleanup(
      `post-cleanup-state-empty-run-${run.run_id}`,
      `/repos/${candidate.identities.repository}/actions/runs/${run.run_id}/artifacts`,
      { total_count: 0, artifacts: [] },
    );
  }
  registerPostCleanup(
    'post-cleanup-variables',
    `/repos/${candidate.identities.repository}/actions/variables`,
    { total_count: 0, variables: [] },
  );
  registerPostCleanup(
    'post-cleanup-secrets',
    `/repos/${candidate.identities.repository}/actions/secrets`,
    { total_count: 0, secrets: [] },
  );
  registerPostCleanup(
    'post-cleanup-environment',
    `/repos/${candidate.identities.repository}/environments/${candidate.environment.name}`,
    restoredEnvironment,
  );
  candidate.identities.operation_ids.forEach((operationId: string, index: number) =>
    registerPostCleanup(
      `post-cleanup-ref-${index === 0 ? 'normal' : 'stale'}`,
      `/repos/${candidate.identities.repository}/git/matching-refs/heads/r4-trusted-proof/${operationId}`,
      [],
    ),
  );
  candidate.authorizations.execution.fixture_prs.forEach((fixture: any, index: number) =>
    registerPostCleanup(
      `post-cleanup-pr-${index === 0 ? 'normal' : 'stale'}-${fixture.number}`,
      `/repos/${candidate.identities.repository}/pulls/${fixture.number}`,
      { number: Number(fixture.number), state: 'closed' },
    ),
  );
  registerPostCleanup(
    `post-cleanup-sticky-comments-normal-pr-${candidate.identities.normal_pr_number}`,
    `/repos/${candidate.identities.repository}/issues/${candidate.identities.normal_pr_number}/comments`,
    [{ id: Number(candidate.cleanup.resources.sticky.comment_id), body: stickyBody }],
  );
  registerPostCleanup(
    `post-cleanup-sticky-comments-stale-pr-${candidate.identities.stale_pr_number}`,
    `/repos/${candidate.identities.repository}/issues/${candidate.identities.stale_pr_number}/comments`,
    [],
  );
  for (const run of candidate.authorizations.execution.operation_runs) {
    registerPostCleanup(
      `post-cleanup-final-run-${run.run_id}`,
      `/repos/${candidate.identities.repository}/actions/runs/${run.run_id}`,
      { id: Number(run.run_id), status: 'completed', conclusion: 'success' },
    );
  }
  const postCleanupCaptureManifest = {
    kind: 'apr-r4-e3-capture-manifest-v1',
    repository_id: captureManifest.repository_id,
    repository: captureManifest.repository,
    operation_ids: captureManifest.operation_ids,
    operation_runs: captureManifest.operation_runs,
    source_map_sha256: captureManifest.source_map_sha256,
    destination_identity_sha256: captureManifest.destination_identity_sha256,
    sources: postCleanupSources,
    artifacts: [],
    finalized: true,
  };
  const postCleanupCaptureManifestSha256 = sha256(canonicalJson(postCleanupCaptureManifest));
  const phaseSources = new Map<string, string[]>([
    ['settle-runs', []],
    [
      'remove-proof-control',
      postCleanupSources
        .filter(({ source_id }) => source_id.startsWith('post-cleanup-control-comments-'))
        .map(({ source_id }) => source_id),
    ],
    [
      'delete-observed-state',
      postCleanupSources
        .filter(({ source_id }) => source_id.startsWith('post-cleanup-state-delete-'))
        .map(({ source_id }) => source_id),
    ],
    [
      'enumerate-empty-state',
      postCleanupSources
        .filter(({ source_id }) => source_id.startsWith('post-cleanup-state-empty-'))
        .map(({ source_id }) => source_id),
    ],
    [
      'remove-authorization-and-secrets',
      postCleanupSources
        .filter(({ source_id }) =>
          ['post-cleanup-variables:page:1', 'post-cleanup-secrets:page:1'].includes(source_id),
        )
        .map(({ source_id }) => source_id),
    ],
    [
      'restore-environment',
      postCleanupSources
        .filter(({ source_id }) => source_id === 'post-cleanup-environment:page:1')
        .map(({ source_id }) => source_id),
    ],
    [
      'retire-fixtures',
      postCleanupSources
        .filter(
          ({ source_id }) =>
            source_id.startsWith('post-cleanup-ref-') || source_id.startsWith('post-cleanup-pr-'),
        )
        .map(({ source_id }) => source_id),
    ],
    [
      'read-back-sticky',
      postCleanupSources
        .filter(({ source_id }) => source_id.startsWith('post-cleanup-sticky-comments-'))
        .map(({ source_id }) => source_id),
    ],
    ['remove-local-credentials', ['cleanup-execution:local-credential-absence']],
    [
      'finalize-private-manifest',
      postCleanupSources
        .filter(({ source_id }) => source_id.startsWith('post-cleanup-final-run-'))
        .map(({ source_id }) => source_id),
    ],
  ]);
  const mutationTargets = new Map<string, string[]>([
    [
      'remove-proof-control',
      generatedCleanup.plan.targets.control_comments.map(
        ({ comment_id }: any) => `comment:${comment_id}`,
      ),
    ],
    [
      'delete-observed-state',
      generatedCleanup.plan.targets.state_artifacts.map(
        ({ artifact_id }: any) => `artifact:${artifact_id}`,
      ),
    ],
    [
      'remove-authorization-and-secrets',
      [
        `variable:${generatedCleanup.plan.targets.authorization_variable.name}`,
        ...generatedCleanup.plan.targets.secrets.map(({ name }: any) => `secret:${name}`),
      ],
    ],
    ['restore-environment', [`environment:${generatedCleanup.plan.targets.environment.name}`]],
    [
      'retire-fixtures',
      [
        ...generatedCleanup.plan.targets.fixture_refs.map(({ ref }: any) => `ref:${ref}`),
        ...generatedCleanup.plan.targets.fixture_prs.map(({ number }: any) => `pr:${number}`),
      ],
    ],
    [
      'remove-local-credentials',
      generatedCleanup.plan.targets.credential_copies.map(({ name }: any) => `credential:${name}`),
    ],
  ]);
  const postSourceById = new Map(postCleanupSources.map((source) => [source.source_id, source]));
  const runTerminalSources = candidate.authorizations.execution.operation_runs.map(
    ({ run_id }: any) => `run-terminal-${run_id}:page:1`,
  );
  const entryStart =
    Math.max(
      candidate.authorizations.cleanup.source.observation.response_received,
      ...runTerminalSources.map(
        (sourceId) =>
          captureManifest.sources.find(({ source_id }: any) => source_id === sourceId)
            .response_received_unix_milliseconds,
      ),
    ) + 1;
  let priorResponse = entryStart + 1;
  const phases = cleanupPhases.map((phase) => {
    const sourceIds = phaseSources.get(phase)!;
    const realSources = sourceIds
      .filter((sourceId) => sourceId !== 'cleanup-execution:local-credential-absence')
      .map((sourceId) => postSourceById.get(sourceId)!);
    const start =
      realSources.length === 0
        ? priorResponse + 1
        : Math.min(...realSources.map((source) => source.request_started_unix_milliseconds)) - 2;
    const response =
      realSources.length === 0
        ? start + 1
        : Math.max(...realSources.map((source) => source.response_received_unix_milliseconds));
    const mutations = (mutationTargets.get(phase) ?? []).map((target_id) => ({
      target_id,
      outcome: 'reconciled-outcome-unknown',
      request: { request_started: start, response_received: start + 1 },
      post_readback_source_ids: sourceIds,
    }));
    priorResponse = response;
    return {
      phase,
      observation: { request_started: start, response_received: response },
      mutations,
      readback_source_ids: sourceIds,
    };
  });
  const cleanupExecution = {
    kind: 'apr-r4-e3-cleanup-execution-v1',
    repository: candidate.identities.repository,
    operation_ids: candidate.identities.operation_ids,
    plan_sha256: generatedCleanup.digest,
    entry: {
      cleanup_authorization_source_id: `authorization-cleanup-comment-${candidate.authorizations.cleanup.source.comment_id}:page:1`,
      capture_manifest_sha256: captureManifestSha256,
      oracle_result_sha256: oracleResultSha256,
      run_terminal_source_ids: runTerminalSources,
      observation: { request_started: entryStart, response_received: entryStart + 1 },
    },
    phases,
    seal: {
      observation: { request_started: priorResponse + 1, response_received: priorResponse + 2 },
      post_cleanup_capture_manifest_sha256: postCleanupCaptureManifestSha256,
      credential_copies_absent: true,
      private_manifest_inputs_sealed: true,
    },
  };
  candidate.restricted_package.capture_manifest_sha256 = captureManifestSha256;
  candidate.restricted_package.oracle_result_sha256 = oracleResultSha256;
  const publicSurfaceCorpus = new Map<string, Buffer>([
    ['worktree:public-log.txt', Buffer.from('public execution log\n', 'utf8')],
  ]);
  const publicScanManifest = scanPublicCandidate({
    candidate: expectedPublic,
    corpus: publicSurfaceCorpus,
    protectedDocuments: new Map(),
    protectedCategories: protectedCanaryCategories(
      candidate.authorizations,
      candidate.identities.repository,
    ),
  });
  candidate.canaries.public_leak_scan = {
    source: 'post-cleanup-repository-and-output-scan',
    candidate_sha256: publicScanManifest.candidate_sha256,
    corpus_sha256: sha256(canonicalJson(publicScanManifest.corpus)),
    scanned_files: publicScanManifest.corpus.length,
    results: publicScanManifest.results,
  };
  const sourceMap = copy(candidate.source_map);
  const captureReference = (sourceId: string) => ({
    source_id: sourceId,
    sha256: sourceDigests.get(sourceId)!,
  });
  const evidenceReferences = (pointer: string) => {
    const referencesByPointer = new Map<string, any[]>([
      [
        '/identities',
        [
          captureReference(
            `authorization-execution-comment-${candidate.authorizations.execution.source.comment_id}:page:1`,
          ),
          { source_id: 'capture-manifest', sha256: captureManifestSha256 },
          { source_id: 'oracle-build-receipt', sha256: sha256(canonicalJson(oracleBuildReceipt)) },
          { source_id: 'oracle-result', sha256: oracleResultSha256 },
          {
            source_id: 'trusted-proof-payload-receipt-v2',
            sha256: payloadReceiptSha256,
          },
        ].sort((a, b) => a.source_id.localeCompare(b.source_id)),
      ],
      [
        '/authorizations',
        ['cleanup', 'execution', 'setup']
          .flatMap((phase) => {
            const source = candidate.authorizations[phase].source;
            return [
              captureReference(`authorization-${phase}-comment-${source.comment_id}:page:1`),
              captureReference(`authorization-${phase}-permission-maintainer:page:1`),
            ];
          })
          .sort((a, b) => a.source_id.localeCompare(b.source_id)),
      ],
      [
        '/environment',
        [
          captureReference('environment-protection:page:1'),
          {
            source_id: 'environment-ui-attestation',
            sha256: uiAttestationSha256,
          },
        ].sort((a, b) => a.source_id.localeCompare(b.source_id)),
      ],
      [
        '/approval_transitions',
        Object.entries<any>(candidate.approval_transitions)
          .flatMap(([phase, transition]) => [
            captureReference(`transition-${phase}-approvals-run-${transition.run_id}:page:1`),
            captureReference(`transition-${phase}-pending-run-${transition.run_id}:page:1`),
            captureReference(`transition-${phase}-jobs-run-${transition.run_id}:page:1`),
          ])
          .sort((a, b) => a.source_id.localeCompare(b.source_id)),
      ],
      [
        '/concurrency',
        Object.entries<any>(candidate.concurrency)
          .flatMap(([scope, concurrency]) => [
            captureReference(
              `concurrency-${scope}-run-${concurrency.terminal.holder_run_id}:page:1`,
            ),
            captureReference(`run-terminal-${concurrency.terminal.holder_run_id}:page:1`),
            captureReference(`run-terminal-${concurrency.terminal.waiter_run_id}:page:1`),
          ])
          .sort((a, b) => a.source_id.localeCompare(b.source_id)),
      ],
      [
        '/proof_control',
        Object.values<any>(candidate.proof_control)
          .flatMap((family) => family.comments)
          .flatMap((comment) => {
            const references = [
              captureReference(`proof-control-comment-${comment.comment_id}:page:1`),
            ];
            if (comment.kind === 'release' || comment.kind === 'stale-release') {
              references.push(
                captureReference(
                  `proof-control-permission-${comment.comment_id}-maintainer:page:1`,
                ),
              );
            }
            return references;
          })
          .sort((a, b) => a.source_id.localeCompare(b.source_id)),
      ],
      [
        '/inventories',
        [{ source_id: 'production-codec-oracle-result', sha256: oracleResultSha256 }],
      ],
      [
        '/cleanup',
        [
          { source_id: 'cleanup-execution', sha256: sha256(canonicalJson(cleanupExecution)) },
          { source_id: 'cleanup-plan', sha256: sha256(canonicalJson(generatedCleanup.plan)) },
          {
            source_id: 'post-cleanup-capture-manifest',
            sha256: postCleanupCaptureManifestSha256,
          },
        ],
      ],
      [
        '/canaries/live',
        [
          { source_id: 'capture-manifest', sha256: captureManifestSha256 },
          { source_id: 'oracle-result', sha256: oracleResultSha256 },
          ...Object.values<any>(candidate.proof_control)
            .flatMap((family) => family.comments)
            .filter((comment) => comment.kind === 'ready' || comment.kind === 'stale-ready')
            .map((comment) =>
              captureReference(`proof-control-comment-${comment.comment_id}:page:1`),
            ),
        ].sort((a, b) => a.source_id.localeCompare(b.source_id)),
      ],
      [
        '/canaries/cross_sink',
        [
          {
            source_id: 'trusted-proof-payload-receipt-v2',
            sha256: payloadReceiptSha256,
          },
        ],
      ],
      [
        '/canaries/public_leak_scan',
        [
          {
            source_id: 'public-leak-scan-result',
            sha256: sha256(canonicalJson(candidate.canaries.public_leak_scan)),
          },
        ],
      ],
      [
        '/restricted_package',
        [
          { source_id: 'capture-manifest', sha256: captureManifestSha256 },
          { source_id: 'oracle-result', sha256: oracleResultSha256 },
          {
            source_id: 'restricted-package-readback',
            sha256: sha256(canonicalJson(candidate.restricted_package)),
          },
        ],
      ],
    ]);
    return referencesByPointer.get(pointer)!;
  };
  const sourceBundle = {
    kind: 'apr-r4-e3-closed-source-bundle-v1',
    source_map_sha256: sha256(canonicalJson(sourceMap)),
    documents: sourceMap.entries.map((entry: any) => {
      const references = evidenceReferences(entry.destination_pointer);
      const evidence = {
        kind: entry.source_kind,
        references,
        set_sha256: sha256(canonicalJson({ kind: entry.source_kind, references })),
      };
      return {
        source_id: entry.source_id,
        destination_pointer: entry.destination_pointer,
        source_contract_sha256: entry.source_contract_sha256,
        evidence,
      };
    }),
  };
  return {
    sourceMap,
    sourceBundle,
    captureManifest,
    captureManifestSha256,
    postCleanupCaptureManifest,
    postCleanupCaptureManifestSha256,
    oracleResult,
    oracleResultSha256,
    capturedSourceBodies,
    postCleanupCapturedSourceBodies,
    retainedDocuments: new Map([
      ['trusted-proof-payload-receipt-v2', payloadReceipt],
      ['oracle-build-receipt', oracleBuildReceipt],
      ['cleanup-plan', generatedCleanup.plan],
      ['cleanup-execution', cleanupExecution],
      ['public-leak-scan-result', candidate.canaries.public_leak_scan],
      ['restricted-package-readback', candidate.restricted_package],
    ]),
    oracleBinaries,
    publicSurfaceCorpus,
    uiAttestation,
    uiAttestationSha256,
    credentialCopiesAbsent: true,
  };
}

function refreshRetainedDocument(assembly: any, pointer: string, sourceId: string) {
  const document = assembly.sourceBundle.documents.find(
    (candidate: any) => candidate.destination_pointer === pointer,
  );
  const reference = document.evidence.references.find(
    (candidate: any) => candidate.source_id === sourceId,
  );
  reference.sha256 = sha256(canonicalJson(assembly.retainedDocuments.get(sourceId)));
  document.evidence.set_sha256 = sha256(
    canonicalJson({ kind: document.evidence.kind, references: document.evidence.references }),
  );
}

function refreshPostCleanupCapture(assembly: any, sourceId: string, value?: any) {
  const source = assembly.postCleanupCaptureManifest.sources.find(
    (candidate: any) => candidate.source_id === sourceId,
  );
  if (source === undefined) throw new Error(`missing synthetic post-cleanup source ${sourceId}`);
  if (value !== undefined) {
    const text = canonicalJson(value);
    assembly.postCleanupCapturedSourceBodies.set(sourceId, { text });
    source.body_sha256 = sha256(Buffer.from(text, 'utf8'));
    source.body_size = String(Buffer.byteLength(text, 'utf8'));
  }
  assembly.postCleanupCaptureManifestSha256 = sha256(
    canonicalJson(assembly.postCleanupCaptureManifest),
  );
  const cleanupDocument = assembly.sourceBundle.documents.find(
    (candidate: any) => candidate.destination_pointer === '/cleanup',
  );
  const reference = cleanupDocument.evidence.references.find(
    (candidate: any) => candidate.source_id === 'post-cleanup-capture-manifest',
  );
  reference.sha256 = assembly.postCleanupCaptureManifestSha256;
  cleanupDocument.evidence.set_sha256 = sha256(
    canonicalJson({
      kind: cleanupDocument.evidence.kind,
      references: cleanupDocument.evidence.references,
    }),
  );
}

describe('R4 E3 executable evidence contract', () => {
  test('reopens every captured source, archive, and encrypted object at final assembly', () => {
    const restrictedRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'r4-assembly-'));
    const packageRoot = path.join(restrictedRoot, 'package');
    fs.mkdirSync(packageRoot);
    const source = Buffer.from('{}');
    const archive = Buffer.from('archive');
    const encrypted = Buffer.from('encrypted-object');
    const sourcePath = path.join(packageRoot, 'source-0001.json');
    const archivePath = path.join(packageRoot, 'artifact-1001.zip');
    const objectPath = path.join(packageRoot, 'artifact-1001.bin');
    fs.writeFileSync(sourcePath, source);
    fs.writeFileSync(archivePath, archive);
    fs.writeFileSync(objectPath, encrypted);
    if (process.platform !== 'win32') {
      fs.chmodSync(sourcePath, 0o600);
      fs.chmodSync(archivePath, 0o600);
      fs.chmodSync(objectPath, 0o600);
    }
    const manifest = {
      sources: [
        {
          body_path: path.basename(sourcePath),
          body_size: String(source.length),
          body_sha256: sha256(source),
          body_file_identity: pinnedFileIdentity(sourcePath),
        },
      ],
      artifacts: [
        {
          archive_path: path.basename(archivePath),
          archive_size: String(archive.length),
          archive_sha256: sha256(archive),
          archive_file_identity: pinnedFileIdentity(archivePath),
          encrypted_object_path: path.basename(objectPath),
          encrypted_object_size: String(encrypted.length),
          encrypted_object_sha256: sha256(encrypted),
          encrypted_object_file_identity: pinnedFileIdentity(objectPath),
        },
      ],
    };
    try {
      expect(() =>
        verifyCapturedFiles(
          restrictedRoot,
          path.join(packageRoot, 'capture-manifest.json'),
          manifest,
        ),
      ).not.toThrow();
      fs.appendFileSync(sourcePath, 'tampered');
      expect(() =>
        verifyCapturedFiles(
          restrictedRoot,
          path.join(packageRoot, 'capture-manifest.json'),
          manifest,
        ),
      ).toThrow();
    } finally {
      fs.rmSync(restrictedRoot, { recursive: true, force: true });
    }
  });

  test('keeps assembler, cleanup generator, and projector offline and mutation-free', () => {
    const offlineSources = [
      'assemble-r4-trusted-proof-evidence.mjs',
      'generate-r4-trusted-proof-cleanup-plan.mjs',
      'project-r4-trusted-proof-evidence.mjs',
    ].map((name) => fs.readFileSync(path.join(root, 'scripts', name), 'utf8'));
    for (const source of offlineSources) {
      expect(source).not.toMatch(/node:https?|child_process|\bfetch\s*\(|\bexec(?:File)?\s*\(/u);
    }
    expect(offlineSources[1]).not.toContain('writeFile');
  });

  test('assembles a source-bound protected package before projection', () => {
    const input = syntheticAssembly();
    expect(
      input.sourceBundle.documents.every(
        (document: any) =>
          !Object.prototype.hasOwnProperty.call(document, 'value') &&
          !Object.prototype.hasOwnProperty.call(document, 'value_sha256'),
      ),
    ).toBe(true);
    const assembled = assembleTrustedProofEvidence(input);
    expect(assembled.publicEvidence).toBeNull();
    const privatePackageManifest = buildFinalizedPrivatePackageManifest({
      host: assembled.host,
      sourceBundle: input.sourceBundle,
      captureManifestSha256: input.captureManifestSha256,
      postCleanupCaptureManifestSha256: input.postCleanupCaptureManifestSha256,
      oracleResultSha256: input.oracleResultSha256,
      cleanupPlan: assembled.cleanupPlan,
      oracleBuildReceiptSha256:
        assembled.host.authorizations.execution.oracle_build.build_receipt_sha256,
      oracleAssemblySha256: input.oracleBinaries.oracle_assembly_sha256,
      productionAssemblySha256: input.oracleBinaries.production_assembly_sha256,
      publicCandidateSha256: sha256(canonicalJson(assembled.publicCandidate)),
      publicScanManifestSha256: sha256(canonicalJson(assembled.publicScanManifest)),
    });
    expect(
      projectFinalizedTrustedProofEvidence({
        host: assembled.host,
        sourceBundle: input.sourceBundle,
        captureManifestSha256: input.captureManifestSha256,
        postCleanupCaptureManifestSha256: input.postCleanupCaptureManifestSha256,
        oracleResultSha256: input.oracleResultSha256,
        cleanupPlan: assembled.cleanupPlan,
        oracleBuildReceiptSha256:
          assembled.host.authorizations.execution.oracle_build.build_receipt_sha256,
        oracleAssemblySha256: input.oracleBinaries.oracle_assembly_sha256,
        productionAssemblySha256: input.oracleBinaries.production_assembly_sha256,
        publicCandidateSha256: sha256(canonicalJson(assembled.publicCandidate)),
        publicScanManifestSha256: sha256(canonicalJson(assembled.publicScanManifest)),
        privatePackageManifest,
      }),
    ).toEqual(expectedPublic);
  });

  test.each([
    [
      'early first mutation',
      (journal: any) => {
        journal.phases[1].mutations[0].request.request_started =
          journal.entry.observation.request_started - 2;
      },
    ],
    [
      'reordered phases',
      (journal: any) => {
        [journal.phases[2], journal.phases[3]] = [journal.phases[3], journal.phases[2]];
      },
    ],
    [
      'mutation without post-readback',
      (journal: any) => {
        journal.phases[1].mutations[0].post_readback_source_ids = [];
      },
    ],
    [
      'reused final readback for an earlier phase',
      (journal: any) => {
        journal.phases[3].readback_source_ids = journal.phases[2].readback_source_ids;
        for (const mutation of journal.phases[3].mutations) {
          mutation.post_readback_source_ids = journal.phases[2].readback_source_ids;
        }
      },
    ],
    [
      'unresolved outcome',
      (journal: any) => {
        journal.phases[2].mutations[0].outcome = 'outcome-unknown';
      },
    ],
    [
      'seal before the last phase',
      (journal: any) => {
        journal.seal.observation.request_started =
          journal.phases.at(-1).observation.request_started;
      },
    ],
  ])('rejects cleanup execution with %s', (_name, mutate) => {
    const input = syntheticAssembly();
    const journal = input.retainedDocuments.get('cleanup-execution');
    mutate(journal);
    refreshRetainedDocument(input, '/cleanup', 'cleanup-execution');
    expect(() => assembleTrustedProofEvidence(input)).toThrow(/cleanup-execution/u);
  });

  test('rejects projection when the finalized private manifest readback drifts', () => {
    const input = syntheticAssembly();
    const assembled = assembleTrustedProofEvidence(input);
    const privatePackageManifest = buildFinalizedPrivatePackageManifest({
      host: assembled.host,
      sourceBundle: input.sourceBundle,
      captureManifestSha256: input.captureManifestSha256,
      postCleanupCaptureManifestSha256: input.postCleanupCaptureManifestSha256,
      oracleResultSha256: input.oracleResultSha256,
      cleanupPlan: assembled.cleanupPlan,
      oracleBuildReceiptSha256:
        assembled.host.authorizations.execution.oracle_build.build_receipt_sha256,
      oracleAssemblySha256: input.oracleBinaries.oracle_assembly_sha256,
      productionAssemblySha256: input.oracleBinaries.production_assembly_sha256,
      publicCandidateSha256: sha256(canonicalJson(assembled.publicCandidate)),
      publicScanManifestSha256: sha256(canonicalJson(assembled.publicScanManifest)),
    });
    privatePackageManifest.host_evidence_sha256 = 'f'.repeat(64);
    expect(() =>
      projectFinalizedTrustedProofEvidence({
        host: assembled.host,
        sourceBundle: input.sourceBundle,
        captureManifestSha256: input.captureManifestSha256,
        postCleanupCaptureManifestSha256: input.postCleanupCaptureManifestSha256,
        oracleResultSha256: input.oracleResultSha256,
        cleanupPlan: assembled.cleanupPlan,
        oracleBuildReceiptSha256:
          assembled.host.authorizations.execution.oracle_build.build_receipt_sha256,
        oracleAssemblySha256: input.oracleBinaries.oracle_assembly_sha256,
        productionAssemblySha256: input.oracleBinaries.production_assembly_sha256,
        publicCandidateSha256: sha256(canonicalJson(assembled.publicCandidate)),
        publicScanManifestSha256: sha256(canonicalJson(assembled.publicScanManifest)),
        privatePackageManifest,
      }),
    ).toThrow(/private-package-finalized-readback/u);
  });

  test('keeps a fully assembled recovery package private when an authenticated extra exists', () => {
    const candidate = copy(host);
    candidate.inventories.observed_cleanup.push({
      artifact_id: '1016',
      object_class: 'publication_failure',
      scope: 'stale',
      operation_id: candidate.identities.operation_ids[1],
      artifact_name: 'apr-r4-publication-failure-1016',
      producing_run_id: '9003',
      producing_run_attempt: 1,
      archive_sha256: '8'.repeat(64),
      encrypted_object_sha256: '9'.repeat(64),
      encrypted_object_size: '2016',
      ownership_evidence_sha256: sha256(
        canonicalJson({
          artifact_id: '1016',
          artifact_name: 'apr-r4-publication-failure-1016',
          producing_run_id: '9003',
          producing_run_attempt: '1',
          archive_sha256: '8'.repeat(64),
          encrypted_object_sha256: '9'.repeat(64),
          encrypted_object_size: '2016',
        }),
      ),
      authenticated: true,
      operation_owned: true,
      disposition: 'recovery-only-delete',
    });
    const generated = generateCleanupPlan({
      operation_ids: candidate.identities.operation_ids,
      proof_control: candidate.proof_control,
      observed_cleanup: candidate.inventories.observed_cleanup,
      resources: candidate.cleanup.resources,
    });
    candidate.cleanup.plan_sha256 = generated.digest;
    candidate.authorizations.cleanup.plan_sha256 = generated.digest;
    candidate.cleanup.projection_gate.exact_seven_success = false;

    const assembled = assembleTrustedProofEvidence(syntheticAssembly(candidate));
    expect(assembled.recoveryOnly).toBe(true);
    expect(assembled.publicEvidence).toBeNull();
    expect(assembled.cleanupPlan.targets.state_artifacts).toHaveLength(16);
  });

  test.each([
    [
      'missing captured object',
      (value: any) => {
        value.captureManifest.artifacts.pop();
        value.captureManifestSha256 = sha256(canonicalJson(value.captureManifest));
        const restricted = value.retainedDocuments.get('restricted-package-readback');
        restricted.capture_manifest_sha256 = value.captureManifestSha256;
        value.oracleResult.capture_manifest_sha256 = value.captureManifestSha256;
        value.oracleResultSha256 = sha256(canonicalJson(value.oracleResult));
        restricted.oracle_result_sha256 = value.oracleResultSha256;
        refreshRetainedDocument(value, '/restricted_package', 'restricted-package-readback');
      },
    ],
    [
      'swapped oracle role',
      (value: any) => {
        value.oracleResult.records[0].role = 'normal-lineage-head';
        value.oracleResultSha256 = sha256(canonicalJson(value.oracleResult));
        const restricted = value.retainedDocuments.get('restricted-package-readback');
        restricted.oracle_result_sha256 = value.oracleResultSha256;
        refreshRetainedDocument(value, '/restricted_package', 'restricted-package-readback');
      },
    ],
    [
      'capture digest mismatch',
      (value: any) => {
        value.captureManifestSha256 = '9'.repeat(64);
      },
    ],
    [
      'oracle build receipt assembly mismatch',
      (value: any) => {
        value.retainedDocuments.get('oracle-build-receipt').oracle_assembly_sha256 = 'f'.repeat(64);
        refreshRetainedDocument(value, '/identities', 'oracle-build-receipt');
      },
    ],
    [
      'replacement oracle binary',
      (value: any) => {
        value.oracleBinaries.oracle_assembly_sha256 = 'f'.repeat(64);
      },
    ],
    [
      'remaining post-cleanup artifact',
      (value: any) => {
        const source = value.postCleanupCaptureManifest.sources.find((candidate: any) =>
          candidate.source_id.startsWith('post-cleanup-state-empty-run-'),
        );
        refreshPostCleanupCapture(value, source.source_id, {
          total_count: 1,
          artifacts: [{ id: 999 }],
        });
      },
    ],
    [
      'omitted post-cleanup source',
      (value: any) => {
        const sourceId = 'post-cleanup-secrets:page:1';
        value.postCleanupCaptureManifest.sources = value.postCleanupCaptureManifest.sources.filter(
          (candidate: any) => candidate.source_id !== sourceId,
        );
        value.postCleanupCapturedSourceBodies.delete(sourceId);
        refreshPostCleanupCapture(value, value.postCleanupCaptureManifest.sources[0].source_id);
      },
    ],
    [
      'post-cleanup route from another operation',
      (value: any) => {
        const source = value.postCleanupCaptureManifest.sources.find((candidate: any) =>
          candidate.source_id.startsWith('post-cleanup-final-run-'),
        );
        source.route = `/repos/${host.identities.repository}/actions/runs/9999`;
        refreshPostCleanupCapture(value, source.source_id);
      },
    ],
    [
      'public surface containing protected evidence',
      (value: any) => {
        value.publicSurfaceCorpus.set(
          'worktree:leaked-source-bundle.json',
          Buffer.from(canonicalJson(value.sourceBundle), 'utf8'),
        );
      },
    ],
    [
      'public surface containing only a protected category fragment',
      (value: any) => {
        const operation = value.captureManifest.operation_ids[0];
        const canary = `APR_R4_E4_PROVIDER_CONTENT_${operation}`;
        value.publicSurfaceCorpus.set(
          'logs:partial-provider-canary.log',
          Buffer.from(canary.slice(9, 31), 'utf8'),
        );
      },
    ],
    [
      'caller scan summary for a different candidate',
      (value: any) => {
        value.retainedDocuments.get('public-leak-scan-result').candidate_sha256 = 'f'.repeat(64);
        refreshRetainedDocument(value, '/canaries/public_leak_scan', 'public-leak-scan-result');
      },
    ],
    [
      'retained credential copy',
      (value: any) => {
        value.credentialCopiesAbsent = false;
      },
    ],
    [
      'unbound source document',
      (value: any) => {
        value.retainedDocuments.get('trusted-proof-payload-receipt-v2').payload_sha256 = 'f'.repeat(
          64,
        );
        refreshRetainedDocument(value, '/identities', 'trusted-proof-payload-receipt-v2');
      },
    ],
    [
      'source evidence not present in the capture manifest',
      (value: any) => {
        const evidence = value.sourceBundle.documents.find(
          (candidate: any) => candidate.destination_pointer === '/approval_transitions',
        ).evidence;
        evidence.references[0].sha256 = 'f'.repeat(64);
        evidence.set_sha256 = sha256(
          canonicalJson({ kind: evidence.kind, references: evidence.references }),
        );
      },
    ],
  ])('rejects assembly with %s', (_name, mutate) => {
    const value = syntheticAssembly();
    mutate(value);
    expect(() => assembleTrustedProofEvidence(value)).toThrow(/APR_R4_E3_EVIDENCE_INVALID/u);
  });

  test('projects the canonical exact-seven evidence byte-stably', () => {
    expect(validateHostEvidence(host).inventory.successIds.size).toBe(7);
    expect(projectTrustedProofEvidence(host)).toEqual(expectedPublic);
    expect(assertPublicSafeEvidence(expectedPublic)).toBe(true);
    expect(canonicalJson(projectTrustedProofEvidence(host))).toBe(
      fs.readFileSync(path.join(fixtureRoot, 'templates', 'public-safe-evidence.json'), 'utf8'),
    );
  });

  test('rejects approval-history readback after the protected job source timestamp', () => {
    const candidate = copy(host);
    candidate.approval_transitions.bootstrap.approval.observation = {
      request_started: 15,
      response_received: 16,
    };
    expect(() => validateHostEvidence(candidate)).toThrow();
  });

  test('recursively closes both schemas against nested extra properties', () => {
    const ajv = new Ajv({ allErrors: true, strict: true });
    const hostSchema = JSON.parse(
      fs.readFileSync(
        path.join(fixtureRoot, 'schemas', 'host-restricted-evidence.schema.json'),
        'utf8',
      ),
    );
    const publicSchema = JSON.parse(
      fs.readFileSync(
        path.join(fixtureRoot, 'schemas', 'public-safe-evidence.schema.json'),
        'utf8',
      ),
    );
    const hostCandidate = copy(host);
    hostCandidate.environment.protection_snapshot.unreviewed = true;
    const publicCandidate = copy(expectedPublic);
    publicCandidate.scheduling.unreviewed = true;
    expect(ajv.compile(hostSchema)(hostCandidate)).toBe(false);
    expect(ajv.compile(publicSchema)(publicCandidate)).toBe(false);
  });

  test('requires public run IDs to remain a sorted unique set', () => {
    const candidate = copy(expectedPublic);
    [candidate.participating_run_ids[0], candidate.participating_run_ids[1]] = [
      candidate.participating_run_ids[1],
      candidate.participating_run_ids[0],
    ];
    expect(() => assertPublicSafeEvidence(candidate)).toThrow(/APR_R4_E3_EVIDENCE_INVALID/u);
  });

  test.each([
    ['missing stale comment', (value: any) => value.proof_control.stale.comments.pop()],
    [
      'duplicate stale comment',
      (value: any) => {
        value.proof_control.stale.comments[3] = copy(value.proof_control.stale.comments[2]);
      },
    ],
    [
      'reordered stale comments',
      (value: any) => {
        [value.proof_control.stale.comments[1], value.proof_control.stale.comments[2]] = [
          value.proof_control.stale.comments[2],
          value.proof_control.stale.comments[1],
        ];
      },
    ],
    [
      'wrong stale predecessor',
      (value: any) => {
        value.proof_control.stale.comments[3].predecessor_comment_id = '8101';
      },
    ],
    [
      'retained stale control',
      (value: any) => {
        value.proof_control.stale.cleanup_outcomes[3].outcome = 'retained';
      },
    ],
  ])('rejects %s', (_name, mutate) => {
    expect(projectMutation(mutate)).toThrow(/APR_R4_E3_EVIDENCE_INVALID/u);
  });

  test.each([
    [
      'invented approved time',
      (value: any) => {
        value.approval_transitions.bootstrap.approval.approved_at = 15;
      },
    ],
    [
      'rerun',
      (value: any) => {
        value.approval_transitions.continuation.run_attempt = 2;
      },
    ],
    [
      'cross-run approval',
      (value: any) => {
        value.approval_transitions.stale.approval.run_id = '9002';
      },
    ],
    [
      'cross-environment approval',
      (value: any) => {
        value.approval_transitions.bootstrap.approval.environment_id = '7002';
      },
    ],
    [
      'pending deployment observed after job start',
      (value: any) => {
        value.approval_transitions.bootstrap.pending.observation.response_received = 99;
      },
    ],
  ])('rejects %s', (_name, mutate) => {
    expect(projectMutation(mutate)).toThrow(/APR_R4_E3_EVIDENCE_INVALID/u);
  });

  test.each([
    [
      'old concurrency API',
      (value: any) => {
        value.concurrency.normal.api_version = '2022-11-28';
      },
    ],
    [
      'incomplete group pagination',
      (value: any) => {
        value.concurrency.stale.pagination_complete = false;
      },
    ],
    [
      'bare status without ahead_of_run',
      (value: any) => {
        value.concurrency.normal.ahead_of_run = [];
      },
    ],
    [
      'cross-group member',
      (value: any) => {
        value.concurrency.stale.ahead_of_run[1].run_id = '9002';
      },
    ],
    [
      'cancelled holder',
      (value: any) => {
        value.concurrency.normal.terminal.holder_cancelled = true;
      },
    ],
  ])('rejects %s', (_name, mutate) => {
    expect(projectMutation(mutate)).toThrow(/APR_R4_E3_EVIDENCE_INVALID/u);
  });

  test.each([
    [
      'missing product anchor',
      (value: any) => {
        value.inventories.expected_success.pop();
      },
    ],
    [
      'duplicate product anchor id',
      (value: any) => {
        value.inventories.expected_success[6].artifact_id = '1006';
      },
    ],
    [
      'synthetic role',
      (value: any) => {
        value.inventories.expected_success[0].role = 'synthetic-reset';
      },
    ],
    [
      'role and codec-class mismatch',
      (value: any) => {
        value.inventories.expected_success[3].object_class = 'acceptance';
      },
    ],
    [
      'unauthenticated cleanup target',
      (value: any) => {
        value.inventories.observed_cleanup[0].authenticated = false;
      },
    ],
    [
      'unexpected ordinary cleanup target',
      (value: any) => {
        value.inventories.observed_cleanup.push({
          artifact_id: '1016',
          object_class: 'publication_failure',
          scope: 'stale',
          operation_id: value.identities.operation_ids[1],
          authenticated: true,
          operation_owned: true,
          disposition: 'delete',
        });
      },
    ],
    [
      'cross-operation cleanup target',
      (value: any) => {
        value.inventories.observed_cleanup[0].operation_id = '8'.repeat(64);
      },
    ],
  ])('rejects %s', (_name, mutate) => {
    expect(projectMutation(mutate)).toThrow(/APR_R4_E3_EVIDENCE_INVALID/u);
  });

  test('includes an authenticated recovery-only extra in cleanup but prohibits projection', () => {
    const candidate = copy(host);
    candidate.inventories.observed_cleanup.push({
      artifact_id: '1016',
      object_class: 'publication_failure',
      scope: 'stale',
      operation_id: candidate.identities.operation_ids[1],
      artifact_name: 'apr-r4-publication-failure-1016',
      producing_run_id: '9003',
      producing_run_attempt: 1,
      archive_sha256: '8'.repeat(64),
      encrypted_object_sha256: '9'.repeat(64),
      encrypted_object_size: '2016',
      ownership_evidence_sha256: 'a'.repeat(64),
      authenticated: true,
      operation_owned: true,
      disposition: 'recovery-only-delete',
    });
    const generated = generateCleanupPlan({
      operation_ids: candidate.identities.operation_ids,
      proof_control: candidate.proof_control,
      observed_cleanup: candidate.inventories.observed_cleanup,
      resources: candidate.cleanup.resources,
    });
    expect(generated.plan.targets.state_artifacts.map((item: any) => item.artifact_id)).toContain(
      '1016',
    );
    candidate.cleanup.plan_sha256 = generated.digest;
    candidate.authorizations.cleanup.plan_sha256 = generated.digest;
    candidate.cleanup.projection_gate.exact_seven_success = false;
    expect(() => projectTrustedProofEvidence(candidate)).toThrow(/recovery-only-no-projection/u);
  });

  test('generates the checked cleanup plan deterministically with all 15 observed resources', () => {
    const input = {
      operation_ids: host.identities.operation_ids,
      proof_control: host.proof_control,
      observed_cleanup: host.inventories.observed_cleanup,
      resources: host.cleanup.resources,
    };
    const first = generateCleanupPlan(input);
    const second = generateCleanupPlan(copy(input));
    expect(first).toEqual(second);
    expect(first.plan.phases.map((item) => item.phase)).toEqual(cleanupPhases);
    expect(first.plan.targets.state_artifacts).toHaveLength(15);
    const mutableTargets = [
      ...first.plan.targets.control_comments,
      ...first.plan.targets.state_artifacts,
      first.plan.targets.authorization_variable,
      ...first.plan.targets.secrets,
      first.plan.targets.environment,
      ...first.plan.targets.fixture_refs,
      ...first.plan.targets.fixture_prs,
      ...first.plan.targets.credential_copies,
    ];
    expect(
      mutableTargets.every(
        (item: any) =>
          typeof item.mutation === 'string' &&
          typeof item.outcome_unknown === 'string' &&
          typeof item.post_readback === 'string',
      ),
    ).toBe(true);
    expect(first.plan.targets.final_state_enumeration.map((item: any) => item.scope)).toEqual([
      'repository-root',
      'normal',
      'stale',
    ]);
    expect(first.plan.targets.sticky.mutation).toBe('none-retain');
    expect(first.canonical).toBe(
      fs.readFileSync(path.join(fixtureRoot, 'cleanup-plan.json'), 'utf8'),
    );
  });

  test.each([
    [
      'a duplicated observed artifact',
      (value: any) => {
        value.observed_cleanup[1].artifact_id = value.observed_cleanup[0].artifact_id;
      },
    ],
    [
      'reordered control targets',
      (value: any) => {
        [value.proof_control.stale.comments[1], value.proof_control.stale.comments[2]] = [
          value.proof_control.stale.comments[2],
          value.proof_control.stale.comments[1],
        ];
      },
    ],
    [
      'a broadened secret target set',
      (value: any) => {
        value.resources.secret_names.push('UNRELATED_SECRET');
      },
    ],
  ])('cleanup generator rejects %s', (_name, mutate) => {
    const input = {
      operation_ids: copy(host.identities.operation_ids),
      proof_control: copy(host.proof_control),
      observed_cleanup: copy(host.inventories.observed_cleanup),
      resources: copy(host.cleanup.resources),
    };
    mutate(input);
    expect(() => generateCleanupPlan(input)).toThrow(/APR_R4_E3_EVIDENCE_INVALID/u);
  });

  test.each([
    [
      'setup authorization broadened',
      (value: any) => value.authorizations.setup.capabilities.push('place-secret'),
    ],
    [
      'future PR coordinate in setup',
      (value: any) => {
        value.authorizations.setup.branches[0].pr_number = '1001';
      },
    ],
    [
      'missing execution credential identity',
      (value: any) => value.authorizations.execution.credential_files.pop(),
    ],
    [
      'cleanup authorization reused',
      (value: any) => {
        value.authorizations.cleanup.phase = 'execution';
      },
    ],
    [
      'wrong cleanup plan digest',
      (value: any) => {
        value.authorizations.cleanup.plan_sha256 = '0'.repeat(64);
      },
    ],
  ])('rejects %s', (_name, mutate) => {
    expect(projectMutation(mutate)).toThrow(/APR_R4_E3_EVIDENCE_INVALID/u);
  });

  test.each([
    [
      'early cleanup entry',
      (value: any) => {
        value.cleanup.entry_gate.all_runs_terminal = false;
      },
    ],
    [
      'reordered cleanup',
      (value: any) => {
        value.cleanup.ordered_readbacks.reverse();
      },
    ],
    [
      'incomplete cleanup readback',
      (value: any) => {
        value.cleanup.ordered_readbacks[2].complete = false;
      },
    ],
    [
      'premature projection',
      (value: any) => {
        value.cleanup.projection_gate.state_empty_complete = false;
      },
    ],
    [
      'credential copy retained',
      (value: any) => {
        value.restricted_package.current_key_copy_absent = false;
      },
    ],
    [
      'unfinalized private manifest',
      (value: any) => {
        value.restricted_package.manifest_finalized = false;
      },
    ],
  ])('rejects %s', (_name, mutate) => {
    expect(projectMutation(mutate)).toThrow(/APR_R4_E3_EVIDENCE_INVALID/u);
  });

  test.each([
    [
      'missing source mapping',
      (value: any) => {
        value.source_map.entries.pop();
      },
    ],
    [
      'mutated source contract digest',
      (value: any) => {
        value.source_map.entries[0].source_contract_sha256 = '0'.repeat(64);
      },
    ],
    [
      'publicly sourced UI fact',
      (value: any) => {
        value.environment.ui_attestation.source_kind = 'public-projection';
      },
    ],
    [
      'cross-environment UI attestation',
      (value: any) => {
        value.environment.ui_attestation.environment = 'other';
      },
    ],
    [
      'administrator bypass',
      (value: any) => {
        value.environment.ui_attestation.administrator_bypass = true;
      },
    ],
    [
      'failed leak scan',
      (value: any) => {
        value.canaries.public_leak_scan.results.state_keys = 'present';
      },
    ],
  ])('rejects %s', (_name, mutate) => {
    expect(projectMutation(mutate)).toThrow(/APR_R4_E3_EVIDENCE_INVALID/u);
  });

  test('public output contains no protected joins or recovery facts', () => {
    const serialized = JSON.stringify(expectedPublic);
    for (const forbidden of [
      'comment_id',
      'operation_id',
      'environment_id',
      'approval',
      'manifest_sha256',
      'plan_sha256',
      'recovery-only',
      'artifact_id',
      'archive',
      'encrypted',
      'lineage',
    ]) {
      expect(serialized).not.toContain(forbidden);
    }
  });
});
