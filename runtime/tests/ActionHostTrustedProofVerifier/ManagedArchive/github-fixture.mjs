// Bounded external HTTPS service. Every source byte has its actual Git blob/tree identity.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { gzipSync } from 'node:zlib';
import { githubKey, repository } from './official-platform.mjs';
export const workflowSha = 'a'.repeat(40),
  actionSha = 'b'.repeat(40),
  builderSha = 'c'.repeat(40);
export const objectHash = (type, bytes) =>
  createHash('sha1').update(`${type} ${bytes.length}\0`).update(bytes).digest('hex');
export const json = (response, document, status = 200) => {
  const bytes = Buffer.from(JSON.stringify(document));
  response
    .writeHead(status, {
      'Content-Type': 'application/json',
      'Content-Length': bytes.length,
      'x-ratelimit-limit': '1000',
      'x-ratelimit-remaining': '950',
      'x-ratelimit-reset': '4102444800',
    })
    .end(bytes);
};
export async function readBody(request) {
  const chunks = [];
  let total = 0;
  for await (const bytes of request) {
    total += bytes.length;
    assert.ok(total <= 2 * 1024 * 1024);
    chunks.push(bytes);
  }
  return Buffer.concat(chunks);
}
function tar(files) {
  const blocks = [];
  const directories = new Set(['apr-d5-fixture/']);
  for (const path of Object.keys(files)) {
    const parts = path.split('/');
    for (let index = 1; index < parts.length; index++)
      directories.add('apr-d5-fixture/' + parts.slice(0, index).join('/') + '/');
  }
  const entries = [
    ...[...directories]
      .sort()
      .map((name) => ({ name, bytes: Buffer.alloc(0), type: '5', mode: '0000775\0' })),
    ...Object.entries(files).map(([path, text]) => ({
      name: 'apr-d5-fixture/' + path,
      bytes: Buffer.from(text),
      type: '0',
      mode: '0000664\0',
    })),
  ];
  for (const { name, bytes, type, mode } of entries) {
    const header = Buffer.alloc(512);
    assert.ok(Buffer.byteLength(name) < 100);
    header.write(name);
    header.write(mode, 100);
    header.write('0000000\0', 108);
    header.write('0000000\0', 116);
    header.write(bytes.length.toString(8).padStart(11, '0') + '\0', 124);
    header.write('00000000000\0', 136);
    header.fill(32, 148, 156);
    header.write(type, 156);
    header.write('ustar\0', 257);
    header.write('00', 263);
    const sum = header.reduce((a, b) => a + b, 0);
    header.write(sum.toString(8).padStart(6, '0') + '\0 ', 148);
    blocks.push(header, bytes, Buffer.alloc((512 - (bytes.length % 512)) % 512));
  }
  blocks.push(Buffer.alloc(1024));
  return gzipSync(Buffer.concat(blocks));
}
export function createGitHubFixture(workflow) {
  const blobs = new Map(),
    trees = new Map(),
    commits = new Map();
  function tree(files) {
    const node = {};
    for (const [path, text] of Object.entries(files)) {
      const pieces = path.split('/');
      let current = node;
      for (const piece of pieces.slice(0, -1)) current = current[piece] ??= {};
      const bytes = Buffer.from(text),
        sha = objectHash('blob', bytes);
      blobs.set(sha, bytes);
      current[pieces.at(-1)] = { sha, bytes };
    }
    function encode(node) {
      const entries = Object.entries(node)
        .map(([path, item]) =>
          item.bytes
            ? { path, mode: '100644', type: 'blob', sha: item.sha, size: item.bytes.length }
            : { path, mode: '040000', type: 'tree', sha: encode(item) },
        )
        .sort((a, b) =>
          Buffer.compare(
            Buffer.from(a.path + (a.type === 'tree' ? '/' : '')),
            Buffer.from(b.path + (b.type === 'tree' ? '/' : '')),
          ),
        );
      const bytes = Buffer.concat(
        entries.flatMap((e) => [
          Buffer.from(`${e.type === 'tree' ? '40000' : e.mode} ${e.path}\0`),
          Buffer.from(e.sha, 'hex'),
        ]),
      );
      const sha = objectHash('tree', bytes);
      trees.set(sha, { sha, truncated: false, tree: entries });
      return sha;
    }
    return encode(node);
  }
  const trusted = {
    '.github/agentic-pr-review/trusted-proof.json': JSON.stringify({
      schema: 'agentic-pr-review.config.v1',
      instructionsPath: '.github/agentic-pr-review/trusted-proof-instructions.md',
      publication: { mode: 'sticky' },
    }),
    '.github/agentic-pr-review/trusted-proof-instructions.md':
      'Review correctness. Never log access tokens. PRIVATE_D5_TRUSTED_POLICY\n',
  };
  commits.set(workflowSha, { sha: workflowSha, tree: { sha: tree(trusted) }, parents: [] });
  const files = [
    {
      'Lookup.cs': 'static class Lookup { public static string? Find(string key) => null; }\n',
      'Caller.cs': 'class Caller { string Call(string key) => Lookup.Find(key).Trim(); }\n',
      'client.ts':
        'export const timeout = (config: { timeoutMs?: number }) => config.timeoutMs ?? 5000;\n',
    },
    {
      'Lookup.cs': 'static class Lookup { public static string? Find(string key) => null; }\n',
      'Caller.cs':
        'class Caller { string Call(string key) => Lookup.Find(key)?.Trim() ?? string.Empty; }\n',
      'client.ts':
        'export const timeout = (config: { timeoutMs?: number }) => config.timeoutMs || 5000;\n',
    },
    {
      'Lookup.cs': 'static class Lookup { public static string? Find(string key) => null; }\n',
      'Caller.cs':
        'class Caller { string Call(string key) => Lookup.Find(key)?.Trim() ?? string.Empty; }\n',
      'client.ts':
        'export const timeout = (config: { timeoutMs?: number }) => config.timeoutMs ?? 5000;\n',
      'log.ts':
        'export function authenticate(accessToken: string) { console.error(accessToken); }\n',
    },
  ];
  // Keep trusted policy files in the PR tree; only the authored review corpus
  // differs from the immutable base. Ordinary V1 also reads GitHub's PR-files
  // view, whose metadata must agree with these exact Git blobs.
  for (const headFiles of files) Object.assign(headFiles, trusted);
  let parent = workflowSha;
  const heads = files.map((items, index) => {
    const root = tree(items),
      text = Buffer.from(
        `tree ${root}\nparent ${parent}\nauthor D5 <d5@example.invalid> 1 +0000\ncommitter D5 <d5@example.invalid> 1 +0000\n\nD5 case ${index}\n`,
      );
    const sha = objectHash('commit', text);
    commits.set(sha, { sha, tree: { sha: root }, parents: [{ sha: parent }] });
    parent = sha;
    return sha;
  });
  let ordinal = 0,
    stale = false,
    comment = null,
    writes = 0;
  const calls = [];
  const knownRuns = new Set([1101]);
  let archiveMode = 'success';
  let holdReadback = false,
    knownCommitBarrier = false,
    readbackWriteBaseline = 0;
  const pull = () => ({
    id: 1000,
    number: 147,
    state: 'open',
    draft: false,
    merged_at: null,
    base: { ref: 'main', sha: workflowSha, repo: { id: 42, full_name: repository } },
    head: { sha: stale ? 'f'.repeat(40) : heads[ordinal], repo: { id: 42, full_name: repository } },
  });
  const issueComment = (body) => ({
    id: 701,
    url: `https://api.github.com/repos/${repository}/issues/comments/701`,
    html_url: `https://github.com/${repository}/pull/147#issuecomment-701`,
    body,
  });
  return {
    heads,
    calls,
    get knownCommitBarrier() {
      return knownCommitBarrier;
    },
    archiveMode(value) {
      assert.ok(['success', 'bad-redirect', 'wrong-archive'].includes(value));
      archiveMode = value;
    },
    holdReadback(value) {
      holdReadback = value;
      knownCommitBarrier = false;
      readbackWriteBaseline = writes;
    },
    get ordinal() {
      return ordinal;
    },
    get writes() {
      return writes;
    },
    get comment() {
      return comment;
    },
    configure(index, id) {
      ordinal = index;
      assert.ok(Number.isSafeInteger(id) && id > 0);
      knownRuns.add(id);
      assert.ok(knownRuns.size <= 1024);
      stale = false;
    },
    setStale() {
      stale = true;
    },
    snapshot() {
      return structuredClone(comment);
    },
    restore(value) {
      comment = structuredClone(value);
    },
    async handle(request, response) {
      const url = new URL(request.url, 'https://' + request.headers.host),
        path = url.pathname;
      calls.push({ host: request.headers.host, method: request.method, path });
      assert.ok(calls.length <= 8000);
      if (request.headers.host === 'codeload.github.com') {
        assert.equal(request.headers.authorization, undefined);
        assert.equal(request.method, 'GET');
        const index = heads.indexOf(path.slice(`/${repository}/legacy.tar.gz/`.length));
        assert.ok(index >= 0);
        const bytes = tar(
          archiveMode === 'wrong-archive'
            ? { ...files[index], 'Caller.cs': 'synthetic wrong source bytes\n' }
            : files[index],
        );
        response
          .writeHead(200, { 'Content-Type': 'application/x-gzip', 'Content-Length': bytes.length })
          .end(bytes);
        return;
      }
      assert.equal(request.headers.host, 'api.github.com');
      assert.equal(request.headers.authorization, 'Bearer ' + githubKey);
      const prefix = `/repos/${repository}`;
      assert.ok(path.startsWith(prefix));
      const suffix = path.slice(prefix.length);
      if (suffix === '')
        return json(response, { id: 42, full_name: repository, default_branch: 'main' });
      const runAttempt = /^\/actions\/runs\/([1-9][0-9]*)\/attempts\/1$/u.exec(suffix);
      if (runAttempt) {
        const requested = Number(runAttempt[1]);
        if (!Number.isSafeInteger(requested) || !knownRuns.has(requested))
          return json(response, {}, 404);
        return json(response, {
          id: requested,
          run_attempt: 1,
          workflow_id: 72,
          name: 'R4 trusted proof',
          path: '.github/workflows/r4-trusted-proof.yml',
          head_branch: 'main',
          head_sha: workflowSha,
          event: 'workflow_dispatch',
          conclusion: null,
          repository: { id: 42, full_name: repository },
          head_repository: { id: 42, full_name: repository },
          actor: { id: 7, login: 'maintainer' },
          triggering_actor: { id: 7, login: 'maintainer' },
          pull_requests: [],
        });
      }
      if (suffix === '/collaborators/maintainer/permission')
        return json(response, { permission: 'write' });
      if (suffix === '/contents/.github/workflows/r4-trusted-proof.yml') {
        assert.equal(url.searchParams.get('ref'), workflowSha);
        const bytes = Buffer.from(workflow);
        return json(response, {
          type: 'file',
          encoding: 'base64',
          name: 'r4-trusted-proof.yml',
          path: '.github/workflows/r4-trusted-proof.yml',
          size: bytes.length,
          sha: objectHash('blob', bytes),
          content: bytes.toString('base64'),
        });
      }
      if (suffix === '/pulls/147') return json(response, pull());
      if (suffix.startsWith('/git/commits/')) {
        const value = commits.get(suffix.slice('/git/commits/'.length));
        assert.ok(value);
        return json(response, value);
      }
      if (suffix.startsWith('/git/trees/')) {
        const value = trees.get(suffix.slice('/git/trees/'.length));
        assert.ok(value);
        return json(response, value);
      }
      if (suffix.startsWith('/git/blobs/')) {
        const sha = suffix.slice('/git/blobs/'.length),
          bytes = blobs.get(sha);
        assert.ok(bytes);
        return json(response, {
          sha,
          size: bytes.length,
          encoding: 'base64',
          content: bytes.toString('base64'),
        });
      }
      if (suffix.startsWith('/tarball/')) {
        const sha = suffix.slice('/tarball/'.length);
        assert.ok(heads.includes(sha));
        response
          .writeHead(302, {
            location:
              archiveMode === 'bad-redirect'
                ? 'https://example.invalid/refused'
                : `https://codeload.github.com/${repository}/legacy.tar.gz/${sha}`,
          })
          .end();
        return;
      }
      if (suffix === '/pulls/147/files' && request.method === 'GET') {
        assert.equal(url.searchParams.get('per_page'), '100');
        assert.equal(url.searchParams.get('page'), '1');
        return json(
          response,
          Object.entries(files[ordinal])
            .filter(([path]) => !Object.hasOwn(trusted, path))
            .map(([filename, text]) => {
              const lines = text.trimEnd().split('\n');
              return {
                sha: objectHash('blob', Buffer.from(text)),
                filename,
                previous_filename: null,
                status: 'added',
                additions: lines.length,
                deletions: 0,
                changes: lines.length,
                patch:
                  `@@ -0,0 +1,${lines.length} @@\n` + lines.map((line) => '+' + line).join('\n'),
              };
            }),
        );
      }
      if (suffix === '/pulls/147/comments' && request.method === 'GET') return json(response, []);
      if (suffix === '/issues/147/comments' && request.method === 'GET')
        return json(response, comment ? [comment] : []);
      if (
        (suffix === '/issues/147/comments' && request.method === 'POST') ||
        (suffix === '/issues/comments/701' && request.method === 'PATCH')
      ) {
        comment = issueComment(JSON.parse(await readBody(request)).body);
        writes++;
        return json(response, comment, request.method === 'POST' ? 201 : 200);
      }
      if (
        suffix === '/issues/comments/701' &&
        request.method === 'GET' &&
        holdReadback &&
        writes > readbackWriteBaseline
      ) {
        assert.ok(comment);
        knownCommitBarrier = true;
        return;
      }
      if (suffix === '/issues/comments/701' && request.method === 'GET')
        return json(response, comment ?? {}, comment ? 200 : 404);
      assert.fail('d5_unexpected_github_route');
    },
  };
}
