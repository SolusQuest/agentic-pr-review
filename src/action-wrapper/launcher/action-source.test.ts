import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { createServer, request as httpRequest, type Server } from 'node:http';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { createRepositoryTestActionSourceResolver } from './action-source.js';

const roots: string[] = [],
  servers: Server[] = [];
afterEach(async () => {
  for (const server of servers.splice(0)) {
    server.closeAllConnections();
    await new Promise<void>((done) => server.close(() => done()));
  }
  await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })));
});
async function fixture(mutation?: string) {
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
      formatVersion: 1,
      archiveName: 'agentic-pr-review-' + version + '-linux-x64.tar.gz',
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
      members: [
        { path: 'agentic-pr-review/agentic-pr-review', mode: 493, size: 1, sha256: 'f'.repeat(64) },
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
  await mkdir(path.join(root, 'dist'));
  await writeFile(path.join(root, 'action.yml'), 'metadata\n');
  await writeFile(path.join(root, 'dist/index.js'), 'bundle\n');
  const files: Record<string, Buffer> = {
    'action.yml': Buffer.from('metadata\n'),
    'dist/index.js': Buffer.from('bundle\n'),
    'payload-map.json': mapBytes,
  };
  const calls: string[] = [];
  const server = createServer((request, response) => {
    const url = new URL(request.url!, 'http://local');
    if (mutation === 'redirect') {
      response.writeHead(302, { Location: 'https://api.github.com/other' });
      response.end();
      return;
    }
    if (mutation === 'oversize') {
      response.writeHead(200, { 'Content-Length': 256 * 1024 + 1 });
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
      response.end(JSON.stringify({ sha: mutation === 'bad-commit' ? 'main' : 'b'.repeat(40) }));
      return;
    }
    const relative = url.pathname.split('/.github/actions/agentic-pr-review/')[1]!;
    const bytes = files[relative]!;
    response.end(
      JSON.stringify({
        type: mutation === 'directory' ? 'dir' : 'file',
        path: mutation === 'path' ? 'other' : '.github/actions/agentic-pr-review/' + relative,
        size: bytes.length + (mutation === 'size' ? 1 : 0),
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
  const resolver = createRepositoryTestActionSourceResolver((url, options, callback) => {
    expect(url.origin).toBe('https://api.github.com');
    expect(url.pathname.startsWith('/repos/SolusQuest/agentic-pr-review/')).toBe(true);
    expect(options.headers).not.toHaveProperty('Authorization');
    expect(options.agent).toBe(false);
    calls.push(url.pathname + url.search);
    return httpRequest(
      new URL(url.pathname + url.search, `http://127.0.0.1:${address.port}`),
      options,
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
