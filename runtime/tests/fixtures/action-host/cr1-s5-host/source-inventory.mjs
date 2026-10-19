import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

const originalDigest = 'ae39a72d21060c36b9e632040a8be0131e138ae9a27b0c1e0bbd098de8fa705e';
const goldenPath = 'runtime/tests/fixtures/action-host/cr1-r8-typed/expected-evidence.json.golden';
const goldenSha256 = '01f52ec5a6f8669af5af9cfc60a10783b3e0b25c17a7ba6cdbb04fe60962e1f7';
const sourceRoot = 'runtime/tests/ActionHostVerifierFixture';
const changedSource = sourceRoot + '/FrameworkSupervisor.cs';
const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex');
const exactKeys = (value, keys) => assert.deepEqual(Object.keys(value).sort(), keys.slice().sort());
const inventoryDigest = (rows, field) =>
  sha256(rows.map((row) => row.path + '\0' + row[field] + '\n').join(''));

export function verifyS5FrameworkSourceInventory(repository, supplied) {
  const inventory =
    supplied ??
    JSON.parse(
      fs.readFileSync(
        path.join(
          repository,
          'runtime/tests/fixtures/action-host/cr1-s5-host/framework-source-inventory.json',
        ),
        'utf8',
      ),
    );
  exactKeys(inventory, [
    'kind',
    'owner_issue',
    'retirement_source_commit',
    'retirement_source_tree',
    'historical_golden',
    'current_role',
    'sdk_context_import_qualified',
    'current_source_inventory_digest',
    'sources',
  ]);
  assert.equal(inventory.kind, 'apr-cr1-s5-framework-source-inventory-v1');
  assert.equal(inventory.owner_issue, 409);
  assert.equal(inventory.retirement_source_commit, 'eb2d4d0a0380cf6083fa6dc8f9ebee8c904dc4e2');
  assert.equal(inventory.retirement_source_tree, '484c40c6aae3087428fb5169a18685eb744aa590');
  assert.equal(inventory.current_role, 'host-only-structural-records');
  assert.equal(inventory.sdk_context_import_qualified, false);
  assert.deepEqual(inventory.historical_golden, {
    path: goldenPath,
    sha256: goldenSha256,
    source_inventory_digest: originalDigest,
    qualification: 'immutable-historical',
  });
  const goldenFile = path.join(repository, goldenPath);
  assert.ok(fs.lstatSync(goldenFile).isFile() && !fs.lstatSync(goldenFile).isSymbolicLink());
  const golden = fs.readFileSync(goldenFile);
  assert.equal(sha256(golden), goldenSha256);
  assert.equal(JSON.parse(golden).source_inventory_digest, originalDigest);
  assert.ok(Array.isArray(inventory.sources));
  assert.equal(inventory.sources.length, 18);
  const declared = inventory.sources.map((row) => row.path);
  assert.equal(new Set(declared).size, declared.length);
  assert.deepEqual(declared, declared.slice().sort());
  for (const row of inventory.sources) {
    exactKeys(row, ['path', 'original_sha256', 'current_sha256', 'disposition']);
    assert.ok(row.path.startsWith(sourceRoot + '/') && !row.path.includes('..'));
    assert.match(row.original_sha256, /^[a-f0-9]{64}$/u);
    assert.match(row.current_sha256, /^[a-f0-9]{64}$/u);
    if (row.path === changedSource) {
      assert.equal(row.disposition, 'current-Host-only-successor');
      assert.notEqual(row.current_sha256, row.original_sha256);
    } else {
      assert.equal(row.disposition, 'unchanged');
      assert.equal(row.current_sha256, row.original_sha256);
    }
  }
  // The original digest binds both the complete path list and original file bytes.
  assert.equal(inventoryDigest(inventory.sources, 'original_sha256'), originalDigest);
  const actual = [];
  function visit(relative) {
    const full = path.join(repository, relative);
    const stat = fs.lstatSync(full);
    assert.ok(!stat.isSymbolicLink(), relative);
    if (stat.isDirectory()) {
      for (const name of fs.readdirSync(full).sort()) {
        if (name !== 'bin' && name !== 'obj') visit(relative + '/' + name);
      }
    } else {
      assert.ok(stat.isFile(), relative);
      actual.push({ path: relative, current_sha256: sha256(fs.readFileSync(full)) });
    }
  }
  visit(sourceRoot);
  // Use codepoint order for the wire framing, independent of filesystem ordering.
  actual.sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0));
  assert.deepEqual(
    actual.map((row) => row.path),
    declared,
  );
  assert.deepEqual(
    actual,
    inventory.sources.map(({ path, current_sha256 }) => ({ path, current_sha256 })),
  );
  const digest = inventoryDigest(actual, 'current_sha256');
  assert.equal(digest, inventory.current_source_inventory_digest);
  assert.notEqual(digest, originalDigest);
  return {
    sourceCount: actual.length,
    historicalDigest: originalDigest,
    currentDigest: digest,
    sdkContextImportQualified: false,
  };
}
