// Test transport endpoints for the real bundled Octokit and DefaultArtifactClient.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';

export const syntheticGitHubToken = 'PRIVATE_D4_SYNTHETIC_GITHUB_TOKEN';
export const syntheticRuntimeToken =
  Buffer.from('{"alg":"none","typ":"JWT"}').toString('base64url') +
  '.' +
  Buffer.from('{"scp":"Actions.Results:d4-run-backend:d4-job-backend"}').toString('base64url') +
  '.';

export function createOfficialPlatform(origin) {
  const calls = [];
  const blocks = new Map();
  let pending;
  let artifact;
  let archive;
  const json = (response, value, status = 200) => {
    const bytes = Buffer.from(JSON.stringify(value));
    response.writeHead(status, {
      'Content-Type': 'application/json',
      'Content-Length': bytes.length,
    });
    response.end(bytes);
  };
  async function body(request) {
    const chunks = [];
    let size = 0;
    for await (const chunk of request) {
      size += chunk.length;
      assert.ok(size <= 1024 * 1024);
      chunks.push(chunk);
    }
    return Buffer.concat(chunks);
  }
  return {
    calls,
    uploaded() {
      assert.ok(artifact && archive);
      return { metadata: structuredClone(artifact), bytes: Buffer.from(archive) };
    },
    async handle(request, response) {
      const url = new URL(request.url, origin());
      const route = url.pathname;
      if (route.startsWith('/twirp/')) {
        assert.equal(request.method, 'POST');
        assert.equal(request.headers.authorization, 'Bearer ' + syntheticRuntimeToken);
        const document = JSON.parse(await body(request));
        calls.push(route);
        if (route.endsWith('/CreateArtifact')) {
          assert.equal(document.workflow_run_backend_id, 'd4-run-backend');
          assert.equal(document.workflow_job_run_backend_id, 'd4-job-backend');
          pending = document;
          blocks.clear();
          json(response, {
            ok: true,
            signedUploadUrl: origin() + '/d4-account/d4-container/d4-blob?sig=synthetic',
          });
        } else if (route.endsWith('/FinalizeArtifact')) {
          assert.ok(pending);
          archive = Buffer.concat([...blocks.values()]);
          assert.ok(archive.length > 0);
          const expiry =
            typeof pending.expires_at === 'string'
              ? pending.expires_at
              : new Date(Number(pending.expires_at.seconds) * 1000).toISOString();
          artifact = {
            id: 1001,
            name: pending.name,
            size_in_bytes: archive.length,
            expired: false,
            expires_at: expiry,
            digest: 'sha256:' + createHash('sha256').update(archive).digest('hex'),
            workflow_run: { id: 900 },
          };
          json(response, { ok: true, artifactId: '1001' });
        } else assert.fail('unexpected results service command');
        return true;
      }
      if (route === '/d4-account/d4-container/d4-blob') {
        assert.equal(request.headers.authorization, undefined);
        assert.equal(request.method, 'PUT');
        assert.equal(url.searchParams.get('sig'), 'synthetic');
        calls.push('blob:' + url.searchParams.get('comp'));
        if (url.searchParams.get('comp') === 'block')
          blocks.set(url.searchParams.get('blockid'), await body(request));
        else if (url.searchParams.get('comp') === 'blocklist') {
          const xml = (await body(request)).toString();
          const order = [
            ...xml.matchAll(/<(?:Latest|Uncommitted)>([^<]+)<\/(?:Latest|Uncommitted)>/gu),
          ];
          assert.ok(order.length > 0);
          const ordered = order.map((match) => blocks.get(match[1]));
          assert.ok(ordered.every(Buffer.isBuffer));
          blocks.clear();
          ordered.forEach((bytes, index) => blocks.set(String(index), bytes));
        } else assert.fail('unexpected signed upload command');
        response.writeHead(201).end();
        return true;
      }
      if (route === '/d4-download') {
        assert.equal(request.headers.authorization, undefined);
        assert.ok(archive);
        calls.push('signed-download');
        response.writeHead(200, { 'Content-Length': archive.length }).end(archive);
        return true;
      }
      const prefix = '/repos/SolusQuest/agentic-pr-review/actions/';
      if (!route.startsWith(prefix)) return false;
      assert.equal(request.headers.authorization, 'token ' + syntheticGitHubToken);
      calls.push(request.method + ' ' + route);
      if (route === prefix + 'artifacts')
        json(response, { total_count: artifact ? 1 : 0, artifacts: artifact ? [artifact] : [] });
      else if (route === prefix + 'artifacts/1001/zip') {
        assert.ok(artifact);
        response.writeHead(302, { location: origin() + '/d4-download' }).end();
      } else if (route === prefix + 'artifacts/1001' && request.method === 'DELETE') {
        assert.ok(artifact);
        artifact = undefined;
        response.writeHead(204).end();
      } else if (route === prefix + 'artifacts/1001')
        json(response, artifact ?? {}, artifact ? 200 : 404);
      else if (route === prefix + 'runs/900/attempts/1')
        json(response, { id: 900, run_attempt: 1 });
      else assert.fail('unexpected authenticated artifact route');
      return true;
    },
  };
}
