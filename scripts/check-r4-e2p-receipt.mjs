import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

function fail(code) {
  throw new Error(`APR_R4_E2P_RECEIPT_INVALID ${code}`);
}

function sha256(bytes) {
  return crypto.createHash('sha256').update(bytes).digest('hex');
}

function read(pathname, maximum = 256 * 1024) {
  const bytes = fs.readFileSync(pathname);
  if (bytes.length === 0 || bytes.length > maximum) fail('file-size');
  return bytes;
}

function parseCanonical(pathname) {
  const bytes = read(pathname, 16_384);
  if (bytes.at(-1) !== 0x0a || bytes.includes(0x0d)) fail('encoding');
  let value;
  try {
    value = JSON.parse(bytes.toString('utf8'));
  } catch {
    fail('json');
  }
  if (`${JSON.stringify(value)}\n` !== bytes.toString('utf8')) fail('canonical');
  return { bytes, value };
}

function exactKeys(value, keys, label) {
  if (
    value === null ||
    Array.isArray(value) ||
    typeof value !== 'object' ||
    JSON.stringify(Object.keys(value)) !== JSON.stringify(keys)
  ) {
    fail(`${label}-keys`);
  }
}

function lowerHex(value, length) {
  return typeof value === 'string' && new RegExp(`^[0-9a-f]{${length}}$`, 'u').test(value);
}

export function verifySealedReceipt({ receiptPath, sourceRoot }) {
  const contractPath = path.join(
    sourceRoot,
    'runtime/tests/fixtures/action-host/trusted-proof-payload/aot/receipt-contract.json',
  );
  const contractBytes = read(contractPath, 16_384);
  const contract = JSON.parse(contractBytes.toString('utf8'));
  exactKeys(contract, ['kind', 'receipt_kind', 'proof_role', 'ordered_fields'], 'contract');
  const { value: receipt } = parseCanonical(receiptPath);
  exactKeys(receipt, contract.ordered_fields, 'receipt');
  if (
    receipt.kind !== contract.receipt_kind ||
    receipt.proof_role !== contract.proof_role ||
    receipt.predecessor_issue !== 179 ||
    receipt.predecessor_comment_id !== 5372084844 ||
    receipt.predecessor_source_commit !== '0b5c96a6fea12906024c68b3d8457ccb7b026ebe' ||
    receipt.predecessor_source_tree !== '8c4fde16f9aaefedb5a715524d9f945c5c3d0d02' ||
    receipt.predecessor_receipt_line_sha256 !==
      '89fbdf016aae3ca2737fe0fb91fb6cc7e4b50761058ddbe881819550e9337e24' ||
    receipt.runner !== 'ubuntu-24.04' ||
    receipt.dotnet_sdk !== '10.0.109' ||
    receipt.node_version !== '24' ||
    receipt.rid !== 'linux-x64' ||
    receipt.executable_relative_path !== 'AgenticPrReview.Runtime.ActionHostTrustedProofPayload' ||
    receipt.wrapper_build_discriminator !== 'r4-w2' ||
    receipt.payload_build_discriminator !== 'r4-w2' ||
    receipt.production_payload_smoke !== 'passed' ||
    receipt.verifier_kind !== 'apr-r4-e2p-trusted-proof-verifier-v1' ||
    receipt.verifier_role !== 'r4-e2p-verifier' ||
    receipt.verifier_executable_relative_path !==
      'AgenticPrReview.Runtime.ActionHostTrustedProofVerifier' ||
    receipt.synthetic_native_aot_route !== 'passed' ||
    receipt.standalone_default_github !== 'not_executed_e4_owned' ||
    receipt.result !== 'passed' ||
    !lowerHex(receipt.source_commit, 40) ||
    !lowerHex(receipt.source_tree, 40) ||
    receipt.action_source_sha !== receipt.source_commit ||
    contract.ordered_fields
      .filter((key) => key.endsWith('_sha256'))
      .some((key) => !lowerHex(receipt[key], 64))
  ) {
    fail('values');
  }
  if (
    receipt.proof_managed_intermediate_sha256 !==
      receipt.verifier_payload_managed_intermediate_sha256 ||
    receipt.runtime_managed_intermediate_sha256 !==
      receipt.verifier_runtime_managed_intermediate_sha256
  ) {
    fail('shared-managed-identity');
  }
  if (receipt.receipt_contract_sha256 !== sha256(contractBytes)) fail('contract-digest');
  return receipt;
}

