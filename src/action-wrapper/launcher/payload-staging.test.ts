import {
  access,
  chmod,
  mkdir,
  mkdtemp,
  readdir,
  rm,
  stat,
  symlink,
  writeFile,
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  acquireInPayloadStaging,
  acquireRepositoryTestOriginalRuntimeArchiveLease,
  type RepositoryTestOriginalRuntimeArchive,
  type RepositoryTestOriginalArchiveContext,
  acquireRepositoryTestOriginalRuntimeFixtureLease,
  admitRepositoryTestOriginalRuntimeFixture,
  REPOSITORY_TEST_FRIEND_STEM,
  type RepositoryTestOriginalRuntimeContext,
  type RepositoryTestOriginalRuntimeFixture,
} from './payload-staging.js';
import { sha256 } from '../../../scripts/release/build-payload.format.mjs';

const roots: string[] = [];
afterEach(async () => {
  for (const root of fixtureRoots.splice(0)) await chmod(root, 0o700).catch(() => {});
  await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })));
  vi.unstubAllEnvs();
});

const previewContext: RepositoryTestOriginalRuntimeContext = Object.freeze({
  sourceCommit: '1'.repeat(40),
  sourceTree: '2'.repeat(40),
  workflowCommit: '3'.repeat(40),
  runId: 123,
  attempt: 1,
  archiveSha256: 'a'.repeat(64),
  receiptSha256: 'b'.repeat(64),
  assemblySha256: 'c'.repeat(64),
});
const fixtureRoots: string[] = [];

async function friendFixture() {
  const context = await fixture();
  await chmod(context.runnerTemp, 0o700);
  const root = path.join(context.runnerTemp, 'friend');
  await mkdir(root, { mode: 0o700 });
  fixtureRoots.push(root);
  const library = REPOSITORY_TEST_FRIEND_STEM + '/1.0.0';
  // Synthetic ownership/configuration bytes only; not an executable or qualified runtime.
  const files = [
    { path: REPOSITORY_TEST_FRIEND_STEM + '.dll', bytes: Buffer.from('not-a-PE-loader-proof') },
    {
      path: REPOSITORY_TEST_FRIEND_STEM + '.runtimeconfig.json',
      bytes: Buffer.from(
        JSON.stringify({
          runtimeOptions: {
            tfm: 'net10.0',
            framework: { name: 'Microsoft.NETCore.App', version: '10.0.0' },
            configProperties: {
              'System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization': false,
              'System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault': true,
            },
          },
        }),
      ),
    },
    {
      path: REPOSITORY_TEST_FRIEND_STEM + '.deps.json',
      bytes: Buffer.from(
        JSON.stringify({
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
        }),
      ),
    },
  ];
  for (const file of files)
    await writeFile(path.join(root, file.path), file.bytes, { mode: 0o644 });
  return {
    root,
    stagingParent: context.runnerTemp,
    workspace: context.workspace,
    context: previewContext,
    signal: new AbortController().signal,
    members: files.map(({ path, bytes }) => ({
      path,
      mode: 0o644,
      size: bytes.length,
      sha256: sha256(bytes),
    })),
  };
}

describe('separate repository-test friend ownership', () => {
  it('does not let JSON or friend tokens impersonate an unpublished original archive', async () => {
    const context: RepositoryTestOriginalArchiveContext = {
      ...previewContext,
      repository: 'SolusQuest/agentic-pr-review-sandbox',
      job: 'review',
      sourceRole: 'pr',
      workflowRef:
        'SolusQuest/agentic-pr-review-sandbox/.github/workflows/agentic-pr-review.yml@refs/heads/main',
    };
    for (const token of [{}, JSON.parse('{}')])
      await expect(
        acquireRepositoryTestOriginalRuntimeArchiveLease(
          token as RepositoryTestOriginalRuntimeArchive,
          context,
          new AbortController().signal,
        ),
      ).rejects.toThrow('wrapper_test_original_archive_invalid');
  });

  it('rejects invented and JSON tokens without any filesystem or child-process authority', async () => {
    for (const token of [{}, JSON.parse('{}')]) {
      await expect(
        acquireRepositoryTestOriginalRuntimeFixtureLease(
          token as RepositoryTestOriginalRuntimeFixture,
          previewContext,
          new AbortController().signal,
        ),
      ).rejects.toThrow('wrapper_test_fixture_invalid');
    }
  });
});

