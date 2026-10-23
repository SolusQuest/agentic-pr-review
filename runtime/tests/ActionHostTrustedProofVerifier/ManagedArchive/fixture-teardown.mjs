// Trusted owner cleanup, only after the isolated PID namespace has ended.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { chmod, lstat, readdir, realpath, rm } from 'node:fs/promises';
import { basename, dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { awaitFixtureGroupClose } from './role-deadline.mjs';
export const TEARDOWN_MS = 30_000;
const success = 'APR_D5_FIXTURE_TEARDOWN_OK\n';
export async function removeFixtureRoot(root) {
  assert.equal(await realpath(root), root);
  assert.equal(dirname(root), await realpath('/tmp'));
  assert.ok(basename(root).startsWith('apr-d5-'));
  const rootInfo = await lstat(root);
  assert.ok(rootInfo.isDirectory() && !rootInfo.isSymbolicLink());
  assert.equal(rootInfo.uid, process.getuid());
  let entries = 0;
  async function reopen(path, depth) {
    assert.ok(depth <= 128 && ++entries <= 262144);
    const stat = await lstat(path);
    if (!stat.isDirectory() || stat.isSymbolicLink()) return;
    assert.equal(stat.uid, process.getuid());
    await chmod(path, 0o700);
    for (const child of await readdir(path)) await reopen(join(path, child), depth + 1);
  }
  await reopen(root, 0);
  await rm(root, { recursive: true, force: true });
}
export async function teardownFixture(root) {
  const child = spawn(process.execPath, [fileURLToPath(import.meta.url), root], {
    detached: true,
    env: { PATH: process.env.PATH ?? '/usr/bin:/bin' },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  const chunks = [];
  let bytes = 0;
  let stderrBytes = 0;
  for (const [stream, isError] of [
    [child.stdout, false],
    [child.stderr, true],
  ])
    stream.on('data', (chunk) => {
      bytes += chunk.length;
      if (isError) stderrBytes += chunk.length;
      else if (bytes <= 128) chunks.push(chunk);
      if (bytes > 128) {
        try {
          process.kill(-child.pid, 'SIGKILL');
        } catch {}
      }
    });
  const result = await awaitFixtureGroupClose(child, TEARDOWN_MS);
  if (
    result.watchdogFired ||
    !result.groupCloseConfirmed ||
    result.code !== 0 ||
    result.signal ||
    stderrBytes ||
    Buffer.concat(chunks).toString() !== success
  )
    throw new Error('d5_fixture_teardown_unconfirmed');
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    assert.equal(process.argv.length, 3);
    await removeFixtureRoot(process.argv[2]);
    process.stdout.write(success);
  } catch {
    process.stdout.write('APR_D5_FIXTURE_TEARDOWN_REFUSED\n');
    process.exitCode = 1;
  }
}
