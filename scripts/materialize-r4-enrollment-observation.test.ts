import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, test } from 'vitest';
import {
  createEnrollmentObservationMaterializer,
  validateEnrollmentObservationReceipt,
} from './materialize-r4-enrollment-observation.mjs';

const sha256 = (value: string | Buffer) => crypto.createHash('sha256').update(value).digest('hex');
const hex40 = (letter: string) => letter.repeat(40);
const hex64 = (letter: string) => letter.repeat(64);

const role = 'normal-bootstrap';
const expected = Object.freeze({
  operation_id: hex64('a'),
  role,
  run_id: '9001',
  expected_event: 'workflow_dispatch',
  expected_run_head_sha: hex40('b'),
});
const destinationIdentity = hex64('c');
const executionAuthorization = hex64('d');
const materializerSource = hex64('e');
const cleanupRoots: string[] = [];

afterEach(() => {
  while (cleanupRoots.length > 0) {
    fs.rmSync(cleanupRoots.pop()!, { recursive: true, force: true });
  }
});

function source(sourceId: string, phase: string, seed: string): Record<string, string> {
  return {
    source_id: sourceId,
    phase,
    fragment_sha256: sha256(`fragment:${seed}`),
    fragment_physical_identity_sha256: sha256(`fragment-identity:${seed}`),
    body_sha256: sha256(`body:${seed}`),
    body_size: String(Buffer.byteLength(`body:${seed}`, 'utf8')),
    body_physical_identity_sha256: sha256(`body-identity:${seed}`),
  };
}

function receipt(overrides: Record<string, unknown> = {}) {
  const prefix = `enrollment-${role}`;
  return {
    kind: 'apr-r4-e2p-enrollment-role-observation-v1',
    execution_authorization_sha256: executionAuthorization,
    destination_identity_sha256: destinationIdentity,
    materializer_source_sha256: materializerSource,
    materializer_build_sha256: '',
    operation_id: expected.operation_id,
    role: expected.role,
    run_id: expected.run_id,
    run_attempt: '1',
    expected_event: expected.expected_event,
    expected_run_head_sha: expected.expected_run_head_sha,
    sources: [
      source(`${prefix}-run-${expected.run_id}:page:1`, `${prefix}-terminal`, 'terminal'),
      source(`${prefix}-jobs-run-${expected.run_id}:page:1`, `${prefix}-jobs`, 'jobs-1'),
      source(`${prefix}-jobs-run-${expected.run_id}:page:2`, `${prefix}-jobs`, 'jobs-2'),
      source(
        `${prefix}-discovery-run-${expected.run_id}:page:1`,
        `${prefix}-discovery`,
        'discovery-1',
      ),
      source(
        `${prefix}-discovery-run-${expected.run_id}:page:2`,
        `${prefix}-discovery`,
        'discovery-2',
      ),
      source(`${prefix}-pull-run-${expected.run_id}:page:1`, `${prefix}-pull`, 'pull'),
      source(`${prefix}-pending-run-${expected.run_id}:page:1`, `${prefix}-pending`, 'pending'),
      source(`${prefix}-approvals-run-${expected.run_id}:page:1`, `${prefix}-approval`, 'approval'),
    ],
    finalized: true,
    ...overrides,
  };
}

type ProcessResult = {
  error?: Error | null;
  signal?: string | null;
  status?: number | null;
  stdout?: string;
};

function setup(result: ProcessResult | ((value: ReturnType<typeof receipt>) => ProcessResult)) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-enrollment-observation-'));
  cleanupRoots.push(root);
  const assembly = path.join(root, 'PhaseFragmentMaterializer.dll');
  const assemblyBytes = Buffer.from('materializer-assembly', 'utf8');
  fs.writeFileSync(assembly, assemblyBytes);
  const build = sha256(assemblyBytes);
  const calls: Array<{ command: string; args: string[]; options: any }> = [];
  const authority = {
    execution_authorization_sha256: executionAuthorization,
    phase_materializer_source_sha256: materializerSource,
    phase_materializer_build_sha256: build,
  };
  const materializer = createEnrollmentObservationMaterializer({
    assembly_path: assembly,
    dotnet_command: 'dotnet',
    restricted_root: path.join(root, 'restricted'),
    destination_identity_sha256: destinationIdentity,
    repository_root: path.join(root, 'repository'),
    worktree_root: path.join(root, 'worktree'),
    execution_authorization: 'authorizations/execution.json',
    producer_journal_directory: 'producer-journal',
    package_name: 'enrollment-package',
    authority,
    run_process(command, args, options) {
      calls.push({ command, args, options });
      const value = receipt({ materializer_build_sha256: build });
      return typeof result === 'function' ? result(value) : result;
    },
  });
  return { assembly, authority, build, calls, materializer, root };
}