describe.skipIf(process.platform !== 'linux' || process.arch !== 'x64')(
  'fixed friend directory lease',
  () => {
    it('binds the separate output to context and defers disposal until every lease releases', async () => {
      const input = await friendFixture();
      const owned = await admitRepositoryTestOriginalRuntimeFixture(input);
      for (const changed of [
        { runId: 124 },
        { archiveSha256: 'd'.repeat(64) },
        { workflowCommit: '4'.repeat(40) },
      ]) {
        await expect(
          acquireRepositoryTestOriginalRuntimeFixtureLease(
            owned.fixture,
            { ...previewContext, ...changed },
            input.signal,
          ),
        ).rejects.toThrow('wrapper_test_fixture_invalid');
      }
      const lease = await acquireRepositoryTestOriginalRuntimeFixtureLease(
        owned.fixture,
        previewContext,
        input.signal,
      );
      expect(Number.isSafeInteger(lease.directoryFd)).toBe(true);
      expect((await stat(input.root)).mode & 0o777).toBe(0o500);
      await expect(owned.dispose()).rejects.toThrow('wrapper_managed_tree_in_use');
      lease.release();
      lease.release();
      await owned.dispose();
      await owned.dispose();
      await expect(access(input.root)).rejects.toThrow();
      await expect(
        acquireRepositoryTestOriginalRuntimeFixtureLease(
          owned.fixture,
          previewContext,
          input.signal,
        ),
      ).rejects.toThrow('wrapper_test_fixture_invalid');
    });

    it.each(['extra-file', 'symlink', 'wrong-hash', 'deps-probing', 'runtime-probing'])(
      'rejects unreviewed fixture inventory/configuration: %s',
      async (kind) => {
        const input = await friendFixture();
        if (kind === 'extra-file') await writeFile(path.join(input.root, 'other.dll'), 'extra');
        if (kind === 'symlink') {
          const file = path.join(input.root, input.members[0].path);
          await rm(file);
          await symlink('/etc/passwd', file);
        }
        if (kind === 'wrong-hash') input.members[0].sha256 = 'e'.repeat(64);
        if (kind.endsWith('probing')) {
          const suffix = kind === 'deps-probing' ? '.deps.json' : '.runtimeconfig.json';
          const member = input.members.find((m) => m.path.endsWith(suffix))!;
          const bytes = Buffer.from('{"additionalProbingPaths":["/tmp/foreign"]}');
          await writeFile(path.join(input.root, member.path), bytes);
          member.size = bytes.length;
          member.sha256 = sha256(bytes);
        }
        await expect(admitRepositoryTestOriginalRuntimeFixture(input)).rejects.toThrow();
        await access(input.root); // Failed admission never deletes caller storage.
      },
    );

    it('rechecks held file identity/hash and refuses foreign cleanup after a changed file', async () => {
      const input = await friendFixture();
      const owned = await admitRepositoryTestOriginalRuntimeFixture(input);
      const file = path.join(input.root, input.members[0].path);
      await chmod(file, 0o600);
      await writeFile(file, 'changed');
      await expect(
        acquireRepositoryTestOriginalRuntimeFixtureLease(
          owned.fixture,
          previewContext,
          input.signal,
        ),
      ).rejects.toThrow();
      await expect(owned.dispose()).rejects.toThrow();
      await access(file);
    });
  },
);
async function fixture() {
  const root = await mkdtemp(path.join(tmpdir(), 'apr-owned-staging-'));
  roots.push(root);
  const runnerTemp = path.join(root, 'runner-temp'),
    workspace = path.join(root, 'workspace');
  await Promise.all([mkdir(runnerTemp), mkdir(workspace)]);
  return { root, runnerTemp, workspace };
}

describe.skipIf(process.platform !== 'linux')('launcher-owned payload parent', () => {
  it('owns a unique 0700 child until payload disposal, then disposes exactly once in order', async () => {
    const context = await fixture();
    const parents: string[] = [];
    const acquire = async (parent: string) => {
      expect(path.dirname(parent)).toBe(context.runnerTemp);
      expect((await stat(parent)).mode & 0o777).toBe(0o700);
      parents.push(parent);
      const payloadRoot = path.join(parent, 'payload');
      await mkdir(payloadRoot);
      const dispose = vi.fn(async () => {
        await access(parent);
        await rm(payloadRoot, { recursive: true });
      });
      return { payloadRoot, dispose };
    };
    const first = await acquireInPayloadStaging({ ...context, acquire });
    const second = await acquireInPayloadStaging({ ...context, acquire });
    expect(parents[0]).not.toBe(parents[1]);
    await Promise.all([first.dispose(), first.dispose(), second.dispose()]);
    await first.dispose();
    expect(await readdir(context.runnerTemp)).toEqual([]);
  });
  it('cleans the parent on acquisition failure while preserving the failure', async () => {
    const context = await fixture();
    const failure = new Error('acquisition-failed');
    await expect(
      acquireInPayloadStaging({
        ...context,
        acquire: async (parent) => {
          await writeFile(path.join(parent, 'partial'), 'partial');
          throw failure;
        },
      }),
    ).rejects.toBe(failure);
    expect(await readdir(context.runnerTemp)).toEqual([]);
  });
  it('cleans after failed payload disposal and preserves the same failure on repeated disposal', async () => {
    const context = await fixture();
    const failure = new Error('disposal-failed');
    const dispose = vi.fn(async () => {
      throw failure;
    });
    const payload = await acquireInPayloadStaging({
      ...context,
      acquire: async (parent) => {
        await mkdir(path.join(parent, 'payload'));
        return { dispose };
      },
    });
    await expect(payload.dispose()).rejects.toBe(failure);
    await expect(payload.dispose()).rejects.toBe(failure);
    expect(dispose).toHaveBeenCalledTimes(1);
    expect(await readdir(context.runnerTemp)).toEqual([]);
  });
  it.each(['relative', 'missing', 'file', 'workspace', 'nested', 'symlink-workspace'])(
    'rejects unsuitable runner storage before acquisition: %s',
    async (kind) => {
      const context = await fixture();
      let runnerTemp = context.runnerTemp;
      if (kind === 'relative') runnerTemp = 'relative';
      if (kind === 'missing') runnerTemp = path.join(context.root, 'missing');
      if (kind === 'file') {
        runnerTemp = path.join(context.root, 'file');
        await writeFile(runnerTemp, 'file');
      }
      if (kind === 'workspace') runnerTemp = context.workspace;
      if (kind === 'nested') {
        runnerTemp = path.join(context.workspace, 'nested');
        await mkdir(runnerTemp);
      }
      if (kind === 'symlink-workspace') {
        runnerTemp = path.join(context.root, 'alias');
        await symlink(context.workspace, runnerTemp);
      }
      const acquire = vi.fn();
      await expect(acquireInPayloadStaging({ ...context, runnerTemp, acquire })).rejects.toThrow();
      expect(acquire).not.toHaveBeenCalled();
      expect(await readdir(context.runnerTemp)).toEqual([]);
    },
  );
});
