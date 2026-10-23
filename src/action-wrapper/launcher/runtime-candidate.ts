import { constants } from 'node:fs';
import { lstat, open, readdir, type FileHandle } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { gunzipSync } from 'node:zlib';
import { readBoundedFile } from '../../../scripts/release/build-payload.format.mjs';
import { PINNED_MANAGED_RUNTIME } from './runtime-prerequisite.js';
import { fail } from './validation.js';

interface Member {
  readonly directory: boolean;
  readonly size: number;
  readonly digest: string;
  readonly executable: boolean;
}
function requireRuntime(value: unknown): asserts value {
  if (!value) fail('wrapper_managed_runtime_prerequisite');
}

/** The fixed original digest is authority; no preparation-supplied inventory is trusted. */
function runtimeMembers(archive: Buffer): Map<string, Member> {
  requireRuntime(
    archive.length === PINNED_MANAGED_RUNTIME.archiveBytes &&
      createHash('sha512').update(archive).digest('hex') === PINNED_MANAGED_RUNTIME.sha512,
  );
  const tar = gunzipSync(archive, { maxOutputLength: 512 * 1024 * 1024 });
  const members = new Map<string, Member>();
  let offset = 0;
  const text = (bytes: Buffer) => bytes.toString('ascii').replace(/\0.*$/su, '');
  const octal = (bytes: Buffer): number => {
    const value = text(bytes).trim();
    requireRuntime(/^[0-7]+$/u.test(value));
    const number = parseInt(value, 8);
    requireRuntime(Number.isSafeInteger(number));
    return number;
  };
  while (offset + 512 <= tar.length) {
    const header = tar.subarray(offset, offset + 512);
    if (header.every((byte) => byte === 0)) break;
    requireRuntime(members.size < 1024);
    const prefix = text(header.subarray(345, 500));
    let name = (prefix ? prefix + '/' : '') + text(header.subarray(0, 100));
    requireRuntime(name.startsWith('./'));
    name = name.slice(2).replace(/\/$/u, '');
    requireRuntime(
      name.length <= 1024 &&
        (name === '' ||
          name.split('/').every((p) => /^[A-Za-z0-9_.+-]+$/u.test(p) && p !== '.' && p !== '..')) &&
        !members.has(name),
    );
    const size = octal(header.subarray(124, 136));
    const mode = octal(header.subarray(100, 108));
    const directory = header[156] === 53;
    requireRuntime(directory || header[156] === 48 || header[156] === 0);
    requireRuntime(size <= 64 * 1024 * 1024 && (directory ? size === 0 : size > 0));
    const end = offset + 512 + size;
    requireRuntime(end <= tar.length);
    members.set(name, {
      directory,
      size,
      executable: (mode & 0o111) !== 0,
      digest: createHash('sha256')
        .update(tar.subarray(offset + 512, end))
        .digest('hex'),
    });
    offset += 512 + Math.ceil(size / 512) * 512;
  }
  requireRuntime(offset + 1024 <= tar.length && tar.subarray(offset).every((byte) => byte === 0));
  requireRuntime(members.get('')?.directory && members.get('dotnet')?.executable);
  return members;
}

/** Reopen descriptor-relative and compare every candidate byte with the pinned original. */
export async function verifyRuntimeCandidate(request: {
  readonly archivePath: string;
  readonly root: string;
  readonly signal: AbortSignal;
}): Promise<void> {
  const members = runtimeMembers(
    await readBoundedFile(request.archivePath, PINNED_MANAGED_RUNTIME.archiveBytes),
  );
  const flags = constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK;
  const root = await open(request.root, flags | constants.O_DIRECTORY);
  const seen = new Set<string>();
  const same = (
    a: Awaited<ReturnType<FileHandle['stat']>>,
    b: Awaited<ReturnType<FileHandle['stat']>>,
  ) =>
    a.ino === b.ino &&
    a.dev === b.dev &&
    a.mode === b.mode &&
    a.size === b.size &&
    a.mtimeMs === b.mtimeMs &&
    a.ctimeMs === b.ctimeMs;
  async function visit(relative: string, handle: FileHandle): Promise<void> {
    request.signal.throwIfAborted();
    const expected = members.get(relative);
    requireRuntime(expected && !seen.has(relative));
    seen.add(relative);
    const before = await handle.stat();
    requireRuntime(
      before.uid === process.getuid?.() &&
        (before.mode & 0o022) === 0 &&
        (expected.directory
          ? before.isDirectory()
          : before.isFile() && before.nlink === 1 && before.size === expected.size),
    );
    if (expected.directory) {
      for (const child of await readdir(`/proc/self/fd/${handle.fd}`)) {
        const named = `/proc/self/fd/${handle.fd}/${child}`;
        const next = relative ? relative + '/' + child : child;
        const member = members.get(next);
        requireRuntime(member);
        const info = await lstat(named);
        const opened = await open(named, flags | (member.directory ? constants.O_DIRECTORY : 0));
        try {
          requireRuntime(same(info, await opened.stat()));
          await visit(next, opened);
          requireRuntime(same(info, await lstat(named)));
        } finally {
          await opened.close();
        }
      }
    } else {
      requireRuntime(((before.mode & 0o111) !== 0) === expected.executable);
      const hash = createHash('sha256');
      const buffer = Buffer.alloc(64 * 1024);
      let offset = 0;
      while (offset < expected.size) {
        request.signal.throwIfAborted();
        const { bytesRead } = await handle.read(
          buffer,
          0,
          Math.min(buffer.length, expected.size - offset),
          offset,
        );
        requireRuntime(bytesRead > 0);
        hash.update(buffer.subarray(0, bytesRead));
        offset += bytesRead;
      }
      requireRuntime(hash.digest('hex') === expected.digest);
    }
    requireRuntime(same(before, await handle.stat()));
  }
  try {
    const named = await lstat(request.root);
    requireRuntime(same(named, await root.stat()));
    await visit('', root);
    requireRuntime(seen.size === members.size && same(named, await lstat(request.root)));
  } finally {
    await root.close();
  }
}
