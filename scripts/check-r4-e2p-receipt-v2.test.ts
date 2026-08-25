import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { afterEach, describe, expect, it } from 'vitest';
import { verifyReceiptV2 } from './check-r4-e2p-receipt-v2.mjs';

const roots: string[] = [];
afterEach(() => {
  for (const root of roots.splice(0)) fs.rmSync(root, { recursive: true, force: true });
});

function sha256(value: crypto.BinaryLike) {
  return crypto.createHash('sha256').update(value).digest('hex');
}

function compose(identityOverrides: Record<string, unknown> = {}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-r4-e2p-v2-receipt-'));
  roots.push(root);
  const payloadPath = path.join(root, 'payload');
  fs.writeFileSync(payloadPath, 'staged-native-payload');
  const payloadSha256 = sha256(fs.readFileSync(payloadPath));
  const identityPath = path.join(root, 'identity.json');
  const identity = {
    source_commit: '1'.repeat(40),
    source_tree: '2'.repeat(40),
    compiled_payload_source_commit: '1'.repeat(40),
    compiled_payload_source_tree: '2'.repeat(40),
    compiled_payload_proof_kind: 'apr-r4-e2p-trusted-proof-payload-v2',
    payload_sha256: payloadSha256,
    proof_managed_intermediate_sha256: '3'.repeat(64),
    runtime_managed_intermediate_sha256: '4'.repeat(64),
    managed_architecture_sha256: '5'.repeat(64),
    verifier_sha256: '6'.repeat(64),
    verifier_managed_intermediate_sha256: '7'.repeat(64),
    verifier_payload_managed_intermediate_sha256: '3'.repeat(64),
    verifier_runtime_managed_intermediate_sha256: '4'.repeat(64),
    verifier_managed_architecture_sha256: '8'.repeat(64),
    verifier_evidence_sha256: '9'.repeat(64),
    build_pair_sha256: '',
  };
  identity.build_pair_sha256 = sha256(
    [
      'apr-r4-e2p-build-pair-v2',
      identity.payload_sha256,
      identity.proof_managed_intermediate_sha256,
      identity.runtime_managed_intermediate_sha256,
      identity.managed_architecture_sha256,
      identity.verifier_sha256,
      identity.verifier_managed_intermediate_sha256,
      identity.verifier_payload_managed_intermediate_sha256,
      identity.verifier_runtime_managed_intermediate_sha256,
      identity.verifier_managed_architecture_sha256,
      identity.verifier_evidence_sha256,
    ].join('\n') + '\n',
  );
  Object.assign(identity, identityOverrides);
  fs.writeFileSync(identityPath, `${JSON.stringify(identity)}\n`);
  const args = [
    'scripts/compose-r4-e2p-receipt-v2.mjs',
    '--identity',
    identityPath,
    '--contract',
    'runtime/tests/fixtures/action-host/trusted-proof-payload/aot/receipt-contract-v2.json',
    '--predecessor',
    'runtime/tests/fixtures/action-host/trusted-proof-payload/predecessor-anchor.json',
    '--action',
    '.github/actions/agentic-pr-review/action.yml',
    '--bundle',
    '.github/actions/agentic-pr-review/dist/index.js',
    '--workflow',
    'runtime/tests/fixtures/action-host/trusted-proof-payload/workflow/r4-trusted-proof-v2.yml.template',
    '--preflight-contract',
    'runtime/tests/fixtures/action-host/trusted-proof-payload/workflow/preflight-contract-v2.json',
    '--provider-contract',
    'runtime/tests/fixtures/action-host/trusted-proof-payload/deterministic-provider-contract.json',
    '--control-contract',
    'runtime/tests/fixtures/action-host/trusted-proof-payload/proof-control-contract.json',
    '--stale-contract',
    'runtime/tests/fixtures/action-host/trusted-proof-payload/stale-window-contract.json',
    '--trusted-config',
    '.github/agentic-pr-review/trusted-proof.json',
    '--trusted-instructions',
    '.github/agentic-pr-review/trusted-proof-instructions.md',
    '--preparation-contract',
    'runtime/tests/fixtures/action-host/trusted-proof-payload/preparation-contract-v2.json',
    '--preparation-script',
    'runtime/scripts/prepare-r4-trusted-proof-payload-v2.sh',
    '--warning-policy',
    'runtime/tests/fixtures/action-host/trusted-proof-payload/aot/warning-policy.txt',
    '--verifier-warning-policy',
    'runtime/tests/fixtures/action-host/trusted-proof-payload/aot/verifier-warning-policy-v2.txt',
  ];
  const result = spawnSync(process.execPath, args, {
    cwd: process.cwd(),
    encoding: 'utf8',
  });
  expect(result.status, result.stderr).toBe(0);
  const prefix = 'APR_R4_E2P_RECEIPT_V2 ';
  expect(result.stdout.startsWith(prefix)).toBe(true);
  const receiptPath = path.join(root, 'receipt.json');
  fs.writeFileSync(receiptPath, `${result.stdout.slice(prefix.length).trim()}\n`);
  return { payloadPath, receiptPath, sourceRoot: process.cwd() };
}

