import crypto from 'node:crypto';
import fs from 'node:fs';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, test, vi } from 'vitest';
import {
  canonicalJson,
  sha256 as contractSha256,
  validateEnrollmentExecutionAuthorityPackage,
} from './r4-trusted-proof-contract.mjs';
import {
  ENROLLMENT_CONTRACT,
  bindEnrollmentRecord,
  canonicalAuthorizationManifest,
  createGhTransport,
  executeDurableEnrollmentPhase,
  prepareEnrollmentRecord,
  readEnrollmentJournal,
  recordSha256,
  recoveryEndpointForDirectory,
  runEnrollmentCli,
  validateEnrollmentRecord,
} from './execute-r4-trusted-proof-enrollment.mjs';
import { validateEnrollmentObservationReceipt } from './materialize-r4-enrollment-observation.mjs';

const sha = (letter: string) => letter.repeat(40);
const digest = (value: string) => crypto.createHash('sha256').update(value).digest('hex');

function gitObjectSha(type: string, value: string) {
  const body = Buffer.from(value, 'utf8');
  return crypto
    .createHash('sha1')
    .update(Buffer.from(`${type} ${body.length}\0`, 'utf8'))
    .update(body)
    .digest('hex');
}

function commitSha(tree: string, parents: string[], metadata: { date: string; message: string }) {
  const identity = ENROLLMENT_CONTRACT.commitMetadata.identity;
  const timestamp = Math.floor(Date.parse(metadata.date) / 1000);
  return gitObjectSha(
    'commit',
    [
      `tree ${tree}`,
      ...parents.map((parent) => `parent ${parent}`),
      `author ${identity.name} <${identity.email}> ${timestamp} +0000`,
      `committer ${identity.name} <${identity.email}> ${timestamp} +0000`,
      '',
      metadata.message,
    ].join('\n'),
  );
}

function record() {
  const workflow = sha('a');
  const workflowTree = sha('b');
  const initialTree = sha('c');
  const advancedTree = sha('d');
  const initialBlob = gitObjectSha('blob', ENROLLMENT_CONTRACT.initialCanary);
  const advancedBlob = gitObjectSha('blob', ENROLLMENT_CONTRACT.advancedCanary);
  const normalCommit = commitSha(
    initialTree,
    [workflow, ENROLLMENT_CONTRACT.normal.oldHead],
    ENROLLMENT_CONTRACT.commitMetadata.normal,
  );
  const staleCommit = commitSha(
    initialTree,
    [workflow, ENROLLMENT_CONTRACT.stale.oldHead],
    ENROLLMENT_CONTRACT.commitMetadata.stale,
  );
  const advancedCommit = commitSha(
    advancedTree,
    [staleCommit],
    ENROLLMENT_CONTRACT.commitMetadata.advance,
  );
  const input = {
    repository_id: '42',
    payload_sha256: digest('payload'),
    materialized: {
      merge_sha: workflow,
      merge_tree: workflowTree,
      normal: {
        prior_head: ENROLLMENT_CONTRACT.normal.oldHead,
        head: normalCommit,
        tree: initialTree,
        parents: [workflow, ENROLLMENT_CONTRACT.normal.oldHead],
      },
      stale: {
        prior_head: ENROLLMENT_CONTRACT.stale.oldHead,
        head: staleCommit,
        tree: initialTree,
        parents: [workflow, ENROLLMENT_CONTRACT.stale.oldHead],
        advanced_head: advancedCommit,
        advanced_tree: advancedTree,
        advanced_parents: [staleCommit],
      },
      canary: {
        path: ENROLLMENT_CONTRACT.canaryPath,
        initial_blob: initialBlob,
        advanced_blob: advancedBlob,
      },
    },
  };
  const prepared = prepareEnrollmentRecord(input);
  const authority = validateEnrollmentExecutionAuthorityPackage(
    executionAuthorityPackage(prepared.sha256),
  );
  return bindEnrollmentRecord({
    ...input,
    execution_authority: authority,
  });
}

function pullBinding(value: ReturnType<typeof record>, fixture: any, head: string) {
  return JSON.stringify({
    repository: ENROLLMENT_CONTRACT.repository,
    number: fixture.pr_number,
    state: 'open',
    draft: false,
    base_ref: 'main',
    base_sha: value.coordinates.workflow_sha,
    head_ref: fixture.ref.slice('refs/heads/'.length),
    head_sha: head,
  });
}

function observations(value: ReturnType<typeof record>) {
  return value.role_plan.map((plan: any, index: number) => ({
    name: plan.name,
    run_id: String(9001 + index),
  }));
}

function fakeObservationMaterializer(value: ReturnType<typeof record>) {
  const destinationIdentity = digest('observation-destination');
  const source = (role: string, runId: string, kind: string, page = 1) => ({
    source_id:
      kind === 'run'
        ? `enrollment-${role}-run-${runId}:page:1`
        : kind === 'approvals'
          ? `enrollment-${role}-approvals-run-${runId}:page:1`
          : kind === 'pull'
            ? `enrollment-${role}-pull-run-${runId}:page:1`
            : `enrollment-${role}-${kind}-run-${runId}:page:${page}`,
    phase: `enrollment-${role}-${kind === 'run' ? 'terminal' : kind === 'approvals' ? 'approval' : kind}`,
    fragment_sha256: digest(`${role}:${kind}:${page}:fragment`),
    fragment_physical_identity_sha256: digest(`${role}:${kind}:${page}:fragment-identity`),
    body_sha256: digest(`${role}:${kind}:${page}:body`),
    body_size: '1',
    body_physical_identity_sha256: digest(`${role}:${kind}:${page}:body-identity`),
  });
  const validate = (receipt: any, expected: any) =>
    validateEnrollmentObservationReceipt(receipt, {
      authority: value.authority,
      destination_identity_sha256: destinationIdentity,
      ...expected,
    });
  return {
    destination_identity_sha256: destinationIdentity,
    validate,
    materialize(expected: any) {
      const protectedRole = expected.role !== 'stale-follow-on';
      const sources = [
        source(expected.role, expected.run_id, 'run'),
        source(expected.role, expected.run_id, 'jobs'),
        source(expected.role, expected.run_id, 'discovery'),
        source(expected.role, expected.run_id, 'pull'),
        ...(protectedRole
          ? [
              source(expected.role, expected.run_id, 'pending'),
              source(expected.role, expected.run_id, 'approvals'),
            ]
          : []),
      ];
      return validate(
        {
          kind: 'apr-r4-e2p-enrollment-role-observation-v1',
          execution_authorization_sha256: value.authority.execution_authorization_sha256,
          destination_identity_sha256: destinationIdentity,
          materializer_source_sha256: value.authority.phase_materializer_source_sha256,
          materializer_build_sha256: value.authority.phase_materializer_build_sha256,
          operation_id: expected.operation_id,
          role: expected.role,
          run_id: expected.run_id,
          run_attempt: '1',
          expected_event: expected.expected_event,
          expected_run_head_sha: expected.expected_run_head_sha,
          sources,
          finalized: true,
        },
        expected,
      );
    },
  };
}

