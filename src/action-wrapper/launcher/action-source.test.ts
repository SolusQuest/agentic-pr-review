import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { createServer, request as httpRequest, type Server } from 'node:http';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import {
  createRepositoryTestActionSourceResolver,
  createRepositoryTestManagedActionSourceResolver,
} from './action-source.js';

const roots: string[] = [],
  servers: Server[] = [];
afterEach(async () => {
  for (const server of servers.splice(0)) {
    server.closeAllConnections();
    await new Promise<void>((done) => server.close(() => done()));
  }
  await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })));
});
async function fixture(
  mutation?: string,
  largeBundle = false,
  defaultBundleMediaType = false,
  inlineBundleBytes?: number,
  managed = false,
) {
  const root = await mkdtemp(path.join(tmpdir(), 'apr-action-t-'));
  roots.push(root);
  const version = 'v0.0.0-internal.1',
    buildId = 'd'.repeat(64);
  const map = {
    formatVersion: 1,
    repository: 'SolusQuest/agentic-pr-review',
    tag: 'payload-' + version,
    builder: {
      repository: 'SolusQuest/agentic-pr-review',
      workflowPath: '.github/workflows/release.yml',
      workflowCommit: 'c'.repeat(40),
    },
    payload: {
      formatVersion: managed ? 'managed-v1' : 1,
      archiveName:
        'agentic-pr-review-' + version + '-linux-x64' + (managed ? '-managed' : '') + '.tar.gz',
      archiveSize: 128,
      archiveSha256: 'f'.repeat(64),
      identity: {
        releaseVersion: version,
        platform: 'linux-x64',
        sourceCommit: 'a'.repeat(40),
        sourceTree: 'e'.repeat(40),
        buildId,
        informationalVersion: version + '+build.' + buildId,
      },
      members: managed
        ? [{ path: 'agentic-pr-review/manifest.json', mode: 420, size: 1, sha256: 'f'.repeat(64) }]
        : [
            {
              path: 'agentic-pr-review/agentic-pr-review',
              mode: 493,
              size: 1,
              sha256: 'f'.repeat(64),
            },
            { path: 'agentic-pr-review/manifest.json', mode: 420, size: 1, sha256: 'f'.repeat(64) },
            {
              path: 'agentic-pr-review/THIRD-PARTY-NOTICES.txt',
              mode: 420,
              size: 1,
              sha256: 'f'.repeat(64),
            },
          ],
    },
  };
  const mapBytes = Buffer.from(JSON.stringify(map));
  const bundle = inlineBundleBytes
    ? Buffer.alloc(inlineBundleBytes, 'x')
    : largeBundle
      ? Buffer.alloc(2 * 1024 * 1024, 'x')
      : Buffer.from('bundle\n');
  const files: Record<string, Buffer> = {
    'action.yml': Buffer.from('metadata\n'),
    'payload-map.json': mapBytes,
    ...(managed
      ? {
          'internal/action.yml': Buffer.from('launch metadata\n'),
          'internal/prepare/action.yml': Buffer.from('prepare metadata\n'),
          'internal/dist/index.js': bundle,
          'internal/dist/sdk-upload.js': Buffer.alloc(2 * 1024 * 1024, 'x'),
          'internal/dist/prepare.js': Buffer.alloc(869_949, 'x'),
          'internal/dist/cleanup.js': Buffer.from('cleanup bundle\n'),
        }
      : { 'dist/index.js': bundle }),
  };
  for (const [relative, bytes] of Object.entries(files)) {
    await mkdir(path.dirname(path.join(root, relative)), { recursive: true });
    await writeFile(path.join(root, relative), bytes);
  }
  const calls: string[] = [];
  const server = createServer((request, response) => {
    const url = new URL(request.url!, 'http://local');
    if (mutation === 'redirect') {
      response.writeHead(302, { Location: 'https://api.github.com/other' });
      response.end();
      return;
    }
    if (mutation === 'oversize') {
      response.writeHead(200, { 'Content-Length': 3 * 1024 * 1024 });
      response.end('{}');
      return;
    }
    if (mutation === 'encoding') {
      response.writeHead(200, { 'Content-Encoding': 'gzip' });
      response.end('{}');
      return;
    }
    if (mutation === 'duplicate') {
      response.end('{"sha":"' + 'b'.repeat(40) + '","sha":"' + 'c'.repeat(40) + '"}');
      return;
    }
    if (url.pathname.includes('/commits/')) {
      if (request.headers.accept !== 'application/vnd.github+json') {
        response.writeHead(415).end();
        return;
      }
      response.end(JSON.stringify({ sha: mutation === 'bad-commit' ? 'main' : 'b'.repeat(40) }));
      return;
    }
    const relative = url.pathname.split('/.github/actions/agentic-pr-review/')[1]!;
    const bytes = files[relative]!;
    // The Contents API only supports object/raw representations for 1-100 MB files.
    // Raw would omit the object metadata this resolver needs; reject default JSON.
    if (
      bytes.length > 1024 * 1024 &&
      request.headers.accept !== 'application/vnd.github.object+json'
    ) {
      response.writeHead(415).end();
      return;
    }
    response.end(
      JSON.stringify({
        type: mutation === 'directory' ? 'dir' : 'file',
        path: mutation === 'path' ? 'other' : '.github/actions/agentic-pr-review/' + relative,
        size: bytes.length + (mutation === 'size' ? 1 : 0),
        content:
          inlineBundleBytes && bytes.length <= 1024 * 1024
            ? bytes
                .toString('base64')
                .match(/.{1,60}/gu)!
                .join('\n') + '\n'
            : '',
        encoding: inlineBundleBytes && bytes.length <= 1024 * 1024 ? 'base64' : 'none',
        sha:
          mutation === 'digest'
            ? '0'.repeat(40)
            : createHash('sha1').update(`blob ${bytes.length}\0`).update(bytes).digest('hex'),
      }),
    );
  });
  servers.push(server);
  await new Promise<void>((done) => server.listen(0, '127.0.0.1', done));
  const address = server.address() as { port: number };
  const createResolver = managed
    ? createRepositoryTestManagedActionSourceResolver
    : createRepositoryTestActionSourceResolver;
  const resolver = createResolver((url, options, callback) => {
    expect(url.origin).toBe('https://api.github.com');
    expect(url.pathname.startsWith('/repos/SolusQuest/agentic-pr-review/')).toBe(true);
    expect(options.headers).not.toHaveProperty('Authorization');
    expect(options.agent).toBe(false);
    calls.push(url.pathname + url.search);
    return httpRequest(
      new URL(url.pathname + url.search, `http://127.0.0.1:${address.port}`),
      defaultBundleMediaType && url.pathname.endsWith('/dist/index.js')
        ? { ...options, headers: { ...options.headers, Accept: 'application/vnd.github+json' } }
        : options,
      callback,
    );
  });
  const request = {
    actionRoot: root,
    mapBytes,
    actionRepository: 'SolusQuest/agentic-pr-review',
    actionRef: 'b'.repeat(40),
    signal: new AbortController().signal,
  };
  return { resolver, request, calls, version: map.payload.identity.releaseVersion };
}
describe('installed Action T binding', () => {
  it('binds all eight managed sources with both inline bundles and a larger SDK bundle', async () => {
    const { resolver, request, calls } = await fixture(undefined, false, false, 966_209, true);
    await expect(resolver(request)).resolves.toBe(request.actionRef);
    expect(calls).toHaveLength(8);
    expect(calls.every((call) => call.endsWith('?ref=' + request.actionRef))).toBe(true);
  });
  it.each(['digest', 'oversize', 'encoding'])(
    'keeps managed source validation closed on %s with inline content',
    async (mutation) => {
      const { resolver, request } = await fixture(mutation, false, false, 966_209, true);
      await expect(resolver(request)).rejects.toThrow('wrapper_action_source_invalid');
    },
  );
  it.each([869_949, 966_209, 1024 * 1024, 1024 * 1024 + 1])(
    'binds a %i-byte bundle with the real Contents inline-content boundary',
    async (bytes) => {
      // Published .2 prepare/index sizes, then both sides of GitHub's 1 MiB boundary.
      const { resolver, request, calls } = await fixture(undefined, false, false, bytes);
      await expect(resolver(request)).resolves.toBe(request.actionRef);
      expect(calls).toHaveLength(3);
    },
  );
  it('rejects default Contents media type specifically at the multi-megabyte bundle', async () => {
    const { resolver, request, calls } = await fixture(undefined, true, true);
    await expect(resolver(request)).rejects.toThrow('wrapper_action_source_invalid');
    expect(calls).toHaveLength(3);
    expect(calls[2]).toContain('/dist/index.js?ref=');
  });
  it.each([false, true])(
    'binds a multi-megabyte bundle using bounded object metadata (tag=%s)',
    async (tag) => {
      const { resolver, request, calls, version } = await fixture(undefined, true);
      expect(await resolver({ ...request, actionRef: tag ? version : request.actionRef })).toBe(
        'b'.repeat(40),
      );
      expect(calls).toHaveLength(tag ? 4 : 3);
    },
  );
  it('uses exact SHA or protected version tag and verifies all three installed blobs', async () => {
    const { resolver, request, calls, version } = await fixture();
    expect(await resolver(request)).toBe('b'.repeat(40));
    expect(calls).toHaveLength(3);
    expect(await resolver({ ...request, actionRef: version })).toBe('b'.repeat(40));
    expect(calls).toHaveLength(7);
    expect(calls.slice(4).every((call) => call.endsWith('?ref=' + 'b'.repeat(40)))).toBe(true);
  });
  it.each(['main', 'v1', '../v0.0.0-internal.1', 'b'.repeat(39), undefined])(
    'refuses unsupported runner ref %s before network',
    async (ref) => {
      const { resolver, request, calls } = await fixture();
      await expect(resolver({ ...request, actionRef: ref })).rejects.toThrow(
        'wrapper_action_source_invalid',
      );
      expect(calls).toEqual([]);
    },
  );
  it.each([
    'redirect',
    'oversize',
    'encoding',
    'duplicate',
    'bad-commit',
    'directory',
    'path',
    'size',
    'digest',
  ])('fails closed on metadata %s', async (mutation) => {
    const { resolver, request, version } = await fixture(mutation);
    await expect(resolver({ ...request, actionRef: version })).rejects.toThrow(
      'wrapper_action_source_invalid',
    );
  });
  it('honors cancellation without network or leaked exception details', async () => {
    const { resolver, request, calls } = await fixture();
    const controller = new AbortController();
    controller.abort('PRIVATE_CANARY');
    await expect(resolver({ ...request, signal: controller.signal })).rejects.toThrow(
      'wrapper_payload_cancelled',
    );
    expect(calls).toEqual([]);
  });
});
