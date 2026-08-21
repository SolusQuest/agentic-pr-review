import crypto from 'node:crypto';
import fs from 'node:fs';

const migrationBaseCommit = '2337e8ae9d3ca0db88f8a38f36c2f17e46a868fc';
const migrationBaseTree = '17bbdc8e1cb6591112a7c871ffba9108ecf3680f';

function fail(message) {
  process.stderr.write(`APR_R4_E2_RECEIPT_INVALID ${message}\n`);
  process.exit(1);
}

function sha256(value) {
  return crypto.createHash('sha256').update(value).digest('hex');
}

function readJson(path) {
  const bytes = fs.readFileSync(path);
  if (bytes.length < 2 || bytes.length > 16_384) fail('json-size');
  return { bytes, value: JSON.parse(bytes.toString('utf8')) };
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

const names = [
  '--identity',
  '--contract',
  '--source-log',
  '--action',
  '--bundle',
  '--warning-policy',
];
if (process.argv.length !== 2 + names.length * 2) fail('usage');
const options = new Map();
for (let index = 2; index < process.argv.length; index += 2) {
  if (!names.includes(process.argv[index]) || options.has(process.argv[index])) {
    fail('arguments');
  }
  options.set(process.argv[index], process.argv[index + 1]);
}
if (options.size !== names.length) fail('arguments');

const identityKeys = [
  'kind',
  'execution_kind',
  'reflection_json_enabled',
  'dynamic_code_supported',
  'launch_action_source_sha',
  'wrapper_build_discriminator',
  'payload_sha256',
  'managed_intermediate_sha256',
  'runtime_intermediate_sha256',
  'managed_architecture_sha256',
  'build_pair_sha256',
  'e1_normalized_evidence_sha256',
  'source_inventory_digest',
  'replacement_record_digest',
  'base_inventory_digest',
  'canary_table_digest',
];
const identity = readJson(options.get('--identity')).value;
exactKeys(identity, identityKeys, 'identity');
if (
  identity.kind !== 'apr-r4-e2-action-host-native-aot-identity-v1' ||
  identity.execution_kind !== 'native-aot' ||
  identity.reflection_json_enabled !== false ||
  identity.dynamic_code_supported !== false ||
  !lowerHex(identity.launch_action_source_sha, 40) ||
  identity.wrapper_build_discriminator !== 'r4-w2' ||
  identityKeys.slice(6).some((key) => !lowerHex(identity[key], 64))
) {
  fail('identity-values');
}

const contractKeys = [
  'kind',
  'receipt_kind',
  'migration_base_commit',
  'migration_base_tree',
  'ordered_fields',
];
const contractDocument = readJson(options.get('--contract'));
const contract = contractDocument.value;
exactKeys(contract, contractKeys, 'contract');
if (
  contract.kind !== 'apr-r4-e2-action-host-receipt-contract-v1' ||
  contract.receipt_kind !== 'apr-r4-e2-action-host-receipt-v1' ||
  contract.migration_base_commit !== migrationBaseCommit ||
  contract.migration_base_tree !== migrationBaseTree ||
  !Array.isArray(contract.ordered_fields) ||
  new Set(contract.ordered_fields).size !== contract.ordered_fields.length
) {
  fail('contract-values');
}

const sourceLines = fs
  .readFileSync(options.get('--source-log'), 'utf8')
  .split(/\r?\n/u)
  .filter((line) => line !== '');
const commitLines = sourceLines.filter((line) => line.startsWith('APR_R4_W13_SOURCE_COMMIT '));
const treeLines = sourceLines.filter((line) => line.startsWith('APR_R4_W13_SOURCE_TREE '));
if (commitLines.length !== 1 || treeLines.length !== 1) fail('source-markers');
const sourceCommit = commitLines[0].split(' ')[1];
const sourceTree = treeLines[0].split(' ')[1];
if (!lowerHex(sourceCommit, 40) || !lowerHex(sourceTree, 40)) {
  fail('source-identity');
}

const receipt = {
  kind: contract.receipt_kind,
  migration_base_commit: contract.migration_base_commit,
  migration_base_tree: contract.migration_base_tree,
  source_commit: sourceCommit,
  source_tree: sourceTree,
  launch_action_source_sha: identity.launch_action_source_sha,
  action_metadata_sha256: sha256(fs.readFileSync(options.get('--action'))),
  wrapper_bundle_sha256: sha256(fs.readFileSync(options.get('--bundle'))),
  wrapper_build_discriminator: identity.wrapper_build_discriminator,
  payload_sha256: identity.payload_sha256,
  managed_intermediate_sha256: identity.managed_intermediate_sha256,
  runtime_intermediate_sha256: identity.runtime_intermediate_sha256,
  managed_architecture_sha256: identity.managed_architecture_sha256,
  executable_sha256: identity.payload_sha256,
  build_pair_sha256: identity.build_pair_sha256,
  aot_warning_policy_sha256: sha256(fs.readFileSync(options.get('--warning-policy'))),
  e1_normalized_evidence_sha256: identity.e1_normalized_evidence_sha256,
  source_inventory_digest: identity.source_inventory_digest,
  replacement_record_digest: identity.replacement_record_digest,
  base_inventory_digest: identity.base_inventory_digest,
  canary_table_digest: identity.canary_table_digest,
  receipt_contract_sha256: sha256(contractDocument.bytes),
  result: 'passed',
};
exactKeys(receipt, contract.ordered_fields, 'receipt');
const serialized = JSON.stringify(receipt);
if (serialized.length > 4096 || /token|secret|prompt|credential/iu.test(serialized)) {
  fail('public-safety');
}
process.stdout.write(`APR_R4_E2_RECEIPT ${serialized}\n`);
