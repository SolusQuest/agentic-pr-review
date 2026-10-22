// Synthetic service for the actual T1 private upload producer and C# REST
// consumer. ZIP bytes come from the pinned SDK; no live service is contacted.
import { createServer } from 'node:http';
import { readFile, writeFile, rename, rm, stat } from 'node:fs/promises';
import { join } from 'node:path';
import { createZipUploadStream } from '../../../node_modules/@actions/artifact/lib/internal/upload/zip.js';
import type { ArtifactClient } from '@actions/artifact';
import { createArtifactBridgeServer } from './bridge-server.js';
import { ArtifactBridgeStaging } from './staging.js';
import { OfficialArtifactOperations } from './official-artifact-operations.js';
import { digestBytes, readArtifactArchive } from './transport-envelope.js';
import { ArtifactBridgeOperationBudget } from './operation-budget.js';

const [
  endpoint,
  build,
  stagingRoot,
  controlRoot,
  runId = '7001',
  runAttempt = '2',
  epoch = String(Math.floor(Date.now() / 1000)),
] = process.argv.slice(2);
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
const artifactClient = {
  uploadArtifact: async (
    name: string,
    files: string[],
    _root: string,
    options: { retentionDays: number },
  ) => {
    counts.sdk++;
    const source = files[0]!;
    const envelopeBytes = await readFile(source);
    const envelope = JSON.parse(envelopeBytes.toString('utf8'));
    const producer = {
      run: Number(envelope.producing_run_id),
      attempt: Number(envelope.producing_run_attempt),
    };
    envelopeBytes.fill(0);
    const stream = await createZipUploadStream(
      [
        {
          sourcePath: source,
          destinationPath: 'artifact-envelope.json',
          stats: await stat(source),
        },
      ],
      0,
    );
    const chunks: Buffer[] = [];
    for await (const chunk of stream) chunks.push(chunk as Buffer);
    const archive = Buffer.concat(chunks);
    for (const chunk of chunks) chunk.fill(0);
    // Retained S2 reader consumes the original ZIP emitted by the current
    // upload producer, including the maximum carrier in shared conformance.
    const budget = new ArtifactBridgeOperationBudget(
      new AbortController().signal,
      () => performance.now(),
      performance.now(),
    );
    try {
      const decoded = await readArtifactArchive(archive, digestBytes(archive), budget);
      try {
        if (
          decoded.encryptedObjectDigest !== envelope.encrypted_object_digest ||
          decoded.encryptedBytes.length !== Number(envelope.encrypted_object_size)
        )
          throw new Error('historical_reader_mismatch');
        counts.historical_reads++;
      } finally {
        decoded.encryptedBytes.fill(0);
      }
    } finally {
      budget.dispose();
    }
    const item = {
      id: nextId++,
      name,
      archive,
      digest: digestBytes(archive),
      ...producer,
      expiry: new Date(
        Number(epoch) * 1000 + options.retentionDays * 86400000 + 60000,
      ).toISOString(),
    };
    objects.set(item.id, item);
    await saveCounts();
    if (await flag('may-commit-upload')) throw new Error('synthetic_lost_sdk_ack');
    if (await flag('bad-receipt')) return { id: item.id, digest: 'bad', size: archive.length };
    return { id: item.id, digest: item.digest, size: archive.length };
  },
} as unknown as ArtifactClient;
const executor = new OfficialArtifactOperations({
  currentRunId: runId,
  currentRunAttempt: runAttempt,
  staging: await ArtifactBridgeStaging.create(stagingRoot),
  artifactClient,
  utcNow: () => Number(epoch) * 1000,
});
const bridge = createArtifactBridgeServer({
  endpoint,
  buildDiscriminator: build,
  executor: {
    execute: async (command, signal) => {
      if (command.operation !== 'upload_immutable') counts.node_non_upload++;
      return executor.execute(command, signal);
    },
  },
});
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
    const url = new URL(request.url!, 'http://fixture');
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
await new Promise<void>((resolve) => bridge.listen(endpoint, resolve));
await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
const address = server.address();
if (!address || typeof address === 'string') throw new Error('fixture_address');
await saveCounts();
process.stdout.write(`READY ${address.port}\n`);
