import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { afterEach, describe, expect, it } from 'vitest';
import { verifyReceipt } from './check-r4-e2p-receipt.mjs';

const roots: string[] = [];
afterEach(() => {
  for (const root of roots.splice(0)) fs.rmSync(root, { recursive: true, force: true });
});

function sha256(bytes: Buffer) {
  return crypto.createHash('sha256').update(bytes).digest('hex');
}

function compose() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-r4-e2p-receipt-'));
  roots.push(root);
  const payloadPath = path.join(root, 'payload');
  fs.writeFileSync(payloadPath, 'native-payload');
  const identityPath = path.join(root, 'identity.json');
  fs.writeFileSync(
    identityPath,
    `${JSON.stringify({
      source_commit: '1'.repeat(40),
      source_tree: '2'.repeat(40),
      payload_sha256: sha256(fs.readFileSync(payloadPath)),
      proof_managed_intermediate_sha256: '3'.repeat(64),
      runtime_managed_intermediate_sha256: '4'.repeat(64),
      managed_architecture_sha256: '5'.repeat(64),
      verifier_sha256: '6'.repeat(64),
      verifier_managed_intermediate_sha256: '7'.repeat(64),
      verifier_payload_managed_intermediate_sha256: '3'.repeat(64),
      verifier_runtime_managed_intermediate_sha256: '4'.repeat(64),
      verifier_managed_architecture_sha256: '8'.repeat(64),
      verifier_evidence_sha256: '9'.repeat(64),
      build_pair_sha256: 'a'.repeat(64),
    })}\n`,
  );
  const sourceRoot = process.cwd();
  const args = [
    'scripts/compose-r4-e2p-receipt.mjs',
    '--identity',
    identityPath,
    '--contract',
    'runtime/tests/fixtures/action-host/trusted-proof-payload/aot/receipt-contract.json',
    '--predecessor',
    'runtime/tests/fixtures/action-host/trusted-proof-payload/predecessor-anchor.json',
    '--action',
    '.github/actions/agentic-pr-review/action.yml',
    '--bundle',
    '.github/actions/agentic-pr-review/dist/index.js',
    '--workflow',
    'runtime/tests/fixtures/action-host/trusted-proof-payload/workflow/r4-trusted-proof.yml.template',
    '--preflight-contract',
    'runtime/tests/fixtures/action-host/trusted-proof-payload/workflow/preflight-contract.json',
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
    'runtime/tests/fixtures/action-host/trusted-proof-payload/preparation-contract.json',
    '--preparation-script',
    'runtime/scripts/prepare-r4-trusted-proof-payload.sh',
    '--warning-policy',
    'runtime/tests/fixtures/action-host/trusted-proof-payload/aot/warning-policy.txt',
    '--verifier-warning-policy',
    'runtime/tests/fixtures/action-host/trusted-proof-payload/aot/verifier-warning-policy.txt',
  ];
  const result = spawnSync(process.execPath, args, { cwd: sourceRoot, encoding: 'utf8' });
  expect(result.status, result.stderr).toBe(0);
  expect(result.stdout.match(/^APR_R4_E2P_RECEIPT /gmu)).toHaveLength(1);
  const receiptPath = path.join(root, 'receipt.json');
  fs.writeFileSync(receiptPath, `${result.stdout.slice('APR_R4_E2P_RECEIPT '.length).trim()}\n`);
  return { payloadPath, receiptPath, sourceRoot };
}

describe('R4 E2P supplemental receipt', () => {
  it('proves the offline two-root handoff without occupying the authoritative path', () => {
    const fixtureRoot = path.join(
      process.cwd(),
      'runtime/tests/fixtures/action-host/trusted-proof-payload/two-root-consumer',
    );
    const receiptPath = path.join(
      fixtureRoot,
      'control-root/runtime/tests/fixtures/action-host/trusted-proof/trusted-proof-payload-receipt.json',
    );
    const payloadPath = path.join(fixtureRoot, 'payload-source/synthetic-payload.bin');

    expect(
      fs.existsSync(
        path.join(
          process.cwd(),
          'runtime/tests/fixtures/action-host/trusted-proof/trusted-proof-payload-receipt.json',
        ),
      ),
    ).toBe(false);
    expect(verifyReceipt({ receiptPath, payloadPath, sourceRoot: process.cwd() }).result).toBe(
      'passed',
    );
  });

  it('composes and verifies one canonical offline receipt', () => {
    const fixture = compose();
    const receipt = verifyReceipt(fixture);
    expect(receipt.kind).toBe('apr-r4-e2p-trusted-proof-payload-v1');
    expect(receipt.proof_role).toBe('r4-e2p');
    expect(receipt.payload_build_discriminator).toBe('r4-w2');
  });

  it('rejects unknown fields and payload drift', () => {
    const fixture = compose();
    const value = JSON.parse(fs.readFileSync(fixture.receiptPath, 'utf8'));
    fs.writeFileSync(fixture.receiptPath, `${JSON.stringify({ ...value, extra: true })}\n`);
    expect(() => verifyReceipt(fixture)).toThrow(/receipt-keys/u);

    const second = compose();
    fs.appendFileSync(second.payloadPath, 'drift');
    expect(() => verifyReceipt(second)).toThrow(/payload-digest/u);
  });

  it('rejects noncanonical and stale source identities', () => {
    const fixture = compose();
    const original = fs.readFileSync(fixture.receiptPath, 'utf8');
    fs.writeFileSync(fixture.receiptPath, ` ${original}`);
    expect(() => verifyReceipt(fixture)).toThrow(/canonical/u);

    const second = compose();
    const value = JSON.parse(fs.readFileSync(second.receiptPath, 'utf8'));
    value.action_source_sha = '9'.repeat(40);
    fs.writeFileSync(second.receiptPath, `${JSON.stringify(value)}\n`);
    expect(() => verifyReceipt(second)).toThrow(/values/u);
  });
});
