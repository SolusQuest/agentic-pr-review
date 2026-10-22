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
  'runtime/tests/fixtures/action-host/trusted-proof-payload/deterministic-typed-chat-contract.json',
  'runtime/tests/fixtures/action-host/trusted-proof-payload/proof-control-contract.json',
  'runtime/tests/fixtures/action-host/trusted-proof-payload/stale-window-contract.json',
  'runtime/tests/fixtures/action-host/trusted-proof-payload/preparation-contract-v2.json',
  'runtime/tests/fixtures/action-host/trusted-proof-payload/aot/warning-policy.txt',
  'runtime/tests/fixtures/action-host/trusted-proof-payload/aot/verifier-warning-policy-v2.txt',
  'runtime/tests/fixtures/action-host/cr1-s5-host/contract.json',
  'runtime/tests/ActionHostTrustedProofPayload/CurrentHostTestComposition.cs',
  'runtime/tests/ActionHostTrustedProofPayload/CurrentHostAuthoredRunnerFactory.cs',
  'runtime/tests/ActionHostTrustedProofPayload/CurrentHostAuthoredWork.cs',
  'runtime/tests/ActionHostTrustedProofPayload/AuthoredReviewWork.cs',
  'runtime/tests/ActionHostTrustedProofPayload/TrustedProofPayloadRuntimePorts.cs',
  'runtime/tests/ActionHostTrustedProofVerifier/CurrentHostRecordingWorkObserver.cs',
  'runtime/tests/ActionHostVerifierFixture/FrameworkSupervisor.cs',
  'runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/CurrentHostTraceOracleTests.cs',
  'runtime/tests/ActionHostTrustedProofVerifier/SolusRestored/Program.cs',
  'runtime/tests/ActionHostTrustedProofVerifier/SolusRestored/ScriptedHandler.cs',
  'runtime/tests/SolusToolAdapterFixture/RuntimeTests.cs',
  '.github/workflows/runtime-ci.yml',
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
// Owner409 has a separate current contract; the original V2 validator above
// continues to validate immutable historical evidence only.
export const s5HostContractSha256 =
  'dd31878c4a7019759d3b5f804bf8fa52dc69c8e5a04f67fd738cd9fe6a4ba430';
