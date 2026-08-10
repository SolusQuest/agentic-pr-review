import { mkdir, mkdtemp, readFile, rename, rm, symlink, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';

import { ArtifactBridgeStaging, ArtifactBridgeStagingError } from './staging.js';
import { ARTIFACT_BRIDGE_LIMITS } from './limits.js';

const roots: string[] = [];

afterEach(async () => {
  await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })));
});

describe('artifact bridge staging root', () => {
  it('reads a stable regular source and creates a destination exclusively', async () => {
    const root = await temporaryRoot();
    const parent = path.join(root, 'csharp', 'op');
    await mkdir(parent, { recursive: true });
    await writeFile(path.join(parent, 'source.bin'), Buffer.from([1, 2, 3]));
    const staging = await ArtifactBridgeStaging.create(root);
    await expect(staging.readSource('csharp/op/source.bin')).resolves.toEqual(
      Buffer.from([1, 2, 3]),
    );
    await staging.writeDestination('csharp/op/destination.bin', Buffer.from([4, 5]));
    await expect(readFile(path.join(parent, 'destination.bin'))).resolves.toEqual(
      Buffer.from([4, 5]),
    );
    await expect(
      staging.writeDestination('csharp/op/destination.bin', Buffer.from([6])),
    ).rejects.toBeDefined();
  });

  it('rejects a source above 2 MiB', async () => {
    const root = await temporaryRoot();
    await mkdir(path.join(root, 'csharp', 'op'), { recursive: true });
    await writeFile(
      path.join(root, 'csharp', 'op', 'source.bin'),
      Buffer.alloc(ARTIFACT_BRIDGE_LIMITS.maximumEncryptedObjectBytes + 1),
    );
    const staging = await ArtifactBridgeStaging.create(root);
    await expect(staging.readSource('csharp/op/source.bin')).rejects.toBeInstanceOf(
      ArtifactBridgeStagingError,
    );
  });

  it('rejects a symlinked parent where the platform permits links', async () => {
    const root = await temporaryRoot();
    const outside = await temporaryRoot();
    await writeFile(path.join(outside, 'source.bin'), Buffer.from([1]));
    try {
      await symlink(outside, path.join(root, 'linked'), 'junction');
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'EPERM') return;
      throw error;
    }
    const staging = await ArtifactBridgeStaging.create(root);
    await expect(staging.readSource('linked/source.bin')).rejects.toBeInstanceOf(
      ArtifactBridgeStagingError,
    );
  });

  it('rejects a configured root symlink and a replaced root identity', async () => {
    const target = await temporaryRoot();
    const parent = await temporaryRoot();
    const linkedRoot = path.join(parent, 'linked-root');
    try {
      await symlink(target, linkedRoot, 'junction');
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'EPERM') return;
      throw error;
    }
    await expect(ArtifactBridgeStaging.create(linkedRoot)).rejects.toBeInstanceOf(
      ArtifactBridgeStagingError,
    );

    const root = await temporaryRoot();
    const staging = await ArtifactBridgeStaging.create(root);
    const original = `${root}-original`;
    roots.push(original);
    await rename(root, original);
    await mkdir(root, { mode: 0o700 });
    await expect(staging.createOperationDirectory()).rejects.toBeInstanceOf(
      ArtifactBridgeStagingError,
    );
  });

  it('does not follow a substituted final destination', async () => {
    const root = await temporaryRoot();
    const outside = await temporaryRoot();
    const parent = path.join(root, 'csharp', 'op');
    await mkdir(parent, { recursive: true });
    const marker = path.join(outside, 'keep');
    await writeFile(marker, 'keep');
    try {
      await symlink(marker, path.join(parent, 'destination.bin'), 'file');
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'EPERM') return;
      throw error;
    }
    const staging = await ArtifactBridgeStaging.create(root);
    await expect(
      staging.writeDestination('csharp/op/destination.bin', Buffer.from('changed')),
    ).rejects.toBeInstanceOf(ArtifactBridgeStagingError);
    await expect(readFile(marker, 'utf8')).resolves.toBe('keep');
  });

  it('never cleans a directory outside its generated operation root', async () => {
    const root = await temporaryRoot();
    const outside = await temporaryRoot();
    const marker = path.join(outside, 'keep');
    await writeFile(marker, Buffer.from('keep'));
    const staging = await ArtifactBridgeStaging.create(root);
    await expect(staging.cleanupOperationDirectory(outside)).rejects.toBeInstanceOf(
      ArtifactBridgeStagingError,
    );
    await expect(readFile(marker, 'utf8')).resolves.toBe('keep');

    const operation = await staging.createOperationDirectory();
    await staging.cleanupOperationDirectory(operation);
    await expect(readFile(marker, 'utf8')).resolves.toBe('keep');

    const replaced = await staging.createOperationDirectory();
    await rm(replaced, { recursive: true });
    try {
      await symlink(outside, replaced, 'junction');
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'EPERM') return;
      throw error;
    }
    await expect(staging.cleanupOperationDirectory(replaced)).rejects.toBeInstanceOf(
      ArtifactBridgeStagingError,
    );
    await expect(readFile(marker, 'utf8')).resolves.toBe('keep');
  });
});

async function temporaryRoot(): Promise<string> {
  const root = await mkdtemp(path.join(os.tmpdir(), 'apr-staging-test-'));
  roots.push(root);
  return root;
}
