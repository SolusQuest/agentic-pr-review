import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

const actionSourceSha = '5b5769753653bb3fd3e68cf8b7bb88a1bd350613';
const partitionFields = [
  'kind',
  'total_record_count',
  'live_anchor_count',
  'transient_record_count',
  'internally_reconciled_count',
  'cleanup_self_deleted_count',
  'live_anchor_object_identities',
  'internally_reconciled_object_identities',
  'cleanup_self_deleted_object_identities',
  'transaction_route_evidence_sha256',
];

function fail(code) {
  throw new Error(`APR_R4_E2P_RECEIPT_V2_INVALID ${code}`);
}

function sha256(bytes) {
  return crypto.createHash('sha256').update(bytes).digest('hex');
}

function read(pathname, maximum = 256 * 1024) {
  const bytes = fs.readFileSync(pathname);
  if (bytes.length === 0 || bytes.length > maximum) fail('file-size');
  return bytes;
}

function canonical(pathname, maximum = 32_768) {
  const bytes = read(pathname, maximum);
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
  )
    fail(`${label}-keys`);
}

function lowerHex(value, length) {
  return typeof value === 'string' && new RegExp(`^[0-9a-f]{${length}}$`, 'u').test(value);
}

function exactIdentitySet(value, length, label) {
  if (
    !Array.isArray(value) ||
    value.length !== length ||
    value.some((item) => !lowerHex(item, 64)) ||
    JSON.stringify(value) !== JSON.stringify([...value].sort()) ||
    new Set(value).size !== value.length
  )
    fail(label);
}

function verifyPartition(receipt) {
  const value = receipt.transaction_partition;
  exactKeys(value, partitionFields, 'partition');
  if (
    value.kind !== 'apr-r4-e4-synthetic-transaction-partition-v1' ||
    value.total_record_count !== 35 ||
    value.live_anchor_count !== 7 ||
    value.transient_record_count !== 28 ||
    value.internally_reconciled_count !== 13 ||
    value.cleanup_self_deleted_count !== 15 ||
    value.total_record_count !== value.live_anchor_count + value.transient_record_count ||
    value.transient_record_count !==
      value.internally_reconciled_count + value.cleanup_self_deleted_count
  )
    fail('partition-counts');
  exactIdentitySet(value.live_anchor_object_identities, 7, 'partition-live');
  exactIdentitySet(
    value.internally_reconciled_object_identities,
    13,
    'partition-internally-reconciled',
  );
  exactIdentitySet(
    value.cleanup_self_deleted_object_identities,
    15,
    'partition-cleanup-self-deleted',
  );
  const union = [
    ...value.live_anchor_object_identities,
    ...value.internally_reconciled_object_identities,
    ...value.cleanup_self_deleted_object_identities,
  ];
  if (new Set(union).size !== 35) fail('partition-membership');
  const preimage = {
    kind: value.kind,
    payload_source_commit: receipt.compiled_payload_source_commit,
    payload_source_tree: receipt.compiled_payload_source_tree,
    payload_sha256: receipt.payload_sha256,
    verifier_sha256: receipt.verifier_sha256,
    total_record_count: value.total_record_count,
    live_anchor_count: value.live_anchor_count,
    transient_record_count: value.transient_record_count,
    internally_reconciled_count: value.internally_reconciled_count,
    cleanup_self_deleted_count: value.cleanup_self_deleted_count,
    live_anchor_object_identities: value.live_anchor_object_identities,
    internally_reconciled_object_identities: value.internally_reconciled_object_identities,
    cleanup_self_deleted_object_identities: value.cleanup_self_deleted_object_identities,
  };
  if (value.transaction_route_evidence_sha256 !== sha256(JSON.stringify(preimage))) {
    fail('partition-digest');
  }
}

