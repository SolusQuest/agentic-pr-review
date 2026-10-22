import {
  chmod,
  link,
  mkdir,
  mkdtemp,
  readFile,
  realpath,
  rm,
  symlink,
  writeFile,
} from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { UploadStaging } from './upload-staging.js';

const roots: string[] = [];
afterEach(async () => {
  for (const root of roots.splice(0)) {
    await chmod(path.join(root, 'source'), 0o700).catch(() => {});
    await rm(root, { recursive: true, force: true });
  }
});
async function fixture() {
  const root = await realpath(await mkdtemp(path.join(tmpdir(), 'apr-upload-copy-')));
  roots.push(root);
  await mkdir(path.join(root, 'source'), { mode: 0o700 });
  const bytes = Buffer.from('complete opaque carrier');
  const file = path.join(root, 'source', 'carrier');
  await writeFile(file, bytes, { mode: 0o400 });
  const staging = await UploadStaging.create(root);
  return { root, file, bytes, staging, digest: createHash('sha256').update(bytes).digest('hex') };
}

describe.skipIf(process.platform !== 'linux')('held upload copy', () => {
  it.each([0o500, 0o700])(
    'accepts private C# source directory mode %i and copies independently',
    async (mode) => {
      const f = await fixture();
      await chmod(path.join(f.root, 'source'), mode);
      try {
        const copy = await f.staging.copy(
          'source/carrier',
          f.bytes.length,
          f.digest,
          new AbortController().signal,
        );
        expect(
          await readFile(path.join(copy.operationDirectory, 'artifact-envelope.json')),
        ).toEqual(f.bytes);
        await copy.cleanup();
        expect(await readFile(f.file)).toEqual(f.bytes);
      } finally {
        await f.staging.dispose();
      }
    },
  );
  it.each(['directory-symlink', 'file-symlink', 'hardlink', 'public-directory', 'wrong-hash'])(
    'rejects %s before SDK ownership',
    async (fault) => {
      const f = await fixture();
      let relative = 'source/carrier';
      if (fault === 'directory-symlink') {
        await symlink(path.join(f.root, 'source'), path.join(f.root, 'alias'));
        relative = 'alias/carrier';
      }
      if (fault === 'file-symlink') {
        await symlink(f.file, path.join(f.root, 'source', 'alias'));
        relative = 'source/alias';
      }
      if (fault === 'hardlink') await link(f.file, path.join(f.root, 'source', 'alias'));
      if (fault === 'public-directory') await chmod(path.join(f.root, 'source'), 0o750);
      try {
        await expect(
          f.staging.copy(
            relative,
            f.bytes.length,
            fault === 'wrong-hash' ? '0'.repeat(64) : f.digest,
            new AbortController().signal,
          ),
        ).rejects.toThrow();
      } finally {
        await f.staging.dispose();
      }
    },
  );
});
