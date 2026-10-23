import {
  chmod,
  link,
  readFile,
  rename,
  symlink,
  writeFile,
  access,
  lstat,
  readdir,
  rm,
} from 'node:fs/promises';
import path from 'node:path';
import { describe, expect, test } from 'vitest';
import {
  managedContext,
  managedMap,
  packageManagedFiles,
  minimalManagedFiles,
} from './managed-host-fixture.js';
import { ACTION_SOURCE } from './release-payload-fixture.js';
import {
  acquireVerifiedManagedTreeLease,
  reopenManagedPayload,
} from '../../src/action-wrapper/launcher/payload-staging.js';

async function clearFixture(root: string): Promise<void> {
  // Test-owned directories only; never follow an injected candidate symlink.
  if ((await lstat(root)).isDirectory()) {
    await chmod(root, 0o700);
    for (const child of await readdir(root)) {
      const named = path.join(root, child);
      if ((await lstat(named)).isDirectory()) await clearFixture(named);
    }
  }
  await rm(root, { recursive: true, force: true });
}

describe.skipIf(process.platform !== 'linux' || process.arch !== 'x64')(
  'cross-step managed-tree admission',
  () => {
    test('closes preparer authority, then opens a fresh owner that retains the original files', async () => {
      const packaged = packageManagedFiles(minimalManagedFiles());
      const f = await managedContext(packaged);
      try {
        const lease = await acquireVerifiedManagedTreeLease(
          f.payload.tree,
          new AbortController().signal,
        );
        await expect(f.payload.detachForHandoff()).rejects.toThrow();
        lease.release();
        await f.payload.detachForHandoff();
        await expect(
          acquireVerifiedManagedTreeLease(f.payload.tree, new AbortController().signal),
        ).rejects.toThrow();
        const reopened = await reopenManagedPayload({
          existingRoot: f.payload.root,
          stagingParent: path.dirname(f.payload.root),
          map: managedMap(packaged.receipt),
          actionSourceSha: ACTION_SOURCE,
          workspace: f.workspace,
          signal: new AbortController().signal,
        });
        expect(reopened.tree).not.toBe(f.payload.tree);
        const freshLease = await acquireVerifiedManagedTreeLease(
          reopened.tree,
          new AbortController().signal,
        );
        expect(freshLease.files.length).toBe(packaged.receipt.members.length);
        await expect(reopened.dispose()).rejects.toThrow();
        freshLease.release();
        await reopened.dispose();
        await expect(access(f.payload.root)).rejects.toThrow();
      } finally {
        await clearFixture(f.base);
      }
    });

    test.each(['bytes', 'mode', 'extra', 'missing', 'symlink', 'hardlink'])(
      'refuses changed candidate %s without deleting or repairing it',
      async (fault) => {
        const packaged = packageManagedFiles(minimalManagedFiles());
        const f = await managedContext(packaged);
        try {
          await f.payload.detachForHandoff();
          const root = f.payload.root;
          const entry = path.join(root, 'agentic-pr-review/AgenticPrReview.Runtime.dll');
          const directory = path.dirname(entry);
          if (fault === 'bytes') {
            await chmod(entry, 0o600);
            await writeFile(entry, Buffer.from('changed'));
            await chmod(entry, 0o400);
          } else if (fault === 'mode') await chmod(entry, 0o600);
          else {
            await chmod(directory, 0o700);
            if (fault === 'extra')
              await writeFile(path.join(directory, 'extra'), 'outside inventory');
            else {
              await rename(entry, entry + '.original');
              if (fault === 'symlink') await symlink(entry + '.original', entry);
              if (fault === 'hardlink') await link(entry + '.original', entry);
            }
            await chmod(directory, 0o500);
          }
          const before = fault === 'mode' ? await readFile(entry) : undefined;
          await expect(
            reopenManagedPayload({
              existingRoot: root,
              stagingParent: path.dirname(root),
              map: managedMap(packaged.receipt),
              actionSourceSha: ACTION_SOURCE,
              workspace: f.workspace,
              signal: new AbortController().signal,
            }),
          ).rejects.toThrow();
          await access(root);
          if (before) expect(await readFile(entry)).toEqual(before);
        } finally {
          await clearFixture(f.base);
        }
      },
    );
  },
);
