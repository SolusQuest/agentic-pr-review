// Controlled HTTP service for the actual C# caller and isolated pinned SDK.
// It retains original ZIP bytes; no live service is contacted.
import { createServer } from 'node:http';
import { readFile, writeFile, rename, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { createUploadServer } from './upload-server.js';
import { UploadStaging } from './upload-staging.js';
import { UploadOperations } from './upload-operations.js';
import { SdkProcessOwner } from './sdk-process-owner.js';
import { digestBytes, readArtifactArchive } from './historical-transport-envelope.fixture.js';
import { ArtifactBridgeOperationBudget } from './operation-budget.js';

const [endpoint, build, stagingRoot, controlRoot, runId = '7001', runAttempt = '2'] =
  process.argv.slice(2);
if (!endpoint || !build || !stagingRoot || !controlRoot) throw new Error('fixture_arguments');
interface Stored {
  id: number;
  name: string;
  archive: Buffer;
  digest: string;
  run: number;
  attempt: number;
  expiry: string;
}
const objects = new Map<number, Stored>();
let nextId = 1000;
const counts = {
  sdk: 0,
  rest: 0,
  anonymous: 0,
  deletes: 0,
  node_non_upload: 0,
  historical_reads: 0,
  sdk_descendants: 0,
  capability_requests: 0,
  sdk_finalizes: 0,
};
const flag = async (name: string): Promise<boolean> => {
  try {
    await rm(join(controlRoot, name));
    return true;
  } catch {
    return false;
  }
};
let pendingCounts = Promise.resolve();
const saveCounts = () => {
  const snapshot = JSON.stringify(counts);
  pendingCounts = pendingCounts.then(async () => {
    const temporary = join(controlRoot, 'counts.tmp');
    await writeFile(temporary, snapshot);
    await rename(temporary, join(controlRoot, 'counts.json'));
  });
  return pendingCounts;
};
const pending = new Map<
  number,
  { name: string; expires: unknown; blocks: Map<string, Buffer>; archive?: Buffer }
>();
const lostCreateNames = new Set<string>();
let servicePort = 0;
const runtimeToken =
  Buffer.from('{"alg":"none","typ":"JWT"}').toString('base64url') +
  '.' +
  Buffer.from(
    JSON.stringify({
      scp: `Actions.Results:fixture-run-${runId}:fixture-job-${runId}-${runAttempt}`,
    }),
  ).toString('base64url') +
  '.';
let executor: UploadOperations;
const bridge = createUploadServer({
  endpoint,
  buildDiscriminator: build,
  executor: {
    execute: async (command, signal) => {
      counts.capability_requests++;
      await saveCounts();
      return executor.execute(command, signal);
    },
    sealAdmission: () => executor.sealAdmission(),
    awaitQuiescence: (ms) => executor.awaitQuiescence(ms),
    dispose: () => executor.dispose(),
  },
});
const body = async (
  request: AsyncIterable<Uint8Array>,
  maximum = 9 * 1024 * 1024,
): Promise<Buffer> => {
  const chunks: Buffer[] = [];
  let length = 0;
  for await (const chunk of request) {
    length += chunk.length;
    if (length > maximum) throw new Error('fixture_body_bound');
    chunks.push(Buffer.from(chunk));
  }
  return Buffer.concat(chunks, length);
};
const descriptor = (item: Stored) => ({
  id: item.id,
  name: item.name,
  size_in_bytes: item.archive.length,
  digest: 'sha256:' + item.digest,
  workflow_run: { id: item.run },
  expires_at: item.expiry,
  expired: false,
});
const server = createServer(async (request, response) => {
  try {
    // Observe the actual uploader's process closure at every service boundary.
    // The build audit separately refuses process/worker creation dependencies.
    if (process.platform === 'linux') {
      const children = (await readFile(`/proc/self/task/${process.pid}/children`, 'utf8')).trim();
      for (const pid of children.split(' ').filter(Boolean)) {
        const descendants = await readFile(`/proc/${pid}/task/${pid}/children`, 'utf8').catch(
          () => '',
        );
        if (descendants.trim()) counts.sdk_descendants++;
      }
    }
    const url = new URL(request.url!, 'http://fixture');
    const sdkJson = (status: number, value: unknown) => {
      response.writeHead(status, { 'content-type': 'application/json' });
      response.end(JSON.stringify(value));
    };
    if (url.pathname.endsWith('/CreateArtifact')) {
      counts.sdk++;
      const document = JSON.parse((await body(request)).toString('utf8'));
      if (lostCreateNames.has(document.name)) {
        sdkJson(409, { code: 'already_exists', msg: 'synthetic' });
        return;
      }
      const id = nextId++;
      pending.set(id, { name: document.name, expires: document.expires_at, blocks: new Map() });
      if (await flag('lost-create-response')) {
        lostCreateNames.add(document.name);
        response.destroy();
        return;
      }
      sdkJson(200, {
        ok: true,
        signedUploadUrl: `http://127.0.0.1:${servicePort}/account/container/${id}?sig=synthetic`,
      });
      return;
    }
    if (url.pathname.startsWith('/account/container/')) {
      if (await flag('blob-failure')) {
        request.resume();
        sdkJson(400, { error: 'synthetic' });
        return;
      }
      const item = pending.get(Number(url.pathname.split('/').at(-1)));
      if (!item) throw new Error('fixture_upload_missing');
      if (url.searchParams.get('comp') === 'block')
        item.blocks.set(url.searchParams.get('blockid')!, await body(request));
      else if (url.searchParams.get('comp') === 'blocklist') {
        const xml = (await body(request)).toString('utf8');
        item.archive = Buffer.concat(
          [...xml.matchAll(/<Latest>([^<]+)<\/Latest>/gu)].map(
            (match) => item.blocks.get(match[1]!)!,
          ),
        );
      } else item.archive = await body(request);
      response.writeHead(201);
      response.end();
      return;
    }
    if (url.pathname.endsWith('/FinalizeArtifact')) {
      counts.sdk_finalizes++;
      const document = JSON.parse((await body(request)).toString('utf8'));
      const found = [...pending].find(([, item]) => item.name === document.name);
      if (!found) {
        sdkJson(400, { code: 'invalid_argument', msg: 'synthetic' });
        return;
      }
      const [id, item] = found;
      const archive = item.archive!;
      const budget = new ArtifactBridgeOperationBudget(
        new AbortController().signal,
        () => performance.now(),
        performance.now(),
      );
      const decoded = await readArtifactArchive(archive, digestBytes(archive), budget);
      decoded.encryptedBytes.fill(0);
      budget.dispose();
      counts.historical_reads++;
      const expiry =
        typeof item.expires === 'string'
          ? item.expires
          : new Date(Number((item.expires as { seconds: string }).seconds) * 1000).toISOString();
      objects.set(id, {
        id,
        name: item.name,
        archive,
        digest: digestBytes(archive),
        run: Number(runId),
        attempt: Number(runAttempt),
        expiry,
      });
      pending.delete(id);
      await saveCounts();
      if (await flag('may-commit-upload')) {
        response.destroy();
        return;
      }
      sdkJson(200, { ok: true, artifactId: (await flag('bad-receipt')) ? '0' : String(id) });
      return;
    }
    const anonymous = url.pathname.startsWith('/archive/');
    if (anonymous) {
      counts.anonymous++;
      if (request.headers.authorization !== undefined) throw new Error('anonymous_credential');
    } else counts.rest++;
    await saveCounts();
    const json = (status: number, value: unknown) => {
      response.writeHead(status, { 'content-type': 'application/json', etag: '"fixture"' });
      response.end(JSON.stringify(value));
    };
    const metadata = /^\/repos\/[^/]+\/[^/]+\/actions\/artifacts\/(\d+)(\/zip)?$/.exec(
      url.pathname,
    );
    if (anonymous) {
      const item = objects.get(Number(url.pathname.split('/').at(-1)));
      if (!item) {
        json(404, {});
        return;
      }
      if (await flag('bad-archive')) {
        response.end(Buffer.from('wrong'));
        return;
      }
      response.writeHead(200, { 'content-length': item.archive.length });
      response.end(item.archive);
      return;
    }
    if (metadata) {
      const item = objects.get(Number(metadata[1]));
      if (!item || (await flag('missing-readback'))) {
        json(404, {});
        return;
      }
      if (request.method === 'DELETE') {
        counts.deletes++;
        await saveCounts();
        if (await flag('delete-unknown')) {
          json(500, {});
          return;
        }
        objects.delete(item.id);
        if (await flag('may-commit-delete')) {
          request.socket.destroy();
          return;
        }
        response.writeHead(204);
        response.end();
        return;
      }
      if (metadata[2]) {
        const location = (await flag('unsafe-redirect'))
          ? 'http://127.0.0.1/private'
          : `https://fixtures.blob.core.windows.net/archive/${item.id}`;
        response.writeHead(302, { location });
        response.end();
        return;
      }
      const value = descriptor(item);
      if (await flag('fractional-size')) value.size_in_bytes = 0.5;
      if (await flag('overflow-size')) value.size_in_bytes = Number.MAX_VALUE;
      if (await flag('wrong-name')) value.name = 'wrong';
      if (await flag('wrong-run')) value.workflow_run.id++;
      if (await flag('missing-run')) (value as Record<string, unknown>).workflow_run = null;
      if (await flag('zero-run')) value.workflow_run.id = 0;
      if (await flag('unsafe-run')) value.workflow_run.id = Number.MAX_SAFE_INTEGER + 1;
      if (await flag('bad-digest')) value.digest = 'sha256:' + '0'.repeat(64);
      if (await flag('short-retention'))
        value.expires_at = new Date(Date.now() + 1000).toISOString();
      if (await flag('expired')) value.expired = true;
      if ((await flag('not-modified')) && request.headers['if-none-match']) {
        response.writeHead(304);
        response.end();
        return;
      }
      json(200, value);
      return;
    }
    const attempt = /\/actions\/runs\/(\d+)\/attempts\/(\d+)$/.exec(url.pathname);
    if (attempt) {
      json(200, {
        id: Number(attempt[1]),
        run_attempt: Number(attempt[2]) + ((await flag('wrong-attempt')) ? 1 : 0),
      });
      return;
    }
    if (url.pathname.endsWith('/actions/artifacts')) {
      const page = Number(url.searchParams.get('page'));
      let values = [...objects.values()].map(descriptor);
      try {
        values = JSON.parse(await readFile(join(controlRoot, 'listing.json'), 'utf8'));
      } catch {
        /* ordinary object inventory */
      }
      let total = values.length;
      if (await flag('fractional-total')) total = 0.5;
      if (await flag('overflow-total')) total = Number.MAX_SAFE_INTEGER + 1;
      if (page === 2 && (await flag('drift-total'))) total--;
      if (page === 2 && (await flag('fail-later'))) {
        json(503, {});
        return;
      }
      if ((await flag('duplicate-list')) && values.length) values = [...values, values[0]!];
      if (await flag('truncated-list')) total += 100;
      json(200, { total_count: total, artifacts: values.slice((page - 1) * 100, page * 100) });
      return;
    }
    json(404, {});
  } catch {
    response.destroy();
  } finally {
    await saveCounts();
  }
});
await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
const address = server.address();
if (!address || typeof address === 'string') throw new Error('fixture_address');
servicePort = address.port;
executor = new UploadOperations(
  await UploadStaging.create(stagingRoot),
  new SdkProcessOwner(join(controlRoot, 'sdk-upload.mjs'), {
    ACTIONS_RUNTIME_TOKEN: runtimeToken,
    ACTIONS_RESULTS_URL: `http://127.0.0.1:${servicePort}`,
    GITHUB_SERVER_URL: 'https://github.com',
  }),
  runId,
  runAttempt,
);
await new Promise<void>((resolve) => bridge.listen(endpoint, resolve));
await saveCounts();
process.stdout.write(`READY ${address.port}\n`);
