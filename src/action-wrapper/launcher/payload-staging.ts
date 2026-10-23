import { constants, type Stats } from 'node:fs';
import {
  chmod,
  lstat,
  mkdtemp,
  open,
  opendir,
  realpath,
  rm,
  type FileHandle,
} from 'node:fs/promises';
import {
  materializeManagedPackage,
  sha256,
  type ManagedReceipt,
  type PayloadMember,
} from '../../../scripts/release/build-payload.format.mjs';
import type { ManagedPayloadMap } from './payload-map.js';
import path from 'node:path';

import { fail } from './validation.js';

/** Owns a private child of runner-provided storage through acquisition and disposal. */
export async function acquireInPayloadStaging<T extends { dispose(): Promise<void> }>(request: {
  readonly runnerTemp: string;
  readonly workspace: string;
  readonly acquire: (stagingParent: string) => Promise<T>;
}): Promise<T> {
  if (!path.isAbsolute(request.runnerTemp) || !path.isAbsolute(request.workspace))
    fail('wrapper_payload_invalid');
  const runnerTemp = await realpath(request.runnerTemp);
  const workspace = await realpath(request.workspace);
  if (runnerTemp === workspace || runnerTemp.startsWith(`${workspace}${path.sep}`))
    fail('wrapper_payload_invalid');
  const stagingParent = await mkdtemp(path.join(runnerTemp, 'apr-payload-'));
  const removeParent = () => rm(stagingParent, { recursive: true, force: true });
  try {
    await chmod(stagingParent, 0o700);
    const stat = await lstat(stagingParent);
    if (!stat.isDirectory() || (stat.mode & 0o777) !== 0o700) fail('wrapper_payload_invalid');
    const payload = await request.acquire(stagingParent);
    const managed = registeredManagedPayload(payload);
    let disposal: Promise<void> | undefined;
    return {
      ...payload,
      dispose: () =>
        (disposal ??= (async () => {
          try {
            await payload.dispose();
          } catch (error) {
            if (managed) disposal = undefined;
            else await removeParent().catch(() => {});
            throw error;
          }
          try {
            await removeParent();
          } catch (error) {
            if (managed) disposal = undefined;
            throw error;
          }
        })()),
    };
  } catch (error) {
    await removeParent().catch(() => {});
    throw error;
  }
}

