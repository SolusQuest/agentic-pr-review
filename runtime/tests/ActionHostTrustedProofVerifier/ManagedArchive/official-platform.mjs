// External test service for the real bundled Octokit/DefaultArtifactClient.
// It stores original SDK ZIP bytes; it never constructs an APR state record.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';

export const githubKey = 'PRIVATE_D5_SYNTHETIC_GITHUB_KEY';
export const providerKey = 'PRIVATE_D5_SYNTHETIC_PROVIDER_KEY';
export const stateKey = Buffer.alloc(32, 0x74).toString('base64');
export const repository = 'SolusQuest/agentic-pr-review';
export function runtimeToken(run, attempt) {
  return (
    Buffer.from('{"alg":"none","typ":"JWT"}').toString('base64url') +
    '.' +
    Buffer.from(
      JSON.stringify({ scp: `Actions.Results:d5-run-${run}:d5-job-${run}-${attempt}` }),
    ).toString('base64url') +
    '.'
  );
}
const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex');
const json = (response, value, status = 200) => {
  const bytes = Buffer.from(JSON.stringify(value));
  response.writeHead(status, {
    'Content-Type': 'application/json',
    'Content-Length': bytes.length,
  });
  response.end(bytes);
};
async function body(request, maximum = 8 * 1024 * 1024) {
  const chunks = [];
  let size = 0;
  for await (const chunk of request) {
    size += chunk.length;
    assert.ok(size <= maximum, 'd5_official_body_bound');
    chunks.push(chunk);
  }
  return Buffer.concat(chunks);
}

