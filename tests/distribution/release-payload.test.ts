import { execFileSync } from 'node:child_process';
import { createServer, request as httpRequest, type ServerResponse } from 'node:http';
import {
  chmod,
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
import { afterAll, beforeAll, describe, expect, test } from 'vitest';
import { sha256 } from '../../scripts/release/build-payload.format.mjs';
import {
  createRepositoryTestPayloadResolver,
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

async function fakeRelease(fixture: Fixture, behavior: Behavior = {}) {
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
