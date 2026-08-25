import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import Ajv from 'ajv';
import { describe, expect, test } from 'vitest';
import { pinnedFileIdentity, verifyCapturedFiles } from './assemble-r4-trusted-proof-evidence.mjs';
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

function syntheticAssembly(input = host) {
  const candidate = copy(input);
  const roleById = new Map(
    candidate.inventories.expected_success.map((record: any) => [record.artifact_id, record.role]),
  );
  const metadataRunIds = [
    ...new Set(
      candidate.inventories.observed_cleanup.map((record: any) => record.producing_run_id),
    ),
  ];
  const sourceDigests = new Map<string, string>();
  for (const phase of ['cleanup', 'execution', 'setup']) {
    sourceDigests.set(
      `authorization-${phase}:page:1`,
      candidate.authorizations[phase].source.capture_body_sha256,
    );
  }
  sourceDigests.set(
    'environment-protection:page:1',
    candidate.environment.protection_snapshot.readback_sha256,
  );
  for (const [phase, transition] of Object.entries<any>(candidate.approval_transitions)) {
    for (const sourceId of [
      `${transition.approval.source_id}:page:1`,
      `${transition.pending.source_id}:page:1`,
      `${phase}-jobs-attempt-1:page:1`,
    ]) {
      sourceDigests.set(sourceId, sha256(Buffer.from(`capture-${sourceId}`, 'utf8')));
    }
  }
  for (const sourceId of ['concurrency-normal:page:1', 'concurrency-stale:page:1']) {
    sourceDigests.set(sourceId, sha256(Buffer.from(`capture-${sourceId}`, 'utf8')));
  }
  for (const family of Object.values<any>(candidate.proof_control)) {
    for (const comment of family.comments) {
      sourceDigests.set(`proof-control-${comment.comment_id}:page:1`, comment.capture_body_sha256);
    }
  }
  for (const sourceId of ['cleanup-readbacks:page:1', 'live-canaries:page:1']) {
    sourceDigests.set(sourceId, sha256(Buffer.from(`capture-${sourceId}`, 'utf8')));
  }
  const evidenceSources = [...sourceDigests].map(([sourceId, bodySha256], index) => ({
    source_id: sourceId,
    route: `/repos/SolusQuest/agentic-pr-review/evidence/${encodeURIComponent(sourceId)}`,
    page: 1,
    status: 200,
    body_path: `source-${String(metadataRunIds.length + index + 1).padStart(4, '0')}.json`,
    body_sha256: bodySha256,
    body_size: '3',
    body_file_identity: '8'.repeat(64),
    safe_headers_sha256: '2'.repeat(64),
    request_started_unix_milliseconds: 1,
    response_received_unix_milliseconds: 2,
    next_route: null,
  }));
  const captureManifest = {
    kind: 'apr-r4-e3-capture-manifest-v1',
    repository_id: candidate.identities.repository_id,
    repository: candidate.identities.repository,
    operation_ids: candidate.identities.operation_ids,
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
    records: candidate.inventories.observed_cleanup.map((record: any) => ({
      artifact_id: record.artifact_id,
      role: roleById.get(record.artifact_id) ?? 'internal-record',
      scope: record.scope,
      object_class: record.object_class,
      object_identity: '5'.repeat(64),
      producing_run_identity: record.producing_run_id,
      producing_run_attempt: '1',
      payload_sha256: '6'.repeat(64),
    })),
  };
  const oracleResultSha256 = sha256(canonicalJson(oracleResult));
  candidate.restricted_package.capture_manifest_sha256 = captureManifestSha256;
  candidate.restricted_package.oracle_result_sha256 = oracleResultSha256;
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
          {
            source_id: 'trusted-proof-payload-receipt-v2',
            sha256: '3556512b430867b41086938f55b6553f5f289fae3a1bb3a62d5755a01f9551e1',
          },
        ],
      ],
      [
        '/authorizations',
        ['cleanup', 'execution', 'setup'].map((phase) =>
          captureReference(`authorization-${phase}:page:1`),
        ),
      ],
      [
        '/environment',
        [
          captureReference('environment-protection:page:1'),
          {
            source_id: 'environment-ui-attestation',
            sha256: candidate.environment.ui_attestation.capture_sha256,
          },
        ].sort((a, b) => a.source_id.localeCompare(b.source_id)),
      ],
      [
        '/approval_transitions',
        Object.entries<any>(candidate.approval_transitions)
          .flatMap(([phase, transition]) => [
            captureReference(`${transition.approval.source_id}:page:1`),
            captureReference(`${transition.pending.source_id}:page:1`),
            captureReference(`${phase}-jobs-attempt-1:page:1`),
          ])
          .sort((a, b) => a.source_id.localeCompare(b.source_id)),
      ],
      [
        '/concurrency',
        [
          captureReference('concurrency-normal:page:1'),
          captureReference('concurrency-stale:page:1'),
        ],
      ],
      [
        '/proof_control',
        Object.values<any>(candidate.proof_control)
          .flatMap((family) => family.comments)
          .map((comment) => captureReference(`proof-control-${comment.comment_id}:page:1`))
          .sort((a, b) => a.source_id.localeCompare(b.source_id)),
      ],
      [
        '/inventories',
        [{ source_id: 'production-codec-oracle-result', sha256: oracleResultSha256 }],
      ],
      [
        '/cleanup',
        [
          { source_id: 'cleanup-plan', sha256: candidate.cleanup.plan_sha256 },
          captureReference('cleanup-readbacks:page:1'),
        ],
      ],
      ['/canaries/live', [captureReference('live-canaries:page:1')]],
      [
        '/canaries/cross_sink',
        [
          {
            source_id: 'trusted-proof-payload-receipt-v2',
            sha256: '3556512b430867b41086938f55b6553f5f289fae3a1bb3a62d5755a01f9551e1',
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
        ],
      ],
    ]);
    return referencesByPointer.get(pointer)!;
  };
  const sourceBundle = {
    kind: 'apr-r4-e3-closed-source-bundle-v1',
    source_map_sha256: sha256(canonicalJson(sourceMap)),
    documents: sourceMap.entries.map((entry: any) => {
      const value = entry.destination_pointer
        .split('/')
        .slice(1)
        .reduce((current: any, segment: string) => current[segment], candidate);
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
        value_sha256: sha256(canonicalJson(value)),
        value,
      };
    }),
  };
  return {
    sourceMap,
    sourceBundle,
    captureManifest,
    captureManifestSha256,
    oracleResult,
    oracleResultSha256,
    credentialCopiesAbsent: true,
  };
}