export function createOfficialPlatform(origin) {
  const artifacts = new Map();
  const pending = new Map();
  const runs = new Map();
  const calls = [];
  let current;
  let nextId = 1001;
  const configure = (run, attempt = 1) => {
    assert.ok(Number.isSafeInteger(run) && run > 0 && Number.isSafeInteger(attempt) && attempt > 0);
    current = { run, attempt };
    runs.set(`${run}/${attempt}`, { id: run, run_attempt: attempt });
    return runtimeToken(run, attempt);
  };
  const snapshot = () =>
    [...artifacts].map(([id, item]) => [
      id,
      {
        metadata: structuredClone(item.metadata),
        bytes: Buffer.from(item.bytes),
      },
    ]);
  const restore = (entries) => {
    assert.equal(pending.size, 0, 'd5_upload_must_finish_before_restore');
    artifacts.clear();
    for (const [id, item] of entries)
      artifacts.set(id, {
        metadata: structuredClone(item.metadata),
        bytes: Buffer.from(item.bytes),
      });
  };
  return {
    calls,
    configure,
    snapshot,
    restore,
    get size() {
      return artifacts.size;
    },
    async handle(request, response) {
      const url = new URL(request.url, origin());
      const route = url.pathname;
      if (route.startsWith('/twirp/')) {
        assert.ok(current, 'd5_official_invocation_required');
        assert.equal(request.method, 'POST');
        assert.equal(
          request.headers.authorization,
          'Bearer ' + runtimeToken(current.run, current.attempt),
        );
        const document = JSON.parse(await body(request, 64 * 1024));
        assert.equal(document.workflow_run_backend_id, `d5-run-${current.run}`);
        assert.equal(
          document.workflow_job_run_backend_id,
          `d5-job-${current.run}-${current.attempt}`,
        );
        if (route.endsWith('/CreateArtifact')) {
          assert.equal(typeof document.name, 'string');
          assert.ok(document.name.length > 0 && document.name.length <= 512);
          assert.ok(
            ![...artifacts.values()].some(
              (item) =>
                item.metadata.name === document.name &&
                item.metadata.workflow_run.id === current.run,
            ),
          );
          const id = nextId++;
          pending.set(id, { document, run: current.run, blocks: new Map(), archive: null });
          calls.push({ operation: 'create', id, run: current.run });
          json(response, {
            ok: true,
            signedUploadUrl: origin() + `/d5-account/d5-container/${id}?sig=synthetic`,
          });
        } else if (route.endsWith('/FinalizeArtifact')) {
          const candidates = [...pending].filter(
            ([, item]) => item.run === current.run && item.document.name === document.name,
          );
          assert.equal(candidates.length, 1);
          const [id, item] = candidates[0];
          assert.ok(item.archive?.length > 0, 'd5_complete_zip_required');
          const expiry =
            typeof item.document.expires_at === 'string'
              ? item.document.expires_at
              : new Date(Number(item.document.expires_at.seconds) * 1000).toISOString();
          const metadata = {
            id,
            name: item.document.name,
            size_in_bytes: item.archive.length,
            expired: false,
            expires_at: expiry,
            digest: 'sha256:' + sha256(item.archive),
            workflow_run: { id: item.run },
          };
          assert.ok(Number.isFinite(Date.parse(expiry)));
          artifacts.set(id, { metadata, bytes: Buffer.from(item.archive) });
          pending.delete(id);
          assert.ok(artifacts.size <= 1024);
          assert.ok(
            [...artifacts.values()].reduce((sum, value) => sum + value.bytes.length, 0) <=
              64 * 1024 * 1024,
          );
          calls.push({ operation: 'finalize', id, run: current.run });
          json(response, { ok: true, artifactId: String(id) });
        } else assert.fail('d5_unknown_results_command');
        return true;
      }
      const upload = /^\/d5-account\/d5-container\/([1-9][0-9]*)$/u.exec(route);
      if (upload) {
        assert.equal(request.method, 'PUT');
        assert.equal(request.headers.authorization, undefined);
        assert.equal(url.searchParams.get('sig'), 'synthetic');
        const item = pending.get(Number(upload[1]));
        assert.ok(item);
        if (url.searchParams.get('comp') === 'block') {
          const id = url.searchParams.get('blockid');
          assert.ok(id && !item.blocks.has(id) && item.blocks.size < 64);
          item.blocks.set(id, await body(request));
        } else if (url.searchParams.get('comp') === 'blocklist') {
          const xml = (await body(request, 64 * 1024)).toString();
          const order = [
            ...xml.matchAll(/<(?:Latest|Uncommitted)>([^<]+)<\/(?:Latest|Uncommitted)>/gu),
          ];
          assert.ok(order.length > 0 && order.length <= 64);
          const blocks = order.map((match) => item.blocks.get(match[1]));
          assert.ok(blocks.every(Buffer.isBuffer));
          item.archive = Buffer.concat(blocks);
        } else assert.fail('d5_unknown_blob_command');
        response.writeHead(201).end();
        return true;
      }
      const downloaded = /^\/d5-download\/([1-9][0-9]*)$/u.exec(route);
      if (downloaded) {
        assert.equal(request.method, 'GET');
        assert.equal(request.headers.authorization, undefined);
        const item = artifacts.get(Number(downloaded[1]));
        assert.ok(item);
        calls.push({ operation: 'download', id: item.metadata.id });
        response.writeHead(200, { 'Content-Length': item.bytes.length }).end(item.bytes);
        return true;
      }
      const prefix = `/repos/${repository}/actions/`;
      if (!route.startsWith(prefix)) return false;
      assert.equal(request.headers.authorization, 'token ' + githubKey);
      if (route === prefix + 'artifacts') {
        assert.equal(request.method, 'GET');
        const page = Number(url.searchParams.get('page') ?? '1');
        const perPage = Number(url.searchParams.get('per_page') ?? '100');
        assert.ok(Number.isSafeInteger(page) && page > 0 && page <= 11 && perPage === 100);
        const values = [...artifacts.values()].map((item) => item.metadata);
        calls.push({ operation: 'list', page, total: values.length });
        json(response, {
          total_count: values.length,
          artifacts: values.slice((page - 1) * perPage, page * perPage),
        });
        return true;
      }
      const artifact = new RegExp('^' + prefix + 'artifacts/([1-9][0-9]*)(/zip)?$', 'u').exec(
        route,
      );
      if (artifact) {
        const id = Number(artifact[1]);
        const item = artifacts.get(id);
        if (!item) {
          json(response, {}, 404);
          return true;
        }
        if (artifact[2]) {
          assert.equal(request.method, 'GET');
          response.writeHead(302, { location: origin() + '/d5-download/' + id }).end();
        } else if (request.method === 'DELETE') {
          artifacts.delete(id);
          calls.push({ operation: 'delete', id });
          response.writeHead(204).end();
        } else {
          assert.equal(request.method, 'GET');
          calls.push({ operation: 'metadata', id });
          json(response, item.metadata);
        }
        return true;
      }
      const run = new RegExp('^' + prefix + 'runs/([1-9][0-9]*)/attempts/([1-9][0-9]*)$', 'u').exec(
        route,
      );
      if (run) {
        assert.equal(request.method, 'GET');
        const value = runs.get(`${run[1]}/${run[2]}`);
        calls.push({ operation: 'run-attempt', run: Number(run[1]), attempt: Number(run[2]) });
        json(response, value ?? {}, value ? 200 : 404);
        return true;
      }
      assert.fail('d5_unknown_artifact_route');
    },
  };
}
