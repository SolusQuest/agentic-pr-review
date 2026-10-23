// Loopback-only synthetic services. No real Actions credential is received or used.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { createHash } from 'node:crypto';
import { readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { createOfficialPlatform } from './managed-generated-official-platform.mjs';
import { readGeneratedActionAudit } from './generated-action-audit.mjs';
import { probeGeneratedUpload } from './generated-upload-probe.mjs';
import { repository, cases } from './composite-proof-source.mjs';
const work = process.argv[2];
const config = JSON.parse(await readFile(join(work, 'config.json'), 'utf8'));
const archive = await readFile(config.archive),
  runtime = await readFile(config.runtime);
const receipt = config.map.payload;
const metadata = Object.fromEntries(
  Object.entries(config.assetSha1).map(([relative, sha]) => [
    relative,
    {
      type: 'file',
      path: '.github/actions/agentic-pr-review/' + relative,
      sha,
      size: config.assetSizes[relative],
    },
  ]),
);
const mapBytes = Buffer.from(JSON.stringify(config.map));
metadata['payload-map.json'] = {
  type: 'file',
  path: '.github/actions/agentic-pr-review/payload-map.json',
  size: mapBytes.length,
  sha: createHash('sha1')
    .update('blob ' + mapBytes.length + '\0')
    .update(mapBytes)
    .digest('hex'),
};
let origin;
const platform = createOfficialPlatform(() => origin);
const errors = [];
const send = (response, value) => {
  const bytes = Buffer.from(JSON.stringify(value));
  response.writeHead(200, { 'Content-Length': bytes.length }).end(bytes);
};
let activeRequests = 0;
const server = createServer((request, response) => {
  activeRequests++;
  response.once('close', () => activeRequests--);
  void (async () => {
    if (request.url === '/control/stop') {
      await writeFile(
        join(work, 'service-result.json'),
        JSON.stringify({
          controlledCalls: platform.calls,
          errors,
          activeRequests: activeRequests - 1,
        }),
      );
      response.end('stopped');
      server.close();
      clearInterval(probe);
      clearTimeout(deadline);
      return;
    }
    if (await platform.handle(request, response)) return;
    const prefix = '/repos/' + repository;
    if (request.url.startsWith(prefix + '/contents/')) {
      const relative = request.url
        .slice((prefix + '/contents/.github/actions/agentic-pr-review/').length)
        .split('?')[0];
      assert.equal(new URL(request.url, origin).searchParams.get('ref'), config.actionSource);
      assert.ok(metadata[relative]);
      send(response, { ...metadata[relative], content: '', encoding: 'none' });
      return;
    }
    if (request.url === prefix + '/releases/tags/' + config.map.tag) {
      send(response, { id: 123, tag_name: config.map.tag, draft: false });
      return;
    }
    if (request.url === prefix + '/releases/123/assets?per_page=100') {
      send(response, [
        {
          id: 456,
          name: receipt.archiveName,
          state: 'uploaded',
          size: receipt.archiveSize,
          browser_download_url: `https://github.com/${repository}/releases/download/${config.map.tag}/${receipt.archiveName}`,
        },
      ]);
      return;
    }
    if (
      request.url === `/${repository}/releases/download/${config.map.tag}/${receipt.archiveName}`
    ) {
      response.writeHead(200, { 'Content-Length': archive.length }).end(archive);
      return;
    }
    if (request.url === new URL(config.runtimeUrl).pathname) {
      response.writeHead(200, { 'Content-Length': runtime.length }).end(runtime);
      return;
    }
    throw new Error('unexpected_controlled_route');
  })().catch((error) => {
    errors.push('controlled_request_failed');
    response.destroy();
  });
});
await new Promise((resolve, reject) => {
  server.once('error', reject);
  server.listen(0, '127.0.0.1', resolve);
});
origin = 'http://127.0.0.1:' + server.address().port;
config.origin = origin;
for (const name of cases) {
  const directory = join(work, name);
  await writeFile(
    join(directory, 'fixture.json'),
    JSON.stringify({
      audit: join(directory, 'audit.jsonl'),
      origin,
      bridgeProbe: name === 'valid',
      bridgeRelease: join(directory, 'release-host'),
    }),
  );
}
await writeFile(join(work, 'config.json'), JSON.stringify(config));
let probing = false,
  finished = false;
const probe = setInterval(() => {
  if (probing || finished) return;
  probing = true;
  void (async () => {
    const launch = (await readGeneratedActionAudit(join(work, 'valid/audit.jsonl'))).find(
      (row) => row.kind === 'launch',
    );
    if (!launch) return;
    finished = true;
    await probeGeneratedUpload(launch.document, platform);
    await writeFile(join(work, 'valid/release-host'), 'controlled SDK upload completed');
  })()
    .catch((error) => {
      errors.push('sdk_probe_failed');
      finished = true;
    })
    .finally(() => {
      probing = false;
    });
}, 25);
const deadline = setTimeout(() => {
  errors.push('fixture_deadline');
  server.closeAllConnections();
  server.close();
  clearInterval(probe);
}, 900_000);
await writeFile(join(work, 'ready'), 'ready');
