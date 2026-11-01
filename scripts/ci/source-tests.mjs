import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const shardCount = 6;
const project = fileURLToPath(new URL('../../', import.meta.url));
const weightsPath = fileURLToPath(new URL('./source-test-weights.json', import.meta.url));

export function verifyPartition(files, shards) {
  assert.ok(Array.isArray(files) && files.length >= shardCount);
  assert.equal(new Set(files).size, files.length, 'Duplicate discovered test');
  assert.equal(shards.length, shardCount, 'Closed six-shard inventory');
  assert.ok(
    shards.every((shard) => Array.isArray(shard) && shard.length > 0),
    'Empty shard',
  );
  const selected = shards.flat();
  assert.equal(new Set(selected).size, selected.length, 'Duplicate shard member');
  assert.deepEqual([...selected].sort(), [...files].sort(), 'Incomplete shard union');
}

export function planShards(files, weights = {}) {
  assert.ok(Array.isArray(files));
  for (const file of files) {
    assert.equal(typeof file, 'string');
    assert.ok(file.length > 0 && !path.isAbsolute(file) && !file.split('/').includes('..'));
  }
  assert.ok(weights && typeof weights === 'object' && !Array.isArray(weights));
  for (const weight of Object.values(weights))
    assert.ok(Number.isFinite(weight) && weight > 0 && weight <= 3_600_000);
  const weight = (file) => (Object.hasOwn(weights, file) ? weights[file] : 1000);
  const shards = Array.from({ length: shardCount }, () => []);
  const loads = Array(shardCount).fill(0);
  for (const file of [...files].sort((a, b) => weight(b) - weight(a) || a.localeCompare(b, 'en'))) {
    const index = loads.indexOf(Math.min(...loads));
    shards[index].push(file);
    loads[index] += weight(file);
  }
  for (const shard of shards) shard.sort();
  verifyPartition(files, shards);
  return { shards, estimatedMilliseconds: loads };
}

export function discoverTests(root = project, filters = []) {
  const text = execFileSync(
    process.execPath,
    [
      path.join(root, 'node_modules/vitest/vitest.mjs'),
      'list',
      ...filters,
      '--filesOnly',
      '--json',
    ],
    { cwd: root, encoding: 'utf8', maxBuffer: 8 * 1024 * 1024 },
  );
  const entries = JSON.parse(text);
  assert.ok(Array.isArray(entries) && entries.length > 0, 'No discovered tests');
  const files = entries
    .map((entry) => {
      assert.ok(entry && typeof entry.file === 'string');
      const absolute = path.resolve(root, entry.file);
      const relative = path.relative(root, absolute).split(path.sep).join('/');
      assert.ok(relative && !relative.startsWith('../') && !path.isAbsolute(relative));
      assert.equal(fs.realpathSync(absolute), absolute, 'Test must not redirect through a symlink');
      return relative;
    })
    .sort();
  assert.equal(new Set(files).size, files.length, 'Duplicate discovery');
  return files;
}

export function verifySelection(planned, actual) {
  assert.deepEqual(
    [...actual].sort(),
    [...planned].sort(),
    'Vitest filters changed shard coverage',
  );
}

function main(args) {
  assert.ok(
    (args.length === 1 && args[0] === '--plan') ||
      (args.length === 2 && args[0] === '--shard' && /^[1-6]$/u.test(args[1])),
    'Usage: source-tests.mjs --plan | --shard <1..6>',
  );
  const files = discoverTests();
  const plan = planShards(files, JSON.parse(fs.readFileSync(weightsPath, 'utf8')));
  if (args[0] === '--plan') {
    console.log(JSON.stringify({ files, ...plan }, null, 2));
    return;
  }
  const index = Number(args[1]) - 1;
  const selected = plan.shards[index];
  verifySelection(selected, discoverTests(project, selected));
  const git = (...gitArgs) =>
    execFileSync('git', gitArgs, { cwd: project, encoding: 'utf8' }).trim();
  const head = git('rev-parse', 'HEAD');
  if (process.env.GITHUB_ACTIONS === 'true') {
    assert.match(process.env.EXPECTED_SOURCE_SHA ?? '', /^[a-f0-9]{40}$/u);
    assert.equal(head, process.env.EXPECTED_SOURCE_SHA);
  }
  console.log(
    JSON.stringify({
      sourceCommit: head,
      sourceTree: git('rev-parse', 'HEAD^{tree}'),
      censusSha256: createHash('sha256').update(JSON.stringify(files)).digest('hex'),
      shard: index + 1,
      shardCount,
      selected,
    }),
  );
  const result = spawnSync(
    process.execPath,
    [
      path.join(project, 'node_modules/vitest/vitest.mjs'),
      'run',
      ...selected,
      '--maxWorkers=1',
      '--minWorkers=1',
    ],
    { cwd: project, stdio: 'inherit' },
  );
  if (result.error || result.signal || result.status !== 0) process.exit(1);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url))
  main(process.argv.slice(2));
