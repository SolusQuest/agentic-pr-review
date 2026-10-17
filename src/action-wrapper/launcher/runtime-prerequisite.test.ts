import { chmod, mkdir, mkdtemp, readdir, rm } from 'node:fs/promises';
import { createServer, request as httpRequest, type RequestListener, type Server } from 'node:http';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';

import {
  createRepositoryTestRuntimePrerequisiteAcquirer,
  PINNED_MANAGED_RUNTIME,
  validatePinnedRuntimeListing,
} from './runtime-prerequisite.js';

const framework = './shared/Microsoft.NETCore.App/' + PINNED_MANAGED_RUNTIME.version + '/';
const fxr = './host/fxr/' + PINNED_MANAGED_RUNTIME.version + '/';
function row(name: string, size = 1, mode = '-rw-r--r--'): string {
  return mode + ' 2001/2001 ' + size + ' 2026-10-04 21:53:07 ' + name + '\n';
}
function listing(): string {
  return [
    ...[
      './',
      './host/',
      './host/fxr/',
      fxr,
      './shared/',
      './shared/Microsoft.NETCore.App/',
      framework,
    ].map((name) => row(name, 0, 'drwxr-xr-x')),
    row('./dotnet', 71_744, '-rwxr-xr-x'),
    row('./LICENSE.txt', 1_116),
    row(fxr + 'libhostfxr.so', 326_888, '-rwxr-xr-x'),
    ...[
      'libcoreclr.so',
      'libhostpolicy.so',
      'System.Private.CoreLib.dll',
      'Microsoft.NETCore.App.deps.json',
      'Microsoft.NETCore.App.runtimeconfig.json',
    ].map((name) => row(framework + name)),
  ].join('');
}
const validate = (text: string) => validatePinnedRuntimeListing(Buffer.from(text));

describe('pinned official runtime archive admission before extraction', () => {
  it('admits the explicit runtime-only layout while treating archive uid/gid as non-authority', () => {
    expect(() => validate(listing())).not.toThrow();
    expect(() => validate(listing().replaceAll('2001/2001', '0/0'))).not.toThrow();
  });

  it.each([
    ['symbolic link', row(framework + 'link', 1, 'lrwxr-xr-x')],
    ['hard link', row(framework + 'link', 1, 'hrw-r--r--')],
    ['special file', row(framework + 'fifo', 0, 'prw-r--r--')],
    ['absolute path', row('/tmp/outside')],
    ['traversal', row(framework + '../outside')],
    ['dot segment', row(framework + './outside')],
    ['empty segment', row(framework + '/outside')],
    ['escaped newline', row(framework + 'bad\\nname')],
    ['space in name', row(framework + 'bad name')],
    ['duplicate file', row('./dotnet')],
    ['directory/file alias', row('./host')],
    ['extra SDK', row('./sdk/10.0.109/SDK.dll')],
    ['other runtime version', row('./shared/Microsoft.NETCore.App/10.0.14/Extra.dll')],
    ['unexpected root file', row('./libhostpolicy.so')],
    ['unexpected subdirectory', row(framework + 'en/', 0, 'drwxr-xr-x')],
    ['group writable file', row(framework + 'Extra.dll', 1, '-rw-rw-r--')],
    ['other writable file', row(framework + 'Extra.dll', 1, '-rw-r--rw-')],
    ['set-id file', row(framework + 'Extra.dll', 1, '-rwsr-xr-x')],
    ['nonzero directory bytes', row('./extra/', 1, 'drwxr-xr-x')],
    ['oversized file', row(framework + 'Extra.dll', 64 * 1024 * 1024 + 1)],
    ['unsafe integer size', row(framework + 'Extra.dll', Number.MAX_SAFE_INTEGER + 1)],
    ['empty runtime file', row(framework + 'Extra.dll', 0)],
  ])('rejects %s without granting extraction authority', (_name, extra) => {
    expect(() => validate(listing() + extra)).toThrow();
  });

  it('requires every explicit ancestor and the runtime prerequisites', () => {
    for (const line of listing().split('\n').filter(Boolean)) {
      if (line.endsWith('LICENSE.txt')) continue;
      expect(() => validate(listing().replace(line + '\n', ''))).toThrow();
    }
  });

  it('bounds total expanded inventory independently of the per-file bound', () => {
    const extra = Array.from({ length: 9 }, (_, index) =>
      row(framework + 'Large' + index + '.dll', 64 * 1024 * 1024),
    ).join('');
    expect(() => validate(listing() + extra)).toThrow();
  });

  it('bounds the entry inventory without allocating expanded file contents', () => {
    const extra = Array.from({ length: 1024 }, (_, index) =>
      row(framework + 'Extra' + index + '.dll'),
    ).join('');
    expect(() => validate(listing() + extra)).toThrow();
  });

  it.each(['', listing().slice(0, -1), '\ufeff' + listing(), listing() + '\n'])(
    'rejects incomplete or noncanonical listing text',
    (text) => {
      expect(() => validate(text)).toThrow();
    },
  );

  it('rejects malformed UTF-8 and bounded-output overflow', () => {
    expect(() => validatePinnedRuntimeListing(Buffer.from([0xff]))).toThrow();
    expect(() => validatePinnedRuntimeListing(Buffer.alloc(2 * 1024 * 1024 + 1))).toThrow();
  });
});

