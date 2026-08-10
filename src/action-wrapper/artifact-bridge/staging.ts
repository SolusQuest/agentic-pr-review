import { constants, type Stats } from 'node:fs';
import { lstat, open, realpath, type FileHandle } from 'node:fs/promises';
import path from 'node:path';

import { relativePath } from './contracts.js';
import { ARTIFACT_BRIDGE_LIMITS, ARTIFACT_ENVELOPE_ENTRY } from './limits.js';
import type { ArtifactBridgeOperationBudget } from './operation-budget.js';

export class ArtifactBridgeStagingError extends Error {
  constructor() {
    super('artifact_bridge_staging_invalid');
    this.name = 'ArtifactBridgeStagingError';
  }
}

export class ArtifactBridgeStaging {
  private constructor(
    private readonly canonicalRoot: string,
    private readonly rootIdentity: FileIdentity,
  ) {}

  static async create(configuredRoot: string): Promise<ArtifactBridgeStaging> {
    const configured = path.resolve(configuredRoot);
    try {
      const configuredStat = await lstat(configured);
      if (configuredStat.isSymbolicLink()) throw new ArtifactBridgeStagingError();
    } catch {
      throw new ArtifactBridgeStagingError();
    }
    const canonicalRoot = await realpath(configured);
    const rootStat = await lstat(canonicalRoot);
    if (path.relative(configured, canonicalRoot) !== '' || !safePrivateDirectory(rootStat)) {
      throw new ArtifactBridgeStagingError();
    }
    return new ArtifactBridgeStaging(canonicalRoot, identity(rootStat));
  }

  async readSource(relative: string, budget?: ArtifactBridgeOperationBudget): Promise<Buffer> {
    budget?.throwIfExpired();
    await this.assertRoot();
    const resolved = await this.resolveExistingFile(relative);
    const handle = await openStagedFile(resolved, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0));
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
      budget?.throwIfExpired();
      const bytes = await handle.readFile(budget ? { signal: budget.signal } : undefined);
      budget?.throwIfExpired();
      const after = await handle.stat();
      await this.assertOpenedPath(resolved, after);
      if (
        bytes.length !== before.size ||
        after.size !== before.size ||
        after.mtimeMs !== before.mtimeMs ||
        !sameIdentity(after, identity(before))
      ) {
        throw new ArtifactBridgeStagingError();
      }
      return bytes;
    } catch (error) {
      if (budget?.signal.aborted) budget.throwIfExpired();
      if (error instanceof ArtifactBridgeStagingError) throw error;
      throw new ArtifactBridgeStagingError();
    } finally {
      await handle.close();
    }
  }

  async writeUploadEnvelope(
    sourceRelative: string,
    bytes: Buffer,
    budget: ArtifactBridgeOperationBudget,
  ): Promise<{ readonly envelopePath: string; readonly operationDirectory: string }> {
    if (bytes.length < 1 || bytes.length > ARTIFACT_BRIDGE_LIMITS.maximumStagingFileBytes) {
      throw new ArtifactBridgeStagingError();
    }
    budget.throwIfExpired();
    await this.assertRoot();
    const source = await this.resolveExistingFile(sourceRelative);
    const operationDirectory = path.dirname(source);
    const envelopePath = path.join(operationDirectory, ARTIFACT_ENVELOPE_ENTRY);
    await this.writeExistingEmptyFile(envelopePath, bytes, budget);
    return { envelopePath, operationDirectory };
  }

  async writeDestination(
    relative: string,
    bytes: Buffer,
    budget?: ArtifactBridgeOperationBudget,
  ): Promise<void> {
    if (bytes.length < 1 || bytes.length > ARTIFACT_BRIDGE_LIMITS.maximumEncryptedObjectBytes) {
      throw new ArtifactBridgeStagingError();
    }
    budget?.throwIfExpired();
    await this.assertRoot();
    const destination = await this.resolveExistingFile(relative);
    await this.writeExistingEmptyFile(destination, bytes, budget);
  }

  private async writeExistingEmptyFile(
    destination: string,
    bytes: Buffer,
    budget?: ArtifactBridgeOperationBudget,
  ): Promise<void> {
    const handle = await openStagedFile(
      destination,
      constants.O_WRONLY | (constants.O_NOFOLLOW ?? 0),
    );
    try {
      const before = await handle.stat();
      await this.assertOpenedPath(destination, before);
      if (!before.isFile() || before.size !== 0) throw new ArtifactBridgeStagingError();
      budget?.throwIfExpired();
      await handle.writeFile(bytes, budget ? { signal: budget.signal } : undefined);
      budget?.throwIfExpired();
      const after = await handle.stat();
      await this.assertOpenedPath(destination, after);
      if (
        !after.isFile() ||
        after.size !== bytes.length ||
        !sameIdentity(after, identity(before))
      ) {
        throw new ArtifactBridgeStagingError();
      }
    } catch (error) {
      if (budget?.signal.aborted) budget.throwIfExpired();
      if (error instanceof ArtifactBridgeStagingError) throw error;
      throw new ArtifactBridgeStagingError();
    } finally {
      await handle.close();
    }
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

  private async resolve(relative: string): Promise<string> {
    if (!relativePath(relative)) throw new ArtifactBridgeStagingError();
    const segments = relative.split('/');
    let current = this.canonicalRoot;
    for (const segment of segments.slice(0, -1)) {
      current = path.join(current, segment);
      const stat = await lstat(current);
      if (!safePrivateDirectory(stat)) {
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

async function openStagedFile(filePath: string, flags: number): Promise<FileHandle> {
  try {
    return await open(filePath, flags);
  } catch {
    throw new ArtifactBridgeStagingError();
  }
}
