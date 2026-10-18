import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

export const v2Cases = [
  'dispatch-bootstrap',
  'dispatch-continuation',
  'stale-head',
  'state-visibility-diagnostic',
  'stale-unauthorized-follow-on',
];
export const preparationCases = [
  'traversal',
  'prefix-escape',
  'symlink-parent',
  'dirty-source',
  'replaced-local-checker',
  'stale-receipt',
  'valid-two-root',
];
export const identityFields = [
  'source_commit',
  'source_tree',
  'compiled_payload_source_commit',
  'compiled_payload_source_tree',
  'compiled_payload_proof_kind',
  'payload_sha256',
  'proof_managed_intermediate_sha256',
  'runtime_managed_intermediate_sha256',
  'managed_architecture_sha256',
  'verifier_sha256',
  'verifier_managed_intermediate_sha256',
  'verifier_payload_managed_intermediate_sha256',
  'verifier_runtime_managed_intermediate_sha256',
  'verifier_managed_architecture_sha256',
  'verifier_evidence_sha256',
  'build_pair_sha256',
];
const fixed = {
  kind: 'apr-d5-v2-managed-proof-v1',
  execution_kind: 'framework-single-file',
  production_payload_smoke: 'passed-six-zero-network-cases',
  synthetic_managed_v2_route: 'passed',
  two_root_preparation: 'passed',
  native_aot_disposition: 'historical-only',
  action_source_sha: '5b5769753653bb3fd3e68cf8b7bb88a1bd350613',
  payload_build_discriminator: 'r4-w2',
  archive_qualified: false,
};
const digestFields = ['wrapper_bundle_sha256', 'proof_recipe_sha256'];
const recipeFiles = [
  'runtime/scripts/verify-r4-trusted-proof-payload-v2.sh',
  'runtime/scripts/prepare-r4-trusted-proof-payload-v2-managed.sh',
  'scripts/check-r4-e2p-managed-receipt-v2.mjs',
  'scripts/check-r4-e2p-managed-architecture.mjs',
  'scripts/build-action.mjs',
  'runtime/tests/ActionHostTrustedProofPayload/AgenticPrReview.Runtime.ActionHostTrustedProofPayload.csproj',
  'runtime/tests/ActionHostTrustedProofVerifier/AgenticPrReview.Runtime.ActionHostTrustedProofVerifier.csproj',
  'runtime/tests/fixtures/action-host/trusted-proof-payload/workflow/r4-trusted-proof-v2.yml.template',
  'runtime/tests/fixtures/action-host/trusted-proof-payload/workflow/preflight-contract-v2.json',
  'runtime/tests/fixtures/action-host/trusted-proof-payload/deterministic-provider-contract.json',
  'runtime/tests/fixtures/action-host/trusted-proof-payload/proof-control-contract.json',
  'runtime/tests/fixtures/action-host/trusted-proof-payload/stale-window-contract.json',
  'runtime/tests/fixtures/action-host/trusted-proof-payload/preparation-contract-v2.json',
  'runtime/tests/fixtures/action-host/trusted-proof-payload/aot/warning-policy.txt',
  'runtime/tests/fixtures/action-host/trusted-proof-payload/aot/verifier-warning-policy-v2.txt',
  '.github/actions/agentic-pr-review/action.yml',
  '.github/agentic-pr-review/trusted-proof.json',
  '.github/agentic-pr-review/trusted-proof-instructions.md',
];
export const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex');
export function recipeDigest(root) {
  return sha256(
    recipeFiles
      .map((file) => `${file}\0${sha256(fs.readFileSync(path.join(root, file)))}\n`)
      .join(''),
  );
}
export function buildPair(receipt) {
  return sha256(
    ['apr-r4-e2p-build-pair-v2', ...identityFields.slice(5, -1).map((key) => receipt[key])].join(
      '\n',
    ) + '\n',
  );
}
export function validateManagedV2Receipt(receipt) {
  assert.ok(receipt && typeof receipt === 'object' && !Array.isArray(receipt));
  assert.deepEqual(
    Object.keys(receipt).sort(),
    [
      ...Object.keys(fixed),
      'role',
      ...identityFields,
      ...digestFields,
      'v2_cases',
      'preparation_cases',
    ].sort(),
  );
  for (const [key, value] of Object.entries(fixed)) assert.equal(receipt[key], value, key);
  assert.ok(['first', 'second'].includes(receipt.role));
  for (const key of ['source_commit', 'source_tree']) assert.match(receipt[key], /^[a-f0-9]{40}$/u);
  assert.equal(receipt.compiled_payload_source_commit, receipt.source_commit);
  assert.equal(receipt.compiled_payload_source_tree, receipt.source_tree);
  assert.equal(receipt.compiled_payload_proof_kind, 'apr-r4-e2p-trusted-proof-payload-v2');
  for (const key of [...identityFields.filter((key) => key.endsWith('_sha256')), ...digestFields]) {
    assert.equal(typeof receipt[key], 'string');
    assert.match(receipt[key], /^[a-f0-9]{64}$/u, key);
  }
  assert.equal(
    receipt.proof_managed_intermediate_sha256,
    receipt.verifier_payload_managed_intermediate_sha256,
  );
  assert.equal(
    receipt.runtime_managed_intermediate_sha256,
    receipt.verifier_runtime_managed_intermediate_sha256,
  );
  assert.equal(receipt.build_pair_sha256, buildPair(receipt));
  assert.deepEqual(receipt.v2_cases, v2Cases);
  assert.deepEqual(receipt.preparation_cases, preparationCases);
}
async function main() {
  const [mode, input, root, payloadOrBundle] = process.argv.slice(2);
  assert.equal(process.argv.length, 6);
  if (mode === 'compose') {
    const identity = JSON.parse(fs.readFileSync(input, 'utf8'));
    assert.deepEqual(Object.keys(identity), identityFields);
    const receipt = {
      ...fixed,
      role: process.env.APR_D5_REGRESSION_ROLE ?? 'first',
      ...identity,
      wrapper_bundle_sha256: sha256(fs.readFileSync(payloadOrBundle)),
      proof_recipe_sha256: recipeDigest(root),
      v2_cases: v2Cases,
      preparation_cases: preparationCases,
    };
    validateManagedV2Receipt(receipt);
    // The caller publishes this line only after every preparation case succeeds.
    process.stdout.write('APR_D5_V2_MANAGED_PROOF ' + JSON.stringify(receipt) + '\n');
  } else if (mode === 'verify') {
    const bytes = fs.readFileSync(input);
    assert.ok(bytes.length > 0 && bytes.length <= 32768);
    const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
    const receipt = JSON.parse(text);
    assert.equal(text, JSON.stringify(receipt) + '\n');
    validateManagedV2Receipt(receipt);
    assert.equal(receipt.proof_recipe_sha256, recipeDigest(root));
    assert.equal(receipt.payload_sha256, sha256(fs.readFileSync(payloadOrBundle)));
    const { generateTrustedProofActionBundle } = await import(
      pathToFileURL(path.join(root, 'scripts/build-action.mjs')).href
    );
    const bundle = await generateTrustedProofActionBundle(root);
    assert.equal(receipt.wrapper_bundle_sha256, sha256(bundle.bytes));
    process.stdout.write('APR_D5_V2_MANAGED_RECEIPT_OK\n');
  } else throw new Error('mode');
}
if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    await main();
  } catch {
    process.stderr.write('APR_D5_V2_MANAGED_RECEIPT_INVALID\n');
    process.exitCode = 1;
  }
}