function executionAuthorityPackage(enrollmentRecordSha256: string) {
  const execution = JSON.parse(
    fs.readFileSync(
      path.resolve(
        import.meta.dirname,
        '..',
        'runtime/tests/fixtures/action-host/trusted-proof/authorizations/execution.json',
      ),
      'utf8',
    ),
  );
  const { source, kind: executionKind, phase: executionPhase, ...executionContract } = execution;
  expect(executionKind).toBe('apr-r4-e3-execution-authorization-v1');
  expect(executionPhase).toBe('execution');
  const authorization = {
    kind: 'apr-r4-e2p-enrollment-execution-authorization-v1',
    phase: 'execution',
    enrollment_record_sha256: enrollmentRecordSha256,
    execution: executionContract,
  };
  const marker = {
    contract: 'apr-r4-e3-maintainer-authorization-v1',
    phase: 'execution',
    repository: execution.coordinates.repository,
    issue_number: Number(source.issue_number),
    authorization,
  };
  const comment = {
    id: Number(source.comment_id),
    body: `<!-- apr-r4-e3-authorization ${JSON.stringify(marker)} -->`,
    user: { id: Number(source.author_id), login: 'maintainer' },
  };
  const permission = {
    permission: source.author_permission,
    user: { id: Number(source.author_id), login: 'maintainer' },
  };
  const bodies: Array<{ source_id: string; text: string }> = [];
  const sources: any[] = [];
  for (const scope of ['normal', 'stale']) {
    for (const [family, value, route] of [
      [
        'comment',
        comment,
        `/repos/${execution.coordinates.repository}/issues/comments/${source.comment_id}`,
      ],
      [
        'permission',
        permission,
        `/repos/${execution.coordinates.repository}/collaborators/maintainer/permission`,
      ],
    ] as const) {
      const sourceId = `authorization-execution-${family}-${scope}-${family === 'comment' ? source.comment_id : 'maintainer'}:page:1`;
      const text = canonicalJson(value);
      bodies.push({ source_id: sourceId, text });
      sources.push({
        source_id: sourceId,
        operation_id: execution.operation_ids[scope === 'normal' ? 0 : 1],
        phase: `baseline-${scope}`,
        route,
        page: 1,
        status: 200,
        body_path: `source-${String(sources.length + 1).padStart(4, '0')}.json`,
        body_sha256: contractSha256(Buffer.from(text, 'utf8')),
        body_size: String(Buffer.byteLength(text, 'utf8')),
        body_file_identity: '8'.repeat(64),
        safe_headers_sha256: '2'.repeat(64),
        request_started_unix_milliseconds: source.observation.request_started,
        response_received_unix_milliseconds: source.observation.response_received,
        next_route: null,
      });
    }
  }
  execution.source.capture_body_sha256 = sources[0].body_sha256;
  execution.source.body_sha256 = contractSha256(Buffer.from(comment.body, 'utf8'));
  execution.source.readback_sha256 = execution.source.body_sha256;
  return {
    kind: 'apr-r4-e2p-enrollment-execution-authority-v1',
    identities: { ...execution.coordinates },
    sources,
    captured_source_bodies: bodies,
    execution_authorization: execution,
  };
}

type FakeOptions = {
  uncertainTarget?: string;
  uncertainMode?: 'before' | 'after' | 'drift';
  definitiveTarget?: string;
  definitiveStatus?: number;
  definitiveMode?: 'before' | 'after';
  readFailureAfterMutationTarget?: string;
};

function fakeTransport(value: ReturnType<typeof record>, options: FakeOptions = {}) {
  const state = new Map<string, string>();
  const calls: Array<{ kind: string; method?: string; target: string; body?: any }> = [];
  state.set(
    `commit-tree:${value.coordinates.workflow_sha}`,
    JSON.stringify({
      commit_sha: value.coordinates.workflow_sha,
      tree_sha: value.coordinates.workflow_tree_sha,
    }),
  );
  state.set(
    `object:tree:${value.coordinates.workflow_tree_sha}`,
    value.coordinates.workflow_tree_sha,
  );
  state.set(`ref:${value.fixtures.normal.ref}`, value.fixtures.normal.old_head);
  state.set(`ref:${value.fixtures.stale.ref}`, value.fixtures.stale.old_head);
  let uncertainty = options.uncertainTarget;
  let readFailureAfterMutation = options.readFailureAfterMutationTarget;

  const apply = (method: string, target: string, body: any) => {
    if (target.startsWith('object:')) state.set(target, target.split(':').at(-1)!);
    if (target.startsWith('ref:')) {
      state.set(target, body.sha);
      const fixture = [value.fixtures.normal, value.fixtures.stale].find(
        (candidate) => `ref:${candidate.ref}` === target,
      );
      if (fixture) state.set(`pull:${fixture.pr_number}`, pullBinding(value, fixture, body.sha));
    }
    if (target.startsWith('variable:')) {
      if (method === 'DELETE') state.delete(target);
      else state.set(target, body.value);
    }
  };

  return {
    calls,
    state,
    async read(target: string) {
      calls.push({ kind: 'read', target });
      if (
        readFailureAfterMutation === target &&
        calls.some((call) => call.kind === 'mutate' && call.target === target)
      ) {
        readFailureAfterMutation = undefined;
        throw new Error('synthetic post-mutation read failure');
      }
      return state.get(target) ?? null;
    },
    async mutate(method: string, target: string, body: any) {
      calls.push({ kind: 'mutate', method, target, body });
      if (options.definitiveTarget === target) {
        if (options.definitiveMode === 'after') apply(method, target, body);
        const error: any = new Error('definitive');
        error.status = options.definitiveStatus ?? 403;
        throw error;
      }
      if (uncertainty === target) {
        uncertainty = undefined;
        if (options.uncertainMode === 'after') apply(method, target, body);
        if (options.uncertainMode === 'drift') state.set(target, sha('f'));
        const error: any = new Error('uncertain');
        error.uncertain = true;
        throw error;
      }
      apply(method, target, body);
      if (target.startsWith('object:')) return target.split(':').at(-1)!;
      if (target.startsWith('ref:')) return body.sha;
      return null;
    },
  };
}

