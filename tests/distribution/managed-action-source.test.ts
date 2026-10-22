import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { createServer, request as httpRequest, type Server } from 'node:http';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';

import { createRepositoryTestManagedActionSourceResolver } from '../../src/action-wrapper/launcher/action-source.js';
import { MANAGED_PAYLOAD_MAP_MAXIMUM_BYTES } from '../../src/action-wrapper/launcher/payload-map.js';
import { managedMap, minimalManagedFiles, packageManagedFiles } from './managed-host-fixture.js';

const roots: string[] = [];
const servers: Server[] = [];
const sourceT = 'b'.repeat(40);
const repository = 'SolusQuest/agentic-pr-review';

afterEach(async () => {
  for (const server of servers.splice(0)) {
    server.closeAllConnections();
    await new Promise<void>((resolve) => server.close(() => resolve()));
  }
  await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })));
});

async function fixture(
  options: {
    decodedSize?: number;
    unknownMapField?: boolean;
    responseBytes?: number;
    responseTarget?: string;
    chunked?: boolean;
    encodedOverflow?: boolean;
    mismatch?: 'sha' | 'size' | 'path' | 'type';
    tag?: boolean;
    sdkMismatch?: boolean;
  } = {},
) {
  const root = await mkdtemp(path.join(tmpdir(), 'apr-managed-action-source-'));
  roots.push(root);
  const map = managedMap(packageManagedFiles(minimalManagedFiles()).receipt);
  const json = Buffer.from(
    JSON.stringify(options.unknownMapField ? { ...map, unknown: true } : map),
  );
  const mapBytes = options.decodedSize
    ? Buffer.concat([json, Buffer.alloc(options.decodedSize - json.length, 0x20)])
    : json;
  const files: Record<string, Buffer> = {
    'action.yml': Buffer.from('metadata\n'),
    'payload-map.json': mapBytes,
    'dist/index.js': Buffer.from('literal-source-fixture\n'),
    'dist/sdk-upload.js': Buffer.from('literal-sdk-source-fixture\n'),
  };
  await mkdir(path.join(root, 'dist'));
  for (const [name, bytes] of Object.entries(files)) await writeFile(path.join(root, name), bytes);
  const calls: string[] = [];
  const server = createServer((request, response) => {
    calls.push(request.url!);
    expect(request.headers.authorization).toBeUndefined();
    const url = new URL(request.url!, 'http://fixture');
    let document: Record<string, unknown>;
    let relative: string;
    if (url.pathname.includes('/commits/')) {
      relative = 'commit';
      expect(request.headers.accept).toBe('application/vnd.github+json');
      document = { sha: sourceT };
    } else {
      relative = url.pathname.split('/.github/actions/agentic-pr-review/')[1]!;
      expect(request.headers.accept).toBe('application/vnd.github.object+json');
      expect(url.searchParams.get('ref')).toBe(sourceT);
      const bytes = files[relative]!;
      document = {
        type: 'file',
        path: '.github/actions/agentic-pr-review/' + relative,
        size: bytes.length,
        sha: createHash('sha1')
          .update('blob ' + bytes.length + '\0')
          .update(bytes)
          .digest('hex'),
        encoding: 'base64',
        content: (bytes.toString('base64').match(/.{1,60}/gu) ?? []).join('\n') + '\n',
      };
      if (relative === 'dist/sdk-upload.js' && options.sdkMismatch) document.sha = '0'.repeat(40);
      if (relative === 'payload-map.json' && options.mismatch)
        document[options.mismatch] = options.mismatch === 'size' ? bytes.length + 1 : 'wrong';
      if (relative === 'payload-map.json' && options.encodedOverflow)
        document.content = 'A'.repeat(512 * 1024);
    }
    let body = Buffer.from(JSON.stringify(document));
    if (options.responseBytes && relative === (options.responseTarget ?? 'payload-map.json')) {
      expect(body.length).toBeLessThanOrEqual(options.responseBytes);
      body = Buffer.concat([body, Buffer.alloc(options.responseBytes - body.length, 0x20)]);
    }
    response.writeHead(200, options.chunked ? {} : { 'Content-Length': body.length });
    response.end(body);
  });
  servers.push(server);
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  const origin = 'http://127.0.0.1:' + (server.address() as { port: number }).port;
  const resolveSource = createRepositoryTestManagedActionSourceResolver(
    (url, request, callback) => {
      expect(url.origin).toBe('https://api.github.com');
      return httpRequest(new URL(url.pathname + url.search, origin), request, callback);
    },
  );
  return {
    calls,
    mapBytes,
    map,
    resolve: () =>
      resolveSource({
        actionRoot: root,
        mapBytes,
        actionRepository: repository,
        actionRef: options.tag ? map.payload.identity.releaseVersion : sourceT,
        signal: new AbortController().signal,
      }),
  };
}

describe('managed Action T and bounded Contents representation', () => {
  it('refuses a substituted SDK child asset before launch', async () => {
    const f = await fixture({ sdkMismatch: true });
    await expect(f.resolve()).rejects.toThrow();
  });
  it('admits the exact decoded 256 KiB map with its larger realistic Base64 Contents body', async () => {
    const f = await fixture({ decodedSize: MANAGED_PAYLOAD_MAP_MAXIMUM_BYTES });
    expect(f.mapBytes.length).toBe(256 * 1024);
    expect(f.map.payload.identity.sourceCommit).not.toBe(sourceT);
    expect(f.map.builder.workflowCommit).not.toBe(sourceT);
    await expect(f.resolve()).resolves.toBe(sourceT);
    expect(f.calls).toHaveLength(4);
  });

  it.each([{ decodedSize: MANAGED_PAYLOAD_MAP_MAXIMUM_BYTES + 1 }, { unknownMapField: true }])(
    'rejects over-limit or unknown-field decoded maps before metadata requests',
    async (options) => {
      const f = await fixture(options);
      await expect(f.resolve()).rejects.toThrow('wrapper_payload_map_invalid');
      expect(f.calls).toEqual([]);
    },
  );

  it.each([false, true])(
    'admits exactly bounded managed outer JSON (chunked=%s)',
    async (chunked) => {
      const f = await fixture({ decodedSize: 256 * 1024, responseBytes: 512 * 1024, chunked });
      await expect(f.resolve()).resolves.toBe(sourceT);
    },
  );

  it.each([false, true])('rejects one-byte outer overflow (chunked=%s)', async (chunked) => {
    const f = await fixture({ responseBytes: 512 * 1024 + 1, chunked });
    await expect(f.resolve()).rejects.toThrow('wrapper_action_source_invalid');
  });

  it('bounds encoded-content expansion without treating that remote content as loading authority', async () => {
    const f = await fixture({ encodedOverflow: true });
    await expect(f.resolve()).rejects.toThrow('wrapper_action_source_invalid');
  });

  it.each(['action.yml', 'dist/index.js', 'dist/sdk-upload.js', 'commit'])(
    'retains the original 256 KiB metadata cap for %s',
    async (responseTarget) => {
      const f = await fixture({
        responseBytes: 256 * 1024 + 1,
        responseTarget,
        tag: responseTarget === 'commit',
      });
      await expect(f.resolve()).rejects.toThrow('wrapper_action_source_invalid');
    },
  );

  it.each(['sha', 'size', 'path', 'type'] as const)(
    'never relaxes the exact installed map %s comparison',
    async (mismatch) => {
      const f = await fixture({ decodedSize: 256 * 1024, mismatch });
      await expect(f.resolve()).rejects.toThrow('wrapper_action_source_invalid');
    },
  );
});
