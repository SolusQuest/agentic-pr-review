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
      'candidate-bootstrap',
      'acceptance-bootstrap',
      'apr-r4-root',
      'transaction-continuation',
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
        (value.product.continuation.predecessor_acceptance_receipt_id = 'other-acceptance'),
    ],
    ['preexisting-root', (value: any) => value.state.pre_state.inventory.push('existing-root')],
    [
      'incomplete-root-pagination',
      (value: any) => (value.state.pre_state.root_pagination_complete = false),
    ],
    ['missing-deletion', (value: any) => value.cleanup.deleted_state_ids.pop()],
    [
      'duplicate-created-id',
      (value: any) => (value.state.created[1].artifact_id = value.state.created[0].artifact_id),
    ],
    ['surviving-root', (value: any) => value.state.final_state.inventory.push('state-root')],
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
      (value: any) => (value.cleanup.terminal_resources.workflow_runs.all_terminal = false),
    ],
    ['stale-state-mutation', (value: any) => (value.product.stale.state_mutated = true)],
    ['stale-sticky-mutation', (value: any) => (value.product.stale.sticky_mutated = true)],
    ['unreviewed-narrative', (value: any) => (value.product.narrative = 'run 9002 accepted state')],
  ])('rejects host evidence mutation: %s', (_name, mutate) => {
    const candidate = clone(hostTemplate);
    mutate(candidate);
    expect(() => projectTrustedProofEvidence(candidate)).toThrow(/APR_R4_E3_EVIDENCE_INVALID/u);
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