export function verifyReceiptV2({ receiptPath, sourceRoot, payloadPath }) {
  const contractPath = path.join(
    sourceRoot,
    'runtime/tests/fixtures/action-host/trusted-proof-payload/aot/receipt-contract-v2.json',
  );
  const contractBytes = read(contractPath, 16_384);
  const contract = JSON.parse(contractBytes.toString('utf8'));
  exactKeys(contract, ['kind', 'receipt_kind', 'proof_role', 'ordered_fields'], 'contract');
  if (contract.kind !== 'apr-r4-e2p-receipt-contract-v2') fail('contract-kind');
  const { value: receipt } = canonical(receiptPath);
  exactKeys(receipt, contract.ordered_fields, 'receipt');
  if (
    receipt.kind !== 'apr-r4-e2p-trusted-proof-payload-v2' ||
    receipt.proof_role !== 'r4-e2p' ||
    !lowerHex(receipt.source_commit, 40) ||
    !lowerHex(receipt.source_tree, 40) ||
    receipt.compiled_payload_source_commit !== receipt.source_commit ||
    receipt.compiled_payload_source_tree !== receipt.source_tree ||
    receipt.action_source_sha !== actionSourceSha ||
    receipt.source_commit === receipt.action_source_sha ||
    receipt.wrapper_build_discriminator !== 'r4-w2' ||
    receipt.payload_build_discriminator !== 'r4-w2' ||
    receipt.production_payload_smoke !== 'passed' ||
    receipt.verifier_kind !== 'apr-r4-e2p-trusted-proof-verifier-v1' ||
    receipt.verifier_role !== 'r4-e2p-verifier' ||
    receipt.synthetic_native_aot_route !== 'passed' ||
    receipt.standalone_default_github !== 'not_executed_e4_owned' ||
    receipt.result !== 'passed' ||
    contract.ordered_fields
      .filter((key) => key.endsWith('_sha256'))
      .some((key) => !lowerHex(receipt[key], 64))
  )
    fail('values');
  if (
    receipt.proof_managed_intermediate_sha256 !==
      receipt.verifier_payload_managed_intermediate_sha256 ||
    receipt.runtime_managed_intermediate_sha256 !==
      receipt.verifier_runtime_managed_intermediate_sha256
  )
    fail('shared-managed-identity');
  verifyPartition(receipt);

  const fixedFiles = new Map([
    ['action_metadata_sha256', '.github/actions/agentic-pr-review/action.yml'],
    ['wrapper_bundle_sha256', '.github/actions/agentic-pr-review/dist/index.js'],
    [
      'workflow_topology_sha256',
      'runtime/tests/fixtures/action-host/trusted-proof-payload/workflow/r4-trusted-proof-v2.yml.template',
    ],
    [
      'preflight_contract_sha256',
      'runtime/tests/fixtures/action-host/trusted-proof-payload/workflow/preflight-contract-v2.json',
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
      'runtime/tests/fixtures/action-host/trusted-proof-payload/preparation-contract-v2.json',
    ],
    ['preparation_script_sha256', 'runtime/scripts/prepare-r4-trusted-proof-payload-v2.sh'],
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
    if (receipt[field] !== sha256(read(path.join(sourceRoot, relative), 16 * 1024 * 1024))) {
      fail(`digest-${field}`);
    }
  }
  if (receipt.receipt_contract_sha256 !== sha256(contractBytes)) fail('contract-digest');
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
  verifyReceiptV2({
    receiptPath: options.get('--receipt'),
    sourceRoot: options.get('--source-root'),
    payloadPath: options.get('--payload'),
  });
  process.stdout.write('APR_R4_E2P_RECEIPT_V2_OK\n');
}

if (import.meta.url === `file://${process.argv[1]?.replaceAll('\\', '/')}`) {
  try {
    main();
  } catch (error) {
    process.stderr.write(
      `${error instanceof Error ? error.message : 'APR_R4_E2P_RECEIPT_V2_INVALID'}\n`,
    );
    process.exitCode = 1;
  }
}
