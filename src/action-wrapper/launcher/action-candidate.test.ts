import { access, mkdir, mkdtemp, rename, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { createManagedScratch, reopenManagedScratch } from './managed-scratch-owner.js';
import { cleanupActionCandidate } from './action-candidate.js';

const roots: string[] = [];
afterEach(async () => {
  await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })));
});
async function fixture() {
  const root = await mkdtemp(path.join(tmpdir(), 'apr-candidate-test-'));
  roots.push(root);
  const runnerTemp = path.join(root, 'runner'),
    workspace = path.join(root, 'workspace');
  await mkdir(runnerTemp, { mode: 0o700 });
  await mkdir(workspace, { mode: 0o700 });
  const owner = await createManagedScratch(runnerTemp);
  const identity = owner.identity;
  await owner.detachForHandoff();
  return { root, runnerTemp, workspace, identity, encoded: JSON.stringify(identity) };
}

describe.skipIf(process.platform !== 'linux')('preparation post ownership', () => {
  it('cleans an unclaimed exact owner once and accepts a subsequent post', async () => {
    const f = await fixture();
    await writeFile(path.join(f.identity.tempRoot, 'partial'), 'no secrets');
    await cleanupActionCandidate(f.encoded, f.runnerTemp, f.workspace);
    await expect(access(f.identity.tempRoot)).rejects.toThrow();
    await cleanupActionCandidate(f.encoded, f.runnerTemp, f.workspace);
  });
  it('retains a launch claim and its files without reclaiming its lifetime', async () => {
    const f = await fixture();
    await mkdir(path.join(f.identity.tempRoot, 'claimed'), { mode: 0o700 });
    await writeFile(path.join(f.identity.tempRoot, 'retained'), 'active SDK data');
    await cleanupActionCandidate(f.encoded, f.runnerTemp, f.workspace);
    await access(path.join(f.identity.tempRoot, 'retained'));
    const launch = await reopenManagedScratch(f.identity);
    await launch.cleanup();
  });
  it('refuses a replacement directory and preserves both original and replacement', async () => {
    const f = await fixture();
    const original = f.identity.tempRoot + '-old';
    await rename(f.identity.tempRoot, original);
    await mkdir(f.identity.tempRoot, { mode: 0o700 });
    await writeFile(path.join(f.identity.tempRoot, 'preserved'), 'replacement');
    await expect(cleanupActionCandidate(f.encoded, f.runnerTemp, f.workspace)).rejects.toThrow();
    await access(original);
    await access(path.join(f.identity.tempRoot, 'preserved'));
  });
  it('refuses a changed cleanup identity or reviewed-workspace candidate', async () => {
    const f = await fixture();
    for (const encoded of [
      JSON.stringify({ ...f.identity, rootIno: '0' }),
      JSON.stringify({ ...f.identity, extra: true }),
    ])
      await expect(cleanupActionCandidate(encoded, f.runnerTemp, f.workspace)).rejects.toThrow();
    await expect(cleanupActionCandidate(f.encoded, f.runnerTemp, f.runnerTemp)).rejects.toThrow();
    await access(f.identity.tempRoot);
  });
});
