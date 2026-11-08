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
  inspectManagedPackage,
  canonicalJson,
  sha256,
  type ManagedReceipt,
  type PayloadMember,
} from '../../../scripts/release/build-payload.format.mjs';
import type { ManagedPayloadMap } from './payload-map.js';
import path from 'node:path';

import { exactRecord, fail, lowerHex } from './validation.js';
import { parseStrictJson } from './strict-json.js';
import { isDeepStrictEqual } from 'node:util';

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
type ManagedFilesystemOwner = Omit<ManagedOwner, 'builder'>;
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
async function checkRoot(
  owner: Pick<ManagedOwner, 'parent' | 'parentIdentity' | 'parentHandle' | 'name' | 'rootHandle'>,
): Promise<void> {
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
  owner: ManagedFilesystemOwner,
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
async function closeFiles(owner: Pick<ManagedOwner, 'files'>): Promise<void> {
  const results = await Promise.allSettled(
    [...owner.files.values()].map((guard) => guard.handle.close()),
  );
  const failure = results.find((result) => result.status === 'rejected');
  if (failure?.status === 'rejected') throw failure.reason;
}
async function restoreDirectories(owner: ManagedFilesystemOwner, partial = false): Promise<void> {
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
function disposeOwner(owner: ManagedFilesystemOwner): Promise<void> {
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

/** Repository-test fixture ownership only: this is NOT a qualified production tree or seal. */
declare const repositoryTestFixtureBrand: unique symbol;
export interface RepositoryTestOriginalRuntimeFixture {
  readonly [repositoryTestFixtureBrand]: true;
}
export const REPOSITORY_TEST_FRIEND_STEM = 'AgenticPrReview.Runtime.ActionHostTrustedProofPayload';
function fixtureNames(): readonly string[] {
  return [
    REPOSITORY_TEST_FRIEND_STEM + '.deps.json',
    REPOSITORY_TEST_FRIEND_STEM + '.dll',
    REPOSITORY_TEST_FRIEND_STEM + '.runtimeconfig.json',
  ];
}
export interface RepositoryTestOriginalRuntimeContext {
  readonly sourceCommit: string;
  readonly sourceTree: string;
  readonly workflowCommit: string;
  readonly runId: number;
  readonly attempt: 1;
  readonly archiveSha256: string;
  readonly receiptSha256: string;
  readonly assemblySha256: string;
}
interface RepositoryTestFixtureOwner {
  readonly name: string;
  readonly parent: string;
  readonly parentHandle: FileHandle;
  readonly parentIdentity: Stats;
  readonly rootHandle: FileHandle;
  readonly context: RepositoryTestOriginalRuntimeContext;
  readonly files: Map<string, FileGuard>;
  rootSnapshot: Stats;
  removed: boolean;
  references: number;
  closing: boolean;
  disposed: boolean;
  cleanup?: Promise<void>;
}
let repositoryTestFixtures: WeakMap<object, RepositoryTestFixtureOwner> | undefined;

function fixtureContext(
  value: RepositoryTestOriginalRuntimeContext,
): RepositoryTestOriginalRuntimeContext {
  const record = exactRecord(value, [
    'sourceCommit',
    'sourceTree',
    'workflowCommit',
    'runId',
    'attempt',
    'archiveSha256',
    'receiptSha256',
    'assemblySha256',
  ]);
  if (
    !['sourceCommit', 'sourceTree', 'workflowCommit'].every((key) => lowerHex(record[key], 40)) ||
    !['archiveSha256', 'receiptSha256', 'assemblySha256'].every((key) =>
      lowerHex(record[key], 64),
    ) ||
    record.sourceCommit === record.workflowCommit ||
    !Number.isSafeInteger(record.runId) ||
    (record.runId as number) < 1 ||
    record.attempt !== 1
  )
    fail('wrapper_test_fixture_invalid');
  return Object.freeze({ ...value });
}

async function fixtureConfiguration(owner: RepositoryTestFixtureOwner): Promise<void> {
  const read = async (suffix: string): Promise<unknown> => {
    const guard = owner.files.get(REPOSITORY_TEST_FRIEND_STEM + suffix)!;
    const bytes = Buffer.alloc(guard.member.size);
    let offset = 0;
    while (offset < bytes.length) {
      const result = await guard.handle.read(bytes, offset, bytes.length - offset, offset);
      if (!result.bytesRead) fail('wrapper_test_fixture_invalid');
      offset += result.bytesRead;
    }
    return parseStrictJson(bytes, 64 * 1024);
  };
  const config = exactRecord(await read('.runtimeconfig.json'), ['runtimeOptions']);
  const options = exactRecord(config.runtimeOptions, ['tfm', 'framework', 'configProperties']);
  const framework = exactRecord(options.framework, ['name', 'version']);
  const properties = options.configProperties;
  if (
    options.tfm !== 'net10.0' ||
    framework.name !== 'Microsoft.NETCore.App' ||
    framework.version !== '10.0.0' ||
    properties === null ||
    typeof properties !== 'object' ||
    Array.isArray(properties)
  )
    fail('wrapper_test_fixture_invalid');
  const switches = properties as Record<string, unknown>;
  if (
    switches['System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization'] !== false ||
    switches['System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault'] !== true ||
    Object.entries(switches).some(
      ([key, value]) =>
        ![
          'System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization',
          'System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault',
          'System.Reflection.Metadata.MetadataUpdater.IsSupported',
        ].includes(key) ||
        (key === 'System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault'
          ? value !== true
          : value !== false),
    )
  )
    fail('wrapper_test_fixture_invalid');
  const library = REPOSITORY_TEST_FRIEND_STEM + '/1.0.0';
  // No probing path or production dependency may be supplied by the fixture's deps manifest.
  if (
    !isDeepStrictEqual(await read('.deps.json'), {
      runtimeTarget: { name: '.NETCoreApp,Version=v10.0', signature: '' },
      compilationOptions: {},
      targets: {
        '.NETCoreApp,Version=v10.0': {
          [library]: {
            runtime: { [REPOSITORY_TEST_FRIEND_STEM + '.dll']: {} },
          },
        },
      },
      libraries: { [library]: { type: 'project', serviceable: false, sha512: '' } },
    })
  )
    fail('wrapper_test_fixture_invalid');
}

async function auditFixture(owner: RepositoryTestFixtureOwner, signal: AbortSignal): Promise<void> {
  signal.throwIfAborted();
  await checkRoot(owner);
  const before = await owner.rootHandle.stat();
  requireOwned(before, true, 0o500);
  if (
    !sameSnapshot(before, owner.rootSnapshot) ||
    !isDeepStrictEqual(await names(owner.rootHandle, 4), fixtureNames())
  )
    fail('wrapper_test_fixture_invalid');
  for (const [name, guard] of owner.files) {
    signal.throwIfAborted();
    await checkFile(guard, fdPath(owner.rootHandle, name), false);
  }
  await fixtureConfiguration(owner);
  if (
    !sameSnapshot(before, await owner.rootHandle.stat()) ||
    !isDeepStrictEqual(await names(owner.rootHandle, 4), fixtureNames())
  )
    fail('wrapper_test_fixture_invalid');
  await checkRoot(owner);
  signal.throwIfAborted();
}

/** Fixed three-file output of the explicit friend build; no executable/module selection. */
export async function admitRepositoryTestOriginalRuntimeFixture(request: {
  readonly root: string;
  readonly stagingParent: string;
  readonly workspace: string;
  readonly context: RepositoryTestOriginalRuntimeContext;
  readonly members: readonly PayloadMember[];
  readonly signal: AbortSignal;
}): Promise<{
  readonly fixture: RepositoryTestOriginalRuntimeFixture;
  dispose(): Promise<void>;
}> {
  if (process.platform !== 'linux' || process.arch !== 'x64')
    fail('wrapper_payload_platform_unsupported');
  const context = fixtureContext(request.context);
  if (!Array.isArray(request.members) || request.members.length !== 3)
    fail('wrapper_test_fixture_invalid');
  const members = request.members.map((member) => {
    exactRecord(member, ['path', 'mode', 'size', 'sha256']);
    return Object.freeze({ ...member });
  });
  if (
    members.length !== 3 ||
    !isDeepStrictEqual(members.map((m) => m.path).sort(), fixtureNames()) ||
    members.some(
      (m) =>
        !Number.isSafeInteger(m.size) ||
        m.size < 1 ||
        m.size > (m.path.endsWith('.dll') ? 16 * 1024 * 1024 : 64 * 1024) ||
        m.mode !== 0o644 ||
        !lowerHex(m.sha256, 64),
    )
  )
    fail('wrapper_test_fixture_invalid');
  if (![request.root, request.stagingParent, request.workspace].every(path.isAbsolute))
    fail('wrapper_test_fixture_invalid');
  const parent = await realpath(request.stagingParent);
  const workspace = await realpath(request.workspace);
  if (
    parent !== request.stagingParent ||
    parent === workspace ||
    parent.startsWith(workspace + path.sep) ||
    path.dirname(request.root) !== parent ||
    (await realpath(request.root)) !== request.root
  )
    fail('wrapper_test_fixture_invalid');
  const parentHandle = await open(parent, directoryFlags());
  let rootHandle: FileHandle | undefined;
  const files = new Map<string, FileGuard>();
  try {
    const parentIdentity = await parentHandle.stat();
    requireOwned(parentIdentity, true, 0o700);
    const name = path.basename(request.root);
    rootHandle = await open(fdPath(parentHandle, name), directoryFlags());
    requireOwned(await rootHandle.stat(), true, 0o700);
    if (!isDeepStrictEqual(await names(rootHandle, 4), fixtureNames()))
      fail('wrapper_test_fixture_invalid');
    for (const member of members) {
      request.signal.throwIfAborted();
      const named = fdPath(rootHandle, member.path);
      const snapshot = await lstat(named);
      requireOwned(snapshot, false, 0o644);
      if (snapshot.size !== member.size) fail('wrapper_test_fixture_invalid');
      const handle = await open(named, fileFlags());
      const guard = { handle, member, snapshot };
      files.set(member.path, guard);
      await checkFile(guard, named, true);
    }
    await rootHandle.chmod(0o500);
    const owner: RepositoryTestFixtureOwner = {
      name,
      parent,
      parentHandle,
      parentIdentity,
      rootHandle,
      context,
      files,
      rootSnapshot: await rootHandle.stat(),
      removed: false,
      references: 0,
      closing: false,
      disposed: false,
    };
    await auditFixture(owner, request.signal);
    const token = Object.freeze({}) as RepositoryTestOriginalRuntimeFixture;
    (repositoryTestFixtures ??= new WeakMap()).set(token, owner);
    return Object.freeze({ fixture: token, dispose: () => disposeFixture(owner) });
  } catch (error) {
    // A failed admission grants no deletion authority over caller storage.
    const cleanup = await Promise.allSettled([
      closeFiles({ files }),
      rootHandle?.close(),
      parentHandle.close(),
    ]);
    if (cleanup.some((result) => result.status === 'rejected') && error instanceof Error)
      Object.defineProperty(error, 'cleanupError', {
        value: new Error('wrapper_test_fixture_cleanup_failed'),
      });
    throw error;
  }
}

function disposeFixture(owner: RepositoryTestFixtureOwner): Promise<void> {
  owner.closing = true;
  if (owner.disposed) return Promise.resolve();
  if (owner.references) return Promise.reject(new ManagedTreeInUseError());
  return (owner.cleanup ??= (async () => {
    try {
      if (!owner.removed) {
        await auditFixture(owner, new AbortController().signal);
        await owner.rootHandle.chmod(0o700);
        await checkRoot(owner);
        await rm(fdPath(owner.parentHandle, owner.name), { recursive: true, force: false });
        owner.removed = true;
      }
      await closeFiles(owner);
      await owner.rootHandle.close();
      await owner.parentHandle.close();
      owner.disposed = true;
    } catch (error) {
      // Revoke loading and close retained handles even when hostile storage must be preserved.
      await Promise.allSettled([
        closeFiles(owner),
        owner.rootHandle.close(),
        owner.parentHandle.close(),
      ]);
      owner.cleanup = undefined;
      throw error;
    }
  })());
}

export async function acquireRepositoryTestOriginalRuntimeFixtureLease(
  fixture: RepositoryTestOriginalRuntimeFixture,
  expected: RepositoryTestOriginalRuntimeContext,
  signal: AbortSignal,
): Promise<{ readonly directoryFd: number; release(): void }> {
  const owner = repositoryTestFixtures?.get(fixture);
  if (
    !owner ||
    owner.closing ||
    owner.disposed ||
    !isDeepStrictEqual(owner.context, fixtureContext(expected))
  )
    fail('wrapper_test_fixture_invalid');
  owner.references += 1;
  let retained = false;
  try {
    await auditFixture(owner, signal);
    if (owner.closing) fail('wrapper_test_fixture_invalid');
    let released = false;
    const lease = Object.freeze({
      directoryFd: owner.rootHandle.fd,
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

/** Independent unpublished original bytes. This filesystem capability is NOT qualification. */
declare const repositoryTestOriginalArchiveBrand: unique symbol;
export interface RepositoryTestOriginalRuntimeArchive {
  readonly [repositoryTestOriginalArchiveBrand]: true;
}
export interface RepositoryTestOriginalArchiveContext extends RepositoryTestOriginalRuntimeContext {
  readonly repository: 'SolusQuest/agentic-pr-review-sandbox';
  readonly job: 'review';
  readonly workflowRef: string;
  readonly sourceRole: 'pr' | 'main';
}
interface OriginalArchiveOwner extends ManagedFilesystemOwner {
  readonly context: RepositoryTestOriginalArchiveContext;
  readonly pid: number;
}
let originalArchiveOwners: WeakMap<object, OriginalArchiveOwner> | undefined;

/** Shared identity view only; stripping publication provenance never creates a seal. */
export function originalArchiveFixtureContext(
  context: RepositoryTestOriginalArchiveContext,
): RepositoryTestOriginalRuntimeContext {
  return fixtureContext({
    sourceCommit: context.sourceCommit,
    sourceTree: context.sourceTree,
    workflowCommit: context.workflowCommit,
    runId: context.runId,
    attempt: context.attempt,
    archiveSha256: context.archiveSha256,
    receiptSha256: context.receiptSha256,
    assemblySha256: context.assemblySha256,
  });
}
function originalArchiveContext(
  value: RepositoryTestOriginalArchiveContext,
): RepositoryTestOriginalArchiveContext {
  exactRecord(value, [
    'sourceCommit',
    'sourceTree',
    'workflowCommit',
    'runId',
    'attempt',
    'archiveSha256',
    'receiptSha256',
    'assemblySha256',
    'repository',
    'job',
    'workflowRef',
    'sourceRole',
  ]);
  originalArchiveFixtureContext(value);
  if (
    value.repository !== 'SolusQuest/agentic-pr-review-sandbox' ||
    value.job !== 'review' ||
    value.workflowRef !==
      value.repository + '/.github/workflows/agentic-pr-review.yml@refs/heads/main' ||
    !['pr', 'main'].includes(value.sourceRole)
  )
    fail('wrapper_test_original_archive_invalid');
  return Object.freeze({ ...value });
}
function originalArchiveCurrent(context: RepositoryTestOriginalArchiveContext): void {
  for (const [key, expected] of [
    ['GITHUB_REPOSITORY', context.repository],
    ['GITHUB_JOB', context.job],
    ['GITHUB_WORKFLOW_REF', context.workflowRef],
    ['GITHUB_WORKFLOW_SHA', context.workflowCommit],
    ['GITHUB_RUN_ID', String(context.runId)],
    ['GITHUB_RUN_ATTEMPT', String(context.attempt)],
    ['EXPECTED_HEAD', context.sourceCommit],
    ['WORKFLOW_HEAD', context.workflowCommit],
  ])
    if (process.env[key] !== expected) fail('wrapper_test_original_archive_invalid');
}

/** Only the fixed genuine sealed-once consumer may use these bytes for execution.
 * This factory checks ownership/bytes, never registers qualification or a published map.
 */
export async function stageRepositoryTestOriginalRuntimeArchive(request: {
  readonly archiveBytes: Uint8Array;
  readonly receiptBytes: Uint8Array;
  readonly context: RepositoryTestOriginalArchiveContext;
  readonly stagingParent: string;
  readonly workspace: string;
  readonly signal: AbortSignal;
}): Promise<{
  readonly root: string;
  readonly archive: RepositoryTestOriginalRuntimeArchive;
  readonly receipt: ManagedReceipt;
  dispose(): Promise<void>;
}> {
  if (process.platform !== 'linux' || process.arch !== 'x64')
    fail('wrapper_payload_platform_unsupported');
  const context = originalArchiveContext(request.context);
  originalArchiveCurrent(context);
  const archive = Buffer.from(request.archiveBytes),
    rawReceipt = Buffer.from(request.receiptBytes);
  if (
    archive.length < 1 ||
    archive.length > 16 * 1024 * 1024 ||
    rawReceipt.length < 1 ||
    rawReceipt.length > 65536 ||
    sha256(archive) !== context.archiveSha256 ||
    sha256(rawReceipt) !== context.receiptSha256
  )
    fail('wrapper_test_original_archive_invalid');
  const checked = inspectManagedPackage(archive, parseStrictJson(rawReceipt, 65536)).receipt;
  const receipt: ManagedReceipt = Object.freeze({
    ...checked,
    identity: Object.freeze({ ...checked.identity }),
    members: Object.freeze(checked.members.map((member) => Object.freeze({ ...member }))),
  });
  if (
    canonicalJson(receipt) + '\n' !== rawReceipt.toString('utf8') ||
    receipt.identity.sourceCommit !== context.sourceCommit ||
    receipt.identity.sourceTree !== context.sourceTree ||
    receipt.members.find(
      (member) => member.path === 'agentic-pr-review/AgenticPrReview.Runtime.dll',
    )?.sha256 !== context.assemblySha256
  )
    fail('wrapper_test_original_archive_invalid');
  if (![request.stagingParent, request.workspace].every(path.isAbsolute))
    fail('wrapper_test_original_archive_invalid');
  const parent = await realpath(request.stagingParent),
    workspace = await realpath(request.workspace);
  if (
    parent !== request.stagingParent ||
    parent === workspace ||
    parent.startsWith(workspace + path.sep)
  )
    fail('wrapper_test_original_archive_invalid');
  const parentHandle = await open(parent, directoryFlags());
  let rootHandle: FileHandle | undefined;
  let owner: OriginalArchiveOwner | undefined;
  let name: string | undefined;
  try {
    const parentIdentity = await parentHandle.stat();
    requireOwned(parentIdentity, true, 0o700);
    request.signal.throwIfAborted();
    const materialized = await materializeManagedPackage(archive, receipt, fdPath(parentHandle));
    name = path.basename(materialized.root);
    rootHandle = await open(fdPath(parentHandle, name), directoryFlags());
    const children = new Map<string, string[]>([['', []]]);
    for (const member of receipt.members) {
      const parts = member.path.split('/');
      for (let index = 0; index < parts.length; index++) {
        const relative = parts.slice(0, index).join('/');
        if (!children.has(relative)) children.set(relative, []);
        const entries = children.get(relative)!;
        if (!entries.includes(parts[index])) entries.push(parts[index]);
      }
    }
    for (const entries of children.values()) entries.sort();
    owner = {
      root: path.join(parent, name),
      name,
      parent,
      parentHandle,
      parentIdentity,
      rootHandle,
      receipt,
      actionSourceSha: context.sourceCommit,
      context,
      pid: process.pid,
      directories: new Map(),
      files: new Map(),
      children,
      references: 0,
      closing: false,
      disposed: false,
      removed: false,
    };
    await audit(owner, true, request.signal);
    await audit(owner, false, request.signal);
    originalArchiveCurrent(context);
    const token = Object.freeze({}) as RepositoryTestOriginalRuntimeArchive;
    (originalArchiveOwners ??= new WeakMap()).set(token, owner);
    const retained = owner;
    return Object.freeze({
      root: owner.root,
      archive: token,
      receipt,
      dispose: () => disposeOwner(retained),
    });
  } catch (error) {
    try {
      if (owner) {
        await checkRoot(owner);
        await closeFiles(owner);
        await restoreDirectories(owner, true);
        await checkRoot(owner);
        await rm(fdPath(parentHandle, owner.name), { recursive: true, force: false });
      }
    } catch (cleanupError) {
      if (error instanceof Error)
        Object.defineProperty(error, 'cleanupError', { value: cleanupError });
    } finally {
      await rootHandle?.close();
      await parentHandle.close();
    }
    throw error;
  } finally {
    archive.fill(0);
  }
}

export async function acquireRepositoryTestOriginalRuntimeArchiveLease(
  token: RepositoryTestOriginalRuntimeArchive,
  expected: RepositoryTestOriginalArchiveContext,
  signal: AbortSignal,
): Promise<{
  readonly directoryFd: number;
  readonly receipt: ManagedReceipt;
  readonly actionSourceSha: string;
  readonly files: VerifiedManagedTreeLease['files'];
  release(): void;
}> {
  const owner = originalArchiveOwners?.get(token);
  if (
    !owner ||
    owner.pid !== process.pid ||
    owner.closing ||
    owner.disposed ||
    !isDeepStrictEqual(owner.context, originalArchiveContext(expected))
  )
    fail('wrapper_test_original_archive_invalid');
  originalArchiveCurrent(owner.context);
  owner.references += 1;
  let retained = false;
  try {
    await audit(owner, false, signal);
    originalArchiveCurrent(owner.context);
    if (owner.closing) fail('wrapper_test_original_archive_invalid');
    let released = false;
    const lease = Object.freeze({
      directoryFd: owner.rootHandle.fd,
      receipt: owner.receipt,
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
