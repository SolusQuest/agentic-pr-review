import {
  mkdtemp,
  mkdir,
  writeFile,
  chmod,
  symlink,
  readFile,
  rm,
  realpath,
  stat,
} from 'node:fs/promises';
import { join } from 'node:path';
import { spawn } from 'node:child_process';
import { afterEach, expect, test, vi } from 'vitest';
import {
  withGeneratedCleanup,
  confirmGeneratedGroupClose,
  cleanupGeneratedFixture,
  closeGeneratedServers,
} from './generated-fixture-cleanup.mjs';
import { awaitFixtureGroupClose } from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/role-deadline.mjs';
afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});
test('preserves primary identity when cleanup also throws and reports only the closed marker', async () => {
  const primary = new Error('primary case failure');
  const secondary = vi.fn();
  await expect(
    withGeneratedCleanup(
      async () => {
        throw primary;
      },
      async () => {
        throw new Error('PRIVATE_CLEANUP_PATH');
      },
      secondary,
    ),
  ).rejects.toBe(primary);
  expect(secondary).toHaveBeenCalledOnce();
  await expect(
    withGeneratedCleanup(
      async () => {
        throw primary;
      },
      async () => {
        throw new Error('secondary');
      },
      () => {
        throw new Error('logging failed');
      },
    ),
  ).rejects.toBe(primary);
});
test('cleanup-only failure blocks success and successful cleanup precedes returning a receipt', async () => {
  const error = new Error('cleanup failed');
  await expect(
    withGeneratedCleanup(
      async () => 'receipt',
      async () => {
        throw error;
      },
    ),
  ).rejects.toBe(error);
  const events: string[] = [];
  const value = await withGeneratedCleanup(
    async () => {
      events.push('action');
      return 'receipt';
    },
    async () => {
      events.push('cleanup');
    },
  );
  events.push(value);
  expect(events).toEqual(['action', 'cleanup', 'receipt']);
});
test('does not reopen a test root without actual group/service close confirmation', async () => {
  const root = await mkdtemp(join(await realpath('/tmp'), 'apr-d4-unknown-'));
  try {
    await writeFile(join(root, 'retained'), 'private');
    await expect(cleanupGeneratedFixture(root, false)).rejects.toThrow('close_unconfirmed');
    expect(await readFile(join(root, 'retained'), 'utf8')).toBe('private');
  } finally {
    await rm(root, { force: true, recursive: true });
  }
});
test('reuses bounded trusted teardown for sealed D4 trees and leaves symlink targets intact', async () => {
  const tmp = await realpath('/tmp');
  const root = await mkdtemp(join(tmp, 'apr-d4-cleanup-'));
  const outside = await mkdtemp(join(tmp, 'apr-fixture-outside-'));
  try {
    await writeFile(join(outside, 'retained'), 'outside');
    await mkdir(join(root, 'sealed'));
    await writeFile(join(root, 'sealed', 'entry'), 'bytes');
    await symlink(outside, join(root, 'link'));
    await chmod(join(root, 'sealed'), 0o500);
    await cleanupGeneratedFixture(root, true);
    await expect(stat(root)).rejects.toMatchObject({ code: 'ENOENT' });
    expect(await readFile(join(outside, 'retained'), 'utf8')).toBe('outside');
  } finally {
    await rm(root, { force: true, recursive: true });
    await rm(outside, { force: true, recursive: true });
  }
});
test('unconfirmed close or surviving process group cannot authorize cleanup', async () => {
  vi.useFakeTimers();
  vi.spyOn(process, 'kill').mockReturnValue(true);
  const pending = confirmGeneratedGroupClose(
    { pid: 123456 },
    Promise.resolve({ groupCloseConfirmed: true }),
  );
  const rejected = expect(pending).rejects.toThrow('observation_timeout');
  await vi.advanceTimersByTimeAsync(5000);
  await rejected;
  await expect(
    confirmGeneratedGroupClose({ pid: 123456 }, Promise.resolve({ groupCloseConfirmed: false })),
  ).rejects.toThrow('close_unconfirmed');
});
test('actual detached child close plus absent process group is confirmed', async () => {
  const child = spawn(process.execPath, ['-e', 'setTimeout(() => {}, 1000)'], {
    detached: true,
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  const closed = awaitFixtureGroupClose(child, 2000);
  try {
    process.kill(-child.pid!, 'SIGKILL');
    expect((await confirmGeneratedGroupClose(child, closed)).groupCloseConfirmed).toBe(true);
  } finally {
    try {
      process.kill(-child.pid!, 'SIGKILL');
    } catch {}
  }
});

test('bounded service cleanup accepts already closed services but not unknown close', async () => {
  const closed = {
    closeAllConnections: vi.fn(),
    close: (done) => done(Object.assign(new Error('closed'), { code: 'ERR_SERVER_NOT_RUNNING' })),
  };
  await closeGeneratedServers([closed]);
  expect(closed.closeAllConnections).toHaveBeenCalledOnce();
  vi.useFakeTimers();
  const pending = closeGeneratedServers([{ closeAllConnections() {}, close() {} }]);
  const rejected = expect(pending).rejects.toThrow('observation_timeout');
  await vi.advanceTimersByTimeAsync(5000);
  await rejected;
});
