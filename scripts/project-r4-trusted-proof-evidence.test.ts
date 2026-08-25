import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, test } from 'vitest';
import { verifyCapturedFiles } from './assemble-r4-trusted-proof-evidence.mjs';
import {
  assembleTrustedProofEvidence,
  assertPublicSafeEvidence,
  canonicalJson,
  cleanupPhases,
  generateCleanupPlan,
  projectTrustedProofEvidence,
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

function syntheticAssembly() {
  const candidate = copy(host);
  const roleById = new Map(
    candidate.inventories.expected_success.map((record: any) => [record.artifact_id, record.role]),
  );
  const captureManifest = {
    kind: 'apr-r4-e3-capture-manifest-v1',
    repository_id: candidate.identities.repository_id,
    repository: candidate.identities.repository,
    operation_ids: candidate.identities.operation_ids,
    source_map_sha256: sha256(canonicalJson(candidate.source_map)),
    destination_identity_sha256: candidate.restricted_package.destination_identity_sha256,
    sources: [
      {
        source_id: 'runs:page:1',
        route: '/repos/SolusQuest/agentic-pr-review/actions/runs?per_page=100',
        page: 1,
        status: 200,
        body_path: 'source-0001.json',
        body_sha256: '1'.repeat(64),
        body_size: '3',
        safe_headers_sha256: '2'.repeat(64),
        request_started_unix_milliseconds: 1,
        response_received_unix_milliseconds: 2,
        next_route: null,
      },
    ],
    artifacts: candidate.inventories.observed_cleanup.map((record: any) => ({
      artifact_id: record.artifact_id,
      artifact_name: `artifact-${record.artifact_id}`,
      expected_role: roleById.get(record.artifact_id) ?? 'internal-record',
      scope: record.scope,
      opaque_name: `opaque-${record.artifact_id}`,
      producing_run_id: '9001',
      producing_run_attempt: '1',
      download_route: `/repos/SolusQuest/agentic-pr-review/actions/artifacts/${record.artifact_id}/zip`,
      download_safe_headers_sha256: '7'.repeat(64),
      download_request_started_unix_milliseconds: 3,
      download_response_received_unix_milliseconds: 4,
      archive_path: `artifact-${record.artifact_id}.zip`,
      archive_sha256: '3'.repeat(64),
      archive_size: '100',
      encrypted_object_path: `artifact-${record.artifact_id}.bin`,
      encrypted_object_sha256: '4'.repeat(64),
      encrypted_object_size: '50',
    })),
    finalized: true,
  };
  const captureManifestSha256 = sha256(canonicalJson(captureManifest));
  const oracleResult = {
    kind: 'apr-r4-e3-production-codec-oracle-result-v1',
    capture_manifest_sha256: captureManifestSha256,
    exact_seven_success: true,
    recovery_only: false,
    records: candidate.inventories.observed_cleanup.map((record: any) => ({
      artifact_id: record.artifact_id,
      role: roleById.get(record.artifact_id) ?? 'internal-record',
      scope: record.scope,
      object_class: record.object_class,
      object_identity: '5'.repeat(64),
      producing_run_identity: '9001',
      producing_run_attempt: '1',
      payload_sha256: '6'.repeat(64),
    })),
  };
  const oracleResultSha256 = sha256(canonicalJson(oracleResult));
  candidate.restricted_package.capture_manifest_sha256 = captureManifestSha256;
  candidate.restricted_package.oracle_result_sha256 = oracleResultSha256;
  return {
    host: candidate,
    captureManifest,
    captureManifestSha256,
    oracleResult,
    oracleResultSha256,
    credentialCopiesAbsent: true,
  };
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
    const manifest = {
      sources: [
        {
          body_path: path.basename(sourcePath),
          body_size: String(source.length),
          body_sha256: sha256(source),
        },
      ],
      artifacts: [
        {
          archive_path: path.basename(archivePath),
          archive_size: String(archive.length),
          archive_sha256: sha256(archive),
          encrypted_object_path: path.basename(objectPath),
          encrypted_object_size: String(encrypted.length),
          encrypted_object_sha256: sha256(encrypted),
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
    const assembled = assembleTrustedProofEvidence(syntheticAssembly());
    expect(assembled.publicEvidence).toEqual(expectedPublic);
  });

  test.each([
    [
      'missing captured object',
      (value: any) => {
        value.captureManifest.artifacts.pop();
        value.captureManifestSha256 = sha256(canonicalJson(value.captureManifest));
        value.host.restricted_package.capture_manifest_sha256 = value.captureManifestSha256;
        value.oracleResult.capture_manifest_sha256 = value.captureManifestSha256;
        value.oracleResultSha256 = sha256(canonicalJson(value.oracleResult));
        value.host.restricted_package.oracle_result_sha256 = value.oracleResultSha256;
      },
    ],
    [
      'swapped oracle role',
      (value: any) => {
        value.oracleResult.records[0].role = 'normal-lineage-head';
        value.oracleResultSha256 = sha256(canonicalJson(value.oracleResult));
        value.host.restricted_package.oracle_result_sha256 = value.oracleResultSha256;
      },
    ],
    [
      'capture digest mismatch',
      (value: any) => {
        value.captureManifestSha256 = '9'.repeat(64);
      },
    ],
    [
      'retained credential copy',
      (value: any) => {
        value.credentialCopiesAbsent = false;
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
      'approval observed after job start',
      (value: any) => {
        value.approval_transitions.bootstrap.approval.observation.response_received = 99;
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
    expect(generated.plan.targets.state_artifact_ids).toContain('1016');
    candidate.cleanup.plan_sha256 = generated.digest;
    candidate.authorizations.cleanup.plan_sha256 = generated.digest;
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
    expect(first.plan.targets.state_artifact_ids).toHaveLength(15);
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
