// External test service for the real bundled Octokit/DefaultArtifactClient.
// It stores original SDK ZIP bytes; it never constructs an APR state record.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { observeResponse } from './operation-trace.mjs';

export const githubKey = 'PRIVATE_D5_SYNTHETIC_GITHUB_KEY';
export const providerKey = 'PRIVATE_D5_SYNTHETIC_PROVIDER_KEY';
export const stateKey = Buffer.alloc(32, 0x74).toString('base64');
export const repository = 'SolusQuest/agentic-pr-review';
export const archiveDownloadHost = 'apr-d5.blob.core.windows.net';
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
// Current HTTPS JSON validators describe the complete bytes actually served.
// Historical HTTP and unsuccessful reads retain their original response behavior.
export function writeConditionalJson(request, response, bytes, headers) {
  const etag = '"' + sha256(bytes) + '"';
  if (request.method === 'GET' && request.headers['if-none-match'] === etag) {
    const { 'Content-Length': _length, ...bodyless } = headers;
    response.writeHead(304, { ...bodyless, ETag: etag }).end();
  } else response.writeHead(200, { ...headers, ETag: etag }).end(bytes);
}
const json = (response, value, status = 200, conditionalRequest) => {
  const bytes = Buffer.from(JSON.stringify(value));
  const headers = {
    'Content-Type': 'application/json',
    'Content-Length': bytes.length,
  };
  if (status === 200 && conditionalRequest)
    return writeConditionalJson(conditionalRequest, response, bytes, headers);
  response.writeHead(status, headers);
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

export function createOfficialPlatform(origin, trace, transaction) {
  const artifacts = new Map();
  const pending = new Map();
  const runs = new Map();
  const calls = [];
  let current;
  let nextId = 1001;
  let fault = null,
    faultReached = null;
  const heldResponses = new Set();
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
    assert.equal(heldResponses.size, 0, 'v1_sdk_response_must_settle_before_restore');
    artifacts.clear();
    for (const [id, item] of entries)
      artifacts.set(id, {
        metadata: structuredClone(item.metadata),
        bytes: Buffer.from(item.bytes),
      });
  };
  function download(request, response, route) {
    const downloaded = /^\/d5-download\/([1-9][0-9]*)$/u.exec(route);
    if (downloaded) {
      assert.equal(request.method, 'GET');
      assert.equal(request.headers.authorization, undefined);
      const item = artifacts.get(Number(downloaded[1]));
      assert.ok(item);
      observeResponse(trace, 'zip_body', response, item.metadata.id);
      calls.push({ operation: 'download', id: item.metadata.id });
      transaction?.delivered(response, 'downloaded', item.metadata.id);
      response.writeHead(200, { 'Content-Length': item.bytes.length }).end(item.bytes);
      return true;
    }
    return false;
  }
  function artifactRest(
    request,
    response,
    url,
    authorization,
    downloadOrigin,
    conditional = false,
  ) {
    const route = url.pathname;
    const prefix = `/repos/${repository}/actions/`;
    if (!route.startsWith(prefix + 'artifacts')) return false;
    assert.equal(request.headers.authorization, authorization);
    if (route === prefix + 'artifacts') {
      assert.equal(request.method, 'GET');
      const page = Number(url.searchParams.get('page') ?? '1');
      const perPage = Number(url.searchParams.get('per_page') ?? '100');
      assert.ok(Number.isSafeInteger(page) && page > 0 && page <= 11 && perPage === 100);
      const values = [...artifacts.values()].map((item) => item.metadata);
      observeResponse(trace, 'list', response);
      calls.push({ operation: 'list', page, total: values.length });
      json(
        response,
        {
          total_count: values.length,
          artifacts: values.slice((page - 1) * perPage, page * perPage),
        },
        200,
        conditional ? request : undefined,
      );
      return true;
    }
    const artifact = new RegExp('^' + prefix + 'artifacts/([1-9][0-9]*)(/zip)?$', 'u').exec(route);
    if (artifact) {
      const id = Number(artifact[1]);
      const item = artifacts.get(id);
      observeResponse(
        trace,
        artifact[2] ? 'zip_redirect' : request.method === 'DELETE' ? 'delete' : 'meta',
        response,
        id,
      );
      if (!item) {
        json(response, {}, 404);
        return true;
      }
      if (artifact[2]) {
        assert.equal(request.method, 'GET');
        response.writeHead(302, { location: downloadOrigin + '/d5-download/' + id }).end();
      } else if (request.method === 'DELETE') {
        artifacts.delete(id);
        calls.push({ operation: 'delete', id });
        response.writeHead(204).end();
      } else {
        assert.equal(request.method, 'GET');
        calls.push({ operation: 'metadata', id });
        json(response, item.metadata, 200, conditional ? request : undefined);
      }
      return true;
    }
    assert.fail('d5_unknown_artifact_route');
  }
  return {
    calls,
    configure,
    snapshot,
    restore,
    setFault(value) {
      fault = value;
      faultReached = null;
    },
    get faultReached() {
      return faultReached;
    },
    waitForHeldClose() {
      return Promise.all(
        [...heldResponses].map(
          (response) => new Promise((resolve) => response.once('close', resolve)),
        ),
      );
    },
    get size() {
      return artifacts.size;
    },
    async handleHttps(request, response) {
      const host = request.headers.host;
      const url = new URL(request.url, 'https://' + host);
      if (host === archiveDownloadHost) {
        assert.ok(download(request, response, url.pathname), 'd5_unknown_archive_download');
        return true;
      }
      if (host !== 'api.github.com') return false;
      return artifactRest(
        request,
        response,
        url,
        'Bearer ' + githubKey,
        'https://' + archiveDownloadHost,
        true,
      );
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
          if (fault?.kind === 'acceptance-refused' && document.name.startsWith(fault.family)) {
            faultReached = { kind: fault.kind };
            transaction?.event('acceptance_refused');
            json(response, { ok: false }, 400);
            return true;
          }
          assert.ok(
            ![...artifacts.values()].some(
              (item) =>
                item.metadata.name === document.name &&
                item.metadata.workflow_run.id === current.run,
            ),
          );
          const id = nextId++;
          observeResponse(trace, 'sdk_create', response, id);
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
          observeResponse(trace, 'sdk_finalize', response, id);
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
          transaction?.finalized(id, artifacts.get(id));
          if (
            fault?.kind?.startsWith('acceptance-') &&
            document.name.startsWith(fault.family) &&
            (!fault.select || (await fault.select(id)))
          ) {
            faultReached = { kind: fault.kind, id };
            transaction?.event('finalize_held', id);
            heldResponses.add(response);
            response.once('close', () => {
              heldResponses.delete(response);
              transaction?.event('held_response_closed', id);
            });
            return true;
          }
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
          observeResponse(trace, 'blob_block', response, Number(upload[1]));
          const id = url.searchParams.get('blockid');
          assert.ok(id && !item.blocks.has(id) && item.blocks.size < 64);
          item.blocks.set(id, await body(request));
        } else if (url.searchParams.get('comp') === 'blocklist') {
          observeResponse(trace, 'blob_commit', response, Number(upload[1]));
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
      if (download(request, response, route)) return true;
      if (artifactRest(request, response, url, 'token ' + githubKey, origin())) return true;
      const prefix = `/repos/${repository}/actions/`;
      if (!route.startsWith(prefix)) return false;
      assert.equal(request.headers.authorization, 'token ' + githubKey);
      const run = new RegExp('^' + prefix + 'runs/([1-9][0-9]*)/attempts/([1-9][0-9]*)$', 'u').exec(
        route,
      );
      if (run) {
        assert.equal(request.method, 'GET');
        const value = runs.get(`${run[1]}/${run[2]}`);
        observeResponse(trace, 'producer_run', response);
        calls.push({ operation: 'run-attempt', run: Number(run[1]), attempt: Number(run[2]) });
        json(response, value ?? {}, value ? 200 : 404);
        return true;
      }
      assert.fail('d5_unknown_artifact_route');
    },
  };
}
