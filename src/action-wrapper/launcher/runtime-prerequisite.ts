import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import {
  chmod,
  lstat,
  mkdir,
  mkdtemp,
  open,
  realpath,
  rm,
  type FileHandle,
} from 'node:fs/promises';
import type { IncomingMessage } from 'node:http';
import { request as httpsRequest } from 'node:https';
import path from 'node:path';

import { ManagedRuntimePrerequisiteError } from './managed-runtime.js';
import type { RepositoryTestPayloadTransport } from './release-payload.js';

/** Maintainer-owned provisioning policy, compiled into the exact Action source. */
export const PINNED_MANAGED_RUNTIME = Object.freeze({
  version: '10.0.13',
  url: 'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-linux-x64.tar.gz',
  sha512:
    '9574d95364110409416735e7842796f8d614c21d74cb25d204a3e4cfd11b2c05efdf19ca89b8069650a04f64add2705967dcb04b8665a36d7a3f30a5d8c05afe',
  archiveBytes: 36_648_012,
});
const MAXIMUM_LISTING_BYTES = 2 * 1024 * 1024;
const MAXIMUM_ENTRIES = 1024;
const MAXIMUM_FILE_BYTES = 64 * 1024 * 1024;
const MAXIMUM_EXPANDED_BYTES = 512 * 1024 * 1024;
const COMMAND_KILL_GRACE_MS = 100;
const COMMAND_CLOSE_GRACE_MS = 5_000;

export interface RuntimePrerequisiteRequest {
  readonly runnerTemp: string;
  readonly workspace: string;
  readonly signal: AbortSignal;
}
export interface OwnedRuntimePrerequisite {
  readonly trustedRuntimeRoot: string;
  dispose(): Promise<void>;
}

/** Installation remains owned until the actual extractor closes. */
export class RuntimePrerequisiteTerminationUnconfirmedError extends ManagedRuntimePrerequisiteError {
  constructor(
    readonly terminationConfirmed: Promise<void>,
    readonly resourcesReleased: Promise<void>,
  ) {
    super();
    this.name = 'RuntimePrerequisiteTerminationUnconfirmedError';
    void resourcesReleased.catch(() => {});
  }
}

class CommandTerminationUnconfirmedError extends Error {
  constructor(readonly terminationConfirmed: Promise<void>) {
    super('wrapper_managed_runtime_prerequisite');
  }
}

function requirePrerequisite(condition: unknown): asserts condition {
  if (!condition) throw new ManagedRuntimePrerequisiteError();
}
function inside(candidate: string, root: string): boolean {
  return candidate === root || candidate.startsWith(root + path.sep);
}

/** Admission precedes extraction; this is the pinned target tar's escaped C-locale listing. */
export function validatePinnedRuntimeListing(bytes: Uint8Array): void {
  requirePrerequisite(bytes.byteLength > 0 && bytes.byteLength <= MAXIMUM_LISTING_BYTES);
  requirePrerequisite(!(bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf));
  const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  requirePrerequisite(text.endsWith('\n'));
  const names = new Set<string>();
  const directories = new Set<string>();
  const files = new Set<string>();
  const framework = 'shared/Microsoft.NETCore.App/' + PINNED_MANAGED_RUNTIME.version;
  const fxr = 'host/fxr/' + PINNED_MANAGED_RUNTIME.version;
  const parents = new Set([
    '',
    'host',
    'host/fxr',
    fxr,
    'shared',
    'shared/Microsoft.NETCore.App',
    framework,
  ]);
  let total = 0;
  for (const line of text.slice(0, -1).split('\n')) {
    const match =
      /^([d-][rwx-]{9}) +[0-9]+\/[0-9]+ +([0-9]+) [0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2} (\.\/[A-Za-z0-9_.+/-]*)$/u.exec(
        line,
      );
    requirePrerequisite(match && names.size < MAXIMUM_ENTRIES);
    const mode = match[1]!;
    const size = Number(match[2]);
    const directory = mode[0] === 'd';
    requirePrerequisite(mode[5] !== 'w' && mode[8] !== 'w' && Number.isSafeInteger(size));
    let relative = match[3]!.slice(2);
    requirePrerequisite(
      directory ? relative === '' || relative.endsWith('/') : !relative.endsWith('/'),
    );
    if (relative.endsWith('/')) relative = relative.slice(0, -1);
    requirePrerequisite(
      relative.length <= 1024 &&
        (relative === '' ||
          relative.split('/').every((part) => part !== '' && part !== '.' && part !== '..')) &&
        !names.has(relative),
    );
    names.add(relative);
    if (directory) {
      requirePrerequisite(size === 0 && parents.has(relative));
      directories.add(relative);
    } else {
      requirePrerequisite(
        size > 0 &&
          size <= MAXIMUM_FILE_BYTES &&
          (total += size) <= MAXIMUM_EXPANDED_BYTES &&
          (['dotnet', 'LICENSE.txt', 'ThirdPartyNotices.txt'].includes(relative) ||
            relative === fxr + '/libhostfxr.so' ||
            (relative.startsWith(framework + '/') &&
              !relative.slice(framework.length + 1).includes('/'))),
      );
      files.add(relative);
    }
  }
  requirePrerequisite([...parents].every((name) => directories.has(name)));
  for (const name of [
    'dotnet',
    fxr + '/libhostfxr.so',
    ...[
      'libcoreclr.so',
      'libhostpolicy.so',
      'System.Private.CoreLib.dll',
      'Microsoft.NETCore.App.deps.json',
      'Microsoft.NETCore.App.runtimeconfig.json',
    ].map((name) => framework + '/' + name),
  ])
    requirePrerequisite(files.has(name));
}