export function verifyReceipt({ receiptPath, sourceRoot, payloadPath }) {
  const receipt = verifySealedReceipt({ receiptPath, sourceRoot });
  const fixedFiles = new Map([
    ['action_metadata_sha256', '.github/actions/agentic-pr-review/action.yml'],
    ['wrapper_bundle_sha256', '.github/actions/agentic-pr-review/dist/index.js'],
    [
      'workflow_topology_sha256',
      'runtime/tests/fixtures/action-host/trusted-proof-payload/workflow/r4-trusted-proof.yml.template',
    ],
    [
      'preflight_contract_sha256',
      'runtime/tests/fixtures/action-host/trusted-proof-payload/workflow/preflight-contract.json',
    ],
    [
      'deterministic_provider_contract_sha256',
      'runtime/tests/fixtures/action-host/trusted-proof-payload/deterministic-provider-contract.json',
    ],
    [
      'proof_control_contract_sha256',
      'runtime/tests/fixtures/action-host/trusted-proof-payload/proof-control-contract.json',
    ],
    [
      'stale_window_contract_sha256',
      'runtime/tests/fixtures/action-host/trusted-proof-payload/stale-window-contract.json',
    ],
    ['trusted_config_sha256', '.github/agentic-pr-review/trusted-proof.json'],
    ['trusted_instructions_sha256', '.github/agentic-pr-review/trusted-proof-instructions.md'],
    [
      'preparation_contract_sha256',
      'runtime/tests/fixtures/action-host/trusted-proof-payload/preparation-contract.json',
    ],
    ['preparation_script_sha256', 'runtime/scripts/prepare-r4-trusted-proof-payload.sh'],
    [
      'aot_warning_policy_sha256',
      'runtime/tests/fixtures/action-host/trusted-proof-payload/aot/warning-policy.txt',
    ],
    [
      'verifier_aot_warning_policy_sha256',
      'runtime/tests/fixtures/action-host/trusted-proof-payload/aot/verifier-warning-policy.txt',
    ],
  ]);
  for (const [field, relative] of fixedFiles) {
    if (receipt[field] !== sha256(read(path.join(sourceRoot, relative), 8 * 1024 * 1024))) {
      fail(`digest-${field}`);
    }
  }
  if (payloadPath && receipt.payload_sha256 !== sha256(read(payloadPath, 256 * 1024 * 1024))) {
    fail('payload-digest');
  }
  return receipt;
}

function main() {
  const names = ['--receipt', '--source-root', '--payload'];
  if (process.argv.length !== 2 + names.length * 2) fail('usage');
  const options = new Map();
  for (let index = 2; index < process.argv.length; index += 2) {
    if (!names.includes(process.argv[index]) || options.has(process.argv[index])) fail('arguments');
    options.set(process.argv[index], process.argv[index + 1]);
  }
  verifyReceipt({
    receiptPath: options.get('--receipt'),
    sourceRoot: options.get('--source-root'),
    payloadPath: options.get('--payload'),
  });
  process.stdout.write('APR_R4_E2P_RECEIPT_OK\n');
}

if (import.meta.url === `file://${process.argv[1]?.replaceAll('\\', '/')}`) {
  try {
    main();
  } catch (error) {
    process.stderr.write(
      `${error instanceof Error ? error.message : 'APR_R4_E2P_RECEIPT_INVALID'}\n`,
    );
    process.exitCode = 1;
  }
}
