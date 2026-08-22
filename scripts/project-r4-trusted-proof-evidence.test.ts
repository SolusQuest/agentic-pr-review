import { execFileSync, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, test } from 'vitest';
import {
  assertPublicSafeEvidence,
  projectTrustedProofEvidence,
} from './project-r4-trusted-proof-evidence.mjs';

const root = path.resolve(import.meta.dirname, '..');
const script = path.join(root, 'scripts', 'project-r4-trusted-proof-evidence.mjs');
const fixtureRoot = path.join(root, 'runtime', 'tests', 'fixtures', 'action-host', 'trusted-proof');
const hostTemplatePath = path.join(fixtureRoot, 'templates', 'host-restricted-evidence.json');
const hostTemplate = JSON.parse(fs.readFileSync(hostTemplatePath, 'utf8')) as Record<string, any>;
const expectedPublic = JSON.parse(
  fs.readFileSync(path.join(fixtureRoot, 'templates', 'public-safe-evidence.json'), 'utf8'),
) as Record<string, any>;
const temporaryRoots: string[] = [];

afterEach(() => {
  for (const directory of temporaryRoots.splice(0)) {
    fs.rmSync(directory, { force: true, recursive: true });
  }
});

function clone<T>(value: T): T {
  return structuredClone(value);
}

function canonicalInput(value: unknown) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-r4-e3-evidence-'));
  temporaryRoots.push(directory);
  const pathname = path.join(directory, 'host.json');
  fs.writeFileSync(pathname, `${JSON.stringify(value)}\n`);
  return pathname;
}