/** Opaque capability: only the owner's registry can admit a tree into a loading lease. */
declare const managedTreeBrand: unique symbol;
export interface VerifiedManagedTree {
  readonly [managedTreeBrand]: true;
}
export interface VerifiedManagedPayload {
  /** Diagnostic only; consumers obtain loading authority from tree through a lease. */
  readonly root: string;
  readonly tree: VerifiedManagedTree;
  readonly identity: ManagedReceipt['identity'];
  readonly builder: ManagedPayloadMap['builder'];
  readonly actionSourceSha: string;
  detachForHandoff(): Promise<void>;
  dispose(): Promise<void>;
}
export interface VerifiedManagedTreeLease {
  /** D3 must explicitly inherit this descriptor and use its child fd-root for loading. */
  readonly directoryFd: number;
  readonly entrypointRelativePath: 'agentic-pr-review/AgenticPrReview.Runtime.dll';
  readonly receipt: ManagedReceipt;
  readonly builder: ManagedPayloadMap['builder'];
  readonly actionSourceSha: string;
  readonly files: readonly Readonly<{ member: PayloadMember; fd: number }>[];
  /** Only after the consumer confirms it can no longer load dependencies. */
  release(): void;
}
export class ManagedTreeInUseError extends Error {
  readonly code = 'wrapper_managed_tree_in_use';
  constructor() {
    super('wrapper_managed_tree_in_use');
  }
}
interface FileGuard {
  readonly handle: FileHandle;
  readonly member: PayloadMember;
  snapshot: Stats;
}
interface ManagedOwner {
  readonly root: string;
  readonly name: string;
  readonly parent: string;
  readonly parentHandle: FileHandle;
  readonly parentIdentity: Stats;
  readonly rootHandle: FileHandle;
  readonly receipt: ManagedReceipt;
  readonly builder: ManagedPayloadMap['builder'];
  readonly actionSourceSha: string;
  readonly directories: Map<string, Stats>;
  readonly files: Map<string, FileGuard>;
  readonly children: Map<string, string[]>;
  references: number;
  closing: boolean;
  disposed: boolean;
  removed: boolean;
  cleanup?: Promise<void>;
}
// Lazy construction keeps the unselected managed route out of the ordinary native bundle.
let managedOwners: WeakMap<object, ManagedOwner> | undefined;
function registeredManagedPayload(value: unknown): boolean {
  if (value === null || typeof value !== 'object') return false;
  const token = (value as { tree?: unknown }).tree;
  return typeof token === 'object' && token !== null && managedOwners?.has(token) === true;
}
function directoryFlags(): number {
  return constants.O_RDONLY | constants.O_DIRECTORY | constants.O_NOFOLLOW;
}
function fileFlags(): number {
  return constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK;
}
function fdPath(handle: FileHandle, child = ''): string {
  return `/proc/self/fd/${handle.fd}${child ? `/${child}` : ''}`;
}
function sameIdentity(left: Stats, right: Stats): boolean {
  return left.dev === right.dev && left.ino === right.ino && left.uid === right.uid;
}
function sameSnapshot(left: Stats, right: Stats): boolean {
  return (
    sameIdentity(left, right) &&
    left.mode === right.mode &&
    left.nlink === right.nlink &&
    left.size === right.size &&
    left.mtimeMs === right.mtimeMs &&
    left.ctimeMs === right.ctimeMs
  );
}
function requireOwned(stat: Stats, directory: boolean, mode: number): void {
  if (
    stat.uid !== process.getuid?.() ||
    (stat.mode & 0o7777) !== mode ||
    (directory ? !stat.isDirectory() : !stat.isFile() || stat.nlink !== 1)
  )
    fail('wrapper_managed_tree_invalid');
}
async function names(handle: FileHandle, maximum: number): Promise<string[]> {
  const result: string[] = [];
  const directory = await opendir(fdPath(handle));
  for await (const entry of directory) {
    if (result.length >= maximum) fail('wrapper_managed_tree_invalid');
    result.push(entry.name);
  }
  return result.sort();
}
async function checkRoot(owner: ManagedOwner): Promise<void> {
  const parent = await lstat(owner.parent);
  requireOwned(parent, true, 0o700);
  if (!sameIdentity(parent, owner.parentIdentity)) fail('wrapper_managed_tree_invalid');
  const root = await lstat(fdPath(owner.parentHandle, owner.name));
  if (!root.isDirectory() || !sameIdentity(root, await owner.rootHandle.stat()))
    fail('wrapper_managed_tree_invalid');
}
async function checkFile(guard: FileGuard, named: string, initial: boolean): Promise<void> {
  const before = await guard.handle.stat();
  requireOwned(before, false, initial ? 0o644 : 0o400);
  if (!sameSnapshot(before, guard.snapshot) || !sameSnapshot(before, await lstat(named)))
    fail('wrapper_managed_tree_invalid');
  const bytes = Buffer.alloc(guard.member.size + 1);
  let offset = 0;
  while (offset < bytes.length) {
    const { bytesRead } = await guard.handle.read(bytes, offset, bytes.length - offset, offset);
    if (!bytesRead) break;
    offset += bytesRead;
  }
  const after = await guard.handle.stat();
  if (
    offset !== guard.member.size ||
    sha256(bytes.subarray(0, offset)) !== guard.member.sha256 ||
    !sameSnapshot(before, after) ||
    !sameSnapshot(after, await lstat(named))
  )
    fail('wrapper_managed_tree_invalid');
  if (initial) {
    await guard.handle.chmod(0o400);
    guard.snapshot = await guard.handle.stat();
  }
}
async function audit(
  owner: ManagedOwner,
  initial: boolean,
  signal: AbortSignal,
  reopening = false,
): Promise<void> {
  signal.throwIfAborted();
  await checkRoot(owner);
  async function walk(relative: string, handle: FileHandle): Promise<void> {
    signal.throwIfAborted();
    const before = await handle.stat();
    requireOwned(before, true, initial && !reopening ? 0o700 : 0o500);
    if (!initial && !sameSnapshot(before, owner.directories.get(relative)!))
      fail('wrapper_managed_tree_invalid');
    if (initial) owner.directories.set(relative, before);
    const expected = owner.children.get(relative)!;
    if (JSON.stringify(await names(handle, expected.length + 1)) !== JSON.stringify(expected))
      fail('wrapper_managed_tree_invalid');
    for (const name of expected) {
      signal.throwIfAborted();
      const child = relative ? `${relative}/${name}` : name;
      const named = fdPath(handle, name);
      const stat = await lstat(named);
      if (owner.children.has(child)) {
        requireOwned(stat, true, initial && !reopening ? 0o700 : 0o500);
        const directory = await open(named, directoryFlags());
        try {
          if (!sameSnapshot(stat, await directory.stat())) fail('wrapper_managed_tree_invalid');
          await walk(child, directory);
          if (!sameSnapshot(await lstat(named), await directory.stat()))
            fail('wrapper_managed_tree_invalid');
        } finally {
          await directory.close();
        }
      } else {
        let guard = owner.files.get(child);
        if (initial) {
          requireOwned(stat, false, reopening ? 0o400 : 0o644);
          const member = owner.receipt.members.find((item) => item.path === child)!;
          if (stat.size !== member.size) fail('wrapper_managed_tree_invalid');
          const opened = await open(named, fileFlags());
          guard = { handle: opened, member, snapshot: stat };
          owner.files.set(child, guard);
        }
        if (!guard) fail('wrapper_managed_tree_invalid');
        await checkFile(guard, named, initial && !reopening);
      }
    }
    if (JSON.stringify(await names(handle, expected.length + 1)) !== JSON.stringify(expected))
      fail('wrapper_managed_tree_invalid');
    if (initial) {
      if (!reopening) await handle.chmod(0o500);
      owner.directories.set(relative, await handle.stat());
    } else if (!sameSnapshot(before, await handle.stat())) fail('wrapper_managed_tree_invalid');
  }
  await walk('', owner.rootHandle);
  await checkRoot(owner);
  signal.throwIfAborted();
}
async function closeFiles(owner: ManagedOwner): Promise<void> {
  const results = await Promise.allSettled(
    [...owner.files.values()].map((guard) => guard.handle.close()),
  );
  const failure = results.find((result) => result.status === 'rejected');
  if (failure?.status === 'rejected') throw failure.reason;
}
async function restoreDirectories(owner: ManagedOwner, partial = false): Promise<void> {
  // Traverse only verified owned inodes, never chmod a substituted symlink or foreign directory.
  async function walk(relative: string, handle: FileHandle): Promise<void> {
    await handle.chmod(0o700);
    for (const name of owner.children.get(relative) ?? []) {
      const child = relative ? `${relative}/${name}` : name;
      if (!owner.children.has(child)) continue;
      const stat = await lstat(fdPath(handle, name));
      const expected = owner.directories.get(child);
      if (
        (!expected && !partial) ||
        (expected && !sameIdentity(stat, expected)) ||
        !stat.isDirectory() ||
        stat.uid !== process.getuid?.()
      )
        fail('wrapper_managed_tree_invalid');
      const directory = await open(fdPath(handle, name), directoryFlags());
      try {
        if (!sameIdentity(await directory.stat(), expected ?? stat))
          fail('wrapper_managed_tree_invalid');
        await walk(child, directory);
      } finally {
        await directory.close();
      }
    }
  }
  await walk('', owner.rootHandle);
}
function disposeOwner(owner: ManagedOwner): Promise<void> {
  owner.closing = true;
  if (owner.disposed) return Promise.resolve();
  if (owner.references > 0) return Promise.reject(new ManagedTreeInUseError());
  return (owner.cleanup ??= (async () => {
    try {
      if (!owner.removed) {
        await checkRoot(owner);
        await closeFiles(owner);
        await restoreDirectories(owner);
        await checkRoot(owner);
        await rm(fdPath(owner.parentHandle, owner.name), { recursive: true, force: false });
        owner.removed = true;
      }
      await owner.rootHandle.close();
      await owner.parentHandle.close();
      owner.disposed = true;
    } catch (error) {
      owner.cleanup = undefined;
      throw error;
    }
  })());
}

