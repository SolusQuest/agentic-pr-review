import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { verifyS5FrameworkSourceInventory } from './source-inventory.mjs';

const fixture = 'runtime/tests/fixtures/action-host/';
const historicalPath = fixture + 'cr1-r8-typed/expected-evidence.json.golden';
const historicalSha256 = '01f52ec5a6f8669af5af9cfc60a10783b3e0b25c17a7ba6cdbb04fe60962e1f7';
const recordPath = fixture + 'cr1-r8-typed/replacement-record.json';
const recordSha256 = '5f419cb3247f5cfc731b3f07d322111fd074ab389292de2bb09b4355344bb2e0';
const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex');
const readRegular = (file) => {
  const stat = fs.lstatSync(file);
  assert.ok(stat.isFile() && !stat.isSymbolicLink(), file);
  return fs.readFileSync(file);
};
const exactKeys = (value, keys) => assert.deepEqual(Object.keys(value).sort(), keys.slice().sort());

// Produce a separately owned expectation from immutable historical invariants.
// It never reads execution output. The finite counter/identity changes need
// their own reviewed protocol disposition; arbitrary golden replacement is not
// accepted. Actual independent Host execution remains a separate requirement.
export function createS5FrameworkGolden(repository, supplied) {
  const original = readRegular(path.join(repository, historicalPath));
  assert.equal(sha256(original), historicalSha256);
  assert.equal(sha256(readRegular(path.join(repository, recordPath))), recordSha256);
  const inventory = verifyS5FrameworkSourceInventory(repository);
  const contractBytes = readRegular(
    path.join(repository, fixture + 'cr1-s5-host/framework-expectations.json'),
  );
  assert.equal(
    sha256(contractBytes),
    'aa3a3ceefcd041461a8834017900d0102e5ce98057b44056efab501063de7603',
  );
  const frozen = JSON.parse(contractBytes);
  const contract = supplied ?? frozen;
  assert.deepEqual(contract, frozen);
  exactKeys(contract, [
    'kind',
    'owner_issue',
    'host_role',
    'sdk_context_import_qualified',
    'historical_golden_sha256',
    'source_inventory_digest',
    'counter_dispositions',
    'identity_dispositions',
  ]);
  assert.equal(contract.kind, 'apr-cr1-s5-framework-expectations-v1');
  assert.equal(contract.owner_issue, 409);
  assert.equal(contract.host_role, 'host-only-structural-records');
  assert.equal(contract.sdk_context_import_qualified, false);
  assert.equal(contract.historical_golden_sha256, historicalSha256);
  assert.equal(contract.source_inventory_digest, inventory.currentDigest);
  const expected = JSON.parse(original);
  expected.schema = 'apr.action-host.cr1-s5-current-framework-evidence.v1';
  expected.owner_issue = 409;
  expected.host_role = 'host-only-structural-records';
  expected.sdk_context_import_qualified = false;
  expected.historical_golden_sha256 = historicalSha256;
  expected.observation_domain = 'apr-s5-current-framework-host-work-v1';
  expected.source_inventory_digest = inventory.currentDigest;
  // Keep all original status/call/publication/cancellation/security expectations
  // except the one explicitly authorized current typed admission failure.
  for (const scenario of expected.scenarios) {
    assert.equal(scenario.ContinuationObserved, true);
    delete scenario.ContinuationObserved;
    scenario.CurrentWorkBoundarySatisfied = true;
    if (scenario.name === 'dispatch-cross-head-conflict') {
      assert.equal(scenario.status, 'state_conflict');
      scenario.status = 'authorization_failed';
    }
  }
  assert.equal(expected.scenarios.length, 32);
  assert.equal(expected.continuation.prior_marker_first_request_exact, true);
  assert.equal(expected.continuation.prior_marker_carried_without_reinjection, true);
  delete expected.continuation.prior_marker_first_request_exact;
  delete expected.continuation.prior_marker_carried_without_reinjection;
  expected.continuation.prior_native_history_absent = true;
  expected.continuation.sdk_context_import_qualified = false;
  applyFiniteDispositions(expected, contract);
  return expected;
}

function applyFiniteDispositions(expected, contract) {
  const counters = [
    'scenarios.dispatch-continuation.GitHubRequests',
    'scenarios.dispatch-continuation.StateOperations',
    'scenarios.dispatch-cross-head-conflict.StateOperations',
    'official_bridge.twirp',
    'official_bridge.blob',
    'official_bridge.rest',
    'official_bridge.rest_not_modified',
    'official_bridge.rest_primary',
    'official_bridge.rest_secondary_points',
    'official_bridge.finalize',
    'official_bridge.signed_download',
  ];
  const identities = [
    'continuation.state_identity_digest',
    'normalized_exact_delete_identity_digest',
    'canary_route_coverage_digest',
  ];
  assert.deepEqual(
    contract.counter_dispositions.map((row) => row.path),
    counters,
  );
  assert.deepEqual(
    contract.identity_dispositions.map((row) => row.path),
    identities,
  );
  for (const [rows, numeric] of [
    [contract.counter_dispositions, true],
    [contract.identity_dispositions, false],
  ]) {
    for (const row of rows) {
      exactKeys(row, ['path', 'historical', 'current', 'reason']);
      assert.ok(row.reason.length >= 60);
      const keys = row.path.split('.');
      let target = expected;
      if (keys[0] === 'scenarios') {
        assert.equal(keys.length, 3);
        target = expected.scenarios.find((item) => item.name === keys[1]);
      } else if (keys.length === 2) target = expected[keys[0]];
      else assert.equal(keys.length, 1);
      const key = keys.at(-1);
      assert.equal(target[key], row.historical);
      assert.notEqual(row.current, row.historical);
      if (numeric) assert.ok(Number.isSafeInteger(row.current) && row.current >= 0);
      else assert.match(row.current, /^[a-f0-9]{64}$/u);
      target[key] = row.current;
    }
  }
  // The added claim archive has one Create/Finalize pair and one block plus
  // block-list upload. Extra reads do not add publication/mutation points.
  const delta = Object.fromEntries(
    contract.counter_dispositions.map((row) => [row.path, row.current - row.historical]),
  );
  const finalized = delta['official_bridge.finalize'];
  assert.equal(finalized, 1);
  assert.equal(delta['official_bridge.twirp'], 2 * finalized);
  assert.equal(delta['official_bridge.blob'], 2 * finalized);
  assert.equal(
    delta['official_bridge.rest'],
    delta['official_bridge.rest_not_modified'] + delta['official_bridge.rest_primary'],
  );
  assert.equal(delta['official_bridge.rest_secondary_points'], delta['official_bridge.rest']);
}

export function verifyS5FrameworkEvidence(repository, actual) {
  assert.deepEqual(actual, createS5FrameworkGolden(repository));
  return { cases: actual.scenarios.length, sdkContextImportQualified: false };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  assert.equal(process.argv.length, 4);
  const result = createS5FrameworkGolden(path.resolve(process.argv[2]));
  fs.writeFileSync(process.argv[3], JSON.stringify(result, null, 2) + '\n', {
    flag: 'wx',
    mode: 0o600,
  });
}