function validResult(value: ReturnType<typeof receipt>): ProcessResult {
  return { error: null, status: 0, signal: null, stdout: `${JSON.stringify(value)}\n` };
}

describe('R4 enrollment observation materializer', () => {
  test('accepts the exact canonical role receipt and invokes the restricted materializer without secrets', () => {
    const prior = {
      GITHUB_TOKEN: process.env.GITHUB_TOKEN,
      GH_TOKEN: process.env.GH_TOKEN,
      DEEPSEEK_API_KEY: process.env.DEEPSEEK_API_KEY,
      OPENAI_API_KEY: process.env.OPENAI_API_KEY,
      AGENTIC_PR_REVIEW_STATE_KEY: process.env.AGENTIC_PR_REVIEW_STATE_KEY,
      APR_UNRELATED_TEST_SECRET: process.env.APR_UNRELATED_TEST_SECRET,
    };
    Object.assign(process.env, {
      GITHUB_TOKEN: 'must-not-reach-materializer',
      GH_TOKEN: 'must-not-reach-materializer',
      DEEPSEEK_API_KEY: 'must-not-reach-materializer',
      OPENAI_API_KEY: 'must-not-reach-materializer',
      AGENTIC_PR_REVIEW_STATE_KEY: 'must-not-reach-materializer',
      APR_UNRELATED_TEST_SECRET: 'must-not-reach-materializer',
    });
    try {
      const configured = setup(validResult);
      const observed = configured.materializer.materialize(expected);
      expect(observed.receipt).toEqual(receipt({ materializer_build_sha256: configured.build }));
      expect(observed.sha256).toBe(sha256(JSON.stringify(observed.receipt)));
      expect(configured.materializer.destination_identity_sha256).toBe(destinationIdentity);
      expect(configured.materializer.validate(observed.receipt, expected)).toEqual(observed);
      expect(() =>
        configured.materializer.validate(
          { ...observed.receipt, destination_identity_sha256: hex64('f') },
          expected,
        ),
      ).toThrow(/receipt-values/u);
      expect(configured.calls).toHaveLength(1);
      expect(configured.calls[0]).toMatchObject({ command: 'dotnet' });
      expect(configured.calls[0].args).toEqual([
        configured.assembly,
        'enrollment-observation',
        '--restricted-root',
        path.join(configured.root, 'restricted'),
        '--destination-identity',
        destinationIdentity,
        '--repository-root',
        path.join(configured.root, 'repository'),
        '--worktree-root',
        path.join(configured.root, 'worktree'),
        '--execution-authorization',
        'authorizations/execution.json',
        '--execution-authorization-sha256',
        executionAuthorization,
        '--producer-journal-directory',
        'producer-journal',
        '--package-name',
        'enrollment-package',
        '--role',
        expected.role,
        '--run-id',
        expected.run_id,
        '--expected-event',
        expected.expected_event,
        '--expected-run-head',
        expected.expected_run_head_sha,
        '--operation-id',
        expected.operation_id,
      ]);
      expect(configured.calls[0].options).toMatchObject({
        encoding: 'utf8',
        windowsHide: true,
        maxBuffer: 1024 * 1024,
      });
      for (const name of Object.keys(prior)) {
        expect(configured.calls[0].options.env).not.toHaveProperty(name);
      }
    } finally {
      for (const [name, value] of Object.entries(prior)) {
        if (value === undefined) delete process.env[name];
        else process.env[name] = value;
      }
    }
  });

  test('fails closed when process execution does not complete exactly', () => {
    for (const result of [
      { error: new Error('spawn failed'), status: null, signal: null, stdout: '' },
      { error: null, status: 1, signal: null, stdout: '' },
      { error: null, status: null, signal: 'SIGKILL', stdout: '' },
    ]) {
      const configured = setup(result);
      expect(() => configured.materializer.materialize(expected)).toThrow(/materializer-process/u);
    }
  });

  test('rejects noncanonical, malformed, or semantically tampered child output', () => {
    const cases: Array<{
      name: string;
      result: (value: ReturnType<typeof receipt>) => ProcessResult;
      code: RegExp;
    }> = [
      {
        name: 'pretty json',
        result: (value) => ({
          error: null,
          status: 0,
          signal: null,
          stdout: `${JSON.stringify(value, null, 2)}\n`,
        }),
        code: /materializer-canonical/u,
      },
      {
        name: 'invalid json',
        result: () => ({ error: null, status: 0, signal: null, stdout: '{not-json}\n' }),
        code: /materializer-json/u,
      },
      {
        name: 'execution authority tamper',
        result: (value) => validResult({ ...value, execution_authorization_sha256: hex64('f') }),
        code: /receipt-values/u,
      },
      {
        name: 'materializer source tamper',
        result: (value) => validResult({ ...value, materializer_source_sha256: hex64('f') }),
        code: /receipt-values/u,
      },
      {
        name: 'unexpected source topology',
        result: (value) =>
          validResult({
            ...value,
            sources: value.sources.map((entry, index) =>
              index === 1 ? { ...entry, source_id: `${entry.source_id}-extra` } : entry,
            ),
          }),
        code: /source-contract/u,
      },
      {
        name: 'missing exact pull source',
        result: (value) =>
          validResult({
            ...value,
            sources: value.sources.filter((entry) => !entry.phase.endsWith('-pull')),
          }),
        code: /source-contract/u,
      },
      {
        name: 'noncontiguous discovery pages',
        result: (value) =>
          validResult({
            ...value,
            sources: value.sources.map((entry) =>
              entry.phase.endsWith('-discovery') && entry.source_id.endsWith(':page:2')
                ? { ...entry, source_id: entry.source_id.replace(':page:2', ':page:3') }
                : entry,
            ),
          }),
        code: /source-contract/u,
      },
      {
        name: 'duplicate source identity',
        result: (value) =>
          validResult({
            ...value,
            sources: value.sources.map((entry, index) =>
              index === 1
                ? {
                    ...entry,
                    body_physical_identity_sha256: value.sources[0].body_physical_identity_sha256,
                  }
                : entry,
            ),
          }),
        code: /source-values/u,
      },
    ];
    for (const entry of cases) {
      const configured = setup(entry.result);
      expect(() => configured.materializer.materialize(expected), entry.name).toThrow(entry.code);
    }
  });

  test('binds role, run, event, and merged workflow head to the executor-provided plan', () => {
    const configured = setup(validResult);
    expect(() =>
      configured.materializer.materialize({ ...expected, role: 'normal-inert-preflight' }),
    ).toThrow(/materialize-expected-values/u);
    for (const changed of [
      { ...expected, role: 'stale-follow-on' },
      { ...expected, run_id: '9002' },
      { ...expected, expected_event: 'workflow_run' },
      { ...expected, expected_run_head_sha: hex40('f') },
    ]) {
      expect(() => configured.materializer.materialize(changed)).toThrow(/receipt-values/u);
    }
  });

  test('rejects a substituted assembly and direct receipt shape, binding, and topology drift', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-enrollment-observation-authority-'));
    cleanupRoots.push(root);
    const assembly = path.join(root, 'materializer.dll');
    fs.writeFileSync(assembly, 'different assembly', 'utf8');
    expect(() =>
      createEnrollmentObservationMaterializer({
        assembly_path: assembly,
        dotnet_command: 'dotnet',
        restricted_root: root,
        destination_identity_sha256: destinationIdentity,
        repository_root: root,
        worktree_root: root,
        execution_authorization: 'authorizations/execution.json',
        producer_journal_directory: 'producer-journal',
        package_name: 'enrollment-package',
        authority: {
          execution_authorization_sha256: executionAuthorization,
          phase_materializer_source_sha256: materializerSource,
          phase_materializer_build_sha256: hex64('f'),
        },
        run_process: () => ({ status: 0, signal: null, stdout: '' }),
      }),
    ).toThrow(/materializer-build/u);

    const configured = setup(validResult);
    const authority = configured.authority;
    const good = receipt({ materializer_build_sha256: configured.build });
    expect(() =>
      validateEnrollmentObservationReceipt(
        { ...good, materializer_build_sha256: hex64('f') },
        {
          authority,
          destination_identity_sha256: destinationIdentity,
          ...expected,
        },
      ),
    ).toThrow(/receipt-values/u);
    const missing = { ...good } as Record<string, unknown>;
    delete missing.finalized;
    expect(() =>
      validateEnrollmentObservationReceipt(missing, {
        authority,
        destination_identity_sha256: destinationIdentity,
        ...expected,
      }),
    ).toThrow(/receipt-shape/u);
    expect(() =>
      validateEnrollmentObservationReceipt(
        { ...good, sources: [...good.sources.slice(0, 1)] },
        { authority, destination_identity_sha256: destinationIdentity, ...expected },
      ),
    ).toThrow(/receipt-values/u);
  });
});
