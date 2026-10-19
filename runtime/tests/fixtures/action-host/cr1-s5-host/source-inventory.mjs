import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

const originalDigest = 'ae39a72d21060c36b9e632040a8be0131e138ae9a27b0c1e0bbd098de8fa705e';
const goldenPath = 'runtime/tests/fixtures/action-host/cr1-r8-typed/expected-evidence.json.golden';
const goldenSha256 = '01f52ec5a6f8669af5af9cfc60a10783b3e0b25c17a7ba6cdbb04fe60962e1f7';
const sourceRoot = 'runtime/tests/ActionHostVerifierFixture';
const changedSources = [
  sourceRoot + '/AgenticPrReview.Runtime.ActionHostVerifierFixture.csproj',
  sourceRoot + '/FrameworkHost.cs',
  sourceRoot + '/FrameworkProviderHandler.cs',
  sourceRoot + '/FrameworkSupervisor.cs',
];
const observerSource = sourceRoot + '/CurrentFrameworkChatObserver.cs';
const linkedSources = [
  'runtime/tests/ActionHostTrustedProofPayload/CurrentHostTestComposition.cs',
  'runtime/tests/ActionHostTrustedProofPayload/TrustedProofOperationRequestAccounting.cs',
];
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
    'linked_sources',
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
  assert.equal(inventory.sources.length, 19);
  const declared = inventory.sources.map((row) => row.path);
  assert.equal(new Set(declared).size, declared.length);
  assert.deepEqual(declared, declared.slice().sort());
  for (const row of inventory.sources) {
    exactKeys(row, ['path', 'original_sha256', 'current_sha256', 'disposition']);
    assert.ok(row.path.startsWith(sourceRoot + '/') && !row.path.includes('..'));
    if (row.path === observerSource) {
      assert.equal(row.original_sha256, null);
      assert.equal(row.disposition, 's5-current-observer');
      assert.match(row.current_sha256, /^[a-f0-9]{64}$/u);
      continue;
    }
    assert.match(row.original_sha256, /^[a-f0-9]{64}$/u);
    assert.match(row.current_sha256, /^[a-f0-9]{64}$/u);
    if (changedSources.includes(row.path)) {
      assert.equal(row.disposition, 'current-Host-only-successor');
      assert.notEqual(row.current_sha256, row.original_sha256);
    } else {
      assert.equal(row.disposition, 'unchanged');
      assert.equal(row.current_sha256, row.original_sha256);
    }
  }
  // The original digest binds both the complete path list and original file bytes.
  const originalRows = inventory.sources.filter((row) => row.path !== observerSource);
  assert.equal(originalRows.length, 18);
  assert.equal(inventoryDigest(originalRows, 'original_sha256'), originalDigest);
  assert.deepEqual(
    inventory.sources
      .filter((row) => row.disposition === 'current-Host-only-successor')
      .map((row) => row.path),
    changedSources.slice().sort(),
  );
  assert.deepEqual(
    inventory.linked_sources.map((row) => row.path),
    linkedSources,
  );
  for (const row of inventory.linked_sources) {
    exactKeys(row, ['path', 'sha256']);
    const full = path.join(repository, row.path);
    assert.ok(fs.lstatSync(full).isFile() && !fs.lstatSync(full).isSymbolicLink());
    assert.equal(sha256(fs.readFileSync(full)), row.sha256);
    if (row.path.endsWith('/TrustedProofOperationRequestAccounting.cs'))
      assert.equal(row.sha256, '8d329c0cb48e7ff80a0ac8540415afd54e06080b091e3b3211862922e22d11ee');
  }
  const projectPath = sourceRoot + '/AgenticPrReview.Runtime.ActionHostVerifierFixture.csproj';
  const project = fs.readFileSync(path.join(repository, projectPath), 'utf8');
  const actualLinks = [...project.matchAll(/<Compile\s+Include="([^"]+)"/gu)]
    .map((match) => path.posix.normalize(sourceRoot + '/' + match[1].replaceAll('\\', '/')))
    .sort();
  assert.deepEqual(actualLinks, linkedSources);
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
  const closure = [
    ...actual,
    ...inventory.linked_sources.map((row) => ({ path: row.path, current_sha256: row.sha256 })),
  ].sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0));
  const digest = inventoryDigest(closure, 'current_sha256');
  assert.equal(digest, inventory.current_source_inventory_digest);
  assert.notEqual(digest, originalDigest);
  return {
    sourceCount: closure.length,
    historicalDigest: originalDigest,
    currentDigest: digest,
    sdkContextImportQualified: false,
  };
}
