import * as fs from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { createManagedScratch } from './managed-scratch-owner.js';

const roots: string[] = [];
afterEach(async () => {
  vi.restoreAllMocks();
  await Promise.all(roots.splice(0).map((root) => fs.rm(root, { recursive: true, force: true })));
});

async function fixture() {
  const parent = await fs.mkdtemp(path.join(tmpdir(), 'apr-scratch-owner-'));
  roots.push(parent);
  return { parent, owner: await createManagedScratch(parent) };
}

describe.runIf(process.platform === 'linux')('managed bridge scratch ownership', () => {
  it('removes sealed nested directories without changing external symlink or hardlink targets', async () => {
    const { parent, owner } = await fixture();
    const external = path.join(parent, 'outside');
    await fs.mkdir(external, { mode: 0o750 });
    const file = path.join(external, 'retained');
    await fs.writeFile(file, 'outside bytes', { mode: 0o640 });
    const nested = path.join(owner.tempRoot, 'sealed', 'nested');
    await fs.mkdir(nested, { recursive: true });
    await fs.symlink(external, path.join(nested, 'directory-link'));
    await fs.symlink(file, path.join(nested, 'file-link'));
    await fs.link(file, path.join(nested, 'hard-link'));
    await fs.writeFile(path.join(nested, 'private'), 'private bytes', { mode: 0o400 });
    await fs.chmod(nested, 0o500);
    await fs.chmod(path.dirname(nested), 0o500);
    await fs.chmod(owner.tempRoot, 0o500);
    await owner.cleanup();
    await owner.cleanup();
    await expect(fs.stat(owner.tempRoot)).rejects.toMatchObject({ code: 'ENOENT' });
    expect(await fs.readFile(file, 'utf8')).toBe('outside bytes');
    expect((await fs.stat(file)).mode & 0o777).toBe(0o640);
    expect((await fs.stat(external)).mode & 0o777).toBe(0o750);
  });

  it.each(['root', 'parent'] as const)('retains both owners after %s replacement', async (kind) => {
    const { parent, owner } = await fixture();
    const original = kind === 'root' ? owner.tempRoot : parent;
    const moved = `${original}-moved`;
    if (kind === 'parent') roots.push(moved);
    await fs.rename(original, moved);
    await fs.mkdir(original, { mode: 0o700 });
    const sentinel = path.join(original, 'sentinel');
    await fs.writeFile(sentinel, 'other owner');
    await expect(owner.cleanup()).rejects.toThrow('wrapper_managed_scratch_cleanup_failed');
    await expect(owner.cleanup()).rejects.toThrow('wrapper_managed_scratch_cleanup_failed');
    expect(await fs.readFile(sentinel, 'utf8')).toBe('other owner');
    expect((await fs.stat(moved)).isDirectory()).toBe(true);
  });

  it('retains a tree containing a foreign-UID entry and closes its guards', async () => {
    const { parent, owner } = await fixture();
    const child = path.join(owner.tempRoot, 'foreign');
    await fs.writeFile(child, 'retained');
    const foreign = await fs.lstat(child, { bigint: true });
    foreign.uid += 1n;
    const lstat = vi.spyOn(fs, 'lstat');
    lstat.mockResolvedValueOnce(await fs.stat(parent, { bigint: true }));
    lstat.mockResolvedValueOnce(await fs.stat(owner.tempRoot, { bigint: true }));
    lstat.mockResolvedValueOnce(foreign);
    await expect(owner.cleanup()).rejects.toThrow('wrapper_managed_scratch_cleanup_failed');
    vi.restoreAllMocks();
    expect(await fs.readFile(child, 'utf8')).toBe('retained');
    // A rejected cleanup stays rejected after the injected metadata condition disappears.
    await expect(owner.cleanup()).rejects.toThrow('wrapper_managed_scratch_cleanup_failed');
  });

  it('rejects traversal beyond its metadata depth bound and retains the tree', async () => {
    const { owner } = await fixture();
    const nested = path.join(owner.tempRoot, ...Array<string>(130).fill('d'));
    await fs.mkdir(nested, { recursive: true });
    await expect(owner.cleanup()).rejects.toThrow('wrapper_managed_scratch_cleanup_failed');
    expect((await fs.stat(nested)).isDirectory()).toBe(true);
  });
});
