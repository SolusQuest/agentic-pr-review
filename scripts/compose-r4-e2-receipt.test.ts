import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, test } from 'vitest';

const repositoryRoot = path.resolve(import.meta.dirname, '..');
const composer = path.join(repositoryRoot, 'scripts', 'compose-r4-e2-receipt.mjs');
const contractPath = path.join(
  repositoryRoot,
  'runtime',
  'tests',
  'fixtures',
  'action-host',
  'aot',
  'receipt-contract.json',
);
const temporaryRoots: string[] = [];
const hex40 = '1'.repeat(40);
const hex64 = 'a'.repeat(64);

afterEach(() => {
  for (const root of temporaryRoots.splice(0)) {
    fs.rmSync(root, { force: true, recursive: true });
  }
});

function fixture() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-r4-e2-receipt-'));
  temporaryRoots.push(root);
  const paths = {
    identity: path.join(root, 'identity.json'),
    contract: path.join(root, 'contract.json'),
    sourceLog: path.join(root, 'source.log'),
    action: path.join(root, 'action.yml'),
    bundle: path.join(root, 'index.js'),
    warningPolicy: path.join(root, 'warning-policy.txt'),
  };
  const identity = {
    kind: 'apr-r4-e2-action-host-native-aot-identity-v1',
    execution_kind: 'native-aot',
    reflection_json_enabled: false,
    dynamic_code_supported: false,
    launch_action_source_sha: hex40,
    wrapper_build_discriminator: 'r4-w2',
    payload_sha256: hex64,
    managed_intermediate_sha256: '3'.repeat(64),
    runtime_intermediate_sha256: '4'.repeat(64),
    managed_architecture_sha256: '5'.repeat(64),
    build_pair_sha256: '6'.repeat(64),
    e1_normalized_evidence_sha256: '7'.repeat(64),
    source_inventory_digest: '8'.repeat(64),
    replacement_record_digest: '9'.repeat(64),
    base_inventory_digest: 'a'.repeat(64),
    canary_table_digest: 'b'.repeat(64),
  };
  const contract = JSON.parse(fs.readFileSync(contractPath, 'utf8')) as Record<string, unknown>;
  fs.writeFileSync(paths.identity, `${JSON.stringify(identity)}\n`);
  fs.writeFileSync(paths.contract, `${JSON.stringify(contract)}\n`);
  fs.writeFileSync(
    paths.sourceLog,
    `APR_R4_W13_SOURCE_COMMIT ${'c'.repeat(40)}\nAPR_R4_W13_SOURCE_TREE ${'d'.repeat(40)}\n`,
  );
  fs.writeFileSync(paths.action, 'name: synthetic token must remain hashed\n');
  fs.writeFileSync(paths.bundle, 'const credential = "canary";\n');
  fs.writeFileSync(paths.warningPolicy, 'audited warning policy\n');
  return { root, paths, identity, contract };
}

function run(paths: ReturnType<typeof fixture>['paths'], args: string[] = []) {
  const options = [
    '--identity',
    paths.identity,
    '--contract',
    paths.contract,
    '--source-log',
    paths.sourceLog,
    '--action',
    paths.action,
    '--bundle',
    paths.bundle,
    '--warning-policy',
    paths.warningPolicy,
    ...args,
  ];
  return spawnSync(process.execPath, [composer, ...options], {
    cwd: repositoryRoot,
    encoding: 'utf8',
  });
}

function expectRejected(result: ReturnType<typeof run>) {
  expect(result.status).not.toBe(0);
  expect(result.stdout).not.toContain('APR_R4_E2_RECEIPT ');
}

describe('R4 E2 receipt composer', () => {
  test('emits one bounded canonical receipt without copying protected inputs', () => {
    const value = fixture();
    const result = run(value.paths);

    expect(result.status).toBe(0);
    expect(result.stdout.match(/APR_R4_E2_RECEIPT /gu)).toHaveLength(1);
    expect(result.stdout).not.toContain('synthetic token');
    expect(result.stdout).not.toContain('credential');
    expect(result.stdout).not.toContain('const credential');
    const receipt = JSON.parse(result.stdout.slice('APR_R4_E2_RECEIPT '.length)) as Record<
      string,
      unknown
    >;
    expect(Object.keys(receipt)).toEqual(value.contract.ordered_fields);
    expect(receipt.runtime_intermediate_sha256).toBe('4'.repeat(64));
    expect(receipt.managed_architecture_sha256).toBe('5'.repeat(64));
    expect(receipt.result).toBe('passed');
  });

  test('rejects malformed, reordered, oversized, and noncanonical identity inputs', () => {
    const mutations: Array<(value: ReturnType<typeof fixture>) => void> = [
      ({ paths }) => fs.writeFileSync(paths.identity, '{}'),
      ({ paths, identity }) =>
        fs.writeFileSync(paths.identity, JSON.stringify({ extra: hex64, ...identity })),
      ({ paths, identity }) =>
        fs.writeFileSync(
          paths.identity,
          JSON.stringify({ ...identity, payload_sha256: hex64.toUpperCase() }),
        ),
      ({ paths, identity }) =>
        fs.writeFileSync(paths.identity, JSON.stringify({ ...identity, build_pair_sha256: '0' })),
      ({ paths }) => fs.writeFileSync(paths.identity, `{${' '.repeat(16_384)}}`),
    ];

    for (const mutate of mutations) {
      const value = fixture();
      mutate(value);
      expectRejected(run(value.paths));
    }
  });

  test('rejects contract drift including migration base and receipt field shape', () => {
    const mutations: Array<(contract: Record<string, unknown>) => void> = [
      (contract) => {
        contract.migration_base_commit = '0'.repeat(40);
      },
      (contract) => {
        contract.migration_base_tree = '0'.repeat(40);
      },
      (contract) => {
        contract.extra = true;
      },
      (contract) => {
        contract.ordered_fields = (contract.ordered_fields as string[]).slice(1);
      },
      (contract) => {
        contract.ordered_fields = [...(contract.ordered_fields as string[]), 'private_canary'];
      },
      (contract) => {
        contract.ordered_fields = [...(contract.ordered_fields as string[])].reverse();
      },
    ];

    for (const mutate of mutations) {
      const value = fixture();
      mutate(value.contract);
      fs.writeFileSync(value.paths.contract, JSON.stringify(value.contract));
      expectRejected(run(value.paths));
    }
  });

  test('rejects missing, duplicate, and malformed source identities', () => {
    const value = fixture();
    const sourceCases = [
      '',
      `APR_R4_W13_SOURCE_COMMIT ${'c'.repeat(40)}\n`,
      `APR_R4_W13_SOURCE_COMMIT ${'c'.repeat(40)}\nAPR_R4_W13_SOURCE_COMMIT ${'c'.repeat(40)}\nAPR_R4_W13_SOURCE_TREE ${'d'.repeat(40)}\n`,
      `APR_R4_W13_SOURCE_COMMIT ${'C'.repeat(40)}\nAPR_R4_W13_SOURCE_TREE ${'d'.repeat(40)}\n`,
    ];
    for (const source of sourceCases) {
      fs.writeFileSync(value.paths.sourceLog, source);
      expectRejected(run(value.paths));
    }
  });

  test('rejects missing, duplicate, and unknown command-line options', () => {
    const value = fixture();
    expectRejected(
      spawnSync(process.execPath, [composer], {
        cwd: repositoryRoot,
        encoding: 'utf8',
      }),
    );
    expectRejected(run(value.paths, ['--identity', value.paths.identity]));
    expectRejected(run(value.paths, ['--unknown', value.paths.identity]));
  });
});