describe('R4 E3 public-safe evidence projection', () => {
  test('projects the exact closed public template from complete host-restricted evidence', () => {
    expect(projectTrustedProofEvidence(clone(hostTemplate))).toEqual(expectedPublic);
    expect(assertPublicSafeEvidence(clone(expectedPublic))).toBe(true);
  });

  test('allows both independent runs to have attempt one', () => {
    const candidate = clone(hostTemplate);
    candidate.runs.bootstrap.run_attempt = 1;
    candidate.runs.continuation.run_attempt = 1;
    expect(projectTrustedProofEvidence(candidate).participating_run_ids).toEqual(['9001', '9002']);
  });

  test('the CLI prints only canonical public-safe evidence', () => {
    const output = execFileSync(process.execPath, [script, hostTemplatePath], {
      cwd: root,
      encoding: 'utf8',
    });
    expect(output).toBe(`${JSON.stringify(expectedPublic)}\n`);
    for (const protectedValue of [
      'candidate-bootstrap-physical',
      'acceptance-bootstrap-physical',
      'opaque-locator-normal',
      'lineage-object-v2',
    ]) {
      expect(output).not.toContain(protectedValue);
    }
  });

  test.each([
    ['same-run-id', (value: any) => (value.runs.continuation.run_id = value.runs.bootstrap.run_id)],
    ['nonpositive-attempt', (value: any) => (value.runs.continuation.run_attempt = 0)],
    [
      'dispatch-after-run-one',
      (value: any) => (value.runs.continuation.created_at = value.runs.bootstrap.completed_at + 1),
    ],
    [
      'observation-after-completion',
      (value: any) => (value.observation.observed_at = value.runs.bootstrap.completed_at),
    ],
    [
      'protected-start-before-completion',
      (value: any) =>
        (value.runs.continuation.protected_job_started_at = value.runs.bootstrap.completed_at),
    ],
    [
      'protected-allocation-at-observation',
      (value: any) => (value.observation.privileged_job_allocated = true),
    ],
    [
      'environment-admission-at-observation',
      (value: any) => (value.observation.environment_admission_started = true),
    ],
    ['group-mismatch', (value: any) => (value.runs.continuation.concurrency_group = 'other')],
    [
      'normal-head-mismatch',
      (value: any) => (value.runs.continuation.reviewed_head_sha = 'f'.repeat(40)),
    ],
    [
      'sticky-lineage-mismatch',
      (value: any) => (value.product.continuation.sticky_comment_id = '7002'),
    ],
    [
      'predecessor-mismatch',
      (value: any) =>
        (value.product.continuation.predecessor_acceptance_object_identity = 'other-acceptance'),
    ],
    [
      'workflow-base-mismatch',
      (value: any) => (value.identities.reviewed_base_sha = 'e'.repeat(40)),
    ],
    ['normal-parent-mismatch', (value: any) => (value.fixture.normal_parent_sha = 'e'.repeat(40))],
    [
      'authorization-digest-mismatch',
      (value: any) => (value.authorization.manifest_sha256 = 'e'.repeat(64)),
    ],
    [
      'authorization-readback-after-start',
      (value: any) => (value.authorization.normal_read_back_at = 51),
    ],
    [
      'authorization-manifest-discriminator',
      (value: any) => (value.authorization.manifest.kind = 'apr-r4-e3-authorization-manifest-v1'),
    ],
    [
      'authorization-not-removed-between-phases',
      (value: any) => (value.authorization.normal_absent_read_back_at = 721),
    ],
    [
      'stale-authorization-head-mismatch',
      (value: any) => (value.authorization.stale_manifest.fixture_head_sha = 'e'.repeat(40)),
    ],
    ['environment-secret-omitted', (value: any) => value.protected_environment.secret_names.pop()],
    [
      'environment-snapshot-unverified',
      (value: any) => (value.protected_environment.snapshot_sha256 = 'e'.repeat(64)),
    ],
    [
      'proof-ready-readback-mismatch',
      (value: any) => (value.proof_control.normal.ready.readback_body_sha256 = 'e'.repeat(64)),
    ],
    [
      'barrier-release-before-observation',
      (value: any) => (value.proof_control.normal.barrier_released_at = 199),
    ],
    [
      'preexisting-root',
      (value: any) => value.state.pre_state.repository_root.locator_root.push('existing-root'),
    ],
    [
      'incomplete-root-pagination',
      (value: any) => (value.state.pre_state.repository_root.pagination_complete = false),
    ],
    ['missing-deletion', (value: any) => value.cleanup.deleted_physical_artifact_ids.pop()],
    [
      'duplicate-created-id',
      (value: any) =>
        (value.state.created[1].physical_artifact_id = value.state.created[0].physical_artifact_id),
    ],
    [
      'surviving-root',
      (value: any) => value.state.final_state.repository_root.locator_root.push('state-root'),
    ],
    [
      'nonproduction-transaction-class',
      (value: any) => (value.state.created[0].object_class = 'transaction'),
    ],
    [
      'lineage-head-mismatch',
      (value: any) => (value.product.continuation.lineage_head_object_identity = 'other-lineage'),
    ],
    [
      'base-scope-mismatch',
      (value: any) => (value.product.continuation.base_scope_digest = 'e'.repeat(64)),
    ],
    [
      'candidate-to-candidate-predecessor',
      (value: any) => {
        const record = value.state.created.find(
          ({ physical_artifact_id }: any) =>
            physical_artifact_id === 'candidate-continuation-physical',
        );
        record.predecessor_identity = 'candidate-object-v1';
      },
    ],
    [
      'intent-to-intent-predecessor',
      (value: any) => {
        const record = value.state.created.find(
          ({ physical_artifact_id }: any) =>
            physical_artifact_id === 'intent-continuation-physical',
        );
        record.predecessor_identity = 'intent-object-v1';
      },
    ],
    [
      'fabricated-second-normal-lineage-head',
      (value: any) => {
        const record = clone(
          value.state.created.find(
            ({ physical_artifact_id }: any) =>
              physical_artifact_id === 'lineage-head-bootstrap-physical',
          ),
        );
        record.physical_artifact_id = 'lineage-head-fabricated-physical';
        record.object_identity = 'lineage-object-fabricated';
        value.state.created.push(record);
        value.cleanup.deleted_physical_artifact_ids.push(record.physical_artifact_id);
      },
    ],
    ['stale-scope-not-enumerated', (value: any) => delete value.state.final_state.stale],
    [
      'stale-release-before-head-advance',
      (value: any) => (value.product.stale.authorized_stale_run.stale_release_at = 860),
    ],
    [
      'stale-follow-on-authorized',
      (value: any) => (value.product.stale.unauthorized_follow_on_run.authorization_matches = true),
    ],
    [
      'stale-follow-on-completes-before-owner',
      (value: any) => {
        value.product.stale.unauthorized_follow_on_run.workflow_started_at = 900;
        value.product.stale.unauthorized_follow_on_run.completed_at = 910;
      },
    ],
    [
      'proof-marker-arbitrary-digest',
      (value: any) => {
        value.proof_control.normal.ready.body_sha256 = 'e'.repeat(64);
        value.proof_control.normal.ready.readback_body_sha256 = 'e'.repeat(64);
      },
    ],
    [
      'proof-marker-coordinate-mismatch',
      (value: any) => (value.proof_control.normal.ready.pr_number = '1002'),
    ],
    [
      'proof-release-predecessor-mismatch',
      (value: any) => (value.proof_control.normal.release.predecessor_comment_id = '8101'),
    ],
    [
      'proof-cleanup-comment-mismatch',
      (value: any) =>
        (value.proof_control.normal.cleanup_receipt.receipt.comment_outcomes[1].comment_id =
          '8102'),
    ],
    [
      'bootstrap-marker-retained',
      (value: any) =>
        (value.cleanup.terminal_resources.product_sticky.marker =
          value.product.bootstrap.sticky_marker),
    ],
    [
      'key-removed-before-readback',
      (value: any) => (value.cleanup.state_key_removed_after_final_readback = false),
    ],
    [
      'unknown-delete-outcome',
      (value: any) =>
        (value.cleanup.terminal_resources.authorization_variable.terminal_class = 'unknown'),
    ],
    [
      'follow-on-still-running',
      (value: any) =>
        (value.cleanup.terminal_resources.workflow_runs.all_follow_on_runs_terminal = false),
    ],
    [
      'stale-state-mutation',
      (value: any) =>
        (value.product.stale.authorized_stale_run.candidate_persisted_after_revalidation = true),
    ],
    [
      'stale-sticky-mutation',
      (value: any) =>
        (value.product.stale.authorized_stale_run.sticky_mutated_after_revalidation = true),
    ],
    [
      'provider-canary-missing',
      (value: any) => {
        const provider = value.canaries.credential_by_sink.find(
          ({ sink }: any) => sink === 'provider',
        );
        provider.observed_present = false;
      },
    ],
    [
      'canary-sink-credential-mismatch',
      (value: any) => {
        const provider = value.canaries.credential_by_sink.find(
          ({ sink }: any) => sink === 'provider',
        );
        provider.authorized_credential = 'AGENTIC_PR_REVIEW_STATE_KEY';
      },
    ],
    ['unreviewed-narrative', (value: any) => (value.product.narrative = 'run 9002 accepted state')],
  ])('rejects host evidence mutation: %s', (_name, mutate) => {
    const candidate = clone(hostTemplate);
    mutate(candidate);
    expect(() => projectTrustedProofEvidence(candidate)).toThrow(/APR_R4_E3_EVIDENCE_INVALID/u);
  });

  test('admits repeated exact-name families and repeated digests with unique physical IDs', () => {
    const candidate = clone(hostTemplate);
    const candidates = candidate.state.created.filter(
      ({ object_class }: any) => object_class === 'candidate',
    );
    expect(candidates[0].opaque_name).toBe(candidates[1].opaque_name);
    expect(candidates[0].artifact_digest).toBe(candidates[1].artifact_digest);
    expect(projectTrustedProofEvidence(candidate)).toEqual(expectedPublic);
  });

  test.each([
    ['direct-artifact-link', (value: any) => (value.state_outcomes.artifact_id = 'artifact-1')],
    ['run-acceptance-link', (value: any) => (value.state_outcomes.run_id = '9001')],
    [
      'shared-timestamp',
      (value: any) =>
        (value.state_outcomes.observed_at = value.scheduling.barrier_to_queue_delay_ms),
    ],
    ['phase-key', (value: any) => (value.cleanup.phase_key = 'continuation')],
    ['ordinal', (value: any) => (value.publication.ordinal = 2)],
    ['narrative', (value: any) => (value.publication.narrative = 'run 9002 accepted successor')],
    ['producing-run', (value: any) => (value.publication.producing_run_id = '9002')],
    ['ciphertext', (value: any) => (value.cleanup.ciphertext_sha256 = '1'.repeat(64))],
  ])('rejects relational or protected public data: %s', (_name, mutate) => {
    const candidate = clone(expectedPublic);
    mutate(candidate);
    expect(() => assertPublicSafeEvidence(candidate)).toThrow(/APR_R4_E3_EVIDENCE_INVALID/u);
  });

  test('rejects noncanonical, duplicate-key, and oversized host input without echoing it', () => {
    const noncanonical = canonicalInput(hostTemplate);
    fs.writeFileSync(noncanonical, `${JSON.stringify(hostTemplate, null, 2)}\n`);
    const duplicate = canonicalInput(hostTemplate);
    fs.writeFileSync(
      duplicate,
      `${JSON.stringify(hostTemplate).replace('{', '{"kind":"duplicate",')}\n`,
    );
    const oversized = canonicalInput(hostTemplate);
    fs.writeFileSync(oversized, `${'x'.repeat(1024 * 1024 + 1)}\n`);
    for (const pathname of [noncanonical, duplicate, oversized]) {
      const result = spawnSync(process.execPath, [script, pathname], {
        cwd: root,
        encoding: 'utf8',
      });
      expect(result.status).not.toBe(0);
      expect(result.stdout).toBe('');
      expect(result.stderr).toMatch(/^APR_R4_E3_EVIDENCE_INVALID /u);
      expect(result.stderr).not.toContain('candidate-bootstrap');
    }
  });

  test('keeps the projector offline and mutation-free', () => {
    const source = fs.readFileSync(script, 'utf8');
    for (const forbidden of [
      'node:child_process',
      'node:http',
      'node:https',
      'fetch(',
      'gh api',
      'git push',
      'writeFile',
      'unlink',
      'rmSync',
    ]) {
      expect(source).not.toContain(forbidden);
    }
  });
});