function refreshSourceDocument(assembly: any, pointer: string) {
  const document = assembly.sourceBundle.documents.find(
    (candidate: any) => candidate.destination_pointer === pointer,
  );
  document.value_sha256 = sha256(canonicalJson(document.value));
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
    const assembled = assembleTrustedProofEvidence(syntheticAssembly());
    expect(assembled.publicEvidence).toEqual(expectedPublic);
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
        const restricted = value.sourceBundle.documents.find(
          (candidate: any) => candidate.destination_pointer === '/restricted_package',
        ).value;
        restricted.capture_manifest_sha256 = value.captureManifestSha256;
        value.oracleResult.capture_manifest_sha256 = value.captureManifestSha256;
        value.oracleResultSha256 = sha256(canonicalJson(value.oracleResult));
        restricted.oracle_result_sha256 = value.oracleResultSha256;
        refreshSourceDocument(value, '/restricted_package');
      },
    ],
    [
      'swapped oracle role',
      (value: any) => {
        value.oracleResult.records[0].role = 'normal-lineage-head';
        value.oracleResultSha256 = sha256(canonicalJson(value.oracleResult));
        const restricted = value.sourceBundle.documents.find(
          (candidate: any) => candidate.destination_pointer === '/restricted_package',
        ).value;
        restricted.oracle_result_sha256 = value.oracleResultSha256;
        refreshSourceDocument(value, '/restricted_package');
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
    [
      'unbound source document',
      (value: any) => {
        value.sourceBundle.documents[0].value.payload_sha256 = 'f'.repeat(64);
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

  test('allows approval-history readback after the protected job source timestamp', () => {
    const candidate = copy(host);
    candidate.approval_transitions.bootstrap.approval.observation = {
      request_started: 15,
      response_received: 16,
    };
    expect(validateHostEvidence(candidate).projectionEligible).toBe(true);
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