export function acquireRuntimePrerequisite(
  request: RuntimePrerequisiteRequest,
): Promise<OwnedRuntimePrerequisite> {
  return acquire(request, (url, options, callback) => httpsRequest(url, options, callback));
}

/** Repository-only transport substitution cannot change the pinned asset or digest. */
export function createRepositoryTestRuntimePrerequisiteAcquirer(
  transport: RepositoryTestPayloadTransport,
): typeof acquireRuntimePrerequisite {
  return (request) => acquire(request, transport);
}

async function acquire(
  request: RuntimePrerequisiteRequest,
  transport: RepositoryTestPayloadTransport,
): Promise<OwnedRuntimePrerequisite> {
  let parent: string | undefined;
  let disposal: Promise<void> | undefined;
  const dispose = (): Promise<void> =>
    (disposal ??= (async () => {
      try {
        if (parent) await rm(parent, { recursive: true, force: true });
      } catch (error) {
        disposal = undefined;
        throw error;
      }
    })());
  try {
    requirePrerequisite(process.platform === 'linux' && process.arch === 'x64');
    requirePrerequisite(path.isAbsolute(request.runnerTemp) && path.isAbsolute(request.workspace));
    request.signal.throwIfAborted();
    const runnerTemp = await realpath(request.runnerTemp);
    const workspace = await realpath(request.workspace);
    requirePrerequisite(!inside(runnerTemp, workspace));
    for (let ancestor = runnerTemp; ; ancestor = path.dirname(ancestor)) {
      const storage = await lstat(ancestor);
      requirePrerequisite(
        storage.isDirectory() &&
          (storage.uid === 0 || storage.uid === process.getuid?.()) &&
          ((storage.mode & 0o022) === 0 || (storage.uid === 0 && (storage.mode & 0o1000) !== 0)),
      );
      if (path.dirname(ancestor) === ancestor) break;
    }
    const tar = await lstat('/usr/bin/tar');
    requirePrerequisite(
      tar.isFile() && tar.uid === 0 && (tar.mode & 0o022) === 0 && (tar.mode & 0o111) !== 0,
    );
    parent = await mkdtemp(path.join(runnerTemp, 'apr-dotnet-'));
    await chmod(parent, 0o700);
    const root = path.join(parent, 'runtime');
    await mkdir(root, { mode: 0o700 });
    const archive = path.join(parent, 'runtime.tar.gz');
    await downloadArchive(archive, transport, request.signal);
    const listing = await runTar(
      [
        '--list',
        '--verbose',
        '--numeric-owner',
        '--full-time',
        '--quoting-style=escape',
        '-zf',
        archive,
      ],
      parent,
      request.signal,
      30_000,
      MAXIMUM_LISTING_BYTES,
    );
    validatePinnedRuntimeListing(listing);
    await runTar(
      [
        '--extract',
        '--gzip',
        '--file',
        archive,
        '--directory',
        root,
        '--no-same-owner',
        '--no-same-permissions',
      ],
      parent,
      request.signal,
      60_000,
      8 * 1024,
    );
    // The archive's ./ entry can otherwise reset this directory's mode.
    await chmod(root, 0o700);
    await rm(archive);
    request.signal.throwIfAborted();
    return { trustedRuntimeRoot: root, dispose };
  } catch (error) {
    if (error instanceof CommandTerminationUnconfirmedError) {
      throw new RuntimePrerequisiteTerminationUnconfirmedError(
        error.terminationConfirmed,
        error.terminationConfirmed.then(dispose),
      );
    }
    let cleanupError: unknown;
    try {
      await dispose();
    } catch (caught) {
      cleanupError = caught;
    }
    const failure = new ManagedRuntimePrerequisiteError();
    if (cleanupError !== undefined)
      Object.defineProperty(failure, 'cleanupError', { value: cleanupError });
    throw failure;
  }
}

