import { access, mkdir, mkdtemp, readdir, rm, stat, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { acquireInPayloadStaging } from './payload-staging.js';

const roots: string[] = [];
afterEach(async () => {
  await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })));
});
async function fixture() {
  const root = await mkdtemp(path.join(tmpdir(), 'apr-owned-staging-'));
  roots.push(root);
  const runnerTemp = path.join(root, 'runner-temp'),
    workspace = path.join(root, 'workspace');
  await Promise.all([mkdir(runnerTemp), mkdir(workspace)]);
  return { root, runnerTemp, workspace };
}

describe.skipIf(process.platform !== 'linux')('launcher-owned payload parent', () => {
  it('owns a unique 0700 child until payload disposal, then disposes exactly once in order', async () => {
    const context = await fixture();
    const parents: string[] = [];
    const acquire = async (parent: string) => {
      expect(path.dirname(parent)).toBe(context.runnerTemp);
      expect((await stat(parent)).mode & 0o777).toBe(0o700);
      parents.push(parent);
      const payloadRoot = path.join(parent, 'payload');
      await mkdir(payloadRoot);
      const dispose = vi.fn(async () => {
        await access(parent);
        await rm(payloadRoot, { recursive: true });
      });
      return { payloadRoot, dispose };
    };
    const first = await acquireInPayloadStaging({ ...context, acquire });
    const second = await acquireInPayloadStaging({ ...context, acquire });
    expect(parents[0]).not.toBe(parents[1]);
    await Promise.all([first.dispose(), first.dispose(), second.dispose()]);
    await first.dispose();
    expect(await readdir(context.runnerTemp)).toEqual([]);
  });
  it('cleans the parent on acquisition failure while preserving the failure', async () => {
    const context = await fixture();
    const failure = new Error('acquisition-failed');
    await expect(
      acquireInPayloadStaging({
        ...context,
        acquire: async (parent) => {
          await writeFile(path.join(parent, 'partial'), 'partial');
          throw failure;
        },
      }),
    ).rejects.toBe(failure);
    expect(await readdir(context.runnerTemp)).toEqual([]);
  });
  it('cleans after failed payload disposal and preserves the same failure on repeated disposal', async () => {
    const context = await fixture();
    const failure = new Error('disposal-failed');
    const dispose = vi.fn(async () => {
      throw failure;
    });
    const payload = await acquireInPayloadStaging({
      ...context,
      acquire: async (parent) => {
        await mkdir(path.join(parent, 'payload'));
        return { dispose };
      },
    });
    await expect(payload.dispose()).rejects.toBe(failure);
    await expect(payload.dispose()).rejects.toBe(failure);
    expect(dispose).toHaveBeenCalledTimes(1);
    expect(await readdir(context.runnerTemp)).toEqual([]);
  });
  it.each(['relative', 'missing', 'file', 'workspace', 'nested', 'symlink-workspace'])(
    'rejects unsuitable runner storage before acquisition: %s',
    async (kind) => {
      const context = await fixture();
      let runnerTemp = context.runnerTemp;
      if (kind === 'relative') runnerTemp = 'relative';
      if (kind === 'missing') runnerTemp = path.join(context.root, 'missing');
      if (kind === 'file') {
        runnerTemp = path.join(context.root, 'file');
        await writeFile(runnerTemp, 'file');
      }
      if (kind === 'workspace') runnerTemp = context.workspace;
      if (kind === 'nested') {
        runnerTemp = path.join(context.workspace, 'nested');
        await mkdir(runnerTemp);
      }
      if (kind === 'symlink-workspace') {
        runnerTemp = path.join(context.root, 'alias');
        await symlink(context.workspace, runnerTemp);
      }
      const acquire = vi.fn();
      await expect(acquireInPayloadStaging({ ...context, runnerTemp, acquire })).rejects.toThrow();
      expect(acquire).not.toHaveBeenCalled();
      expect(await readdir(context.runnerTemp)).toEqual([]);
    },
  );
});