async function runThrough(
  value: ReturnType<typeof record>,
  transport: ReturnType<typeof fakeTransport>,
  directory: string,
  finalPhase: string,
) {
  const readbacks = observations(value);
  const observationMaterializer = fakeObservationMaterializer(value);
  for (const phase of ENROLLMENT_CONTRACT.phases) {
    await executeDurableEnrollmentPhase({
      directory,
      record: value,
      phase,
      transport,
      observations: readbacks,
      observationMaterializer,
    });
    if (phase === finalPhase) return;
    if (phase === 'authorize-stale') {
      // The producer journal, not this Node executor, owns the stale ref's
      // advancement between authorization and the follow-on observation phase.
      transport.state.set(`ref:${value.fixtures.stale.ref}`, value.fixtures.stale.advanced_head);
      transport.state.set(
        `pull:${value.fixtures.stale.pr_number}`,
        pullBinding(value, value.fixtures.stale, value.fixtures.stale.advanced_head),
      );
    }
  }
}

function writeJournal(directory: string, recordValue: ReturnType<typeof record>, fragments: any[]) {
  let previous = '0'.repeat(64);
  for (const [index, source] of fragments.entries()) {
    const fragment = {
      ...source,
      ordinal: index + 1,
      previous_sha256: previous,
      record_sha256: recordSha256(recordValue),
    };
    const text = `${JSON.stringify(fragment)}\n`;
    const safeAction = fragment.action.replace(/[^a-z0-9-]/gu, '-');
    fs.writeFileSync(
      path.join(
        directory,
        `${String(fragment.ordinal).padStart(6, '0')}-${fragment.kind}-${safeAction}.json`,
      ),
      text,
      'utf8',
    );
    previous = digest(text);
  }
}

