import { constants, type Stats } from 'node:fs';
import { open, realpath, mkdtemp, rm, type FileHandle } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { UPLOAD_LIMITS, uploadRelativePath } from './upload-protocol.js';

export interface OwnedUploadCopy {
  readonly operationDirectory: string;
  cleanup(): Promise<void>;
}

function invalid(): never {
  throw new Error('upload_staging_invalid');
}
function identity(a: Stats, b: Stats): boolean {
  return a.dev === b.dev && a.ino === b.ino;
}
function privateDirectory(stat: Stats): boolean {
  return stat.isDirectory() && stat.uid === process.getuid?.() && (stat.mode & 0o777) === 0o700;
}
function privateSourceDirectory(stat: Stats): boolean {
  return (
    stat.isDirectory() &&
    stat.uid === process.getuid?.() &&
    [0o500, 0o700].includes(stat.mode & 0o777)
  );
}
function privateFile(stat: Stats): boolean {
  return (
    stat.isFile() &&
    stat.uid === process.getuid?.() &&
    stat.nlink === 1 &&
    (stat.mode & 0o077) === 0
  );
}

/** Linux-only held-directory traversal; never follows a checked source pathname. */
export class UploadStaging {
  private constructor(
    private readonly root: FileHandle,
    private readonly canonicalRoot: string,
    private readonly rootIdentity: Stats,
  ) {}

  static async create(configuredRoot: string): Promise<UploadStaging> {
    if (process.platform !== 'linux' || !path.isAbsolute(configuredRoot)) invalid();
    const canonical = await realpath(configuredRoot);
    if (canonical !== configuredRoot) invalid();
    const root = await open(
      canonical,
      constants.O_RDONLY | constants.O_DIRECTORY | constants.O_NOFOLLOW,
    );
    try {
      const stat = await root.stat();
      if (!privateDirectory(stat)) invalid();
      return new UploadStaging(root, canonical, stat);
    } catch (error) {
      await root.close();
      throw error;
    }
  }

  async copy(
    relative: string,
    expectedSize: number,
    expectedDigest: string,
    signal: AbortSignal,
  ): Promise<OwnedUploadCopy> {
    if (
      !uploadRelativePath(relative) ||
      !Number.isSafeInteger(expectedSize) ||
      expectedSize < 1 ||
      expectedSize > UPLOAD_LIMITS.carrierBytes
    )
      invalid();
    signal.throwIfAborted();
    await this.assertRoot();
    const handles: FileHandle[] = [];
    let operation: string | undefined;
    let operationIdentity: Stats | undefined;
    let destinationIdentity: Stats | undefined;
    try {
      let directory = this.root;
      const segments = relative.split('/');
      for (const segment of segments.slice(0, -1)) {
        const next = await open(
          `/proc/self/fd/${directory.fd}/${segment}`,
          constants.O_RDONLY | constants.O_DIRECTORY | constants.O_NOFOLLOW,
        );
        handles.push(next);
        if (!privateSourceDirectory(await next.stat())) invalid();
        directory = next;
      }
      const source = await open(
        `/proc/self/fd/${directory.fd}/${segments.at(-1)!}`,
        constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK,
      );
      handles.push(source);
      const before = await source.stat();
      if (!privateFile(before) || before.size !== expectedSize) invalid();
      operation = await mkdtemp(`/proc/self/fd/${this.root.fd}/sdk-`);
      const operationHandle = await open(
        operation,
        constants.O_RDONLY | constants.O_DIRECTORY | constants.O_NOFOLLOW,
      );
      handles.push(operationHandle);
      operationIdentity = await operationHandle.stat();
      if (!privateDirectory(operationIdentity)) invalid();
      const destination = await open(
        `${operation}/artifact-envelope.json`,
        constants.O_RDWR | constants.O_CREAT | constants.O_EXCL | constants.O_NOFOLLOW,
        0o600,
      );
      handles.push(destination);
      const hash = createHash('sha256');
      const buffer = Buffer.alloc(64 * 1024);
      try {
        let offset = 0;
        while (offset < expectedSize) {
          signal.throwIfAborted();
          const { bytesRead } = await source.read(
            buffer,
            0,
            Math.min(buffer.length, expectedSize - offset),
            offset,
          );
          if (bytesRead === 0) invalid();
          hash.update(buffer.subarray(0, bytesRead));
          let written = 0;
          while (written < bytesRead) {
            const result = await destination.write(
              buffer,
              written,
              bytesRead - written,
              offset + written,
            );
            if (result.bytesWritten === 0) invalid();
            written += result.bytesWritten;
          }
          offset += bytesRead;
        }
        const after = await source.stat();
        if (
          !privateFile(after) ||
          !identity(before, after) ||
          after.size !== before.size ||
          after.mtimeMs !== before.mtimeMs ||
          after.ctimeMs !== before.ctimeMs ||
          hash.digest('hex') !== expectedDigest
        )
          invalid();
        const copiedHash = createHash('sha256');
        let offsetRead = 0;
        while (offsetRead < expectedSize) {
          signal.throwIfAborted();
          const { bytesRead } = await destination.read(
            buffer,
            0,
            Math.min(buffer.length, expectedSize - offsetRead),
            offsetRead,
          );
          if (bytesRead === 0) invalid();
          copiedHash.update(buffer.subarray(0, bytesRead));
          offsetRead += bytesRead;
        }
        destinationIdentity = await destination.stat();
        if (
          !privateFile(destinationIdentity) ||
          destinationIdentity.size !== expectedSize ||
          copiedHash.digest('hex') !== expectedDigest
        )
          invalid();
      } finally {
        buffer.fill(0);
      }
      await this.assertRoot();
      const canonicalOperation = await realpath(operation);
      if (path.dirname(canonicalOperation) !== this.canonicalRoot) invalid();
      const ownedPath = operation;
      const ownedIdentity = operationIdentity;
      const fileIdentity = destinationIdentity;
      return {
        operationDirectory: canonicalOperation,
        cleanup: async () => {
          await this.assertRoot();
          const check = await open(
            ownedPath,
            constants.O_RDONLY | constants.O_DIRECTORY | constants.O_NOFOLLOW,
          );
          try {
            if (!identity(await check.stat(), ownedIdentity)) invalid();
            const file = await open(
              `/proc/self/fd/${check.fd}/artifact-envelope.json`,
              constants.O_RDONLY | constants.O_NOFOLLOW,
            );
            try {
              if (!identity(await file.stat(), fileIdentity)) invalid();
            } finally {
              await file.close();
            }
            await rm(ownedPath, { recursive: true });
          } finally {
            await check.close();
          }
        },
      };
    } catch (error) {
      // Only a directory we created is eligible; uncertain identity stays retained.
      if (operation && operationIdentity) {
        const check = await open(
          operation,
          constants.O_RDONLY | constants.O_DIRECTORY | constants.O_NOFOLLOW,
        ).catch(() => undefined);
        if (check) {
          try {
            if (identity(await check.stat(), operationIdentity))
              await rm(operation, { recursive: true });
          } finally {
            await check.close();
          }
        }
      }
      throw error;
    } finally {
      await Promise.all(handles.map((handle) => handle.close()));
    }
  }

  async dispose(): Promise<void> {
    await this.root.close();
  }
  private async assertRoot(): Promise<void> {
    const current = await open(
      this.canonicalRoot,
      constants.O_RDONLY | constants.O_DIRECTORY | constants.O_NOFOLLOW,
    );
    try {
      if (
        !privateDirectory(await current.stat()) ||
        !identity(await current.stat(), this.rootIdentity)
      )
        invalid();
    } finally {
      await current.close();
    }
  }
}