export const s5PayloadKind = 'apr-cr1-s5-current-host-payload-v1';
const s5Fixed = {
  ...fixed,
  kind: 'apr-cr1-s5-managed-host-proof-v1',
  synthetic_managed_v2_route: 'host-only-current-state-passed',
  owner_issue: 409,
  host_role: 'host-only-structural-records',
  sdk_context_import_qualified: false,
  host_contract_sha256: s5HostContractSha256,
  current_host_observation_domain: 'apr-s5-current-host-typed-work-v1',
  current_host_unit_oracles: 10,
};
export function validateS5HostContract(root) {
  const file = path.join(root, 'runtime/tests/fixtures/action-host/cr1-s5-host/contract.json');
  assert.ok(fs.lstatSync(file).isFile() && !fs.lstatSync(file).isSymbolicLink());
  const bytes = fs.readFileSync(file);
  assert.equal(sha256(bytes), s5HostContractSha256);
  const contract = JSON.parse(bytes);
  for (const entry of contract.original_evidence) {
    const original = path.join(root, entry.path);
    const stat = fs.lstatSync(original);
    assert.ok(stat.isFile() && !stat.isSymbolicLink());
    assert.equal(sha256(fs.readFileSync(original)), entry.sha256, entry.path);
  }
  for (const disposition of contract.retired_format_assertions) {
    for (const oracle of disposition.required_actual_sdk_oracles) {
      const [relative, name] = oracle.split('#');
      assert.ok(name && fs.readFileSync(path.join(root, relative), 'utf8').includes(name), oracle);
    }
  }
  return contract;
}
export function validateS5ManagedReceipt(receipt) {
  assert.ok(receipt && typeof receipt === 'object' && !Array.isArray(receipt));
  assert.deepEqual(
    Object.keys(receipt).sort(),
    [
      ...Object.keys(s5Fixed),
      'role',
      ...identityFields,
      ...digestFields,
      'v2_cases',
      'preparation_cases',
      'current_host_unit_results_sha256',
    ].sort(),
  );
  for (const [key, value] of Object.entries(s5Fixed)) assert.equal(receipt[key], value, key);
  assert.ok(['first', 'second'].includes(receipt.role));
  for (const key of ['source_commit', 'source_tree']) assert.match(receipt[key], /^[a-f0-9]{40}$/u);
  assert.equal(receipt.compiled_payload_source_commit, receipt.source_commit);
  assert.equal(receipt.compiled_payload_source_tree, receipt.source_tree);
  assert.equal(receipt.compiled_payload_proof_kind, s5PayloadKind);
  for (const key of [
    ...identityFields.filter((key) => key.endsWith('_sha256')),
    ...digestFields,
    'current_host_unit_results_sha256',
  ]) {
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
export function validateS5ManagedReceiptPair(receipts) {
  assert.ok(Array.isArray(receipts) && receipts.length === 2);
  for (const receipt of receipts) validateS5ManagedReceipt(receipt);
  assert.deepEqual(
    receipts.map((receipt) => receipt.role),
    ['first', 'second'],
  );
  for (const key of [
    'source_commit',
    'source_tree',
    'owner_issue',
    'host_contract_sha256',
    'host_role',
    'proof_recipe_sha256',
    'wrapper_bundle_sha256',
  ]) {
    assert.equal(receipts[0][key], receipts[1][key], key);
  }
  // Independent executions emit different PID-bound verifier evidence and
  // independently generated test-result identities even for reproducible DLLs.
  for (const key of [
    'verifier_evidence_sha256',
    'build_pair_sha256',
    'current_host_unit_results_sha256',
  ]) {
    assert.notEqual(receipts[0][key], receipts[1][key], key);
  }
}
export const s5HostUnitCases = [
  'CurrentHostTraceOracleTests.ActualAuthoredCurrentWorkTraceIsAdmittedAndIndependentMutationsAreRejected(successor: False)',
  'CurrentHostTraceOracleTests.ActualAuthoredCurrentWorkTraceIsAdmittedAndIndependentMutationsAreRejected(successor: True)',
  'CurrentHostTraceOracleTests.AuthoredCurrentWorkRejectsImportedHistoryBeforeTools(successor: False, mutation: "continuation")',
  'CurrentHostTraceOracleTests.AuthoredCurrentWorkRejectsImportedHistoryBeforeTools(successor: False, mutation: "protocol-reordered")',
  'CurrentHostTraceOracleTests.AuthoredCurrentWorkRejectsImportedHistoryBeforeTools(successor: False, mutation: "protocol-malformed-relation")',
  'CurrentHostTraceOracleTests.AuthoredCurrentWorkRejectsImportedHistoryBeforeTools(successor: False, mutation: "roles")',
  'CurrentHostTraceOracleTests.AuthoredCurrentWorkRejectsImportedHistoryBeforeTools(successor: True, mutation: "continuation")',
  'CurrentHostTraceOracleTests.AuthoredCurrentWorkRejectsImportedHistoryBeforeTools(successor: True, mutation: "protocol-reordered")',
  'CurrentHostTraceOracleTests.AuthoredCurrentWorkRejectsImportedHistoryBeforeTools(successor: True, mutation: "protocol-malformed-relation")',
  'CurrentHostTraceOracleTests.AuthoredCurrentWorkRejectsImportedHistoryBeforeTools(successor: True, mutation: "roles")',
]
  .map((name) => 'AgenticPrReview.Runtime.Tests.Host.Action.' + name)
  .sort();
export function parseS5UnitOracleResults(bytes) {
  assert.ok(Buffer.isBuffer(bytes) && bytes.length > 0 && bytes.length <= 4 * 1024 * 1024);
  const xml = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  assert.ok(!/<!DOCTYPE|<!ENTITY/u.test(xml));
  assert.equal([...xml.matchAll(/<ResultSummary outcome="Completed">/gu)].length, 1);
  const counters = [...xml.matchAll(/<Counters\b([^>]+)\/>/gu)];
  assert.equal(counters.length, 1);
  const totals = Object.fromEntries(
    [...counters[0][1].matchAll(/([A-Za-z]+)="([0-9]+)"/gu)].map((match) => [
      match[1],
      Number(match[2]),
    ]),
  );
  for (const key of ['failed', 'error', 'timeout', 'aborted']) assert.equal(totals[key], 0);
  assert.ok(totals.executed >= s5HostUnitCases.length && totals.passed >= s5HostUnitCases.length);
  const entities = { '&quot;': '"', '&amp;': '&', '&lt;': '<', '&gt;': '>', '&apos;': "'" };
  const prefix = 'AgenticPrReview.Runtime.Tests.Host.Action.CurrentHost';
  const rows = [];
  for (const match of xml.matchAll(/<UnitTestResult\b([^>]*?)\/?>/gu)) {
    const rawAttributes = [...match[1].matchAll(/([A-Za-z]+)="([^"]*)"/gu)];
    assert.equal(
      new Set(rawAttributes.map((attribute) => attribute[1])).size,
      rawAttributes.length,
    );
    const attributes = Object.fromEntries(
      rawAttributes.map((attribute) => [
        attribute[1],
        attribute[2].replace(/&[^;]+;/gu, (entity) => {
          assert.ok(Object.hasOwn(entities, entity));
          return entities[entity];
        }),
      ]),
    );
    if (!attributes.testName?.startsWith(prefix)) continue;
    assert.equal(attributes.outcome, 'Passed');
    assert.match(attributes.executionId, /^[a-f0-9]{8}-(?:[a-f0-9]{4}-){3}[a-f0-9]{12}$/u);
    rows.push(attributes);
  }
  assert.deepEqual(rows.map((row) => row.testName).sort(), s5HostUnitCases);
  assert.equal(new Set(rows.map((row) => row.executionId)).size, s5HostUnitCases.length);
  return { cases: rows.length, sha256: sha256(bytes) };
}
// This owner409 receipt qualifies only the explicitly historical six-operation
// synthetic wrapper. The protected production builder has its separate gate.
export async function verifyS5FixtureWrapperDigest(root, expectedDigest) {
  const { generateHistoricalTrustedProofFixtureActionBundle } = await import(
    pathToFileURL(path.join(root, 'scripts/build-action.mjs')).href
  );
  const bundle = await generateHistoricalTrustedProofFixtureActionBundle(root);
  assert.equal(expectedDigest, sha256(bundle.bytes));
}

async function main() {
  const [mode, input, root, payloadOrBundle] = process.argv.slice(2);
  assert.equal(process.argv.length, 6);
  if (mode === 'compose') {
    const identity = JSON.parse(fs.readFileSync(input, 'utf8'));
    assert.deepEqual(Object.keys(identity), identityFields);
    validateS5HostContract(root);
    const unitFile = process.env.APR_S5_CURRENT_HOST_UNIT_TRX;
    assert.ok(
      unitFile && fs.lstatSync(unitFile).isFile() && !fs.lstatSync(unitFile).isSymbolicLink(),
    );
    const units = parseS5UnitOracleResults(fs.readFileSync(unitFile));
    const receipt = {
      ...s5Fixed,
      role: process.env.APR_D5_REGRESSION_ROLE ?? 'first',
      ...identity,
      wrapper_bundle_sha256: sha256(fs.readFileSync(payloadOrBundle)),
      proof_recipe_sha256: recipeDigest(root),
      v2_cases: v2Cases,
      preparation_cases: preparationCases,
      current_host_unit_results_sha256: units.sha256,
    };
    assert.equal(units.cases, receipt.current_host_unit_oracles);
    validateS5ManagedReceipt(receipt);
    // The caller publishes this line only after every preparation case succeeds.
    process.stdout.write('APR_D5_V2_MANAGED_PROOF ' + JSON.stringify(receipt) + '\n');
  } else if (mode === 'verify') {
    const bytes = fs.readFileSync(input);
    assert.ok(bytes.length > 0 && bytes.length <= 32768);
    const text = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes);
    const receipt = JSON.parse(text);
    assert.equal(text, JSON.stringify(receipt) + '\n');
    validateS5ManagedReceipt(receipt);
    validateS5HostContract(root);
    assert.equal(receipt.proof_recipe_sha256, recipeDigest(root));
    assert.equal(receipt.payload_sha256, sha256(fs.readFileSync(payloadOrBundle)));
    await verifyS5FixtureWrapperDigest(root, receipt.wrapper_bundle_sha256);
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
