// Closed loopback service. Synthetic artifact endpoints are absent in hosted mode.
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import {
  repository,
  workflow,
  sourceMetadata,
  requireProof,
  sha256,
} from '../../../../scripts/cr2-hosted-artifact-policy.mjs';

export async function createService(config) {
  const objects = new Map(),
    pending = new Map();
  const counts = {
    create: 0,
    finalize: 0,
    blob: 0,
    deletes: 0,
    api: 0,
    anonymous: 0,
    source: 0,
    refused: 0,
  };
  let nextId = 910001,
    origin;
  const faults = config.faults ?? {};
  const json = (response, status, value) => {
    const bytes = Buffer.from(JSON.stringify(value));
    response.writeHead(status, {
      'content-type': 'application/json',
      'content-length': bytes.length,
    });
    response.end(bytes);
  };
  async function body(request, maximum = 16384) {
    const chunks = [];
    let length = 0;
    for await (const chunk of request) {
      requireProof((length += chunk.length) <= maximum);
      chunks.push(chunk);
    }
    return Buffer.concat(chunks);
  }
  const metadata = (item) => ({
    id: item.id,
    name: item.name,
    size_in_bytes: item.archive.length,
    digest: 'sha256:' + item.digest,
    workflow_run: { id: Number(config.runA) },
    expires_at: item.expires,
    expired: false,
  });
  const server = createServer((request, response) => {
    void (async () => {
      const url = new URL(request.url, 'http://fixture');
      if (request.method === 'POST' && url.pathname === '/stop') {
        requireProof((await body(request, 128)).toString() === config.stopNonce);
        response.end('stopped');
        server.close();
        return;
      }
      if (request.method === 'POST' && url.pathname === '/observations') {
        requireProof((await body(request, 128)).toString() === config.stopNonce);
        return json(response, 200, { counts, objectIds: [...objects.keys()] });
      }
      if (url.pathname.startsWith(`/repos/${repository}/contents/`)) {
        requireProof(request.method === 'GET' && !request.headers.authorization);
        counts.source++;
        return json(
          response,
          200,
          sourceMetadata(new URL(url.pathname + url.search, 'https://api.github.com'), config),
        );
      }
      if (config.mapBytes) {
        const map = JSON.parse(config.mapBytes),
          receipt = map.payload;
        const prefix = `/repos/${repository}`;
        if (url.pathname === `${prefix}/releases/tags/${map.tag}`)
          return json(response, 200, { id: 123, tag_name: map.tag, draft: false });
        if (request.url === `${prefix}/releases/123/assets?per_page=100`)
          return json(response, 200, [
            {
              id: 456,
              name: receipt.archiveName,
              state: 'uploaded',
              size: receipt.archiveSize,
              browser_download_url: `https://github.com/${repository}/releases/download/${map.tag}/${receipt.archiveName}`,
            },
          ]);
        if (
          url.pathname === `/${repository}/releases/download/${map.tag}/${receipt.archiveName}` ||
          url.pathname === new URL(config.runtimeUrl).pathname
        ) {
          requireProof(!request.headers.authorization);
          const bytes = await readFile(
            url.pathname === new URL(config.runtimeUrl).pathname
              ? config.runtimeArchive
              : config.archive,
          );
          response.writeHead(200, { 'content-length': bytes.length });
          response.end(bytes);
          return;
        }
      }
      requireProof(config.mode === 'rehearsal');
      if (url.pathname.endsWith('/CreateArtifact')) {
        requireProof(
          request.method === 'POST' &&
            request.headers.authorization === 'Bearer ' + config.runtimeToken,
        );
        requireProof(++counts.create <= 5);
        const value = JSON.parse((await body(request)).toString());
        requireProof(/^apr-object-[a-f0-9]{64}-[a-f0-9]{64}$/u.test(value.name));
        const id = nextId++;
        pending.set(id, { id, name: value.name, expires: value.expires_at, blocks: new Map() });
        return json(response, 200, {
          ok: true,
          signedUploadUrl: `${origin}/account/container/${id}?sig=cr2-h1-signed-url-canary`,
        });
      }
      if (url.pathname.startsWith('/account/container/')) {
        requireProof(
          request.method === 'PUT' && !request.headers.authorization && ++counts.blob <= 8,
        );
        const item = pending.get(Number(url.pathname.split('/').at(-1)));
        requireProof(item);
        if (url.searchParams.get('comp') === 'block')
          item.blocks.set(url.searchParams.get('blockid'), await body(request));
        else if (url.searchParams.get('comp') === 'blocklist') {
          const xml = (await body(request)).toString();
          item.archive = Buffer.concat(
            [...xml.matchAll(/<Latest>([^<]+)<\/Latest>/gu)].map((match) =>
              item.blocks.get(match[1]),
            ),
          );
        } else item.archive = await body(request);
        response.writeHead(201);
        response.end();
        return;
      }
      if (url.pathname.endsWith('/FinalizeArtifact')) {
        requireProof(
          request.method === 'POST' &&
            request.headers.authorization === 'Bearer ' + config.runtimeToken &&
            ++counts.finalize <= 5,
        );
        const value = JSON.parse((await body(request)).toString());
        const item = [...pending.values()].find((candidate) => candidate.name === value.name);
        requireProof(item?.archive && item.archive.length <= 16384);
        const expiry =
          typeof item.expires === 'string'
            ? item.expires
            : new Date(Number(item.expires.seconds) * 1000).toISOString();
        objects.set(item.id, { ...item, expires: expiry, digest: sha256(item.archive) });
        if (faults.lostFinalize) {
          response.destroy();
          return;
        }
        pending.delete(item.id);
        return json(response, 200, { ok: true, artifactId: String(item.id) });
      }
      if (url.pathname.startsWith('/archive/')) {
        requireProof(!request.headers.authorization && request.method === 'GET');
        counts.anonymous++;
        const item = objects.get(Number(url.pathname.split('/').at(-1)));
        requireProof(item);
        const bytes = faults.badArchive ? Buffer.from('synthetic-invalid-archive') : item.archive;
        response.writeHead(200, { 'content-length': bytes.length });
        response.end(bytes);
        return;
      }
      requireProof(request.headers.authorization === 'Bearer cr2-h1-rest-token-canary');
      counts.api++;
      if (url.pathname === `/repos/${repository}/actions/artifacts`) {
        requireProof(request.method === 'GET' && url.search === '?per_page=100&page=1');
        if (faults.permission) return json(response, 403, {});
        if (faults.metadataLimit)
          return json(response, 200, {
            total_count: 0,
            artifacts: [],
            padding: 'x'.repeat(524288),
          });
        let total = config.existingRecords ?? 0;
        if (faults.censusDrift && objects.size) total = 100;
        const dummy = Array.from({ length: Math.min(total, 100) }, (_, i) => ({
          id: i + 1,
          name: 'unrelated-' + i,
        }));
        const records = [...dummy, ...[...objects.values()].map(metadata)];
        if (faults.duplicateFamily && objects.size)
          records.push({ ...metadata([...objects.values()][0]), id: 999999 });
        return json(response, 200, {
          total_count:
            total +
            objects.size +
            (faults.duplicateFamily && objects.size ? 1 : 0) +
            (faults.incompleteCensus ? 1 : 0),
          artifacts: records.slice(0, 100),
        });
      }
      const match = new RegExp(
        `^/repos/${repository}/actions/artifacts/([1-9][0-9]*)(/zip)?$`,
      ).exec(url.pathname);
      if (match) {
        const item = objects.get(Number(match[1]));
        if (!item) return json(response, 404, {});
        if (request.method === 'DELETE') {
          requireProof(++counts.deletes === 1);
          if (faults.deleteDenied) return json(response, 403, {});
          objects.delete(item.id);
          if (faults.lostDelete) {
            response.destroy();
            return;
          }
          response.writeHead(204);
          response.end();
          return;
        }
        requireProof(request.method === 'GET');
        if (match[2]) {
          response.writeHead(302, {
            location: faults.unsafeRedirect
              ? 'https://example.invalid/denied'
              : `https://fixtures.blob.core.windows.net/archive/${item.id}?sig=cr2-h1-download-url-canary`,
          });
          response.end();
          return;
        }
        const value = metadata(item);
        if (faults.wrongName) value.name = 'unrelated';
        if (faults.wrongDigest) value.digest = 'sha256:' + '0'.repeat(64);
        if (faults.wrongSize) value.size_in_bytes++;
        if (faults.shortRetention) value.expires_at = new Date(Date.now() + 5000).toISOString();
        return json(response, 200, value);
      }
      const run = /\/actions\/runs\/([1-9][0-9]*)\/attempts\/1$/u.exec(url.pathname);
      if (run) {
        requireProof(request.method === 'GET' && run[1] === config.runA);
        return json(response, 200, {
          id: Number(config.runA),
          run_attempt: faults.wrongAttempt ? 2 : 1,
          head_sha: faults.wrongSource ? '0'.repeat(40) : config.source,
          path: workflow,
          event: 'workflow_dispatch',
          status: 'completed',
          conclusion: 'success',
          actor: { id: 16307884, login: 'Yuee98' },
          triggering_actor: { id: 16307884, login: 'Yuee98' },
          repository: { id: 1263978368 },
          created_at: new Date(config.createdAt ?? Date.now() - 1000).toISOString(),
        });
      }
      requireProof(false);
    })().catch(() => {
      counts.refused++;
      response.destroy();
    });
  });
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  origin = `http://127.0.0.1:${server.address().port}`;
  const timer = setTimeout(() => {
    server.closeAllConnections();
    server.close();
  }, 9 * 60_000);
  timer.unref();
  return {
    origin,
    counts,
    objects,
    faults,
    close: async () => {
      clearTimeout(timer);
      server.closeAllConnections();
      await new Promise((resolve) => server.close(resolve));
    },
  };
}