async function downloadArchive(
  archive: string,
  transport: RepositoryTestPayloadTransport,
  callerSignal: AbortSignal,
): Promise<void> {
  const signal = AbortSignal.any([callerSignal, AbortSignal.timeout(60_000)]);
  signal.throwIfAborted();
  const response: IncomingMessage = await new Promise((resolve, reject) => {
    const outgoing = transport(
      new URL(PINNED_MANAGED_RUNTIME.url),
      {
        method: 'GET',
        agent: false,
        signal,
        headers: {
          'User-Agent': 'agentic-pr-review-runtime-prerequisite',
          'Accept-Encoding': 'identity',
        },
      },
      resolve,
    );
    outgoing.once('error', reject);
    outgoing.end();
  });
  let file: FileHandle | undefined;
  try {
    file = await open(archive, 'wx', 0o600);
    const declared = response.headers['content-length'];
    requirePrerequisite(
      response.statusCode === 200 &&
        response.headers.location === undefined &&
        (response.headers['content-encoding'] === undefined ||
          response.headers['content-encoding'] === 'identity') &&
        (declared === undefined || declared === String(PINNED_MANAGED_RUNTIME.archiveBytes)),
    );
    const digest = createHash('sha512');
    let total = 0;
    for await (const value of response) {
      signal.throwIfAborted();
      const bytes = Buffer.from(value as Uint8Array);
      total += bytes.byteLength;
      requirePrerequisite(total <= PINNED_MANAGED_RUNTIME.archiveBytes);
      digest.update(bytes);
      let offset = 0;
      while (offset < bytes.length) {
        const written = await file.write(bytes, offset, bytes.length - offset);
        requirePrerequisite(written.bytesWritten > 0);
        offset += written.bytesWritten;
      }
    }
    requirePrerequisite(
      response.complete &&
        total === PINNED_MANAGED_RUNTIME.archiveBytes &&
        digest.digest('hex') === PINNED_MANAGED_RUNTIME.sha512,
    );
  } finally {
    response.destroy();
    await file?.close();
  }
}

function runTar(
  args: readonly string[],
  tempRoot: string,
  signal: AbortSignal,
  timeoutMs: number,
  maximumBytes: number,
): Promise<Buffer> {
  signal.throwIfAborted();
  const child = spawn('/usr/bin/tar', [...args], {
    cwd: tempRoot,
    env: { PATH: '/usr/bin:/bin', LC_ALL: 'C', TMPDIR: tempRoot },
    shell: false,
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  let closed = false;
  let resolveClosed!: () => void;
  const terminationConfirmed = new Promise<void>((resolve) => {
    resolveClosed = resolve;
  });
  return new Promise<Buffer>((resolve, reject) => {
    const chunks: Buffer[] = [];
    let outputBytes = 0;
    let stderrBytes = 0;
    let failed = false;
    let finished = false;
    let kill: NodeJS.Timeout | undefined;
    let closeDeadline: NodeJS.Timeout | undefined;
    const clear = () => {
      clearTimeout(timeout);
      if (kill) clearTimeout(kill);
      if (closeDeadline) clearTimeout(closeDeadline);
      signal.removeEventListener('abort', fail);
    };
    const fail = () => {
      if (failed || closed) return;
      failed = true;
      chunks.length = 0;
      try {
        child.kill('SIGTERM');
      } catch {
        /* The actual close barrier remains authoritative. */
      }
      kill = setTimeout(() => {
        if (closed) return;
        try {
          child.kill('SIGKILL');
        } catch {
          /* Do not infer close from signal delivery. */
        }
        closeDeadline = setTimeout(() => {
          if (closed || finished) return;
          finished = true;
          clear();
          reject(new CommandTerminationUnconfirmedError(terminationConfirmed));
        }, COMMAND_CLOSE_GRACE_MS);
      }, COMMAND_KILL_GRACE_MS);
    };
    const timeout = setTimeout(fail, timeoutMs);
    child.once('error', fail);
    child.stdout.on('error', fail);
    child.stderr.on('error', fail);
    child.stdout.on('data', (value: Buffer) => {
      outputBytes += value.length;
      if (outputBytes > maximumBytes) fail();
      if (!failed) chunks.push(Buffer.from(value));
    });
    child.stderr.on('data', (value: Buffer) => {
      stderrBytes += value.length;
      if (stderrBytes > 64 * 1024) fail();
    });
    child.once('close', (code, childSignal) => {
      closed = true;
      resolveClosed();
      clear();
      if (finished) return;
      finished = true;
      if (failed || signal.aborted || code !== 0 || childSignal !== null)
        reject(new ManagedRuntimePrerequisiteError());
      else resolve(Buffer.concat(chunks, outputBytes));
    });
    signal.addEventListener('abort', fail, { once: true });
    if (signal.aborted) fail();
  });
}
