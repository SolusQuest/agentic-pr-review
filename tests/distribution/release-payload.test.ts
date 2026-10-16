import { execFileSync } from 'node:child_process';
import { createServer, request as httpRequest, type ServerResponse } from 'node:http';
import {
  chmod,
  mkdir,
  lstat,
  symlink,
  link,
  copyFile,
  mkdtemp,
  readFile,
  readdir,
  rename,
  rm,
  writeFile,
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { crc32, deflateRawSync, gunzipSync } from 'node:zlib';
import { afterAll, beforeAll, describe, expect, test, vi } from 'vitest';
import fsPromises from 'node:fs/promises';
import { syncBuiltinESMExports } from 'node:module';
import {
  sha256,
  encodeManagedPackage,
  inspectManagedPackage,
  inspectPackage,
  MANAGED_TARGET,
  type ManagedReceipt,
} from '../../scripts/release/build-payload.format.mjs';
import {
  createRepositoryTestPayloadResolver,
  createRepositoryTestManagedPayloadResolver,
  resolveManagedReleasePayload,
  PAYLOAD_USER_AGENT,
  resolveReleasePayload,
  type ReleasePayloadRequest,
  type VerifiedReleasePayload,
} from '../../src/action-wrapper/launcher/release-payload.js';
import {
  HostProcessTerminationUnconfirmedError,
  runHostProcess,
  type HostProcessRunner,
} from '../../src/action-wrapper/launcher/host-process.js';
import { ACTION_SOURCE, releaseFixture } from './release-payload-fixture.js';

import {
  acquireInPayloadStaging,
  acquireVerifiedManagedTreeLease,
  ManagedTreeInUseError,
  type VerifiedManagedTree,
} from '../../src/action-wrapper/launcher/payload-staging.js';

type Fixture = ReturnType<typeof releaseFixture>;
type Resolver = (request: ReleasePayloadRequest) => Promise<VerifiedReleasePayload>;
const supported = process.platform === 'linux' && process.arch === 'x64';

// The actual consumer for D2: no runner can be reached until resolution succeeds.
// #344 owns wiring this lifetime into the generated production Action.
async function launcher(
  request: ReleasePayloadRequest,
  acquire: Resolver,
  observation: { launches: number; payload?: VerifiedReleasePayload },
  beforeSpawn?: (payload: VerifiedReleasePayload) => Promise<void>,
  runner: HostProcessRunner = runHostProcess,
  launchBytes = Buffer.from('{}'),
) {
  const payload = await acquire(request);
  observation.payload = payload;
  try {
    await beforeSpawn?.(payload);
    request.signal.throwIfAborted();
    observation.launches += 1;
    return await runner({
      executableHandle: payload.executableHandle,
      launchBytes,
      tempRoot: payload.root,
      signal: request.signal,
      cancellationKillGraceMs: 100,
      postKillCloseGraceMs: 1000,
    });
  } finally {
    await payload.dispose();
  }
}

interface Behavior {
  release?: unknown;
  assets?: unknown;
  metadataRedirect?: boolean;
  pagination?: boolean;
  metadataOversize?: boolean;
  redirect?: string;
  redirectForever?: boolean;
  body?: Buffer;
  length?: string;
  encoding?: string;
  chunked?: boolean;
  interrupt?: boolean;
  stall?: boolean;
  onBody?: () => void;
}

async function fakeRelease(
  fixture: {
    archive: Buffer;
    receipt: { archiveName: string };
    map: { repository: string; tag: string };
  },
  behavior: Behavior = {},
) {
  const downloadUrl = `https://github.com/${fixture.map.repository}/releases/download/${fixture.map.tag}/${fixture.receipt.archiveName}`;
  const asset = {
    id: 11,
    name: fixture.receipt.archiveName,
    state: 'uploaded',
    size: fixture.archive.length,
    browser_download_url: downloadUrl,
  };
  const seen: { url: string; headers: Record<string, string | string[] | undefined> }[] = [];
  const json = (response: ServerResponse, value: unknown) => {
    response.setHeader('Content-Type', 'application/json');
    response.end(JSON.stringify(value));
  };
  const server = createServer((request, response) => {
    if (request.headers['user-agent'] !== PAYLOAD_USER_AGENT) {
      response.writeHead(403).end();
      return;
    }
    if (request.url?.includes('/tags/')) {
      if (behavior.metadataRedirect) {
        response.writeHead(302, { Location: 'https://attacker.invalid/release' }).end();
      } else if (behavior.metadataOversize) {
        response.setHeader('Transfer-Encoding', 'chunked');
        response.end(' '.repeat(256 * 1024 + 1));
      } else json(response, behavior.release ?? { id: 7, tag_name: fixture.map.tag, draft: false });
    } else if (request.url?.endsWith('/assets?per_page=100')) {
      if (behavior.pagination)
        response.setHeader('Link', '<https://api.github.com/next>; rel="next"');
      json(response, behavior.assets ?? [asset]);
    } else if (behavior.redirect && (request.url !== '/blob' || behavior.redirectForever)) {
      response.writeHead(302, { Location: behavior.redirect }).end();
    } else {
      behavior.onBody?.();
      if (behavior.chunked) response.setHeader('Transfer-Encoding', 'chunked');
      if (behavior.length !== undefined) response.setHeader('Content-Length', behavior.length);
      if (behavior.encoding !== undefined)
        response.setHeader('Content-Encoding', behavior.encoding);
      if (behavior.stall) {
        response.flushHeaders();
        return;
      }
      if (behavior.interrupt) {
        response.write(fixture.archive.subarray(0, 20));
        setImmediate(() => response.destroy());
      } else response.end(behavior.body ?? fixture.archive);
    }
  });
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error('test_server_unavailable');
  const transport: Parameters<typeof createRepositoryTestPayloadResolver>[0]['transport'] = (
    url,
    options,
    callback,
  ) => {
    seen.push({ url: url.href, headers: { ...(options.headers as Record<string, string>) } });
    return httpRequest(
      new URL(url.pathname + url.search, `http://127.0.0.1:${address.port}`),
      options,
      callback,
    );
  };
  return {
    asset,
    seen,
    transport,
    async close() {
      server.closeAllConnections();
      await new Promise<void>((resolve, reject) =>
        server.close((error) => (error ? reject(error) : resolve())),
      );
    },
  };
}

async function context(
  fixture: Fixture,
  behavior: Behavior = {},
  signal = new AbortController().signal,
) {
  const parent = await mkdtemp(join(tmpdir(), 'apr-d2-test-'));
  await writeFile(join(parent, 'sentinel'), 'trusted sentinel');
  const api = await fakeRelease(fixture, behavior);
  const request = {
    mapBytes: fixture.mapBytes,
    actionSourceSha: ACTION_SOURCE,
    stagingParent: parent,
    signal,
  };
  return {
    parent,
    api,
    request,
    acquire: createRepositoryTestPayloadResolver({ transport: api.transport, timeoutMs: 2000 }),
    async checkClean() {
      expect(await readdir(parent)).toEqual(['sentinel']);
      expect(await readFile(join(parent, 'sentinel'), 'utf8')).toBe('trusted sentinel');
    },
    async close() {
      await api.close();
      await rm(parent, { recursive: true, force: true });
    },
  };
}

test.skipIf(supported)(
  'ordinary resolver refuses unsupported execution platforms before networking',
  async () => {
    await expect(
      resolveReleasePayload({
        mapBytes: releaseFixture().mapBytes,
        actionSourceSha: ACTION_SOURCE,
        stagingParent: tmpdir(),
        signal: new AbortController().signal,
      }),
    ).rejects.toThrow('wrapper_payload_platform_unsupported');
  },
);

describe.runIf(supported)('R7-D2 fake release API through actual test launcher', () => {
  let binary: Buffer;
  let compilerRoot: string;
  beforeAll(async () => {
    compilerRoot = await mkdtemp(join(tmpdir(), 'apr-d2-elf-'));
    const executable = join(compilerRoot, 'fixture');
    execFileSync('/usr/bin/cc', [
      '-O2',
      '-o',
      executable,
      fileURLToPath(new URL('./launcher-fixture.c', import.meta.url)),
    ]);
    binary = await readFile(executable);
  });
  afterAll(async () => {
    if (compilerRoot) await rm(compilerRoot, { recursive: true, force: true });
  });

  test.each([false, true])(
    'executes original downloaded bytes across pathname substitution, redirect=%s',
    async (redirect) => {
      const fixture = releaseFixture(binary);
      const ctx = await context(
        fixture,
        redirect ? { redirect: 'https://release-assets.githubusercontent.com/blob' } : {},
      );
      const observation = { launches: 0, payload: undefined as VerifiedReleasePayload | undefined };
      try {
        const result = await launcher(ctx.request, ctx.acquire, observation, async (payload) => {
          expect(payload.identity).toEqual(fixture.receipt.identity);
          expect(payload.actionSourceSha).toBe(ACTION_SOURCE);
          expect(payload.builder.workflowCommit).toBe('1'.repeat(40));
          expect(payload.payloadSha256).toBe(fixture.receipt.members[0].sha256);
          expect(payload.buildDiscriminator).toBe('r7-d0');
          const executable = join(payload.root, 'agentic-pr-review/agentic-pr-review');
          await rename(executable, executable + '.original');
          await copyFile('/usr/bin/false', executable);
        });
        expect(result.completionBytes.toString()).toBe('{"fixture":"original"}');
        expect(result.exitCode).toBe(0);
        expect(observation.launches).toBe(1);
        expect(observation.payload!.executableHandle.fd).toBe(-1);
        await observation.payload!.dispose();
        await ctx.checkClean();
        expect(ctx.api.seen).toHaveLength(redirect ? 4 : 3);
        for (const call of ctx.api.seen) {
          expect(call.headers['User-Agent']).toBe(PAYLOAD_USER_AGENT);
          expect(Object.keys(call.headers).sort()).toEqual(
            call.url.startsWith('https://api.github.com')
              ? ['Accept', 'User-Agent', 'X-GitHub-Api-Version']
              : ['Accept', 'User-Agent'],
          );
        }
      } finally {
        await ctx.close();
      }
    },
  );

  test('ignores ambient credential/path/endpoint inputs on every hop and closes child environment', async () => {
    const values = {
      GITHUB_TOKEN: 'd2-github-canary',
      PROVIDER_API_KEY: 'd2-provider-canary',
      STATE_KEY: 'd2-state-canary',
      INPUT_PAYLOAD_PATH: '/untrusted/payload',
      INPUT_PAYLOAD_ENDPOINT: 'https://attacker.invalid',
      AGENTIC_PR_REVIEW_PREPARED_ROOT: '/untrusted/root',
    };
    const saved = Object.fromEntries(Object.keys(values).map((key) => [key, process.env[key]]));
    Object.assign(process.env, values);
    const ctx = await context(releaseFixture(binary), {
      redirect: 'https://release-assets.githubusercontent.com/blob',
    });
    try {
      expect((await launcher(ctx.request, ctx.acquire, { launches: 0 })).exitCode).toBe(0);
      for (const value of Object.values(values))
        expect(JSON.stringify(ctx.api.seen)).not.toContain(value);
      await ctx.checkClean();
    } finally {
      for (const [key, value] of Object.entries(saved)) {
        if (value === undefined) delete process.env[key];
        else process.env[key] = value;
      }
      await ctx.close();
    }
  });

  test.each([
    'missing',
    'ambiguous',
    'wrong-size',
    'wrong-name',
    'wrong-state',
    'wrong-url',
    'wrong-tag',
    'draft',
    'pagination',
    'full-page',
    'api-redirect',
    'metadata-oversize',
  ])('rejects %s before any process call', async (kind) => {
    const fixture = releaseFixture(binary);
    const ctx = await context(fixture);
    const asset = ctx.api.asset;
    await ctx.close();
    const behavior: Behavior = {};
    if (kind === 'missing') behavior.assets = [];
    if (kind === 'ambiguous') behavior.assets = [asset, { ...asset, id: 12 }];
    if (kind === 'wrong-size') behavior.assets = [{ ...asset, size: asset.size + 1 }];
    if (kind === 'wrong-name') behavior.assets = [{ ...asset, name: 'other.tar.gz' }];
    if (kind === 'wrong-state') behavior.assets = [{ ...asset, state: 'new' }];
    if (kind === 'wrong-url')
      behavior.assets = [{ ...asset, browser_download_url: 'https://attacker.invalid/file' }];
    if (kind === 'wrong-tag') behavior.release = { id: 7, tag_name: 'latest', draft: false };
    if (kind === 'draft') behavior.release = { id: 7, tag_name: fixture.map.tag, draft: true };
    if (kind === 'pagination') behavior.pagination = true;
    if (kind === 'full-page')
      behavior.assets = Array.from({ length: 100 }, (_, index) => ({
        ...asset,
        id: index + 1,
        name: index ? `other-${index}` : asset.name,
      }));
    if (kind === 'api-redirect') behavior.metadataRedirect = true;
    if (kind === 'metadata-oversize') behavior.metadataOversize = true;
    const target = await context(fixture, behavior);
    const observation = { launches: 0 };
    try {
      await expect(launcher(target.request, target.acquire, observation)).rejects.toThrow();
      expect(observation.launches).toBe(0);
      await target.checkClean();
    } finally {
      await target.close();
    }
  });

  test.each([
    'https://attacker.invalid/blob',
    'http://release-assets.githubusercontent.com/blob',
    'https://user:secret@release-assets.githubusercontent.com/blob',
    'https://release-assets.githubusercontent.com:8443/blob',
    'https://release-assets.githubusercontent.com/blob#fragment',
    'https://release-assets.githubusercontent.com.attacker.invalid/blob',
    'https://github.com/SolusQuest/agentic-pr-review/releases/latest/download/file',
  ])('refuses redirect %s without contacting target', async (redirect) => {
    const ctx = await context(releaseFixture(binary), { redirect });
    const observation = { launches: 0 };
    try {
      await expect(launcher(ctx.request, ctx.acquire, observation)).rejects.toThrow();
      expect(ctx.api.seen).toHaveLength(3);
      expect(observation.launches).toBe(0);
      await ctx.checkClean();
    } finally {
      await ctx.close();
    }
  });

  test.each([
    'stream-plus-one',
    'declared-plus-one',
    'short',
    'interrupt',
    'wrong-hash',
    'encoded',
    'redirect-loop',
    'timeout',
  ])('bounds %s and never launches', async (kind) => {
    const fixture = releaseFixture(binary);
    const behavior: Behavior = {};
    if (kind === 'stream-plus-one') {
      behavior.chunked = true;
      behavior.body = Buffer.concat([fixture.archive, Buffer.from([0])]);
    }
    if (kind === 'declared-plus-one') behavior.length = String(fixture.archive.length + 1);
    if (kind === 'short') {
      behavior.chunked = true;
      behavior.body = fixture.archive.subarray(0, fixture.archive.length - 1);
    }
    if (kind === 'interrupt') behavior.interrupt = true;
    if (kind === 'wrong-hash') {
      behavior.body = Buffer.from(fixture.archive);
      behavior.body[20] ^= 1;
    }
    if (kind === 'encoded') behavior.encoding = 'gzip';
    if (kind === 'redirect-loop') {
      behavior.redirect = 'https://release-assets.githubusercontent.com/blob';
      behavior.redirectForever = true;
    }
    if (kind === 'timeout') behavior.stall = true;
    const ctx = await context(fixture, behavior);
    const acquire = createRepositoryTestPayloadResolver({
      transport: ctx.api.transport,
      timeoutMs: kind === 'timeout' ? 100 : 2000,
    });
    const observation = { launches: 0 };
    try {
      await expect(launcher(ctx.request, acquire, observation)).rejects.toThrow();
      expect(observation.launches).toBe(0);
      expect(ctx.api.seen.length).toBeLessThanOrEqual(6);
      await ctx.checkClean();
    } finally {
      await ctx.close();
    }
  });

  test.each(['traversal', 'link', 'duplicate', 'build', 'member'])(
    'rejects independently pinned malformed %s package before materialization/launch',
    async (kind) => {
      const fixture = releaseFixture(binary);
      const tar = gunzipSync(fixture.archive);
      if (kind === 'traversal') {
        tar.fill(0, 0, 100);
        tar.write('../sentinel');
      }
      if (kind === 'link') tar[156] = 50;
      if (kind === 'duplicate') {
        const second = 512 + Math.ceil(binary.length / 512) * 512;
        tar.fill(0, second, second + 100);
        tar.write('agentic-pr-review/agentic-pr-review', second);
      }
      if (kind === 'build') {
        const marker = Buffer.from(fixture.receipt.identity.buildId);
        const at = tar.indexOf(marker);
        expect(at).toBeGreaterThan(0);
        tar[at] = tar[at] === 97 ? 98 : 97;
      }
      if (kind === 'member') tar[512 + 64] ^= 1;
      const trailer = Buffer.alloc(8);
      trailer.writeUInt32LE(crc32(tar));
      trailer.writeUInt32LE(tar.length, 4);
      const archive = Buffer.concat([
        Buffer.from('1f8b0800000000000203', 'hex'),
        deflateRawSync(tar, { level: 9 }),
        trailer,
      ]);
      const map = {
        ...fixture.map,
        payload: {
          ...fixture.receipt,
          archiveSize: archive.length,
          archiveSha256: sha256(archive),
        },
      };
      const ctx = await context({
        ...fixture,
        archive,
        receipt: map.payload,
        map,
        mapBytes: Buffer.from(JSON.stringify(map)),
      });
      const observation = { launches: 0 };
      try {
        await expect(launcher(ctx.request, ctx.acquire, observation)).rejects.toThrow();
        expect(observation.launches).toBe(0);
        await ctx.checkClean();
      } finally {
        await ctx.close();
      }
    },
  );

  test.each(['before-acquire', 'during-download', 'before-spawn', 'in-child', 'unconfirmed'])(
    'cleans handles/staging for %s',
    async (phase) => {
      const controller = new AbortController();
      const ctx = await context(
        releaseFixture(binary),
        phase === 'during-download' ? { stall: true, onBody: () => controller.abort() } : {},
        controller.signal,
      );
      const observation = { launches: 0, payload: undefined as VerifiedReleasePayload | undefined };
      if (phase === 'before-acquire') controller.abort();
      let timer: ReturnType<typeof setTimeout> | undefined;
      try {
        const beforeSpawn = async () => {
          if (phase === 'before-spawn') controller.abort();
          if (phase === 'in-child') timer = setTimeout(() => controller.abort(), 100);
        };
        const runner: HostProcessRunner =
          phase === 'unconfirmed'
            ? async () => {
                throw new HostProcessTerminationUnconfirmedError();
              }
            : runHostProcess;
        await expect(
          launcher(
            ctx.request,
            ctx.acquire,
            observation,
            beforeSpawn,
            runner,
            Buffer.from('{"hold":true}'),
          ),
        ).rejects.toThrow();
        expect(observation.launches).toBe(['in-child', 'unconfirmed'].includes(phase) ? 1 : 0);
        if (observation.payload) expect(observation.payload.executableHandle.fd).toBe(-1);
        await ctx.checkClean();
      } finally {
        if (timer) clearTimeout(timer);
        await ctx.close();
      }
    },
  );

  test.each([false, true])(
    'local archive override retains every identity check, substituted=%s',
    async (substituted) => {
      const fixture = releaseFixture(binary);
      const ctx = await context(fixture);
      const archivePath = join(compilerRoot, substituted ? 'bad.tar.gz' : 'good.tar.gz');
      const bytes = Buffer.from(fixture.archive);
      if (substituted) bytes[20] ^= 1;
      await writeFile(archivePath, bytes);
      const acquire = createRepositoryTestPayloadResolver({
        transport: ctx.api.transport,
        localArchivePath: archivePath,
      });
      const observation = { launches: 0 };
      try {
        if (substituted)
          await expect(launcher(ctx.request, acquire, observation)).rejects.toThrow();
        else
          expect(
            (await launcher(ctx.request, acquire, observation)).completionBytes.toString(),
          ).toBe('{"fixture":"original"}');
        expect(observation.launches).toBe(substituted ? 0 : 1);
        expect(ctx.api.seen).toEqual([]);
        await ctx.checkClean();
      } finally {
        await ctx.close();
      }
    },
  );

  test('snapshots map and Action identity before asynchronous acquisition', async () => {
    const ctx = await context(releaseFixture(binary));
    const acquire = createRepositoryTestPayloadResolver({
      transport(url, options, callback) {
        ctx.request.mapBytes.fill(0);
        ctx.request.actionSourceSha = '9'.repeat(40);
        return ctx.api.transport(url, options, callback);
      },
    });
    const observation = { launches: 0, payload: undefined as VerifiedReleasePayload | undefined };
    try {
      expect((await launcher(ctx.request, acquire, observation)).exitCode).toBe(0);
      expect(observation.payload!.actionSourceSha).toBe(ACTION_SOURCE);
      await ctx.checkClean();
    } finally {
      await ctx.close();
    }
  });

  test.each(['map-platform', 'map-build', 'map-member', 'manifest-source', 'manifest-build'])(
    'local override cannot bypass %s admission',
    async (kind) => {
      const fixture = releaseFixture(binary);
      const map = JSON.parse(fixture.mapBytes.toString());
      if (kind === 'map-platform') map.payload.identity.platform = 'linux-arm64';
      if (kind === 'map-build') map.payload.identity.buildId = '9'.repeat(64);
      if (kind === 'map-member') map.payload.members[0].sha256 = '9'.repeat(64);
      if (kind === 'manifest-source') map.payload.identity.sourceCommit = '9'.repeat(40);
      if (kind === 'manifest-build') {
        map.payload.identity.buildId = '9'.repeat(64);
        map.payload.identity.informationalVersion =
          map.payload.identity.releaseVersion + '+build.' + map.payload.identity.buildId;
      }
      const ctx = await context(fixture);
      const localArchivePath = join(compilerRoot, 'identity-override.tar.gz');
      await writeFile(localArchivePath, fixture.archive);
      const acquire = createRepositoryTestPayloadResolver({
        transport: ctx.api.transport,
        localArchivePath,
      });
      const observation = { launches: 0 };
      try {
        await expect(
          launcher(
            { ...ctx.request, mapBytes: Buffer.from(JSON.stringify(map)) },
            acquire,
            observation,
          ),
        ).rejects.toThrow();
        expect(observation.launches).toBe(0);
        expect(ctx.api.seen).toEqual([]);
        await ctx.checkClean();
      } finally {
        await ctx.close();
      }
    },
  );

  test.skipIf(process.getuid?.() === 0)(
    'does not return success when owned-root removal fails',
    async () => {
      const ctx = await context(releaseFixture(binary));
      const observation = { launches: 0, payload: undefined as VerifiedReleasePayload | undefined };
      try {
        const runner: HostProcessRunner = async (request) => {
          const result = await runHostProcess(request);
          await chmod(ctx.parent, 0o000);
          return result;
        };
        await expect(
          launcher(ctx.request, ctx.acquire, observation, undefined, runner),
        ).rejects.toThrow();
        expect(observation.payload!.executableHandle.fd).toBe(-1);
      } finally {
        await chmod(ctx.parent, 0o700);
        await ctx.close();
      }
    },
  );
});

function managedFixture(packaged?: { archive: Buffer; receipt: ManagedReceipt }) {
  const native = releaseFixture();
  if (!packaged) {
    const notices = Buffer.from('Synthetic complete managed test fixture, not executable proof.\n');
    const nativeInputs = inspectPackage(native.archive, native.receipt).manifest.buildInputs;
    packaged = encodeManagedPackage(
      [
        { path: 'AgenticPrReview.Runtime.dll', bytes: Buffer.from('synthetic managed entry') },
        { path: 'Dependency.dll', bytes: Buffer.from('synthetic dependency') },
        { path: 'fr/Dependency.resources.dll', bytes: Buffer.from('synthetic satellite') },
        { path: 'libexample.so', bytes: Buffer.from('synthetic native asset') },
        { path: 'resources/empty.txt', bytes: Buffer.alloc(0) },
        {
          path: 'AgenticPrReview.Runtime.deps.json',
          bytes: Buffer.from(
            JSON.stringify({
              runtimeTarget: { name: '.NETCoreApp,Version=v10.0/linux-x64' },
              targets: {
                '.NETCoreApp,Version=v10.0/linux-x64': {
                  'Product/1.0.0': { runtime: { 'AgenticPrReview.Runtime.dll': {} } },
                  'Dependency/1.0.0': {
                    runtime: { 'lib/net10.0/Dependency.dll': {} },
                    native: { 'runtimes/linux-x64/native/libexample.so': {} },
                    resources: { 'lib/net10.0/fr/Dependency.resources.dll': { locale: 'fr' } },
                  },
                },
              },
            }),
          ),
        },
        {
          path: 'AgenticPrReview.Runtime.runtimeconfig.json',
          bytes: Buffer.from(
            JSON.stringify({
              runtimeOptions: {
                tfm: 'net10.0',
                framework: { name: 'Microsoft.NETCore.App', version: '10.0.9' },
              },
            }),
          ),
        },
      ],
      notices,
      {
        ...nativeInputs,
        noticesSha256: sha256(notices),
        assetsSha256: '3'.repeat(64),
        target: MANAGED_TARGET,
        toolchain: {
          sdk: '10.0.109',
          sdkDriverSha256: 'e'.repeat(64),
          node: process.versions.node,
          zlib: process.versions.zlib,
          os: 'ubuntu-24.04-x64',
        },
      },
    );
  }
  const map = {
    ...native.map,
    tag: `payload-${packaged.receipt.identity.releaseVersion}`,
    payload: packaged.receipt,
  };
  return { ...packaged, map, mapBytes: Buffer.from(JSON.stringify(map)) };
}
async function managedContext(
  fixture = managedFixture(),
  behavior: Behavior = {},
  signal = new AbortController().signal,
) {
  const base = await mkdtemp(join(tmpdir(), 'apr-managed-staging-test-'));
  const parent = join(base, 'parent'),
    workspace = join(base, 'workspace');
  await mkdir(parent, { mode: 0o700 });
  await mkdir(workspace);
  const api = await fakeRelease(fixture, behavior);
  const request = {
    mapBytes: fixture.mapBytes,
    actionSourceSha: ACTION_SOURCE,
    stagingParent: parent,
    workspace,
    signal,
  };
  const acquire = createRepositoryTestManagedPayloadResolver({
    transport: api.transport,
    timeoutMs: 2000,
  });
  return {
    base,
    parent,
    workspace,
    api,
    request,
    acquire,
    async close() {
      await api.close();
      await rm(base, { recursive: true, force: true });
    },
  };
}

// These tests exercise Linux descriptor semantics. A skipped macOS/Windows run is not qualification.
describe.runIf(supported)('CR1-D2 complete managed release staging', () => {
  test('roundtrips every resource/native/empty asset through exact credential-free release transport', async () => {
    const fixture = managedFixture();
    const c = await managedContext(fixture);
    const payload = await c.acquire(c.request);
    try {
      const lease = await acquireVerifiedManagedTreeLease(payload.tree, c.request.signal);
      try {
        expect(Object.isFrozen(lease)).toBe(true);
        expect(lease.receipt).toEqual(fixture.receipt);
        expect(lease.builder.workflowCommit).toBe('1'.repeat(40));
        expect(lease.actionSourceSha).toBe(ACTION_SOURCE);
        const expected = inspectManagedPackage(fixture.archive, fixture.receipt).files;
        expect(lease.files.map((item) => item.member.path)).toEqual(
          expected.map((item) => item.path),
        );
        for (const file of expected) {
          expect(await readFile(`/proc/self/fd/${lease.directoryFd}/${file.path}`)).toEqual(
            file.bytes,
          );
          const guard = lease.files.find((item) => item.member.path === file.path)!;
          expect(await readFile(`/proc/self/fd/${guard.fd}`)).toEqual(file.bytes);
          expect((await lstat(join(payload.root, file.path))).mode & 0o777).toBe(0o400);
        }
        for (const item of c.api.seen) {
          expect(item.url.startsWith('https://')).toBe(true);
          expect(Object.keys(item.headers).some((name) => /authorization|token/i.test(name))).toBe(
            false,
          );
        }
      } finally {
        lease.release();
        lease.release();
      }
    } finally {
      await payload.dispose();
      await payload.dispose();
      expect(await readdir(c.parent)).toEqual([]);
      await c.close();
    }
  });
  test('busy disposal is finite, pins all files despite cancellation, and permits confirmed-release retry', async () => {
    const c = await managedContext();
    const payload = await c.acquire(c.request);
    const cancellation = new AbortController();
    const lease = await acquireVerifiedManagedTreeLease(payload.tree, cancellation.signal);
    cancellation.abort();
    try {
      for (let attempt = 0; attempt < 2; attempt++)
        await expect(payload.dispose()).rejects.toBeInstanceOf(ManagedTreeInUseError);
      await expect(
        acquireVerifiedManagedTreeLease(payload.tree, c.request.signal),
      ).rejects.toThrow();
      for (const file of lease.files)
        expect((await readFile(`/proc/self/fd/${file.fd}`)).length).toBe(file.member.size);
      expect(await readdir(c.parent)).toHaveLength(1);
      lease.release();
      await payload.dispose();
      expect(await readdir(c.parent)).toEqual([]);
    } finally {
      lease.release();
      await payload.dispose();
      await c.close();
    }
  }, 5000);
  test('outer owner retains managed parent on busy and retries after confirmed child close', async () => {
    const c = await managedContext();
    const payload = await acquireInPayloadStaging({
      runnerTemp: c.parent,
      workspace: c.workspace,
      acquire: (stagingParent) => c.acquire({ ...c.request, stagingParent }),
    });
    const lease = await acquireVerifiedManagedTreeLease(payload.tree, c.request.signal);
    try {
      await expect(payload.dispose()).rejects.toBeInstanceOf(ManagedTreeInUseError);
      await expect(payload.dispose()).rejects.toBeInstanceOf(ManagedTreeInUseError);
      expect(await readdir(c.parent)).toHaveLength(1);
      for (const file of lease.files)
        expect(
          sha256(await readFile(`/proc/self/fd/${lease.directoryFd}/${file.member.path}`)),
        ).toBe(file.member.sha256);
      lease.release();
      await payload.dispose();
      expect(await readdir(c.parent)).toEqual([]);
    } finally {
      lease.release();
      await payload.dispose();
      await c.close();
    }
  }, 5000);
  test('closing races an in-flight acquisition without granting a lease or leaking a reservation', async () => {
    const c = await managedContext();
    const payload = await c.acquire(c.request);
    const acquiring = acquireVerifiedManagedTreeLease(payload.tree, c.request.signal);
    const rejected = expect(acquiring).rejects.toThrow('wrapper_managed_tree_invalid');
    await expect(payload.dispose()).rejects.toBeInstanceOf(ManagedTreeInUseError);
    await rejected;
    await payload.dispose();
    expect(await readdir(c.parent)).toEqual([]);
    await c.close();
  });
  test('aborted acquisition releases its reservation; forged tokens never admit a loading root', async () => {
    const c = await managedContext();
    const payload = await c.acquire(c.request);
    try {
      const abort = new AbortController();
      abort.abort();
      await expect(acquireVerifiedManagedTreeLease(payload.tree, abort.signal)).rejects.toThrow();
      for (const token of [{}, { ...payload.tree }, { root: payload.root }])
        await expect(
          acquireVerifiedManagedTreeLease(token as VerifiedManagedTree, c.request.signal),
        ).rejects.toThrow();
    } finally {
      await payload.dispose();
      expect(await readdir(c.parent)).toEqual([]);
      await c.close();
    }
  });
  test.each([
    'entry',
    'dependency',
    'resource',
    'native',
    'empty',
    'extra-file',
    'extra-directory',
    'missing',
    'symlink',
    'hardlink',
    'fifo',
  ])('rejects observed complete-tree tampering before the consumer fence: %s', async (kind) => {
    const c = await managedContext();
    const payload = await c.acquire(c.request);
    const root = join(payload.root, 'agentic-pr-review');
    try {
      await chmod(root, 0o700);
      const files: Record<string, string> = {
        entry: 'AgenticPrReview.Runtime.dll',
        dependency: 'Dependency.dll',
        resource: 'fr/Dependency.resources.dll',
        native: 'libexample.so',
        empty: 'resources/empty.txt',
      };
      if (files[kind]) {
        const target = join(root, files[kind]);
        await chmod(target, 0o600);
        await writeFile(target, 'mutated');
      }
      if (kind === 'extra-file') await writeFile(join(root, 'unexpected.dll'), 'extra');
      if (kind === 'extra-directory') await mkdir(join(root, 'unexpected'));
      if (kind === 'missing') await rm(join(root, 'Dependency.dll'));
      if (['symlink', 'hardlink', 'fifo'].includes(kind)) {
        const target = join(root, 'Dependency.dll');
        await rm(target);
        if (kind === 'symlink') await symlink(join(c.workspace, 'untrusted.dll'), target);
        if (kind === 'hardlink') await link(join(root, 'AgenticPrReview.Runtime.dll'), target);
        if (kind === 'fifo') execFileSync('mkfifo', [target]);
      }
      await chmod(root, 0o500);
      await expect(
        acquireVerifiedManagedTreeLease(payload.tree, c.request.signal),
      ).rejects.toThrow();
    } finally {
      await payload.dispose();
      expect(await readdir(c.parent)).toEqual([]);
      await c.close();
    }
  });
  test.each(['root', 'ancestor', 'subdirectory'])(
    'rejects namespace replacement and retains unrelated paths until safe cleanup retry: %s',
    async (kind) => {
      const c = await managedContext();
      const payload = await acquireInPayloadStaging({
        runnerTemp: c.parent,
        workspace: c.workspace,
        acquire: (stagingParent) => c.acquire({ ...c.request, stagingParent }),
      });
      const target =
        kind === 'root'
          ? payload.root
          : kind === 'ancestor'
            ? c.parent
            : join(payload.root, 'agentic-pr-review/fr');
      const moved = `${target}-original`;
      const enclosing =
        kind === 'subdirectory' ? join(payload.root, 'agentic-pr-review') : undefined;
      if (enclosing) await chmod(enclosing, 0o700);
      await rename(target, moved);
      await mkdir(target, { mode: 0o700 });
      await writeFile(join(target, 'unrelated'), 'preserve');
      try {
        await expect(
          acquireVerifiedManagedTreeLease(payload.tree, c.request.signal),
        ).rejects.toThrow();
        await expect(payload.dispose()).rejects.toThrow();
        await expect(payload.dispose()).rejects.toThrow();
        expect(await readFile(join(target, 'unrelated'), 'utf8')).toBe('preserve');
      } finally {
        await rm(target, { recursive: true });
        await rename(moved, target);
        await payload.dispose();
        expect(await readdir(c.parent)).toEqual([]);
        await c.close();
      }
    },
  );
  test.each([
    'pre-abort',
    'digest',
    'oversize',
    'wrong-release',
    'duplicate-assets',
    'redirect',
    'stall',
  ])('fails closed and cleans acquisition: %s', async (kind) => {
    const fixture = managedFixture();
    const abort = new AbortController();
    const behavior: Behavior = {};
    if (kind === 'pre-abort') abort.abort();
    if (kind === 'digest') behavior.body = Buffer.alloc(fixture.archive.length);
    if (kind === 'oversize') behavior.body = Buffer.alloc(fixture.archive.length + 1);
    if (kind === 'wrong-release') behavior.release = { id: 7, tag_name: 'latest', draft: false };
    if (kind === 'duplicate-assets')
      behavior.assets = [
        { name: fixture.receipt.archiveName },
        { name: fixture.receipt.archiveName },
      ];
    if (kind === 'redirect') behavior.redirect = 'https://attacker.invalid/archive';
    if (kind === 'stall') behavior.stall = true;
    const c = await managedContext(fixture, behavior, abort.signal);
    try {
      const acquire = createRepositoryTestManagedPayloadResolver({
        transport: c.api.transport,
        timeoutMs: kind === 'stall' ? 25 : 2000,
      });
      await expect(acquire(c.request)).rejects.toThrow();
      expect(await readdir(c.parent)).toEqual([]);
    } finally {
      await c.close();
    }
  });
  test('rejects workspace staging before release download and snapshots caller-owned map bytes', async () => {
    const c = await managedContext();
    try {
      await expect(c.acquire({ ...c.request, stagingParent: c.workspace })).rejects.toThrow();
      expect(c.api.seen).toEqual([]);
      const resolving = c.acquire(c.request);
      c.request.mapBytes.fill(0);
      const payload = await resolving;
      await payload.dispose();
      expect(await readdir(c.parent)).toEqual([]);
    } finally {
      await c.close();
    }
  });
  test.each([
    'traversal',
    'symlink',
    'hardlink',
    'special',
    'duplicate',
    'case',
    'extra',
    'missing',
    'manifest',
    'dependency-path',
  ])('rejects malicious managed archive beyond a valid outer digest: %s', async (kind) => {
    const original = managedFixture();
    let tar = gunzipSync(original.archive);
    const receipt = structuredClone(original.receipt);
    const offsets = new Map<string, { header: number; data: number }>();
    let position = 0;
    for (const member of receipt.members) {
      offsets.set(member.path, { header: position, data: position + 512 });
      position += 512 + Math.ceil(member.size / 512) * 512;
    }
    const first = receipt.members[0],
      offset = offsets.get(first.path)!;
    if (kind === 'traversal') {
      tar.fill(0, offset.header, offset.header + 100);
      tar.write('../workspace/escape', offset.header);
    }
    if (kind === 'symlink') tar[offset.header + 156] = 50;
    if (kind === 'hardlink') tar[offset.header + 156] = 49;
    if (kind === 'special') tar[offset.header + 156] = 54;
    if (kind === 'case') tar[offset.header] = 65;
    if (kind === 'duplicate') {
      const next = receipt.members[1];
      tar.fill(0, offsets.get(next.path)!.header, offsets.get(next.path)!.header + 100);
      tar.write(first.path, offsets.get(next.path)!.header);
    }
    if (kind === 'extra')
      tar = Buffer.concat([
        tar.subarray(0, position),
        tar.subarray(0, 512 + Math.ceil(first.size / 512) * 512),
        Buffer.alloc(1024),
      ]);
    if (kind === 'missing')
      tar = Buffer.concat([tar.subarray(512 + Math.ceil(first.size / 512) * 512)]);
    if (kind === 'manifest' || kind === 'dependency-path') {
      const manifestMember = receipt.members.find((item) => item.path.endsWith('/manifest.json'))!;
      const manifestOffset = offsets.get(manifestMember.path)!;
      let manifestBytes = Buffer.from(
        tar.subarray(manifestOffset.data, manifestOffset.data + manifestMember.size),
      );
      if (kind === 'manifest')
        manifestBytes = Buffer.from(
          manifestBytes.toString().replaceAll('agentic-pr-review', 'workspace-badpath'),
        );
      else {
        const member = receipt.members.find((item) => item.path.endsWith('.deps.json'))!;
        const data = offsets.get(member.path)!.data;
        const bytes = Buffer.from(
          tar
            .subarray(data, data + member.size)
            .toString()
            .replace('lib/net10.0/Dependency.dll', '..//net10.0/Dependency.dll'),
        );
        expect(bytes.length).toBe(member.size);
        bytes.copy(tar, data);
        const previous = member.sha256;
        const digest = sha256(bytes);
        Object.assign(member, { sha256: digest });
        manifestBytes = Buffer.from(manifestBytes.toString().replace(previous, digest));
      }
      expect(manifestBytes.length).toBe(manifestMember.size);
      manifestBytes.copy(tar, manifestOffset.data);
      Object.assign(manifestMember, { sha256: sha256(manifestBytes) });
    }
    // Recompute altered headers and gzip framing, so rejection isn't merely malformed compression.
    if (['traversal', 'symlink', 'hardlink', 'special', 'case', 'duplicate'].includes(kind)) {
      const headers =
        kind === 'duplicate' ? [offsets.get(receipt.members[1].path)!.header] : [offset.header];
      for (const header of headers) {
        tar.fill(32, header + 148, header + 156);
        const sum = tar.subarray(header, header + 512).reduce((total, byte) => total + byte, 0);
        tar.write(sum.toString(8).padStart(6, '0') + '\0 ', header + 148, 'ascii');
      }
    }
    const trailer = Buffer.alloc(8);
    trailer.writeUInt32LE(crc32(tar));
    trailer.writeUInt32LE(tar.length, 4);
    const archive = Buffer.concat([
      original.archive.subarray(0, 10),
      deflateRawSync(tar, { level: 9 }),
      trailer,
    ]);
    Object.assign(receipt, { archiveSize: archive.length, archiveSha256: sha256(archive) });
    const c = await managedContext(managedFixture({ archive, receipt }));
    try {
      await expect(c.acquire(c.request)).rejects.toThrow('wrapper_release_payload_invalid');
      expect(await readdir(c.parent)).toEqual([]);
    } finally {
      await c.close();
    }
  });

  test('descriptor exhaustion closes all partially acquired guards and removes the owned tree', async () => {
    const c = await managedContext();
    const opened: Awaited<ReturnType<typeof fsPromises.open>>[] = [];
    const original = fsPromises.open;
    let injected = false;
    const spy = vi
      .spyOn(fsPromises, 'open')
      .mockImplementation(async (...args: Parameters<typeof fsPromises.open>) => {
        if (opened.length === 14 && !injected) {
          injected = true;
          throw Object.assign(new Error('descriptor exhaustion'), { code: 'EMFILE' });
        }
        const handle = await original(...args);
        opened.push(handle);
        return handle;
      });
    syncBuiltinESMExports();
    try {
      await expect(c.acquire(c.request)).rejects.toThrow();
      expect(injected).toBe(true);
      expect(opened.length).toBeGreaterThanOrEqual(14);
      expect(opened.every((handle) => handle.fd === -1)).toBe(true);
      expect(await readdir(c.parent)).toEqual([]);
    } finally {
      spy.mockRestore();
      syncBuiltinESMExports();
      await c.close();
    }
  });
  test.runIf(process.env.APR_CR1_MANAGED_STAGING_PROOF === '1')(
    'real exact-head managed producer passes credential-free release acquisition and complete tree handoff',
    async () => {
      const outputParent = await mkdtemp(join(tmpdir(), 'apr-managed-stager-proof-'));
      let c: Awaited<ReturnType<typeof managedContext>> | undefined;
      try {
        const source = execFileSync('git', ['rev-parse', 'HEAD']).toString().trim();
        const output = join(outputParent, 'output');
        const proof = execFileSync(
          process.execPath,
          [
            'scripts/release/build-payload.mjs',
            'build-managed',
            '--source',
            source,
            '--version',
            'v0.0.0-internal.1',
            '--output',
            output,
          ],
          { timeout: 600_000, maxBuffer: 4 * 1024 * 1024 },
        ).toString();
        const receipt = JSON.parse(
          await readFile(join(output, 'receipt.json'), 'utf8'),
        ) as ManagedReceipt;
        const archive = await readFile(join(output, receipt.archiveName));
        c = await managedContext(managedFixture({ archive, receipt }));
        const payload = await c.acquire(c.request);
        try {
          const lease = await acquireVerifiedManagedTreeLease(payload.tree, c.request.signal);
          try {
            expect(lease.receipt.identity.sourceCommit).toBe(source);
            const original = inspectManagedPackage(archive, receipt);
            expect(lease.files).toHaveLength(original.files.length);
            expect(original.files.length).toBeGreaterThanOrEqual(9);
            for (const file of original.files)
              expect(await readFile(`/proc/self/fd/${lease.directoryFd}/${file.path}`)).toEqual(
                file.bytes,
              );
            expect(proof).toContain('"roundtrip":true');
            console.log(
              JSON.stringify({
                managedStagerProof: true,
                sourceCommit: source,
                archiveSha256: receipt.archiveSha256,
                verifiedMembers: lease.files.length,
                runtimeLoadingQualified: false,
              }),
            );
          } finally {
            lease.release();
          }
        } finally {
          await payload.dispose();
          expect(await readdir(c.parent)).toEqual([]);
        }
      } finally {
        await c?.close();
        await rm(outputParent, { recursive: true, force: true });
      }
    },
    660_000,
  );
});

test.runIf(!supported)(
  'managed resolver rejects unsupported platform without release acquisition',
  async () => {
    await expect(
      resolveManagedReleasePayload({
        mapBytes: managedFixture().mapBytes,
        actionSourceSha: ACTION_SOURCE,
        stagingParent: '/unused',
        workspace: '/unused-workspace',
        signal: new AbortController().signal,
      }),
    ).rejects.toThrow('wrapper_payload_platform_unsupported');
  },
);
