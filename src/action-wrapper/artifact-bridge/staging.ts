import { constants, lstat, mkdir, open, realpath, rm } from 'node:fs/promises';
import path from 'node:path';
import { randomUUID } from 'node:crypto';

import { relativePath } from './contracts.js';
import { ARTIFACT_BRIDGE_LIMITS } from './limits.js';

export class ArtifactBridgeStagingError extends Error {
  constructor() {
    super('artifact_bridge_staging_invalid');
    this.name = 'ArtifactBridgeStagingError';
  }
}

export class ArtifactBridgeStaging {
  private constructor(private readonly canonicalRoot: string) {}

  static async create(configuredRoot: string): Promise<ArtifactBridgeStaging> {
    await mkdir(configuredRoot, { recursive: true, mode: 0o700 });
    const canonicalRoot = await realpath(configuredRoot);
    const rootStat = await lstat(canonicalRoot);
    if (!rootStat.isDirectory() || rootStat.isSymbolicLink()) {
      throw new ArtifactBridgeStagingError();
    }
    return new ArtifactBridgeStaging(canonicalRoot);
  }

  async createOperationDirectory(): Promise<string> {
    const operation = path.join(this.canonicalRoot, `op-${randomUUID()}`);
    await mkdir(operation, { recursive: false, mode: 0o700 });
    return operation;
  }

  async readSource(relative: string): Promise<Buffer> {
    const resolved = await this.resolveExistingFile(relative);
    const handle = await open(resolved, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0));
    try {
      const before = await handle.stat();
      if (
        !before.isFile() ||
        before.isSymbolicLink() ||
        before.size < 1 ||
        before.size > ARTIFACT_BRIDGE_LIMITS.maximumEncryptedObjectBytes
      ) {
        throw new ArtifactBridgeStagingError();
      }
      const bytes = Buffer.allocUnsafe(before.size);
      let offset = 0;
      while (offset < bytes.length) {
        const read = await handle.read(bytes, offset, bytes.length - offset, offset);
        if (read.bytesRead === 0) throw new ArtifactBridgeStagingError();
        offset += read.bytesRead;
      }
      const after = await handle.stat();
      if (
        after.size !== before.size ||
        after.mtimeMs !== before.mtimeMs ||
        after.ino !== before.ino
      ) {
        throw new ArtifactBridgeStagingError();
      }
      return bytes;
    } finally {
      await handle.close();
    }
  }

  async writeDestination(relative: string, bytes: Buffer): Promise<void> {
    if (bytes.length < 1 || bytes.length > ARTIFACT_BRIDGE_LIMITS.maximumEncryptedObjectBytes) {
      throw new ArtifactBridgeStagingError();
    }
    const destination = await this.resolveNewFile(relative);
    const handle = await open(
      destination,
      constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | (constants.O_NOFOLLOW ?? 0),
      0o600,
    );
    try {
      let offset = 0;
      while (offset < bytes.length) {
        const written = await handle.write(bytes, offset, bytes.length - offset, offset);
        if (written.bytesWritten === 0) throw new ArtifactBridgeStagingError();
        offset += written.bytesWritten;
      }
      await handle.sync();
      const stat = await handle.stat();
      if (!stat.isFile() || stat.size !== bytes.length) {
        throw new ArtifactBridgeStagingError();
      }
    } finally {
      await handle.close();
    }
  }

  async cleanupOperationDirectory(operationDirectory: string): Promise<void> {
    const canonical = path.resolve(operationDirectory);
    if (
      path.dirname(canonical) !== this.canonicalRoot ||
      !path.basename(canonical).startsWith('op-')
    ) {
      throw new ArtifactBridgeStagingError();
    }
    const rootStat = await lstat(this.canonicalRoot);
    if (!rootStat.isDirectory() || rootStat.isSymbolicLink()) {
      throw new ArtifactBridgeStagingError();
    }
    try {
      const operationStat = await lstat(canonical);
      if (!operationStat.isDirectory() || operationStat.isSymbolicLink()) {
        throw new ArtifactBridgeStagingError();
      }
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') return;
      throw error;
    }
    await rm(canonical, { recursive: true, force: true });
  }

  private async resolveExistingFile(relative: string): Promise<string> {
    const resolved = await this.resolve(relative);
    const canonical = await realpath(resolved);
    this.assertInside(canonical);
    const stat = await lstat(resolved);
    if (!stat.isFile() || stat.isSymbolicLink()) {
      throw new ArtifactBridgeStagingError();
    }
    return canonical;
  }

  private async resolveNewFile(relative: string): Promise<string> {
    const resolved = await this.resolve(relative);
    const parent = path.dirname(resolved);
    const canonicalParent = await realpath(parent);
    this.assertInside(canonicalParent);
    const parentStat = await lstat(canonicalParent);
    if (!parentStat.isDirectory() || parentStat.isSymbolicLink()) {
      throw new ArtifactBridgeStagingError();
    }
    return path.join(canonicalParent, path.basename(resolved));
  }

  private async resolve(relative: string): Promise<string> {
    if (!relativePath(relative)) throw new ArtifactBridgeStagingError();
    const segments = relative.split('/');
    let current = this.canonicalRoot;
    for (const segment of segments.slice(0, -1)) {
      current = path.join(current, segment);
      const stat = await lstat(current);
      if (!stat.isDirectory() || stat.isSymbolicLink()) {
        throw new ArtifactBridgeStagingError();
      }
    }
    const resolved = path.join(current, segments.at(-1)!);
    this.assertInside(resolved);
    return resolved;
  }

  private assertInside(candidate: string): void {
    const relative = path.relative(this.canonicalRoot, candidate);
    if (
      relative.length === 0 ||
      relative.startsWith(`..${path.sep}`) ||
      relative === '..' ||
      path.isAbsolute(relative)
    ) {
      throw new ArtifactBridgeStagingError();
    }
  }
}