describe('R4 E2P current-head receipt v2', () => {
  it('admits separate exact payload and Action source identities', () => {
    const fixture = compose();
    const receipt = verifyReceiptV2(fixture);
    expect(receipt.source_commit).toBe('1'.repeat(40));
    expect(receipt.compiled_payload_source_commit).toBe(receipt.source_commit);
    expect(receipt.compiled_payload_proof_kind).toBe(receipt.kind);
    expect(receipt.action_source_sha).toBe('5b5769753653bb3fd3e68cf8b7bb88a1bd350613');
  });

  it('rejects v1, conflated, reordered, and extra receipt surfaces', () => {
    for (const mutate of [
      (value: Record<string, unknown>) => {
        value.kind = 'apr-r4-e2p-trusted-proof-payload-v1';
      },
      (value: Record<string, unknown>) => {
        value.source_commit = value.action_source_sha;
      },
      (value: Record<string, unknown>) => {
        const first = Object.entries(value)[0];
        delete value[first?.[0] ?? 'kind'];
        if (first) value[first[0]] = first[1];
      },
      (value: Record<string, unknown>) => {
        value.extra = true;
      },
    ]) {
      const fixture = compose();
      const value = JSON.parse(fs.readFileSync(fixture.receiptPath, 'utf8')) as Record<
        string,
        unknown
      >;
      mutate(value);
      fs.writeFileSync(fixture.receiptPath, `${JSON.stringify(value)}\n`);
      expect(() => verifyReceiptV2(fixture)).toThrow();
    }
  });

  it('rejects a reintroduced partition and forged compiled payload-source identity', () => {
    for (const mutate of [
      (value: Record<string, unknown>) => {
        value.transaction_partition = {};
      },
      (value: Record<string, unknown>) => {
        value.compiled_payload_source_commit = value.action_source_sha;
      },
      (value: Record<string, unknown>) => {
        value.compiled_payload_source_tree = '0'.repeat(40);
      },
      (value: Record<string, unknown>) => {
        value.source_tree = '0'.repeat(40);
      },
    ]) {
      const fixture = compose();
      const value = JSON.parse(fs.readFileSync(fixture.receiptPath, 'utf8')) as Record<
        string,
        unknown
      >;
      mutate(value);
      fs.writeFileSync(fixture.receiptPath, `${JSON.stringify(value)}\n`);
      expect(() => verifyReceiptV2(fixture)).toThrow();
    }
  });

  it.each([
    ['predecessor_issue', 180],
    ['predecessor_comment_id', 1],
    ['predecessor_source_commit', '0'.repeat(40)],
    ['predecessor_source_tree', '0'.repeat(40)],
    ['predecessor_receipt_line_sha256', '0'.repeat(64)],
    ['compiled_payload_proof_kind', 'apr-r4-e2p-trusted-proof-payload-v1'],
    ['runner', 'self-hosted'],
    ['dotnet_sdk', 'latest'],
    ['node_version', 'latest'],
    ['rid', 'linux-arm64'],
    ['executable_relative_path', 'other-payload'],
    ['verifier_executable_relative_path', 'other-verifier'],
    ['build_pair_sha256', '0'.repeat(64)],
  ])('rejects closed receipt field drift: %s', (field, replacement) => {
    const fixture = compose();
    const value = JSON.parse(fs.readFileSync(fixture.receiptPath, 'utf8')) as Record<
      string,
      unknown
    >;
    value[field] = replacement;
    fs.writeFileSync(fixture.receiptPath, `${JSON.stringify(value)}\n`);
    expect(() => verifyReceiptV2(fixture)).toThrow();
  });

  it.each([
    ['compiled proof kind', { compiled_payload_proof_kind: 'apr-r4-e2p-trusted-proof-payload-v1' }],
    ['compiled source commit', { compiled_payload_source_commit: '3'.repeat(40) }],
    ['compiled source tree', { compiled_payload_source_tree: '3'.repeat(40) }],
    ['derived build pair', { build_pair_sha256: '0'.repeat(64) }],
  ])('makes the composer reject inconsistent %s inputs', (_name, overrides) => {
    expect(() => compose(overrides)).toThrow();
  });
});