// These use real HTTP streams and the production filesystem owner. They do not
// substitute a digest, asset selector, extractor, or successful installation.
describe.skipIf(process.platform !== 'linux' || process.arch !== 'x64')(
  'runtime acquisition transport rejection and cleanup',
  () => {
    const roots: string[] = [];
    const servers: Server[] = [];
    afterEach(async () => {
      for (const server of servers.splice(0)) {
        server.closeAllConnections();
        await new Promise<void>((resolve) => server.close(() => resolve()));
      }
      await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })));
    });

    async function fixture(respond: RequestListener, controller = new AbortController()) {
      const root = await mkdtemp(path.join(tmpdir(), 'apr-prerequisite-transport-'));
      roots.push(root);
      const runnerTemp = path.join(root, 'runner-temp');
      const workspace = path.join(root, 'workspace');
      await Promise.all([mkdir(runnerTemp, { mode: 0o700 }), mkdir(workspace)]);
      const server = createServer(respond);
      servers.push(server);
      await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
      const origin = 'http://127.0.0.1:' + (server.address() as { port: number }).port;
      let calls = 0;
      const acquire = createRepositoryTestRuntimePrerequisiteAcquirer((url, options, callback) => {
        calls++;
        expect(url.href).toBe(PINNED_MANAGED_RUNTIME.url);
        expect(options.method).toBe('GET');
        expect(options.agent).toBe(false);
        expect(options.headers).toEqual({
          'User-Agent': 'agentic-pr-review-runtime-prerequisite',
          'Accept-Encoding': 'identity',
        });
        return httpRequest(origin, options, callback);
      });
      return {
        runnerTemp,
        calls: () => calls,
        run: () => acquire({ runnerTemp, workspace, signal: controller.signal }),
      };
    }

    it.each([
      ['redirect', 302, { location: 'https://example.invalid/runtime' }],
      ['non-success', 503, {}],
      ['encoded body', 200, { 'content-encoding': 'gzip' }],
      ['incorrect advertised length', 200, { 'content-length': '1' }],
    ] as const)(
      'rejects %s and removes the partial installation',
      async (_name, status, headers) => {
        const test = await fixture((_request, response) => {
          response.writeHead(status, headers);
          response.end('x');
        });
        await expect(test.run()).rejects.toThrow('wrapper_managed_runtime_prerequisite');
        expect(test.calls()).toBe(1);
        expect(await readdir(test.runnerTemp)).toEqual([]);
      },
    );

    it('rejects a complete short chunked body before extractor execution', async () => {
      const test = await fixture((_request, response) => response.end('short'));
      await expect(test.run()).rejects.toThrow('wrapper_managed_runtime_prerequisite');
      expect(await readdir(test.runnerTemp)).toEqual([]);
    });

    it('rejects an exact-size body with a different digest', async () => {
      const test = await fixture((_request, response) => {
        response.writeHead(200, { 'content-length': PINNED_MANAGED_RUNTIME.archiveBytes });
        response.end(Buffer.alloc(PINNED_MANAGED_RUNTIME.archiveBytes));
      });
      await expect(test.run()).rejects.toThrow('wrapper_managed_runtime_prerequisite');
      expect(await readdir(test.runnerTemp)).toEqual([]);
    });

    it('rejects chunked overflow independently of the advertised length', async () => {
      const test = await fixture((_request, response) => {
        response.end(Buffer.alloc(PINNED_MANAGED_RUNTIME.archiveBytes + 1));
      });
      await expect(test.run()).rejects.toThrow('wrapper_managed_runtime_prerequisite');
      expect(await readdir(test.runnerTemp)).toEqual([]);
    });

    it('cancels a partial response and releases the private filesystem owner', async () => {
      const controller = new AbortController();
      const test = await fixture((_request, response) => {
        response.write('partial');
        setImmediate(() => controller.abort());
      }, controller);
      await expect(test.run()).rejects.toThrow('wrapper_managed_runtime_prerequisite');
      expect(await readdir(test.runnerTemp)).toEqual([]);
    });

    it('does not contact the server for an already cancelled acquisition', async () => {
      const controller = new AbortController();
      controller.abort();
      const test = await fixture((_request, response) => response.end(), controller);
      await expect(test.run()).rejects.toThrow('wrapper_managed_runtime_prerequisite');
      expect(test.calls()).toBe(0);
      expect(await readdir(test.runnerTemp)).toEqual([]);
    });

    it('rejects a private runner directory beneath a shared writable ancestor before download', async () => {
      const test = await fixture((_request, response) => response.end());
      await chmod(path.dirname(test.runnerTemp), 0o777);
      await expect(test.run()).rejects.toThrow('wrapper_managed_runtime_prerequisite');
      expect(test.calls()).toBe(0);
      expect(await readdir(test.runnerTemp)).toEqual([]);
    });

    it('cleans up when the peer terminates before delivering response headers', async () => {
      const test = await fixture((request) => request.socket.destroy());
      await expect(test.run()).rejects.toThrow('wrapper_managed_runtime_prerequisite');
      expect(await readdir(test.runnerTemp)).toEqual([]);
    });
  },
);