interface ManagedPayloadAdmission {
  readonly archive?: Buffer;
  readonly existingRoot?: string;
  readonly map: ManagedPayloadMap;
  readonly actionSourceSha: string;
  readonly stagingParent: string;
  readonly workspace: string;
  readonly signal: AbortSignal;
}

/** Materialization and cross-step re-admission share the same full-tree audit. */
export function stageManagedPayload(
  request: Omit<ManagedPayloadAdmission, 'existingRoot'> & { archive: Buffer },
): Promise<VerifiedManagedPayload> {
  return admitManagedPayload(request);
}
export function reopenManagedPayload(
  request: Omit<ManagedPayloadAdmission, 'archive'> & { existingRoot: string },
): Promise<VerifiedManagedPayload> {
  return admitManagedPayload(request);
}
async function admitManagedPayload(
  request: ManagedPayloadAdmission,
): Promise<VerifiedManagedPayload> {
  if (process.platform !== 'linux' || process.arch !== 'x64')
    fail('wrapper_payload_platform_unsupported');
  const { map, actionSourceSha, signal } = request;
  const parent = await realpath(request.stagingParent);
  const workspace = await realpath(request.workspace);
  if (parent === workspace || parent.startsWith(`${workspace}${path.sep}`))
    fail('wrapper_managed_tree_invalid');
  const parentHandle = await open(parent, directoryFlags());
  let rootHandle: FileHandle | undefined;
  let owner: ManagedOwner | undefined;
  let name: string | undefined;
  try {
    const parentIdentity = await parentHandle.stat();
    requireOwned(parentIdentity, true, 0o700);
    signal.throwIfAborted();
    // D1's full independent receipt/archive inspection precedes its first materialization write.
    const materialized =
      request.existingRoot === undefined
        ? await materializeManagedPackage(request.archive!, map.payload, fdPath(parentHandle))
        : { root: request.existingRoot };
    if (
      request.existingRoot !== undefined &&
      (!path.isAbsolute(materialized.root) ||
        path.dirname(materialized.root) !== parent ||
        materialized.root !== path.join(parent, path.basename(materialized.root)))
    )
      fail('wrapper_managed_tree_invalid');
    name = path.basename(materialized.root);
    rootHandle = await open(fdPath(parentHandle, name), directoryFlags());
    const children = new Map<string, string[]>([['', []]]);
    for (const member of map.payload.members) {
      const parts = member.path.split('/');
      for (let index = 0; index < parts.length; index++) {
        const relative = parts.slice(0, index).join('/');
        if (!children.has(relative)) children.set(relative, []);
        const values = children.get(relative)!;
        if (!values.includes(parts[index])) values.push(parts[index]);
      }
    }
    for (const values of children.values()) values.sort();
    owner = {
      root: path.join(parent, name),
      name,
      parent,
      parentHandle,
      parentIdentity,
      rootHandle,
      receipt: map.payload,
      builder: map.builder,
      actionSourceSha,
      directories: new Map(),
      files: new Map(),
      children,
      references: 0,
      closing: false,
      disposed: false,
      removed: false,
    };
    await audit(owner, true, signal, request.existingRoot !== undefined);
    // Revalidation checks normalized permissions and every final identity before publishing authority.
    await audit(owner, false, signal);
    const token = Object.freeze({}) as VerifiedManagedTree;
    (managedOwners ??= new WeakMap()).set(token, owner);
    const retained = owner;
    return Object.freeze({
      root: owner.root,
      tree: token,
      identity: map.payload.identity,
      builder: map.builder,
      actionSourceSha,
      detachForHandoff: async () => {
        if (retained.references || retained.closing || retained.disposed)
          throw new ManagedTreeInUseError();
        await audit(retained, false, signal);
        retained.closing = true;
        await closeFiles(retained);
        await retained.rootHandle.close();
        await retained.parentHandle.close();
        retained.disposed = true;
      },
      dispose: () => disposeOwner(retained),
    });
  } catch (error) {
    try {
      if (owner) {
        await closeFiles(owner);
        await checkRoot(owner);
        if (request.existingRoot === undefined) await restoreDirectories(owner, true);
      }
      if (name !== undefined && request.existingRoot === undefined)
        await rm(fdPath(parentHandle, name), { recursive: true, force: true });
    } finally {
      await rootHandle?.close();
      await parentHandle.close();
    }
    throw error;
  }
}

export async function acquireVerifiedManagedTreeLease(
  tree: VerifiedManagedTree,
  signal: AbortSignal,
): Promise<VerifiedManagedTreeLease> {
  const owner = managedOwners?.get(tree);
  if (!owner || owner.closing || owner.disposed) fail('wrapper_managed_tree_invalid');
  owner.references += 1;
  let retained = false;
  try {
    await audit(owner, false, signal);
    if (owner.closing) fail('wrapper_managed_tree_invalid');
    let released = false;
    const lease = Object.freeze({
      directoryFd: owner.rootHandle.fd,
      entrypointRelativePath: 'agentic-pr-review/AgenticPrReview.Runtime.dll' as const,
      receipt: owner.receipt,
      builder: owner.builder,
      actionSourceSha: owner.actionSourceSha,
      files: Object.freeze(
        [...owner.files.values()].map((guard) =>
          Object.freeze({ member: guard.member, fd: guard.handle.fd }),
        ),
      ),
      release: () => {
        if (!released) {
          released = true;
          owner.references -= 1;
        }
      },
    });
    retained = true;
    return lease;
  } finally {
    if (!retained) owner.references -= 1;
  }
}
