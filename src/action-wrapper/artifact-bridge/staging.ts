import { constants, type Stats } from 'node:fs';
import { lstat, mkdir, open, realpath, rm } from 'node:fs/promises';
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
  private readonly operationIdentities = new Map<string, FileIdentity>();

  private constructor(
    private readonly canonicalRoot: string,
    private readonly rootIdentity: FileIdentity,
  ) {}

  static async create(configuredRoot: string): Promise<ArtifactBridgeStaging> {
    const configured = path.resolve(configuredRoot);
    try {
      const configuredStat = await lstat(configured);
      if (configuredStat.isSymbolicLink()) throw new ArtifactBridgeStagingError();
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
    }
    await mkdir(configured, { recursive: true, mode: 0o700 });
    const canonicalRoot = await realpath(configured);
    const rootStat = await lstat(canonicalRoot);
    if (path.relative(configured, canonicalRoot) !== '' || !safePrivateDirectory(rootStat)) {
      throw new ArtifactBridgeStagingError();
    }
    return new ArtifactBridgeStaging(canonicalRoot, identity(rootStat));
  }

  async createOperationDirectory(): Promise<string> {
    await this.assertRoot();
    const operation = path.join(this.canonicalRoot, `op-${randomUUID()}`);
    await mkdir(operation, { recursive: false, mode: 0o700 });
    const operationStat = await lstat(operation);
    if (!safePrivateDirectory(operationStat)) throw new ArtifactBridgeStagingError();
    this.operationIdentities.set(operation, identity(operationStat));
    return operation;
  }

  async writeArchive(operationDirectory: string, bytes: Buffer): Promise<string> {
    if (bytes.length < 1 || bytes.length > ARTIFACT_BRIDGE_LIMITS.maximumStagingFileBytes) {
      throw new ArtifactBridgeStagingError();
    }
    await this.assertRoot();
    await this.assertOperationDirectory(operationDirectory);
    const archivePath = path.join(operationDirectory, 'artifact.zip');
    const handle = await open(
      archivePath,
      constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | (constants.O_NOFOLLOW ?? 0),
      0o600,
    );
    try {
      const before = await handle.stat();
      await this.assertOpenedPath(archivePath, before);
      let offset = 0;
      while (offset < bytes.length) {
        const written = await handle.write(bytes, offset, bytes.length - offset, offset);
        if (written.bytesWritten === 0) throw new ArtifactBridgeStagingError();
        offset += written.bytesWritten;
      }
      await handle.sync();
      const after = await handle.stat();
      await this.assertOpenedPath(archivePath, after);
      if (!after.isFile() || after.size !== bytes.length) {
        throw new ArtifactBridgeStagingError();
      }
      return archivePath;
    } finally {
      await handle.close();
    }
  }

  async readSource(relative: string): Promise<Buffer> {
    await this.assertRoot();
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
      await this.assertOpenedPath(resolved, before);
      const bytes = Buffer.allocUnsafe(before.size);
      let offset = 0;
      while (offset < bytes.length) {
        const read = await handle.read(bytes, offset, bytes.length - offset, offset);
        if (read.bytesRead === 0) throw new ArtifactBridgeStagingError();
        offset += read.bytesRead;
      }
      const after = await handle.stat();
      await this.assertOpenedPath(resolved, after);
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
    await this.assertRoot();
    const destination = await this.resolveNewFile(relative);
    const handle = await open(
      destination,
      constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | (constants.O_NOFOLLOW ?? 0),
      0o600,
    );
    try {
      const before = await handle.stat();
      await this.assertOpenedPath(destination, before);
      let offset = 0;
      while (offset < bytes.length) {
        const written = await handle.write(bytes, offset, bytes.length - offset, offset);
        if (written.bytesWritten === 0) throw new ArtifactBridgeStagingError();
        offset += written.bytesWritten;
      }
      await handle.sync();
      const stat = await handle.stat();
      await this.assertOpenedPath(destination, stat);
      if (!stat.isFile() || stat.size !== bytes.length) {
        throw new ArtifactBridgeStagingError();
      }
    } finally {
      await handle.close();
    }
  }

  async cleanupOperationDirectory(operationDirectory: string): Promise<void> {
    await this.assertRoot();
    const canonical = path.resolve(operationDirectory);
    if (
      path.dirname(canonical) !== this.canonicalRoot ||
      !path.basename(canonical).startsWith('op-')
    ) {
      throw new ArtifactBridgeStagingError();
    }
    const expectedIdentity = this.operationIdentities.get(canonical);
    if (!expectedIdentity) {
      throw new ArtifactBridgeStagingError();
    }
    try {
      const operationStat = await lstat(canonical);
      if (!safePrivateDirectory(operationStat) || !sameIdentity(operationStat, expectedIdentity)) {
        throw new ArtifactBridgeStagingError();
      }
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') {
        this.operationIdentities.delete(canonical);
        return;
      }
      throw error;
    }
    await rm(canonical, { recursive: true, force: true });
    this.operationIdentities.delete(canonical);
  }

  private async resolveExistingFile(relative: string): Promise<string> {
    const resolved = await this.resolve(relative);
    const canonical = await realpath(resolved);
    this.assertInside(canonical);
    const stat = await lstat(resolved);
    if (!stat.isFile() || stat.isSymbolicLink()) {
      throw new ArtifactBridgeStagingError();
    }
    return resolved;
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

  private async assertRoot(): Promise<void> {
    const rootStat = await lstat(this.canonicalRoot);
    if (!safePrivateDirectory(rootStat) || !sameIdentity(rootStat, this.rootIdentity)) {
      throw new ArtifactBridgeStagingError();
    }
  }

  private async assertOperationDirectory(operationDirectory: string): Promise<void> {
    const canonical = path.resolve(operationDirectory);
    const expected = this.operationIdentities.get(canonical);
    if (
      !expected ||
      path.dirname(canonical) !== this.canonicalRoot ||
      !path.basename(canonical).startsWith('op-')
    ) {
      throw new ArtifactBridgeStagingError();
    }
    const stat = await lstat(canonical);
    if (!safePrivateDirectory(stat) || !sameIdentity(stat, expected)) {
      throw new ArtifactBridgeStagingError();
    }
  }

  private async assertOpenedPath(openedPath: string, openedStat: Stats): Promise<void> {
    const canonical = await realpath(openedPath);
    this.assertInside(canonical);
    const namedStat = await lstat(openedPath);
    if (
      namedStat.isSymbolicLink() ||
      !namedStat.isFile() ||
      !sameIdentity(namedStat, identity(openedStat))
    ) {
      throw new ArtifactBridgeStagingError();
    }
  }
}

interface FileIdentity {
  readonly dev: number;
  readonly ino: number;
}

function identity(stat: { readonly dev: number; readonly ino: number }): FileIdentity {
  return { dev: stat.dev, ino: stat.ino };
}

function sameIdentity(
  stat: { readonly dev: number; readonly ino: number },
  expected: FileIdentity,
): boolean {
  return stat.ino === expected.ino && (process.platform === 'win32' || stat.dev === expected.dev);
}

function safePrivateDirectory(stat: Stats): boolean {
  if (!stat.isDirectory() || stat.isSymbolicLink()) return false;
  if (process.platform !== 'win32') {
    if ((stat.mode & 0o077) !== 0) return false;
    if (typeof process.getuid === 'function' && stat.uid !== process.getuid()) return false;
  }
  return true;
}