describe('R4 post-merge enrollment executor', () => {
  test('derives enrollment authority only from paired captured comment and permission readbacks', () => {
    const value = record();
    const prepared = executionAuthorityPackage(value.authority.enrollment_record_sha256);
    const authority = validateEnrollmentExecutionAuthorityPackage(prepared);
    expect(authority.enrollment_record_sha256).toBe(value.authority.enrollment_record_sha256);
    expect(authority.capture_source_sha256).toBe('1'.repeat(64));
    const changed = structuredClone(prepared);
    changed.captured_source_bodies[0].text = '{}\n';
    expect(() => validateEnrollmentExecutionAuthorityPackage(changed)).toThrow(
      /authorization-capture-binding/u,
    );
  });

  test('binds exact B coordinates and only two canonical flat-v2 C values', () => {
    const value = record();
    expect(validateEnrollmentRecord(value)).toBe(true);
    expect(value.kind).not.toBe(ENROLLMENT_CONTRACT.authorizationKind);
    expect(value.fixtures.normal.pr_number).toBe('225');
    expect(value.fixtures.stale.pr_number).toBe('226');
    expect(value.objects.initial_tree.body.base_tree).toBe(value.coordinates.workflow_tree_sha);
    const normal = JSON.parse(value.fixtures.normal.manifest);
    const stale = JSON.parse(value.fixtures.stale.manifest);
    expect(Object.keys(normal)).toEqual([
      'kind',
      'repository_id',
      'repository',
      'pr_number',
      'proof_scope',
      'fixture_head_sha',
      'operation_id',
      'workflow_sha',
      'action_source_sha',
      'payload_source_sha',
      'payload_sha256',
    ]);
    expect(normal.proof_scope).toBe('normal');
    expect(stale.proof_scope).toBe('stale');
    expect(value.fixtures.normal.manifest).toBe(canonicalAuthorizationManifest(normal));
    expect(value.fixtures.stale.manifest).toBe(canonicalAuthorizationManifest(stale));
    expect(value.fixtures.normal.manifest).not.toBe(value.fixtures.stale.manifest);

    const changed = structuredClone(value);
    changed.objects.initial_tree.body.base_tree = value.coordinates.workflow_sha;
    expect(() => validateEnrollmentRecord(changed)).toThrow(/record-canonical/u);
    expect(() =>
      canonicalAuthorizationManifest({
        kind: ENROLLMENT_CONTRACT.authorizationKind,
        coordinates: value.coordinates,
        fixtures: value.fixtures,
      }),
    ).toThrow(/authorization-keys/u);
  });

  test('executes the sole six-phase order with exact readbacks and final variable absence', async () => {
    const value = record();
    const transport = fakeTransport(value);
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-enrollment-test-'));
    try {
      await runThrough(value, transport, directory, 'cleanup');
      const journal = readEnrollmentJournal({ directory, record: value });
      expect(
        journal
          .filter((entry: any) => entry.kind === 'phase-complete')
          .map((entry: any) => entry.phase),
      ).toEqual(ENROLLMENT_CONTRACT.phases);
      const mutations = transport.calls.filter((call) => call.kind === 'mutate');
      expect(
        mutations.filter((call) => call.target.startsWith('variable:')).map((call) => call.method),
      ).toEqual(['POST', 'PATCH', 'DELETE']);
      expect(
        mutations
          .filter((call) => call.target.startsWith('ref:'))
          .every((call) => call.method === 'PATCH' && call.body.force === false),
      ).toBe(true);
      const staleRefresh = mutations.findIndex(
        (call) => call.target === `ref:${value.fixtures.stale.ref}`,
      );
      const normalRefresh = mutations.findIndex(
        (call) => call.target === `ref:${value.fixtures.normal.ref}`,
      );
      expect(staleRefresh).toBeLessThan(normalRefresh);
      expect(
        mutations.filter((call) => call.target === `ref:${value.fixtures.stale.ref}`),
      ).toHaveLength(1);
      expect(journal.some((entry: any) => entry.action === 'advance-stale-ref')).toBe(false);
      expect(transport.state.has(`variable:${ENROLLMENT_CONTRACT.variable}`)).toBe(false);
      expect(transport.state.get(`ref:${value.fixtures.stale.ref}`)).toBe(
        value.fixtures.stale.advanced_head,
      );
      expect(
        journal
          .filter((entry: any) => entry.kind === 'observation')
          .map((entry: any) => entry.action),
      ).toEqual(value.role_plan.map((entry: any) => entry.name));
    } finally {
      fs.rmSync(directory, { recursive: true, force: true });
    }
  }, 15_000);

  test('treats the producer-owned advanced stale ref as an exact readback and never patches it', async () => {
    const value = record();
    const transport = fakeTransport(value);
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-enrollment-producer-advance-'));
    try {
      await runThrough(value, transport, directory, 'authorize-stale');
      await expect(
        executeDurableEnrollmentPhase({
          directory,
          record: value,
          phase: 'advance-stale',
          transport,
          observations: observations(value),
          observationMaterializer: fakeObservationMaterializer(value),
        }),
      ).rejects.toThrow(/readback-read-advanced-stale-ref/u);
      expect(
        transport.calls.filter(
          (call) => call.kind === 'mutate' && call.target === `ref:${value.fixtures.stale.ref}`,
        ),
      ).toHaveLength(1);
    } finally {
      fs.rmSync(directory, { recursive: true, force: true });
    }
  });

  test('reconciles a network-uncertain mutation once and never retries definitive HTTP failure', async () => {
    const value = record();
    const target = `ref:${value.fixtures.normal.ref}`;
    const uncertainTransport = fakeTransport(value, {
      uncertainTarget: target,
      uncertainMode: 'before',
    });
    const uncertainDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-enrollment-uncertain-'));
    try {
      await runThrough(value, uncertainTransport, uncertainDirectory, 'refresh');
      expect(
        uncertainTransport.calls.filter((call) => call.kind === 'mutate' && call.target === target),
      ).toHaveLength(2);
      expect(
        readEnrollmentJournal({ directory: uncertainDirectory, record: value }).some(
          (entry: any) => entry.outcome === 'uncertain-mutation',
        ),
      ).toBe(true);
    } finally {
      fs.rmSync(uncertainDirectory, { recursive: true, force: true });
    }

    const definitiveTransport = fakeTransport(value, {
      definitiveTarget: target,
      definitiveStatus: 422,
    });
    const definitiveDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-enrollment-definite-'));
    try {
      await runThrough(value, definitiveTransport, definitiveDirectory, 'prepare');
      await expect(
        executeDurableEnrollmentPhase({
          directory: definitiveDirectory,
          record: value,
          phase: 'refresh',
          transport: definitiveTransport,
          observations: observations(value),
          observationMaterializer: fakeObservationMaterializer(value),
        }),
      ).rejects.toMatchObject({ status: 422 });
      expect(
        definitiveTransport.calls.filter(
          (call) => call.kind === 'mutate' && call.target === target,
        ),
      ).toHaveLength(1);
    } finally {
      fs.rmSync(definitiveDirectory, { recursive: true, force: true });
    }

    const appliedServerFailureTransport = fakeTransport(value, {
      definitiveTarget: target,
      definitiveStatus: 503,
      definitiveMode: 'after',
      readFailureAfterMutationTarget: target,
    });
    const appliedServerFailureDirectory = fs.mkdtempSync(
      path.join(os.tmpdir(), 'apr-enrollment-server-failure-applied-'),
    );
    try {
      await runThrough(
        value,
        appliedServerFailureTransport,
        appliedServerFailureDirectory,
        'prepare',
      );
      await expect(
        executeDurableEnrollmentPhase({
          directory: appliedServerFailureDirectory,
          record: value,
          phase: 'refresh',
          transport: appliedServerFailureTransport,
          observations: observations(value),
          observationMaterializer: fakeObservationMaterializer(value),
        }),
      ).rejects.toThrow(/read/u);
      expect(
        appliedServerFailureTransport.calls.filter(
          (call) => call.kind === 'mutate' && call.target === target,
        ),
      ).toHaveLength(1);
      await expect(
        executeDurableEnrollmentPhase({
          directory: appliedServerFailureDirectory,
          record: value,
          phase: 'refresh',
          transport: appliedServerFailureTransport,
          observations: observations(value),
          observationMaterializer: fakeObservationMaterializer(value),
        }),
      ).resolves.toMatchObject({ phase: 'refresh', kind: 'phase-complete' });
      expect(
        appliedServerFailureTransport.calls.filter(
          (call) => call.kind === 'mutate' && call.target === target,
        ),
      ).toHaveLength(1);
    } finally {
      fs.rmSync(appliedServerFailureDirectory, { recursive: true, force: true });
    }

    const serverFailureTransport = fakeTransport(value, {
      definitiveTarget: target,
      definitiveStatus: 503,
    });
    const serverFailureDirectory = fs.mkdtempSync(
      path.join(os.tmpdir(), 'apr-enrollment-server-failure-'),
    );
    try {
      await runThrough(value, serverFailureTransport, serverFailureDirectory, 'prepare');
      await expect(
        executeDurableEnrollmentPhase({
          directory: serverFailureDirectory,
          record: value,
          phase: 'refresh',
          transport: serverFailureTransport,
          observations: observations(value),
          observationMaterializer: fakeObservationMaterializer(value),
        }),
      ).rejects.toThrow(/mutation-readback/u);
      expect(
        serverFailureTransport.calls.filter(
          (call) => call.kind === 'mutate' && call.target === target,
        ),
      ).toHaveLength(2);
      expect(
        readEnrollmentJournal({ directory: serverFailureDirectory, record: value }).filter(
          (entry: any) => entry.outcome === 'uncertain-mutation',
        ),
      ).toHaveLength(2);
      await expect(
        executeDurableEnrollmentPhase({
          directory: serverFailureDirectory,
          record: value,
          phase: 'refresh',
          transport: serverFailureTransport,
          observations: observations(value),
          observationMaterializer: fakeObservationMaterializer(value),
        }),
      ).rejects.toThrow(/mutation-retry/u);
      expect(
        serverFailureTransport.calls.filter(
          (call) => call.kind === 'mutate' && call.target === target,
        ),
      ).toHaveLength(2);
      expect(
        serverFailureTransport.calls.some(
          (call) => call.kind === 'mutate' && call.target.startsWith('variable:'),
        ),
      ).toBe(false);
    } finally {
      fs.rmSync(serverFailureDirectory, { recursive: true, force: true });
    }
  }, 15_000);

  test('binds the merged commit to its exact tree before any mutation', async () => {
    const value = record();
    const transport = fakeTransport(value);
    transport.state.set(
      `commit-tree:${value.coordinates.workflow_sha}`,
      JSON.stringify({ commit_sha: value.coordinates.workflow_sha, tree_sha: sha('f') }),
    );
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-enrollment-tree-binding-'));
    try {
      await expect(
        executeDurableEnrollmentPhase({
          directory,
          record: value,
          phase: 'prepare',
          transport,
          observations: [],
          observationMaterializer: fakeObservationMaterializer(value),
        }),
      ).rejects.toThrow(/readback-read-merged-commit-tree/u);
      expect(transport.calls.some((call) => call.kind === 'mutate')).toBe(false);
    } finally {
      fs.rmSync(directory, { recursive: true, force: true });
    }
  });

  test('holds one host-local cross-process phase lease across every remote action', async () => {
    const value = record();
    const base = fakeTransport(value);
    let releaseRead!: () => void;
    let announceRead!: () => void;
    const readReleased = new Promise<void>((resolve) => {
      releaseRead = resolve;
    });
    const readAnnounced = new Promise<void>((resolve) => {
      announceRead = resolve;
    });
    let blocked = false;
    const transport = {
      ...base,
      async read(target: string) {
        if (!blocked && target === `commit-tree:${value.coordinates.workflow_sha}`) {
          blocked = true;
          announceRead();
          await readReleased;
        }
        return base.read(target);
      },
    };
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-enrollment-lock-'));
    try {
      const first = executeDurableEnrollmentPhase({
        directory,
        record: value,
        phase: 'prepare',
        transport,
        observations: [],
        observationMaterializer: fakeObservationMaterializer(value),
      });
      await readAnnounced;
      await expect(
        executeDurableEnrollmentPhase({
          directory,
          record: value,
          phase: 'prepare',
          transport: fakeTransport(value),
          observations: [],
          observationMaterializer: fakeObservationMaterializer(value),
        }),
      ).rejects.toThrow(/phase-lock-busy/u);
      releaseRead();
      await first;
      expect(fs.existsSync(`${path.resolve(directory)}.lock`)).toBe(false);
    } finally {
      releaseRead();
      fs.rmSync(directory, { recursive: true, force: true });
    }
  });

  test('derives one host-local recovery endpoint from the physical parent and full identity hash', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-enrollment-endpoint-'));
    const physical = path.join(root, 'physical');
    const alias = path.join(root, 'alias');
    fs.mkdirSync(physical);
    try {
      const first = recoveryEndpointForDirectory(path.join(physical, 'journal-a'));
      const second = recoveryEndpointForDirectory(path.join(physical, 'journal-b'));
      expect(first).not.toBe(second);
      expect(first).toMatch(/apr-r4-e2p-[a-f0-9]{64}$/u);
      if (process.platform === 'win32') {
        expect(
          recoveryEndpointForDirectory(path.join(physical.toLocaleUpperCase('en-US'), 'JOURNAL-A')),
        ).toBe(first);
      }
      expect(() => recoveryEndpointForDirectory(path.join(root, 'missing', 'journal'))).toThrow(
        /phase-lock-recovery-parent/u,
      );
      try {
        fs.symlinkSync(physical, alias, process.platform === 'win32' ? 'junction' : 'dir');
        expect(recoveryEndpointForDirectory(path.join(alias, 'journal-a'))).toBe(first);
      } catch (error: any) {
        if (!['EPERM', 'EACCES', 'ENOTSUP'].includes(error?.code)) throw error;
      }
    } finally {
      fs.rmSync(alias, { recursive: true, force: true });
      fs.rmSync(root, { recursive: true, force: true });
    }
  });

  test('releases the recovery endpoint even when acquisition-guard release rejects', async () => {
    const value = record();
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-enrollment-recovery-release-'));
    const acquisitionGuard = `${path.resolve(directory)}.lock.acquire`;
    const staleAt = Date.now() - 15 * 60 * 1000 - 1;
    const staleText = `${JSON.stringify({
      kind: 'apr-r4-e2p-enrollment-phase-lock-v1',
      pid: 2147483647,
      instance_id: digest('stale-release-instance'),
      token: digest('stale-release'),
      record_sha256: recordSha256(value),
      phase: 'prepare',
      acquired_at: staleAt,
      expires_at: staleAt + 15 * 60 * 1000,
    })}\n`;
    const staleQuarantine = `${acquisitionGuard}.stale-${digest(staleText)}`;
    const endpoint = recoveryEndpointForDirectory(directory);
    try {
      fs.writeFileSync(acquisitionGuard, staleText, 'utf8');
      const originalUnlink = fs.unlinkSync;
      const unlink = vi.spyOn(fs, 'unlinkSync').mockImplementation((target: fs.PathLike) => {
        if (target === acquisitionGuard) {
          const error: NodeJS.ErrnoException = new Error('injected release failure');
          error.code = 'EPERM';
          throw error;
        }
        return originalUnlink(target);
      });
      try {
        await expect(
          executeDurableEnrollmentPhase({
            directory,
            record: value,
            phase: 'prepare',
            transport: fakeTransport(value),
            observations: [],
            observationMaterializer: fakeObservationMaterializer(value),
          }),
        ).rejects.toThrow(/phase-lock-acquisition-release/u);
      } finally {
        unlink.mockRestore();
      }

      const server = net.createServer();
      try {
        await new Promise<void>((resolve, reject) => {
          server.once('error', reject);
          server.listen({ path: endpoint, exclusive: true }, resolve);
        });
      } finally {
        await new Promise<void>((resolve, reject) => {
          server.close((error) => (error ? reject(error) : resolve()));
        });
      }
    } finally {
      fs.rmSync(directory, { recursive: true, force: true });
      fs.rmSync(acquisitionGuard, { force: true });
      fs.rmSync(staleQuarantine, { force: true });
    }
  });

  test('preserves the business failure and retries a release-pending lock in this process instance', async () => {
    const value = record();
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-enrollment-release-pending-'));
    const destination = `${path.resolve(directory)}.lock`;
    const base = fakeTransport(value);
    const transport = {
      ...base,
      async read(target: string) {
        if (target === `commit-tree:${value.coordinates.workflow_sha}`) return 'wrong';
        return base.read(target);
      },
    };
    const originalUnlink = fs.unlinkSync;
    const unlink = vi.spyOn(fs, 'unlinkSync').mockImplementation((target: fs.PathLike) => {
      if (target === destination) {
        const error: NodeJS.ErrnoException = new Error('injected phase release failure');
        error.code = 'EPERM';
        throw error;
      }
      return originalUnlink(target);
    });
    try {
      await expect(
        executeDurableEnrollmentPhase({
          directory,
          record: value,
          phase: 'prepare',
          transport,
          observations: [],
          observationMaterializer: fakeObservationMaterializer(value),
        }),
      ).rejects.toThrow(/readback-read-merged-commit-tree/u);
    } finally {
      unlink.mockRestore();
    }
    try {
      expect(fs.existsSync(destination)).toBe(true);
      await expect(
        executeDurableEnrollmentPhase({
          directory,
          record: value,
          phase: 'prepare',
          transport: fakeTransport(value),
          observations: [],
          observationMaterializer: fakeObservationMaterializer(value),
        }),
      ).resolves.toMatchObject({ kind: 'phase-complete' });
      expect(fs.existsSync(destination)).toBe(false);
    } finally {
      fs.rmSync(directory, { recursive: true, force: true });
      fs.rmSync(destination, { force: true });
    }
  });

  test('reclaims dead expired acquisition and prior reclamation guards, but leaves live guards busy', async () => {
    const value = record();
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-enrollment-stale-acquire-'));
    const acquisitionGuard = `${path.resolve(directory)}.lock.acquire`;
    const priorReclamationGuard = `${acquisitionGuard}.reclaim`;
    const staleAt = Date.now() - 15 * 60 * 1000 - 1;
    const phaseLock = (pid: number, acquiredAt: number) => ({
      kind: 'apr-r4-e2p-enrollment-phase-lock-v1',
      pid,
      instance_id: digest(`instance:${pid}:${acquiredAt}`),
      token: digest(`${pid}:${acquiredAt}`),
      record_sha256: recordSha256(value),
      phase: 'prepare',
      acquired_at: acquiredAt,
      expires_at: acquiredAt + 15 * 60 * 1000,
    });
    const staleText = `${JSON.stringify(phaseLock(2147483647, staleAt))}\n`;
    const staleQuarantine = `${acquisitionGuard}.stale-${digest(staleText)}`;
    const stalePriorText = `${JSON.stringify(phaseLock(2147483646, staleAt))}\n`;
    const stalePriorQuarantine = `${priorReclamationGuard}.stale-${digest(stalePriorText)}`;
    try {
      fs.writeFileSync(acquisitionGuard, staleText, 'utf8');
      fs.writeFileSync(priorReclamationGuard, stalePriorText, 'utf8');
      await expect(
        executeDurableEnrollmentPhase({
          directory,
          record: value,
          phase: 'prepare',
          transport: fakeTransport(value),
          observations: [],
          observationMaterializer: fakeObservationMaterializer(value),
        }),
      ).resolves.toMatchObject({ kind: 'phase-complete' });
      expect(fs.existsSync(acquisitionGuard)).toBe(false);
      expect(fs.existsSync(priorReclamationGuard)).toBe(false);
      expect(fs.existsSync(staleQuarantine)).toBe(true);
      expect(fs.existsSync(stalePriorQuarantine)).toBe(true);

      const liveAt = Date.now();
      fs.writeFileSync(
        acquisitionGuard,
        `${JSON.stringify(phaseLock(process.pid, liveAt))}\n`,
        'utf8',
      );
      await expect(
        executeDurableEnrollmentPhase({
          directory,
          record: value,
          phase: 'prepare',
          transport: fakeTransport(value),
          observations: [],
          observationMaterializer: fakeObservationMaterializer(value),
        }),
      ).rejects.toThrow(/phase-lock-acquisition-busy/u);

      fs.writeFileSync(acquisitionGuard, staleText, 'utf8');
      fs.writeFileSync(
        priorReclamationGuard,
        `${JSON.stringify(phaseLock(process.pid, liveAt))}\n`,
        'utf8',
      );
      await expect(
        executeDurableEnrollmentPhase({
          directory,
          record: value,
          phase: 'prepare',
          transport: fakeTransport(value),
          observations: [],
          observationMaterializer: fakeObservationMaterializer(value),
        }),
      ).rejects.toThrow(/phase-lock-acquisition-busy/u);
    } finally {
      fs.rmSync(directory, { recursive: true, force: true });
      fs.rmSync(staleQuarantine, { force: true });
      fs.rmSync(stalePriorQuarantine, { force: true });
      fs.rmSync(acquisitionGuard, { force: true });
      fs.rmSync(priorReclamationGuard, { force: true });
    }
  });

  test('reclaims an expired lease with a reused current PID but a prior process-instance identity', async () => {
    const value = record();
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-enrollment-pid-reuse-'));
    const destination = `${path.resolve(directory)}.lock`;
    const acquiredAt = Date.now() - 15 * 60 * 1000 - 1;
    try {
      fs.writeFileSync(
        destination,
        `${JSON.stringify({
          kind: 'apr-r4-e2p-enrollment-phase-lock-v1',
          pid: process.pid,
          instance_id: digest('prior-process-instance'),
          token: digest('prior-process-token'),
          record_sha256: recordSha256(value),
          phase: 'prepare',
          acquired_at: acquiredAt,
          expires_at: acquiredAt + 15 * 60 * 1000,
        })}\n`,
        'utf8',
      );
      await expect(
        executeDurableEnrollmentPhase({
          directory,
          record: value,
          phase: 'prepare',
          transport: fakeTransport(value),
          observations: [],
          observationMaterializer: fakeObservationMaterializer(value),
        }),
      ).resolves.toMatchObject({ kind: 'phase-complete' });
    } finally {
      fs.rmSync(directory, { recursive: true, force: true });
      fs.rmSync(destination, { force: true });
    }
  });

  test('rejects a hash-chained journal that skips an action intent and readback', () => {
    const value = record();
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-enrollment-forged-'));
    const recordDigest = recordSha256(value);
    const start = {
      ordinal: 1,
      kind: 'phase-start',
      phase: 'prepare',
      action: 'prepare',
      attempt: null,
      target: null,
      method: null,
      expected: null,
      observed: null,
      outcome: 'phase-started',
      detail: null,
      previous_sha256: '0'.repeat(64),
      record_sha256: recordDigest,
    };
    const startText = `${JSON.stringify(start)}\n`;
    const skipped = {
      ordinal: 2,
      kind: 'action-complete',
      phase: 'prepare',
      action: 'read-merged-commit-tree',
      attempt: null,
      target: `commit-tree:${value.coordinates.workflow_sha}`,
      method: 'GET',
      expected: JSON.stringify({
        commit_sha: value.coordinates.workflow_sha,
        tree_sha: value.coordinates.workflow_tree_sha,
      }),
      observed: JSON.stringify({
        commit_sha: value.coordinates.workflow_sha,
        tree_sha: value.coordinates.workflow_tree_sha,
      }),
      outcome: 'exact-readback',
      detail: null,
      previous_sha256: digest(startText),
      record_sha256: recordDigest,
    };
    try {
      fs.writeFileSync(path.join(directory, '000001-phase-start-prepare.json'), startText, 'utf8');
      fs.writeFileSync(
        path.join(directory, '000002-action-complete-read-merged-commit-tree.json'),
        `${JSON.stringify(skipped)}\n`,
        'utf8',
      );
      expect(() => readEnrollmentJournal({ directory, record: value })).toThrow(
        /journal-action-shape/u,
      );
    } finally {
      fs.rmSync(directory, { recursive: true, force: true });
    }
  });

  test('rejects a forged completion that is not derived from an exact prior readback', () => {
    const value = record();
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-enrollment-forged-complete-'));
    const recordDigest = recordSha256(value);
    const expected = JSON.stringify({
      commit_sha: value.coordinates.workflow_sha,
      tree_sha: value.coordinates.workflow_tree_sha,
    });
    const fragments = [
      {
        kind: 'phase-start',
        phase: 'prepare',
        action: 'prepare',
        attempt: null,
        target: null,
        method: null,
        expected: null,
        observed: null,
        outcome: 'phase-started',
        detail: null,
      },
      {
        kind: 'intent',
        phase: 'prepare',
        action: 'read-merged-commit-tree',
        attempt: null,
        target: `commit-tree:${value.coordinates.workflow_sha}`,
        method: 'GET',
        expected,
        observed: null,
        outcome: 'read-intent',
        detail: null,
      },
      {
        kind: 'readback',
        phase: 'prepare',
        action: 'read-merged-commit-tree',
        attempt: null,
        target: `commit-tree:${value.coordinates.workflow_sha}`,
        method: 'GET',
        expected,
        observed: null,
        outcome: 'uncertain-read-error',
        detail: { status: null, cause_code: null },
      },
      {
        kind: 'action-complete',
        phase: 'prepare',
        action: 'read-merged-commit-tree',
        attempt: null,
        target: `commit-tree:${value.coordinates.workflow_sha}`,
        method: 'GET',
        expected,
        observed: expected,
        outcome: 'exact-readback',
        detail: null,
      },
    ];
    try {
      let previous = '0'.repeat(64);
      for (const [index, fragment] of fragments.entries()) {
        const entry = {
          ordinal: index + 1,
          ...fragment,
          previous_sha256: previous,
          record_sha256: recordDigest,
        };
        const text = `${JSON.stringify(entry)}\n`;
        const safeAction = entry.action.replace(/[^a-z0-9-]/gu, '-');
        fs.writeFileSync(
          path.join(
            directory,
            `${String(entry.ordinal).padStart(6, '0')}-${entry.kind}-${safeAction}.json`,
          ),
          text,
          'utf8',
        );
        previous = digest(text);
      }
      expect(() => readEnrollmentJournal({ directory, record: value })).toThrow(
        /journal-completion-binding/u,
      );
    } finally {
      fs.rmSync(directory, { recursive: true, force: true });
    }
  });

  test('rejects forged mutation completion outcomes that do not match their wire/readback topology', async () => {
    const value = record();
    const source = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-enrollment-valid-mutation-'));
    try {
      await runThrough(value, fakeTransport(value), source, 'prepare');
      const valid = readEnrollmentJournal({ directory: source, record: value });
      const completionIndex = valid.findIndex(
        (entry: any) => entry.kind === 'action-complete' && entry.action === 'upload-initial-blob',
      );
      expect(completionIndex).toBeGreaterThan(0);

      const verifyForgery = (mutate: (entries: any[]) => void) => {
        const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-enrollment-forged-mutation-'));
        try {
          const entries = structuredClone(valid);
          mutate(entries);
          let previous = '0'.repeat(64);
          for (const entry of entries) {
            entry.previous_sha256 = previous;
            const text = `${JSON.stringify(entry)}\n`;
            const safeAction = entry.action.replace(/[^a-z0-9-]/gu, '-');
            fs.writeFileSync(
              path.join(
                directory,
                `${String(entry.ordinal).padStart(6, '0')}-${entry.kind}-${safeAction}.json`,
              ),
              text,
              'utf8',
            );
            previous = digest(text);
          }
          expect(() => readEnrollmentJournal({ directory, record: value })).toThrow(
            /journal-mutation-shape/u,
          );
        } finally {
          fs.rmSync(directory, { recursive: true, force: true });
        }
      };

      verifyForgery((entries) => {
        entries[completionIndex].outcome = 'reconciled-expected';
      });
      verifyForgery((entries) => {
        entries[completionIndex].outcome = 'uncertain-reconciled';
      });
      verifyForgery((entries) => {
        entries[completionIndex - 1].outcome = 'uncertain-readback';
      });
    } finally {
      fs.rmSync(source, { recursive: true, force: true });
    }
  });

  test('rejects a role completion reconstructed from ordinary GET journal events', async () => {
    const value = record();
    const source = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-enrollment-role-source-'));
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-enrollment-role-forged-'));
    try {
      await runThrough(value, fakeTransport(value), source, 'authorize-stale');
      const fragments = structuredClone(
        readEnrollmentJournal({ directory: source, record: value }),
      );
      const index = fragments.findIndex(
        (entry: any) => entry.kind === 'observation' && entry.action === 'normal-bootstrap',
      );
      expect(index).toBeGreaterThan(0);
      const observation = fragments[index];
      fragments.splice(
        index,
        1,
        {
          ...observation,
          kind: 'intent',
          observed: null,
          outcome: 'read-intent',
          detail: null,
        },
        {
          ...observation,
          kind: 'readback',
          outcome: 'exact-readback',
        },
      );
      writeJournal(directory, value, fragments);
      expect(() => readEnrollmentJournal({ directory, record: value })).toThrow(
        /journal-action-shape/u,
      );
    } finally {
      fs.rmSync(source, { recursive: true, force: true });
      fs.rmSync(directory, { recursive: true, force: true });
    }
  });

  test('binds every role observation receipt to its role-plan head, including stale follow-on', async () => {
    const value = record();
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-enrollment-role-heads-'));
    const base = fakeObservationMaterializer(value);
    const transport = fakeTransport(value);
    const expectedByRole = new Map<string, string>();
    const observationMaterializer = {
      ...base,
      materialize(expected: any) {
        expectedByRole.set(expected.role, expected.expected_run_head_sha);
        return base.materialize(expected);
      },
    };
    try {
      for (const phase of ENROLLMENT_CONTRACT.phases) {
        await executeDurableEnrollmentPhase({
          directory,
          record: value,
          phase,
          transport,
          observations: observations(value),
          observationMaterializer,
        });
        if (phase === 'authorize-stale') {
          transport.state.set(
            `ref:${value.fixtures.stale.ref}`,
            value.fixtures.stale.advanced_head,
          );
          transport.state.set(
            `pull:${value.fixtures.stale.pr_number}`,
            pullBinding(value, value.fixtures.stale, value.fixtures.stale.advanced_head),
          );
        }
      }
      expect(Object.fromEntries(expectedByRole)).toEqual(
        Object.fromEntries(value.role_plan.map((plan: any) => [plan.name, plan.workflow_sha])),
      );
    } finally {
      fs.rmSync(directory, { recursive: true, force: true });
    }
  });

  test('fails closed on drift, observation mismatch, phase skipping, and journal extras', async () => {
    const value = record();
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-enrollment-negative-'));
    try {
      await expect(
        executeDurableEnrollmentPhase({
          directory,
          record: value,
          phase: 'authorize-normal',
          transport: fakeTransport(value),
          observations: observations(value),
          observationMaterializer: fakeObservationMaterializer(value),
        }),
      ).rejects.toThrow(/phase-order/u);
    } finally {
      fs.rmSync(directory, { recursive: true, force: true });
    }

    const driftDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-enrollment-drift-'));
    const driftTransport = fakeTransport(value, {
      uncertainTarget: `ref:${value.fixtures.normal.ref}`,
      uncertainMode: 'drift',
    });
    try {
      await runThrough(value, driftTransport, driftDirectory, 'prepare');
      await expect(
        executeDurableEnrollmentPhase({
          directory: driftDirectory,
          record: value,
          phase: 'refresh',
          transport: driftTransport,
          observations: observations(value),
          observationMaterializer: fakeObservationMaterializer(value),
        }),
      ).rejects.toThrow(/mutation-readback-refresh-normal-ref/u);
    } finally {
      fs.rmSync(driftDirectory, { recursive: true, force: true });
    }

    const observationDirectory = fs.mkdtempSync(
      path.join(os.tmpdir(), 'apr-enrollment-observation-'),
    );
    const observationTransport = fakeTransport(value);
    try {
      await runThrough(value, observationTransport, observationDirectory, 'refresh');
      const changed = observations(value);
      changed[0] = { ...changed[0], head_sha: sha('f') };
      await expect(
        executeDurableEnrollmentPhase({
          directory: observationDirectory,
          record: value,
          phase: 'authorize-normal',
          transport: observationTransport,
          observations: changed,
          observationMaterializer: fakeObservationMaterializer(value),
        }),
      ).rejects.toThrow(/observation-keys/u);
      const duplicatedRun = observations(value);
      duplicatedRun[1] = { ...duplicatedRun[1], run_id: duplicatedRun[0].run_id };
      await expect(
        executeDurableEnrollmentPhase({
          directory: observationDirectory,
          record: value,
          phase: 'authorize-normal',
          transport: observationTransport,
          observations: duplicatedRun,
          observationMaterializer: fakeObservationMaterializer(value),
        }),
      ).rejects.toThrow(/observation-binding/u);
      fs.writeFileSync(path.join(observationDirectory, 'unexpected.txt'), 'x', 'utf8');
      expect(() =>
        readEnrollmentJournal({ directory: observationDirectory, record: value }),
      ).toThrow(/journal-extra-entry/u);
    } finally {
      fs.rmSync(observationDirectory, { recursive: true, force: true });
    }
  });

  test('CLI is inert without --execute and GitHub HTTP errors are typed before reconciliation', async () => {
    let constructed = 0;
    await expect(
      runEnrollmentCli([], {
        createTransport: () => {
          constructed += 1;
          return {};
        },
      }),
    ).resolves.toEqual({ usage: true });
    expect(constructed).toBe(0);
    await expect(runEnrollmentCli(['--execute'])).rejects.toThrow(/usage/u);

    const definitive = createGhTransport({
      repository: ENROLLMENT_CONTRACT.repository,
      runGh: () => ({
        status: 1,
        stdout: 'HTTP/2 403 Forbidden\ncontent-type: application/json\n\n{"message":"forbidden"}',
        stderr: '',
      }),
    });
    try {
      await definitive.mutate('PATCH', `ref:${ENROLLMENT_CONTRACT.normal.operationId}`, {});
      throw new Error('expected definitive failure');
    } catch (error: any) {
      expect(error.status).toBe(403);
      expect(error.uncertain).toBeUndefined();
    }

    const serverFailure = createGhTransport({
      repository: ENROLLMENT_CONTRACT.repository,
      runGh: () => ({
        status: 1,
        stdout: 'HTTP/2 503 Service Unavailable\ncontent-type: application/json\n\n{}',
        stderr: '',
      }),
    });
    await expect(
      serverFailure.mutate('PATCH', `ref:${ENROLLMENT_CONTRACT.normal.operationId}`, {}),
    ).rejects.toMatchObject({ status: 503, uncertain: true });

    const timeout = createGhTransport({
      repository: ENROLLMENT_CONTRACT.repository,
      runGh: () => ({
        status: 1,
        stdout: '',
        stderr: 'connection timed out',
        error: { code: 'ETIMEDOUT' },
      }),
    });
    await expect(
      timeout.mutate('PATCH', `ref:${ENROLLMENT_CONTRACT.normal.operationId}`, {}),
    ).rejects.toMatchObject({ uncertain: true });

    const missing = createGhTransport({
      repository: ENROLLMENT_CONTRACT.repository,
      runGh: () => ({ status: 1, stdout: '', stderr: 'gh: Not Found (HTTP 404)' }),
    });
    await expect(missing.read(`variable:${ENROLLMENT_CONTRACT.variable}`)).resolves.toBeNull();

    const value = record();
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-enrollment-cli-'));
    const recordFile = path.join(root, 'record.json');
    const authorityFile = path.join(root, 'authority.json');
    const observationContextFile = path.join(root, 'observation-context.json');
    const observationsFile = path.join(root, 'observations.json');
    const journal = path.join(root, 'journal');
    try {
      fs.writeFileSync(recordFile, `${JSON.stringify(value)}\n`, 'utf8');
      fs.writeFileSync(
        authorityFile,
        `${JSON.stringify(executionAuthorityPackage(value.authority.enrollment_record_sha256))}\n`,
        'utf8',
      );
      fs.writeFileSync(observationsFile, '[]\n', 'utf8');
      fs.writeFileSync(
        observationContextFile,
        `${JSON.stringify({
          assembly_path: path.join(root, 'capture.dll'),
          dotnet_command: process.execPath,
          restricted_root: root,
          destination_identity_sha256: digest('observation-destination'),
          repository_root: root,
          worktree_root: root,
          execution_authorization: 'execution.json',
          producer_journal_directory: 'producer-journal',
          package_name: 'capture-package',
        })}\n`,
        'utf8',
      );
      await expect(
        runEnrollmentCli(
          [
            '--execute',
            '--record',
            recordFile,
            '--record-sha256',
            recordSha256(value),
            '--execution-authority',
            authorityFile,
            '--observation-context',
            observationContextFile,
            '--journal-dir',
            journal,
            '--phase',
            'prepare',
            '--observations',
            observationsFile,
          ],
          {
            transport: fakeTransport(value),
            observationMaterializer: fakeObservationMaterializer(value),
          },
        ),
      ).resolves.toMatchObject({ phase: 'prepare', kind: 'phase-complete' });
    } finally {
      fs.rmSync(root, { recursive: true, force: true });
    }
  });
});
