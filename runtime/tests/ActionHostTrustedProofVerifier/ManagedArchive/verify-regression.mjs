// Public-package, credential-free FDD invariant regression. Not a Solus archive receipt.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
const repo = fileURLToPath(new URL('../../../../', import.meta.url));
const role = process.env.APR_D5_REGRESSION_ROLE ?? 'first';
assert.ok(['first', 'second'].includes(role));
for (const name of [
  'GITHUB_TOKEN',
  'GH_TOKEN',
  'ACTIONS_RUNTIME_TOKEN',
  'DEEPSEEK_API_KEY',
  'OPENAI_API_KEY',
])
  assert.equal(process.env[name] ?? '', '');
function git(args) {
  return spawnSync('git', args, { cwd: repo, encoding: 'utf8' });
}
assert.equal(git(['status', '--porcelain']).stdout, '');
const source = git(['rev-parse', 'HEAD']).stdout.trim(),
  tree = git(['rev-parse', 'HEAD^{tree}']).stdout.trim();
assert.match(source, /^[a-f0-9]{40}$/u);
assert.match(tree, /^[a-f0-9]{40}$/u);
const result = spawnSync('/bin/bash', ['runtime/scripts/verify-action-host.sh', 'framework'], {
  cwd: repo,
  env: process.env,
  encoding: 'utf8',
  timeout: 1_800_000,
  maxBuffer: 4 * 1024 * 1024,
});
assert.equal(result.error, undefined);
assert.equal(result.status, 0, 'd5_fdd_invariants_failed');
assert.equal(git(['status', '--porcelain']).stdout, '');
// The supervisor compares its entire normalized evidence against this owning
// golden before returning success. Hashes bind the corpus and oracle, not a
// claim that private Solus package execution occurred in this public lane.
const oracle = await readFile(
  new URL(
    '../../fixtures/action-host/r7-action-outputs/expected-evidence.json.golden',
    import.meta.url,
  ),
);
const inventory = await readFile(
  new URL('../../fixtures/action-host/framework/e1-base-inventory.json', import.meta.url),
);
const hash = (bytes) => createHash('sha256').update(bytes).digest('hex');
process.stdout.write(
  'APR_D5_MANAGED_REGRESSION ' +
    JSON.stringify({
      kind: 'apr-d5-managed-regression-v1',
      role,
      source_commit: source,
      source_tree: tree,
      execution_kind: 'framework',
      oracle_sha256: hash(oracle),
      invariant_inventory_sha256: hash(inventory),
      archive_qualified: false,
    }) +
    '\n',
);
