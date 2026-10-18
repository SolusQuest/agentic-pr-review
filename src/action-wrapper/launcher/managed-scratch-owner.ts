import { constants, type BigIntStats } from 'node:fs';
import { lstat, mkdtemp, open, opendir, rm, type FileHandle } from 'node:fs/promises';
import path from 'node:path';

// Cleanup metadata bounds, independent of review/provider execution budgets.
const MAX_DEPTH = 128;
const MAX_ENTRIES = 262_144;
const directoryFlags = constants.O_RDONLY | constants.O_DIRECTORY | constants.O_NOFOLLOW;

function invalid(): never {
  throw new Error('wrapper_managed_scratch_invalid');
}

function sameIdentity(left: BigIntStats, right: BigIntStats): boolean {
  return left.dev === right.dev && left.ino === right.ino && left.uid === right.uid;
}

function fdPath(handle: FileHandle, name?: string): string {
  return `/proc/self/fd/${handle.fd}${name === undefined ? '' : `/${name}`}`;
}

/** Internal owner of a newly created managed bridge scratch directory. */
export async function createManagedScratch(parent: string): Promise<{
  readonly tempRoot: string;
  cleanup(): Promise<void>;
}> {
  const uid = process.getuid?.();
  if (process.platform !== 'linux' || uid === undefined) invalid();
  const parentHandle = await open(parent, directoryFlags | constants.O_NONBLOCK);
  let rootHandle: FileHandle | undefined;
  try {
    const parentIdentity = await parentHandle.stat({ bigint: true });
    if (!sameIdentity(parentIdentity, await lstat(parent, { bigint: true }))) invalid();
    const created = await mkdtemp(fdPath(parentHandle, 'apr-w1-'));
    const name = path.basename(created);
    rootHandle = await open(fdPath(parentHandle, name), directoryFlags | constants.O_NONBLOCK);
    const rootIdentity = await rootHandle.stat({ bigint: true });
    if (!rootIdentity.isDirectory() || rootIdentity.uid !== BigInt(uid)) invalid();
    await rootHandle.chmod(0o700);
    const root = rootHandle;
    const tempRoot = path.join(parent, name);
    let cleanup: Promise<void> | undefined;

    const checkRoot = async (): Promise<void> => {
      const parentNamed = await lstat(parent, { bigint: true });
      const rootNamed = await lstat(fdPath(parentHandle, name), { bigint: true });
      if (
        !parentNamed.isDirectory() ||
        !sameIdentity(parentIdentity, parentNamed) ||
        !sameIdentity(parentIdentity, await parentHandle.stat({ bigint: true })) ||
        !rootNamed.isDirectory() ||
        !sameIdentity(rootIdentity, rootNamed) ||
        !sameIdentity(rootIdentity, await root.stat({ bigint: true }))
      )
        invalid();
    };

    await checkRoot();
    return {
      tempRoot,
      cleanup: () =>
        (cleanup ??= (async () => {
          const failures: unknown[] = [];
          try {
            await checkRoot();
            let entries = 0;
            const restore = async (directory: FileHandle, depth: number): Promise<void> => {
              if (depth > MAX_DEPTH) invalid();
              const identity = await directory.stat({ bigint: true });
              if (
                !identity.isDirectory() ||
                identity.uid !== BigInt(uid) ||
                identity.dev !== rootIdentity.dev
              )
                invalid();
              // Only opened, owned directories change mode. Files and link targets never do.
              await directory.chmod(0o700);
              const children = await opendir(fdPath(directory));
              for await (const child of children) {
                if (++entries > MAX_ENTRIES) invalid();
                const named = fdPath(directory, child.name);
                const before = await lstat(named, { bigint: true });
                if (before.uid !== BigInt(uid) || before.dev !== rootIdentity.dev) invalid();
                if (!before.isDirectory()) continue;
                const nested = await open(named, directoryFlags | constants.O_NONBLOCK);
                try {
                  if (!sameIdentity(before, await nested.stat({ bigint: true }))) invalid();
                  await restore(nested, depth + 1);
                  if (!sameIdentity(before, await lstat(named, { bigint: true }))) invalid();
                } finally {
                  await nested.close();
                }
              }
              if (!sameIdentity(identity, await directory.stat({ bigint: true }))) invalid();
            };
            await restore(root, 0);
            await checkRoot();
            await rm(fdPath(parentHandle, name), { recursive: true, force: false });
          } catch (error) {
            failures.push(error);
          }
          const closed = await Promise.allSettled([root.close(), parentHandle.close()]);
          for (const result of closed)
            if (result.status === 'rejected') failures.push(result.reason);
          if (failures.length)
            throw new AggregateError(failures, 'wrapper_managed_scratch_cleanup_failed');
        })()),
    };
  } catch (error) {
    const closed = await Promise.allSettled([rootHandle?.close(), parentHandle.close()]);
    const failures = [error];
    for (const result of closed) if (result.status === 'rejected') failures.push(result.reason);
    // An unverified/replaced root must remain; never fall back to path-based removal.
    throw new AggregateError(failures, 'wrapper_managed_scratch_creation_failed');
  }
}
