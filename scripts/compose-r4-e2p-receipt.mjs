import crypto from 'node:crypto';
import fs from 'node:fs';

function fail(code) {
  process.stderr.write(`APR_R4_E2P_RECEIPT_INVALID ${code}\n`);
  process.exit(1);
}

function sha256(bytes) {
  return crypto.createHash('sha256').update(bytes).digest('hex');
}

function read(path, maximum = 256 * 1024) {
  const bytes = fs.readFileSync(path);
  if (bytes.length === 0 || bytes.length > maximum) fail('file-size');
  return bytes;
}

function json(path) {
  const bytes = read(path, 16_384);
  return { bytes, value: JSON.parse(bytes.toString('utf8')) };
}

const names = [
  '--identity',
  '--contract',
  '--predecessor',
  '--action',
  '--bundle',
  '--workflow',
  '--preflight-contract',
  '--provider-contract',
  '--control-contract',
  '--stale-contract',
  '--trusted-config',
  '--trusted-instructions',
  '--preparation-contract',
  '--preparation-script',
  '--warning-policy',
  '--verifier-warning-policy',
];
if (process.argv.length !== 2 + names.length * 2) fail('usage');
const options = new Map();
for (let index = 2; index < process.argv.length; index += 2) {
  if (!names.includes(process.argv[index]) || options.has(process.argv[index])) fail('arguments');
  options.set(process.argv[index], process.argv[index + 1]);
}
const identity = json(options.get('--identity')).value;
const contractDocument = json(options.get('--contract'));
const contract = contractDocument.value;
const predecessor = json(options.get('--predecessor')).value;
const receipt = {
  kind: contract.receipt_kind,
  proof_role: contract.proof_role,
  predecessor_issue: predecessor.issue,
  predecessor_comment_id: predecessor.comment_id,
  predecessor_source_commit: predecessor.source_commit,
  predecessor_source_tree: predecessor.source_tree,
  predecessor_receipt_line_sha256: predecessor.receipt_line_sha256,
  source_commit: identity.source_commit,
  source_tree: identity.source_tree,
  runner: 'ubuntu-24.04',
  dotnet_sdk: '10.0.109',
  node_version: '24',
  rid: 'linux-x64',
  executable_relative_path: 'AgenticPrReview.Runtime.ActionHostTrustedProofPayload',
  action_source_sha: identity.source_commit,
  action_metadata_sha256: sha256(read(options.get('--action'), 1024 * 1024)),
  wrapper_bundle_sha256: sha256(read(options.get('--bundle'), 16 * 1024 * 1024)),
  wrapper_build_discriminator: 'r4-w2',
  payload_build_discriminator: 'r4-w2',
  payload_sha256: identity.payload_sha256,
  proof_managed_intermediate_sha256: identity.proof_managed_intermediate_sha256,
  runtime_managed_intermediate_sha256: identity.runtime_managed_intermediate_sha256,
  managed_architecture_sha256: identity.managed_architecture_sha256,
  aot_warning_policy_sha256: sha256(read(options.get('--warning-policy'))),
  production_payload_smoke: 'passed',
  verifier_kind: 'apr-r4-e2p-trusted-proof-verifier-v1',
  verifier_role: 'r4-e2p-verifier',
  verifier_executable_relative_path: 'AgenticPrReview.Runtime.ActionHostTrustedProofVerifier',
  verifier_sha256: identity.verifier_sha256,
  verifier_managed_intermediate_sha256: identity.verifier_managed_intermediate_sha256,
  verifier_payload_managed_intermediate_sha256:
    identity.verifier_payload_managed_intermediate_sha256,
  verifier_runtime_managed_intermediate_sha256:
    identity.verifier_runtime_managed_intermediate_sha256,
  verifier_managed_architecture_sha256: identity.verifier_managed_architecture_sha256,
  verifier_aot_warning_policy_sha256: sha256(read(options.get('--verifier-warning-policy'))),
  verifier_evidence_sha256: identity.verifier_evidence_sha256,
  synthetic_native_aot_route: 'passed',
  standalone_default_github: 'not_executed_e4_owned',
  build_pair_sha256: identity.build_pair_sha256,
  workflow_topology_sha256: sha256(read(options.get('--workflow'))),
  preflight_contract_sha256: sha256(read(options.get('--preflight-contract'))),
  deterministic_provider_contract_sha256: sha256(read(options.get('--provider-contract'))),
  proof_control_contract_sha256: sha256(read(options.get('--control-contract'))),
  stale_window_contract_sha256: sha256(read(options.get('--stale-contract'))),
  trusted_config_sha256: sha256(read(options.get('--trusted-config'))),
  trusted_instructions_sha256: sha256(read(options.get('--trusted-instructions'))),
  preparation_contract_sha256: sha256(read(options.get('--preparation-contract'))),
  preparation_script_sha256: sha256(read(options.get('--preparation-script'))),
  receipt_contract_sha256: sha256(contractDocument.bytes),
  result: 'passed',
};
if (JSON.stringify(Object.keys(receipt)) !== JSON.stringify(contract.ordered_fields)) {
  fail('ordered-fields');
}
const serialized = JSON.stringify(receipt);
if (serialized.length > 8192 || /token|secret|credential|prompt/iu.test(serialized)) {
  fail('public-safety');
}
process.stdout.write(`APR_R4_E2P_RECEIPT ${serialized}\n`);
