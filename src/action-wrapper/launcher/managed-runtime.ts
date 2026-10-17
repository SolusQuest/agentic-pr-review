import { createHash } from 'node:crypto';
import { constants, read, type Stats } from 'node:fs';
import { lstat, open, opendir, realpath, type FileHandle } from 'node:fs/promises';
import path from 'node:path';

import type { VerifiedManagedTreeLease } from './payload-staging.js';
import { parseStrictJson } from './strict-json.js';
import { fail } from './validation.js';

const MAXIMUM_RUNTIME_ENTRIES = 1024;
const MAXIMUM_RUNTIME_FILE_BYTES = 64 * 1024 * 1024;
const MAXIMUM_RUNTIME_BYTES = 512 * 1024 * 1024;
const MAXIMUM_CONFIGURATION_BYTES = 256 * 1024;

export class ManagedRuntimePrerequisiteError extends Error {
  readonly code = 'wrapper_managed_runtime_prerequisite';
  constructor() {
    super('wrapper_managed_runtime_prerequisite');
    this.name = 'ManagedRuntimePrerequisiteError';
  }
}

export class ManagedRuntimeCleanupError extends Error {
  constructor(readonly retryCleanup: () => Promise<void>) {
    super('wrapper_managed_runtime_cleanup_failed');
    this.name = 'ManagedRuntimeCleanupError';
  }
}

export interface AdmittedManagedRuntime {
  readonly executableHandle: FileHandle;
  readonly executablePath: string;
  readonly frameworkVersion: string;
  readonly tempRoot: string;
  validate(): Promise<void>;
  validateProbe(bytes: Uint8Array): void;
  release(): Promise<void>;
}

function prerequisite(condition: unknown): asserts condition {
  if (!condition) throw new ManagedRuntimePrerequisiteError();
}
function inside(candidate: string, root: string): boolean {
  return candidate === root || candidate.startsWith(root + path.sep);
}
function snapshot(left: Stats, right: Stats): boolean {
  return (
    left.dev === right.dev &&
    left.ino === right.ino &&
    left.uid === right.uid &&
    left.mode === right.mode &&
    left.nlink === right.nlink &&
    left.size === right.size &&
    left.mtimeMs === right.mtimeMs &&
    left.ctimeMs === right.ctimeMs
  );
}
function trustedKind(stat: Stats, directory: boolean): void {
  prerequisite(
    (stat.uid === 0 || stat.uid === process.getuid?.()) &&
      (stat.mode & 0o022) === 0 &&
      (directory ? stat.isDirectory() : stat.isFile() && stat.nlink === 1),
  );
}
async function versionNames(directory: string): Promise<string[]> {
  const names: string[] = [];
  const entries = await opendir(directory);
  for await (const entry of entries) {
    prerequisite(
      names.length < 64 && entry.isDirectory() && /^10\.0\.(0|[1-9][0-9]{0,5})$/.test(entry.name),
    );
    names.push(entry.name);
  }
  return names.sort((a, b) => Number(b.split('.')[2]) - Number(a.split('.')[2]));
}

/** The caller supplies a runner/setup-owned installation, never workspace or ambient discovery. */
export async function admitManagedRuntime(request: {
  readonly trustedRuntimeRoot: string;
  readonly workspace: string;
  readonly tempRoot: string;
  readonly signal: AbortSignal;
}): Promise<AdmittedManagedRuntime> {
  const handles = new Set<FileHandle>();
  const closeHandle = async (handle: FileHandle): Promise<void> => {
    await handle.close();
    handles.delete(handle);
  };
  const release = async (): Promise<void> => {
    const results = await Promise.allSettled([...handles].map(closeHandle));
    if (results.some((result) => result.status === 'rejected'))
      throw new ManagedRuntimeCleanupError(release);
  };
  try {
    prerequisite(process.platform === 'linux' && process.arch === 'x64');
    prerequisite(
      [request.trustedRuntimeRoot, request.workspace, request.tempRoot].every(path.isAbsolute),
    );
    request.signal.throwIfAborted();
    const workspace = await realpath(request.workspace);
    const root = await realpath(request.trustedRuntimeRoot);
    const tempRoot = await realpath(request.tempRoot);
    prerequisite(
      !inside(root, workspace) &&
        !inside(path.resolve(request.trustedRuntimeRoot), path.resolve(request.workspace)) &&
        !inside(tempRoot, workspace) &&
        !inside(tempRoot, root) &&
        !inside(root, tempRoot),
    );
    // Shared sticky temporary ancestors are allowed; the actual installation is not shared-writable.
    for (let ancestor = root; ; ancestor = path.dirname(ancestor)) {
      const stat = await lstat(ancestor);
      prerequisite(
        stat.isDirectory() &&
          (stat.uid === 0 || stat.uid === process.getuid?.()) &&
          ((stat.mode & 0o022) === 0 ||
            (ancestor !== root && stat.uid === 0 && (stat.mode & 0o1000) !== 0)),
      );
      if (path.dirname(ancestor) === ancestor) break;
    }
    const scratch = await lstat(tempRoot);
    prerequisite(
      scratch.isDirectory() &&
        scratch.uid === process.getuid?.() &&
        (scratch.mode & 0o7777) === 0o700,
    );
    const guardedOpen = async (named: string, directory: boolean): Promise<FileHandle> => {
      request.signal.throwIfAborted();
      const before = await lstat(named);
      trustedKind(before, directory);
      const handle = await open(
        named,
        constants.O_RDONLY |
          constants.O_NOFOLLOW |
          constants.O_NONBLOCK |
          (directory ? constants.O_DIRECTORY : 0),
      );
      handles.add(handle);
      prerequisite(snapshot(before, await handle.stat()));
      return handle;
    };
    const rootHandle = await guardedOpen(root, true);
    const scratchHandle = await guardedOpen(tempRoot, true);
    const executablePath = path.join(root, 'dotnet');
    const executableHandle = await guardedOpen(executablePath, false);
    prerequisite(((await executableHandle.stat()).mode & 0o111) !== 0);
    const validateNativeImage = async (handle: FileHandle): Promise<void> => {
      const elf = Buffer.alloc(64);
      const header = await handle.read(elf, 0, elf.length, 0);
      prerequisite(
        header.bytesRead === 64 &&
          elf.subarray(0, 7).equals(Buffer.from('7f454c46020101', 'hex')) &&
          [2, 3].includes(elf.readUInt16LE(16)) &&
          elf.readUInt16LE(18) === 62 &&
          elf.readUInt32LE(20) === 1,
      );
    };
    await validateNativeImage(executableHandle);
    const fxrRoot = path.join(root, 'host/fxr');
    const frameworkRoot = path.join(root, 'shared/Microsoft.NETCore.App');
    const ancestors = new Map<string, Stats>();
    for (const directory of ['host', 'host/fxr', 'shared', 'shared/Microsoft.NETCore.App']) {
      const stat = await lstat(path.join(root, directory));
      trustedKind(stat, true);
      ancestors.set(directory, stat);
    }
    const fxrVersions = await versionNames(fxrRoot);
    const frameworkVersions = await versionNames(frameworkRoot);
    prerequisite(fxrVersions.length > 0 && frameworkVersions.length > 0);
    const frameworkVersion = frameworkVersions[0]!;
    prerequisite(
      Number(frameworkVersion.split('.')[2]) >= 9 && Number(fxrVersions[0]!.split('.')[2]) >= 9,
    );
    const ledger = new Map<string, { stat: Stats; digest?: string }>();
    const hashFile = async (handle: FileHandle, size: number): Promise<string> => {
      const hash = createHash('sha256');
      const bytes = Buffer.alloc(64 * 1024);
      let offset = 0;
      while (offset < size) {
        request.signal.throwIfAborted();
        const { bytesRead } = await handle.read(
          bytes,
          0,
          Math.min(bytes.length, size - offset),
          offset,
        );
        prerequisite(bytesRead > 0);
        hash.update(bytes.subarray(0, bytesRead));
        offset += bytesRead;
      }
      return hash.digest('hex');
    };
    const scan = async (initial: boolean): Promise<void> => {
      let count = 0;
      let total = 0;
      const seen = new Set<string>();
      const visit = async (relative: string): Promise<void> => {
        request.signal.throwIfAborted();
        prerequisite(++count <= MAXIMUM_RUNTIME_ENTRIES);
        const named = path.join(root, relative);
        const before = await lstat(named);
        const directory = before.isDirectory();
        trustedKind(before, directory);
        seen.add(relative);
        let digest: string | undefined;
        if (directory) {
          const entries = await opendir(named);
          for await (const entry of entries) await visit(path.posix.join(relative, entry.name));
        } else {
          prerequisite(
            before.size <= MAXIMUM_RUNTIME_FILE_BYTES &&
              (total += before.size) <= MAXIMUM_RUNTIME_BYTES,
          );
          const handle = relative === 'dotnet' ? executableHandle : await guardedOpen(named, false);
          try {
            prerequisite(snapshot(before, await handle.stat()));
            if (
              ['libhostfxr.so', 'libhostpolicy.so', 'libcoreclr.so'].includes(
                path.basename(relative),
              )
            )
              await validateNativeImage(handle);
            if (path.basename(relative) === 'System.Private.CoreLib.dll') {
              const header = Buffer.alloc(64);
              const read = await handle.read(header, 0, header.length, 0);
              prerequisite(
                read.bytesRead === 64 && header.subarray(0, 2).toString('ascii') === 'MZ',
              );
            }
            digest = await hashFile(handle, before.size);
            prerequisite(snapshot(before, await handle.stat()));
          } finally {
            if (handle !== executableHandle) await closeHandle(handle);
          }
        }
        prerequisite(snapshot(before, await lstat(named)));
        if (initial)
          ledger.set(relative, { stat: before, ...(digest === undefined ? {} : { digest }) });
        else {
          const expected = ledger.get(relative);
          prerequisite(expected && snapshot(expected.stat, before) && expected.digest === digest);
        }
      };
      await visit('dotnet');
      await visit(`host/fxr/${fxrVersions[0]}`);
      await visit(`shared/Microsoft.NETCore.App/${frameworkVersion}`);
      prerequisite(initial || seen.size === ledger.size);
    };
    await scan(true);
    for (const relative of [
      `host/fxr/${fxrVersions[0]}/libhostfxr.so`,
      ...[
        'libcoreclr.so',
        'libhostpolicy.so',
        'System.Private.CoreLib.dll',
        'Microsoft.NETCore.App.deps.json',
        'Microsoft.NETCore.App.runtimeconfig.json',
      ].map((name) => `shared/Microsoft.NETCore.App/${frameworkVersion}/${name}`),
    ])
      prerequisite(ledger.get(relative)?.digest && ledger.get(relative)!.stat.size > 0);
    const rootStat = await rootHandle.stat();
    const validate = async (): Promise<void> => {
      try {
        request.signal.throwIfAborted();
        prerequisite(
          (await realpath(request.trustedRuntimeRoot)) === root &&
            snapshot(rootStat, await lstat(root)) &&
            snapshot(rootStat, await rootHandle.stat()),
        );
        prerequisite(
          snapshot(scratch, await lstat(tempRoot)) && snapshot(scratch, await scratchHandle.stat()),
        );
        prerequisite(
          JSON.stringify(await versionNames(fxrRoot)) === JSON.stringify(fxrVersions) &&
            JSON.stringify(await versionNames(frameworkRoot)) === JSON.stringify(frameworkVersions),
        );
        for (const [directory, expected] of ancestors) {
          const stat = await lstat(path.join(root, directory));
          trustedKind(stat, true);
          prerequisite(snapshot(expected, stat));
        }
        await scan(false);
      } catch (error) {
        if (request.signal.aborted) throw error;
        throw new ManagedRuntimePrerequisiteError();
      }
    };
    return {
      executableHandle,
      executablePath,
      frameworkVersion,
      tempRoot,
      validate,
      validateProbe: (bytes) => {
        const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
        const expected = `Microsoft.NETCore.App ${frameworkVersion} [${frameworkRoot}]`;
        prerequisite(text.split(/\r?\n/).filter((line) => line === expected).length === 1);
      },
      release,
    };
  } catch (error) {
    try {
      await release();
    } catch (cleanupError) {
      if (error instanceof Error)
        Object.defineProperty(error, 'cleanupError', { value: cleanupError });
    }
    if (request.signal.aborted) throw error;
    if (error instanceof ManagedRuntimePrerequisiteError) throw error;
    const failure = new ManagedRuntimePrerequisiteError();
    if (error instanceof Error && 'cleanupError' in error)
      Object.defineProperty(failure, 'cleanupError', { value: error.cleanupError });
    throw failure;
  }
}

/** Loading configuration is a code-loading capability even when its bytes are authenticated. */
export async function validateManagedLoadingConfiguration(
  lease: VerifiedManagedTreeLease,
): Promise<void> {
  try {
    const stem = 'agentic-pr-review/AgenticPrReview.Runtime';
    if (lease.files.some(({ member }) => member.path === `${stem}.runtimeconfig.dev.json`))
      fail('wrapper_managed_loading_config_invalid');
    const config = lease.files.find(({ member }) => member.path === `${stem}.runtimeconfig.json`);
    if (!config || config.member.size < 1 || config.member.size > MAXIMUM_CONFIGURATION_BYTES)
      fail('wrapper_managed_loading_config_invalid');
    const bytes = Buffer.alloc(config.member.size);
    let offset = 0;
    while (offset < bytes.length) {
      const bytesRead = await new Promise<number>((resolve, reject) => {
        read(config.fd, bytes, offset, bytes.length - offset, offset, (error, count) =>
          error ? reject(error) : resolve(count),
        );
      });
      if (!bytesRead) fail('wrapper_managed_loading_config_invalid');
      offset += bytesRead;
    }
    if (createHash('sha256').update(bytes).digest('hex') !== config.member.sha256)
      fail('wrapper_managed_loading_config_invalid');
    const record = (value: unknown): Record<string, unknown> => {
      if (value === null || typeof value !== 'object' || Array.isArray(value))
        fail('wrapper_managed_loading_config_invalid');
      return value as Record<string, unknown>;
    };
    const root = record(parseStrictJson(bytes, MAXIMUM_CONFIGURATION_BYTES));
    const options = record(root.runtimeOptions);
    const framework = record(options.framework);
    if (
      Object.keys(root).some((key) => key !== 'runtimeOptions') ||
      Object.keys(options).some((key) => !['tfm', 'framework', 'configProperties'].includes(key)) ||
      options.tfm !== 'net10.0' ||
      Object.keys(framework).some((key) => !['name', 'version'].includes(key)) ||
      framework.name !== 'Microsoft.NETCore.App' ||
      framework.version !== '10.0.9'
    )
      fail('wrapper_managed_loading_config_invalid');
    const properties =
      options.configProperties === undefined ? {} : record(options.configProperties);
    // The pinned SDK's ordinary Boolean feature switches grant no new loading path.
    const allowed = new Set([
      'System.Reflection.Metadata.MetadataUpdater.IsSupported',
      'System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization',
      'System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault',
    ]);
    if (
      Object.entries(properties).some(
        ([name, value]) => !allowed.has(name) || typeof value !== 'boolean',
      )
    )
      fail('wrapper_managed_loading_config_invalid');
  } catch {
    fail('wrapper_managed_loading_config_invalid');
  }
}
